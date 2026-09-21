using Broiler.CSS;
using Broiler.Dom;
using Broiler.Dom.Html;
using static Broiler.HtmlBridge.DomBridgeHostUtils;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private sealed class RenderProjection(
        DomDocument document,
        IReadOnlyDictionary<DomElement, DomElement> projectedToSource)
    {
        public DomDocument Document { get; } = document;

        public DomElement? SourceFor(DomElement projected) =>
            projectedToSource.TryGetValue(projected, out var source) ? source : null;
    }

    // Set only while a projection is prepared. Render-only synthetic nodes must be
    // owned by this document; using _document would advance the live document version.
    private DomDocument? _renderProjectionDocument;

    // Projection element -> script-visible source element for geometry and stable
    // view-transition identity while projection transforms execute.
    private IReadOnlyDictionary<DomElement, DomElement>? _renderProjectionSources;

    private DomDocument NodeFactoryDocument => _renderProjectionDocument ?? _document;

    private DomElement ResolveRenderSource(DomElement element) =>
        _renderProjectionSources is not null &&
        _renderProjectionSources.TryGetValue(element, out var source)
            ? source
            : element;

    /// <summary>
    /// Builds an isolated renderer document. Importing directly into the new owner
    /// prevents clone construction from publishing mutations against the live document.
    /// </summary>
    private RenderProjection CreateRenderProjection()
    {
        var projectedDocument = new DomDocument();
        var projectedToSource = new Dictionary<DomElement, DomElement>(ReferenceEqualityComparer.Instance);
        var sourceToProjected = new Dictionary<DomElement, DomElement>(ReferenceEqualityComparer.Instance);

        if (_document.DocumentType is { } documentType)
            projectedDocument.AppendChild(projectedDocument.ImportNode(documentType));

        var sourceRoot = RenderedDocumentElement;
        if (sourceRoot is null)
            return new RenderProjection(projectedDocument, projectedToSource);

        var projectedRoot = (DomElement)projectedDocument.ImportNode(sourceRoot, deep: true);
        projectedDocument.AppendChild(projectedRoot);
        CopyRenderProjectionState(
            sourceRoot,
            projectedRoot,
            projectedToSource,
            sourceToProjected);

        var previousDocument = _renderProjectionDocument;
        var previousSources = _renderProjectionSources;
        _renderProjectionDocument = projectedDocument;
        _renderProjectionSources = projectedToSource;
        _zoomSpecifiedStyleCache.Clear();
        try
        {
            if (ZoomBakeActive)
                ApplyZoomSerializationStyles(projectedRoot, 1.0);
            ApplySerializationTransforms(projectedRoot);
            ApplyViewTransitionRendering(projectedRoot);
            ReflectRenderState(projectedRoot);
        }
        finally
        {
            _zoomSpecifiedStyleCache.Clear();
            ClearComputedPropsCache();
            _renderProjectionSources = previousSources;
            _renderProjectionDocument = previousDocument;
        }

        return new RenderProjection(projectedDocument, projectedToSource);
    }

    private void CopyRenderProjectionState(
        DomNode source,
        DomNode projected,
        Dictionary<DomElement, DomElement> projectedToSource,
        Dictionary<DomElement, DomElement> sourceToProjected)
    {
        if (source is DomElement sourceElement && projected is DomElement projectedElement)
        {
            projectedToSource[projectedElement] = sourceElement;
            sourceToProjected[sourceElement] = projectedElement;
            CopyBridgeRuntimeStateTo(sourceElement, projectedElement);

            if (sourceElement.InternalShadowRoot is { } sourceShadow)
            {
                var projectedShadow = projectedElement.AttachShadow(
                    sourceShadow.Mode,
                    sourceShadow.DelegatesFocus,
                    sourceShadow.SlotAssignment);

                foreach (var shadowChild in sourceShadow.ChildNodes)
                {
                    var projectedChild = projectedElement.OwnerDocument.ImportNode(shadowChild, deep: true);
                    projectedShadow.AppendChild(projectedChild);
                    CopyRenderProjectionState(
                        shadowChild,
                        projectedChild,
                        projectedToSource,
                        sourceToProjected);
                }
            }
        }

        var sourceChildren = source.ChildNodes;
        var projectedChildren = projected.ChildNodes;
        for (var index = 0; index < sourceChildren.Count && index < projectedChildren.Count; index++)
        {
            CopyRenderProjectionState(
                sourceChildren[index],
                projectedChildren[index],
                projectedToSource,
                sourceToProjected);
        }
    }

    private DomElement CloneSnapshotContentForRender(DomElement source)
    {
        var projectionDocument = _renderProjectionDocument ??
            throw new InvalidOperationException("Snapshot content can only be cloned during render projection.");
        var clone = (DomElement)projectionDocument.ImportNode(source, deep: true);
        CopyRuntimeStateForClonedSubtree(source, clone, deep: true);
        return clone;
    }
}

/// <summary>
/// A scripted <c>src</c> frame's live document, carried to the renderer.
/// <para>
/// A nested browsing context reaches the renderer as markup on its container element: the renderer
/// has no bridge to ask. An <c>&lt;iframe srcdoc&gt;</c> already round-trips that way — the srcdoc
/// attribute is re-serialized from the live sub-document — but a frame loaded from <c>src</c> had no
/// such carrier, so <c>FragmentTreeBuilder.TryLoadEmbeddedDocument</c> re-read the resource
/// <em>from disk</em> and rendered the frame as the file, discarding everything the sub-document's
/// own scripts (or a parent reaching in through <c>frames[0]</c>) had done to it.
/// </para>
/// <para>
/// WPT <c>css-view-transitions/transition-in-empty-iframe</c> is exactly that: the parent calls
/// <c>frames[0].window.startTransition()</c>, the child's update callback unhides a limegreen box,
/// and the frame still painted the file's hidden box — an empty frame (issue #1552). The live
/// document is stamped on the container instead, and only when it has actually diverged from the
/// resource as parsed, so a frame nobody scripted still renders straight from its file.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>Each sub-document as its resource parsed — the "before any script touched it"
    /// serialization that says whether the live document has diverged.</summary>
    private readonly Dictionary<DomDocument, string> _subDocumentSourceMarkup = [];

    /// <summary>The doctype each sub-document's resource declared, so a stamped document keeps the
    /// rendering mode its file had. Serialization emits the document element alone.</summary>
    private readonly Dictionary<DomDocument, string> _subDocumentDoctype = [];

    /// <summary>Records how <paramref name="document"/> looked as parsed from
    /// <paramref name="html"/>, before any script ran against it.</summary>
    private void RecordSubDocumentSourceMarkup(DomDocument document, string html)
    {
        if (SerializeSubDocumentChildren(document) is { } markup)
            _subDocumentSourceMarkup[document] = markup;
        _subDocumentDoctype[document] = HasHtmlDoctype(html) ? "<!DOCTYPE html>" : string.Empty;
    }

    /// <summary>
    /// Stamps the live document of every <c>src</c>-loaded nested browsing context in the projection
    /// that has diverged from its resource, so the renderer paints what the frame became instead of
    /// what its file says.
    /// </summary>
    private void ProjectScriptedFrameDocuments(DomElement element)
    {
        foreach (var child in ChildElements(element).ToList())
            ProjectScriptedFrameDocuments(child);

        // A srcdoc frame already carries its live document in the attribute it was authored with.
        if (!IsNestedBrowsingContextContainer(element.TagName?.ToLowerInvariant()) ||
            HasAttr(element, "srcdoc"))
        {
            return;
        }

        var source = ResolveRenderSource(element);
        if (GetContentDocument(source) is not { } subDocumentRoot ||
            RenderedSubDocumentMarkup(subDocumentRoot) is not { Length: > 0 } markup)
        {
            return;
        }

        // The stamp stands in for the renderer's own loading of the frame's resource, so it applies
        // only where there is a resource to stand in for — a frame with no recorded source markup
        // (about:blank, an empty <iframe>) has nothing being re-read and is left alone. Scripting
        // content into such a frame still renders empty; that is a separate gap.
        //
        // And when the document still matches its resource, the renderer's file path already paints
        // it correctly: keeping every untouched frame off the serialize-and-reparse round trip.
        if (!_subDocumentSourceMarkup.TryGetValue(subDocumentRoot, out var sourceMarkup) ||
            string.Equals(sourceMarkup, markup, StringComparison.Ordinal))
        {
            return;
        }

        SetAttr(element, FrameDocumentAttr,
            _subDocumentDoctype.GetValueOrDefault(subDocumentRoot, string.Empty) + markup);

        if (GetSubDocumentBaseUrl(source) is { Length: > 0 } baseUrl)
            SetAttr(element, FrameDocumentBaseAttr, baseUrl);
    }
}

/// <summary>
/// The zoom half of serialization: bakes each element's used <c>zoom</c> into scaled lengths, and
/// emits scaled <c>::before</c>/<c>::after</c> overrides as bridge-owned author rules.
/// </summary>
public sealed partial class DomBridge
{
    private void ApplyZoomPseudoSerializationOverrides(DomElement root)
    {
        var rules = new List<string>();
        int pseudoIndex = 0;
        CollectZoomPseudoSerializationOverrides(root, 1.0, rules, ref pseudoIndex);
        InjectBridgeStyleRules(root, rules);
    }

    private void CollectZoomPseudoSerializationOverrides(DomElement element, double parentZoom, List<string> rules, ref int pseudoIndex)
    {
        if (IsText(element))
            return;

        var props = GetComputedProps(element);
        var specifiedZoom = props.GetValueOrDefault("zoom");
        var usedZoom = ResolveUsedZoom(specifiedZoom, parentZoom);

        if (Math.Abs(usedZoom - 1.0) > ZoomSerializationEpsilon)
        {
            AppendZoomPseudoSerializationOverride(element, "::before", usedZoom, rules, ref pseudoIndex);
            AppendZoomPseudoSerializationOverride(element, "::after", usedZoom, rules, ref pseudoIndex);
        }

        foreach (var child in ChildElements(element))
            CollectZoomPseudoSerializationOverrides(child, usedZoom, rules, ref pseudoIndex);
    }

    private void AppendZoomPseudoSerializationOverride(DomElement element, string pseudoElement, double usedZoom, List<string> rules, ref int pseudoIndex)
    {
        var pseudoProps = BuildComputedStyleMap(element, pseudoElement);
        var content = pseudoProps.GetValueOrDefault("content")?.Trim();
        if (string.IsNullOrEmpty(content) ||
            string.Equals(content, "none", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(content, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var declarations = new List<string>();
        foreach (var property in ZoomScaledSerializationProperties)
        {
            if (!pseudoProps.TryGetValue(property, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            if (CssLengthScaler.TryScaleValue(value, usedZoom, out var scaled))
                declarations.Add($"{property}: {scaled} !important");
        }

        if (declarations.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(element.Id))
            element.Id = $"broiler-zoom-pseudo-{++pseudoIndex}";

        rules.Add($"#{element.Id}{pseudoElement} {{ {string.Join("; ", declarations)}; }}");
    }

    private void ApplyZoomSerializationStyles(DomElement element, double parentZoom)
    {
        if (IsText(element))
            return;

        var props = GetComputedProps(element);
        var specifiedZoom = props.GetValueOrDefault("zoom");
        var usedZoom = ResolveUsedZoom(specifiedZoom, parentZoom);

        var willScale = Math.Abs(usedZoom - 1.0) > ZoomSerializationEpsilon;
        var willSvg = ShouldApplySvgSerializationAttributes(element);
        if (willScale)
        {
            foreach (var property in ZoomScaledSerializationProperties)
            {
                if (!TryGetZoomSerializableValue(element, props, property, out var value))
                    continue;

                if (CssLengthScaler.TryScaleValue(value, usedZoom, out var scaled))
                    BakedInlineStyle(element)[property] = scaled;
            }

        }

        if (willSvg)
            ApplyZoomSerializationSvgAttributes(element, usedZoom);

        BakedInlineStyle(element).Remove("zoom");

        foreach (var child in ChildElements(element))
            ApplyZoomSerializationStyles(child, usedZoom);
    }

    private bool TryGetZoomSerializableValue(DomElement element, Dictionary<string, string> props, string property, out string value)
    {
        value = string.Empty;
        if (ZoomPreferSpecifiedProperties.Contains(property))
        {
            var specifiedProps = GetZoomSpecifiedStyleMap(element);
            if (specifiedProps.TryGetValue(property, out value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value.Trim(), "inherit", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (props.TryGetValue(property, out value) && !string.IsNullOrWhiteSpace(value))
            return true;

        if (!BakedInlineStyle(element).TryGetValue(property, out var specified) ||
            !string.Equals(specified?.Trim(), "inherit", StringComparison.OrdinalIgnoreCase) ||
            ParentEl(element) == null)
        {
            return false;
        }

        var parentProps = GetComputedProps(ParentEl(element));
        if (parentProps.TryGetValue(property, out value) && !string.IsNullOrWhiteSpace(value))
            return true;

        if (BakedInlineStyle(ParentEl(element)!).TryGetValue(property, out value) && !string.IsNullOrWhiteSpace(value))
            return true;

        return false;
    }

    private Dictionary<string, string> GetZoomSpecifiedStyleMap(DomElement element)
    {
        if (!_zoomSpecifiedStyleCache.TryGetValue(element, out var specified))
        {
            specified = BuildSpecifiedStyleMap(element);
            _zoomSpecifiedStyleCache[element] = specified;
        }

        return specified;
    }
}

/// <summary>
/// Sibling partial peeled out of <c>DomBridge/Serialization.cs</c>
/// to keep it under the 750-line guideline: the cohesive SVG zoom-serialization attribute-scaling
/// cluster. When a subtree carries a used <c>zoom</c>, serialization bakes it into the SVG
/// presentation/geometry attributes (<c>fill</c>/<c>stroke</c>, <c>width</c>/<c>height</c>,
/// <c>points</c>, path <c>d</c>, …) by scaling each length token — resolving font-relative and
/// root-font-relative units against the element's specified font size and the owning element's
/// used zoom. Pure partial-class relocation — no signature, accessibility, or logic change.
/// Entered from <c>ApplyZoomSerializationStyles</c> via <c>ApplyZoomSerializationSvgAttributes</c>.
/// </summary>
public sealed partial class DomBridge
{
    private void ApplyZoomSerializationSvgAttributes(DomElement element, double usedZoom)
    {
        var tag = element.TagName.ToLowerInvariant();
        var props = GetComputedProps(element);

        ApplySvgPresentationAttribute(element, props, "fill", cascadeWins: true);
        ApplySvgPresentationAttribute(element, props, "stroke", cascadeWins: true);
        ApplySvgPresentationAttribute(element, props, "stroke-width", preferInlineStyle: true);

        // CSS Transforms 1 §6/§8. Both are presentation attributes in SVG 2 and both are read off
        // the serialized markup by SvgRenderer, which sees no stylesheet of its own — so a
        // `rect { transform-box: fill-box }` rule reached nothing at all, and the element's
        // `transform` kept turning about the viewport origin instead of about its own box. That is
        // the whole of what the 45 css-transforms/transform-origin/svg-origin-* tests measure;
        // every one of them declares transform-box in a <style> block rather than as an attribute.
        // Neither inherits, so the cascaded value is this element's own and may overwrite the
        // attribute — the same reasoning `fill` and `stroke` are given above.
        // `transform` itself is the third of them, and it was the one left out — so a
        // `#target { transform: rotate(90deg) }` rule reached nothing and the element rendered
        // untransformed, which is the whole of what the css-transforms/transform-box `svgbox-*`
        // family measures: each declares its transform in a <style> block. `transform-box` and
        // `transform-origin` were already projected, and on their own they can only move a
        // transform that arrived by attribute.
        //
        // A value that parses no function at all is skipped rather than written, and that is not a
        // detail — it is the rule CSS Transforms 1 §3 states. This bridge's cascade does not model
        // SVG presentation attributes as declarations, so an element carrying
        // `transform="translate(50)"` and no rule computes `none`, and writing that back would
        // erase the attribute the renderer was going to read. The same guard is what makes an
        // *invalid* declaration fall back to the attribute instead of destroying it:
        // `transform: scale(invalid)` must leave `transform="rotate(90)"` standing, which is
        // exactly what the nine svg-{document,external,inline}-styles-005/006/013 tests assert.
        ApplySvgPresentationAttribute(
            element, props, "transform", cascadeWins: true,
            accept: static value => Layout.IR.SvgTransform.TryParse(value, out _));
        ApplySvgPresentationAttribute(element, props, "transform-box", cascadeWins: true);
        ApplySvgPresentationAttribute(element, props, "transform-origin", cascadeWins: true);

        if (tag is "text" or "textpath")
        {
            ApplySvgPresentationAttribute(element, props, "font-size", preferInlineStyle: true);
            ApplySvgPresentationAttribute(element, props, "font-family");
        }

        switch (tag)
        {
            case "svg":
                ScaleSvgLengthAttribute(element, "width", usedZoom);
                ScaleSvgLengthAttribute(element, "height", usedZoom);
                break;
            case "rect":
                ScaleSvgLengthAttribute(element, "x", usedZoom);
                ScaleSvgLengthAttribute(element, "y", usedZoom);
                ScaleSvgLengthAttribute(element, "width", usedZoom);
                ScaleSvgLengthAttribute(element, "height", usedZoom);
                break;
            case "line":
                ScaleSvgLengthAttribute(element, "x1", usedZoom);
                ScaleSvgLengthAttribute(element, "x2", usedZoom);
                ScaleSvgLengthAttribute(element, "y1", usedZoom);
                ScaleSvgLengthAttribute(element, "y2", usedZoom);
                break;
            case "text":
                ScaleSvgLengthAttribute(element, "x", usedZoom);
                ScaleSvgLengthAttribute(element, "y", usedZoom);
                break;
            case "polygon":
            case "polyline":
                ScaleSvgPointListAttribute(element, "points", usedZoom);
                break;
            case "path":
                ScaleSvgPathDataAttribute(element, "d", usedZoom);
                break;
        }
    }

    /// <param name="cascadeWins">
    /// The cascaded value overwrites an existing presentation attribute rather than deferring to
    /// it. SVG 1.1 §6.4 ranks a presentation attribute as an author-origin rule of specificity 0
    /// inserted at the *start* of the author sheet, so any author rule outranks it — the deferral
    /// has the priority backwards, and `:lang(en) { fill: green }` lost to a `fill="none"`
    /// attribute (WPT conformance-checkers/html-svg/styling-css-05-b-isvalid).
    /// <para>
    /// Set only for <c>fill</c> and <c>stroke</c>. The font properties are inherited, so their
    /// cascaded value on an SVG element is whatever the enclosing document sets: overwriting
    /// there would clobber a <c>font-family="SVGFreeSansASCII"</c> attribute with the body font,
    /// which is a regression rather than a cascade.
    /// </para>
    /// </param>
    private void ApplySvgPresentationAttribute(
        DomElement element, Dictionary<string, string> props, string propertyName,
        bool preferInlineStyle = false, bool cascadeWins = false, Func<string, bool>? accept = null)
    {
        if (!cascadeWins && HasAttr(element, propertyName))
            return;

        string? value = null;
        if (preferInlineStyle && BakedInlineStyle(element).TryGetValue(propertyName, out var inlineValue) && !string.IsNullOrWhiteSpace(inlineValue))
            value = inlineValue;
        else if (props.TryGetValue(propertyName, out var propValue) && !string.IsNullOrWhiteSpace(propValue))
            value = propValue;
        else if (preferInlineStyle && props.TryGetValue(propertyName, out var fallbackProp) && !string.IsNullOrWhiteSpace(fallbackProp))
            value = fallbackProp;

        if (string.IsNullOrWhiteSpace(value))
            return;

        // A value the caller will not accept carries no author intent this attribute should take,
        // and writing it would overwrite one that does. See the `transform` call site for why that
        // is not hypothetical.
        if (accept is not null && !accept(value.Trim()))
            return;

        SetAttr(element, propertyName, value.Trim());
    }

    private void ScaleSvgLengthAttribute(DomElement element, string attributeName, double usedZoom)
    {
        if (!TryGetAttribute(element, attributeName, out var value) ||
            !TryScaleSvgLengthToken(element, value, usedZoom, out var scaled))
        {
            return;
        }

        SetAttr(element, attributeName, scaled);
    }

    private void ScaleSvgPointListAttribute(DomElement element, string attributeName, double usedZoom)
    {
        if (!TryGetAttribute(element, attributeName, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        SetAttr(element, attributeName, ScaleSvgPointRegex().Replace(value, match => ScaleSvgNumericMatch(match, usedZoom)));
    }

    private void ScaleSvgPathDataAttribute(DomElement element, string attributeName, double usedZoom)
    {
        if (!TryGetAttribute(element, attributeName, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        SetAttr(element, attributeName, ScaleSvgPathRegex().Replace(value, match => ScaleSvgNumericMatch(match, usedZoom)));
    }

    private bool TryScaleSvgLengthToken(DomElement element, string value, double usedZoom, out string scaled)
    {
        scaled = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EndsWith('%'))
            return false;

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var unitlessNumber))
        {
            scaled = (unitlessNumber * usedZoom).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        foreach (var unit in SvgZoomScaledUnits)
        {
            if (!trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
                continue;

            var numericPart = trimmed[..^unit.Length];
            if (!double.TryParse(numericPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            if (TryResolveSvgFontRelativeUnitPixels(element, unit, out var unitPixels))
            {
                scaled = (number * unitPixels * usedZoom)
                    .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            var factor = ResolveSvgLengthZoomFactor(element, unit, usedZoom);
            if (Math.Abs(factor - 1.0) < ZoomSerializationEpsilon)
                return false;

            scaled = $"{(number * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{unit}";
            return true;
        }

        return false;
    }

    private bool TryResolveSvgFontRelativeUnitPixels(DomElement element, string unit, out double pixels)
    {
        pixels = 0;
        if (SvgRootFontRelativeUnits.Contains(unit))
        {
            pixels = ResolveOriginalRootSpecifiedFontSizePx() * GetSvgFontRelativeUnitRatio(unit);
            return pixels > 0;
        }

        if (!SvgFontRelativeUnits.Contains(unit))
            return false;

        pixels = ResolveOriginalNearestSpecifiedFontSizePx(element) * GetSvgFontRelativeUnitRatio(unit);
        return pixels > 0;
    }

    private double ResolveOriginalNearestSpecifiedFontSizePx(DomElement element)
    {
        for (DomElement? current = element; current != null; current = ParentEl(current))
        {
            if (TryGetSpecifiedFontSizePx(current, out var fontSize))
                return fontSize;
        }

        return ResolveOriginalRootSpecifiedFontSizePx();
    }

    private double ResolveOriginalRootSpecifiedFontSizePx() =>
        TryGetSpecifiedFontSizePx(DocumentElement, out var fontSize) ? fontSize : 16;

    private bool TryGetSpecifiedFontSizePx(DomElement element, out double fontSize)
    {
        fontSize = 0;
        var specified = BuildSpecifiedStyleMap(element);
        if (TryParsePx(specified.GetValueOrDefault("font-size")) is double px)
        {
            fontSize = px;
            return true;
        }

        if (!specified.TryGetValue("font", out var fontShorthand) || string.IsNullOrWhiteSpace(fontShorthand))
            return false;

        var sizeMatch = FontShortHandRegex().Match(fontShorthand);
        if (!sizeMatch.Success ||
            !double.TryParse(sizeMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out fontSize))
        {
            return false;
        }

        return true;
    }

    private double ResolveSvgLengthZoomFactor(DomElement element, string unit, double usedZoom)
    {
        if (SvgAbsoluteOrViewportUnits.Contains(unit))
            return usedZoom;

        if (SvgRootFontRelativeUnits.Contains(unit))
            return usedZoom / GetRootFontSizeOwnerZoom();

        if (SvgFontRelativeUnits.Contains(unit))
            return usedZoom / GetNearestExplicitFontSizeOwnerZoom(element);

        return usedZoom;
    }

    private double GetNearestExplicitFontSizeOwnerZoom(DomElement element)
    {
        for (DomElement? current = element; current != null; current = ParentEl(current))
        {
            var props = GetComputedProps(current);
            if (props.TryGetValue("font-size", out var fontSize) && !string.IsNullOrWhiteSpace(fontSize))
                return GetUsedZoomForElement(current);
        }

        return 1.0;
    }

    private double GetRootFontSizeOwnerZoom()
    {
        var props = GetComputedProps(DocumentElement);
        if (props.TryGetValue("font-size", out var fontSize) && !string.IsNullOrWhiteSpace(fontSize))
            return GetUsedZoomForElement(DocumentElement);

        return 1.0;
    }
}
