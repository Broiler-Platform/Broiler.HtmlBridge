using Broiler.CSS;
using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.HtmlBridge.Dom.Runtime;
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

    // And the other way, for a pass on the projection that starts from page state: a popover's invoker.
    private IReadOnlyDictionary<DomElement, DomElement>? _renderProjectionTargets;

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
    /// <remarks>
    /// Copying the page's state onto the projection's elements is not a change to the page, so
    /// neither <see cref="RenderVersion"/> (<see cref="NoteRenderStateChange"/>) nor the epoch a
    /// retained geometry snapshot is keyed on (<see cref="BridgeRuntimeStateEpoch.EnterProjection"/>)
    /// counts it.
    /// </remarks>
    private RenderProjection CreateRenderProjection()
    {
        _renderProjectionDepth++;
        using var projecting = BridgeRuntimeStateEpoch.EnterProjection();
        try
        {
            return BuildRenderProjection();
        }
        finally
        {
            _renderProjectionDepth--;
        }
    }

    private RenderProjection BuildRenderProjection()
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
        var previousTargets = _renderProjectionTargets;
        _renderProjectionDocument = projectedDocument;
        _renderProjectionSources = projectedToSource;
        _renderProjectionTargets = sourceToProjected;
        _zoomSpecifiedStyleCache.Clear();
        try
        {
            // The top layer and anchor positioning, which the window gets from nowhere else; first, as
            // ResolveAnchorPositions ran before the serialization transforms. See AnchorResolver.cs.
            using (SuppressMutationDelivery())
                ResolveTopLayerAndAnchorsForRender(projectedRoot);

            if (ZoomBakeActive)
            {
                if (MayUseZoom(projectedRoot))
                    ApplyZoomSerializationStyles(projectedRoot, 1.0);
                else
                    ApplySvgSerializationAttributes(projectedRoot);
            }

            ApplySerializationTransforms(projectedRoot);
            ApplyViewTransitionRendering(projectedRoot);
            WithUserActionStates(() => ReflectRenderState(projectedRoot));
        }
        finally
        {
            _zoomSpecifiedStyleCache.Clear();
            // The projection's elements, and its own style scope, are never asked about again: its root
            // is not the page's. The page's styles still hold. They were all cleared here, so the hit
            // test after each frame a window draws resolved every element of the page again: a quarter
            // of a second a pointer move over html5test.com.
            _styleContext.ForgetComputedPropsOf(projectedDocument);
            _styleContext.DropEngineScopesOf(projectedDocument);
            _renderProjectionSources = previousSources;
            _renderProjectionTargets = previousTargets;
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

    // Set while a frame's live document is serialized for the renderer, so that it carries the user's
    // state of its elements (GetSerializableAttributes).
    private bool _stampUserActionInMarkup;

    /// <summary>
    /// The markup a frame renders, with what the user is doing to its elements stamped on them: a frame
    /// nobody scripted, but whose element the pointer is over, then differs from its resource, and is
    /// rendered from its live document.
    /// </summary>
    private string? RenderedFrameMarkup(DomDocument subDocumentRoot)
    {
        var previous = _stampUserActionInMarkup;
        _stampUserActionInMarkup = true;
        try
        {
            return RenderedSubDocumentMarkup(subDocumentRoot);
        }
        finally
        {
            _stampUserActionInMarkup = previous;
        }
    }

    /// <summary>Records how <paramref name="document"/> looked as parsed from
    /// <paramref name="html"/>, before any script ran against it, and the doctype that keeps its
    /// rendering mode once it is stamped.</summary>
    /// <remarks>
    /// <para>
    /// The mode is the one the renderer gives the resource itself. An unscripted frame is rendered
    /// straight from its file, and Broiler.HTML's <c>SetHtmlWithStyleSet</c> classifies that markup
    /// with <see cref="HtmlDocumentQueries.IsQuirksMode"/>. Before it moved there, Layout's copy did,
    /// with the same answers. A stamp is re-read the same way, and serialization
    /// emits the document element alone, so standards mode is carried as a bare
    /// <c>&lt;!DOCTYPE html&gt;</c> and quirks mode as no doctype, as the page's own serialization
    /// carries it (<see cref="SelectsStandardsMode"/>).
    /// </para>
    /// <para>
    /// This used to ask <c>HtmlDocumentQueries.HasHtmlDoctype</c>, which tests the doctype's name
    /// alone. <c>&lt;!DOCTYPE HTML PUBLIC "-//W3C//DTD HTML 4.0 Transitional//EN"&gt;</c> is named
    /// <c>html</c> and selects quirks mode, so such a frame rendered in quirks mode until a script
    /// touched it, and was then stamped into standards mode. A limited-quirks doctype (XHTML 1.0
    /// Transitional, HTML 4.01 Transitional with a system identifier) still stamps standards mode,
    /// because the renderer models full quirks mode only, and it is what the unscripted frame gets.
    /// </para>
    /// </remarks>
    private void RecordSubDocumentSourceMarkup(DomDocument document, string html)
    {
        if (SerializeSubDocumentChildren(document) is { } markup)
            _subDocumentSourceMarkup[document] = markup;
        _subDocumentDoctype[document] = HtmlDocumentQueries.IsQuirksMode(html) ? string.Empty : "<!DOCTYPE html>";
    }

    /// <summary>
    /// Stamps the live document of every <c>src</c>-loaded nested browsing context in the projection
    /// that has diverged from its resource, so the renderer paints what the frame became instead of
    /// what its file says. An <c>&lt;object&gt;</c> that renders its data is projected first
    /// (<see cref="ProjectObjectContent"/>), which drops its fallback before anything in it is stamped.
    /// </summary>
    private void ProjectScriptedFrameDocuments(DomElement element)
    {
        var objectContent = IsObjectElement(element)
            ? ProjectObjectContent(element, ResolveRenderSource(element))
            : ObjectContentKind.Fallback;
        if (objectContent == ObjectContentKind.Image)
            return;

        foreach (var child in ChildElements(element).ToList())
            ProjectScriptedFrameDocuments(child);

        if (!IsNestedBrowsingContextContainer(element.TagName?.ToLowerInvariant()))
            return;

        // A srcdoc frame already carries its live document in the attribute it was authored with --
        // unless its location took it elsewhere, which its srcdoc no longer says.
        var source = ResolveRenderSource(element);
        var navigated = _frameNavigations.ContainsKey(source);
        if (HasAttr(element, "srcdoc") && !navigated)
            return;

        if (GetContentDocument(source) is not { } subDocumentRoot ||
            RenderedFrameMarkup(subDocumentRoot) is not { Length: > 0 } markup)
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
        //
        // A frame its location navigated is the exception: the renderer would re-read its src, which
        // is the document it left, so its document is stamped whatever it holds. So is an object's
        // document that the renderer would not find from its markup (RendererReadsObjectDocument).
        var stampAnyway = navigated ||
            (objectContent == ObjectContentKind.Document && !RendererReadsObjectDocument(element, source));
        if (!stampAnyway &&
            (!_subDocumentSourceMarkup.TryGetValue(subDocumentRoot, out var sourceMarkup) ||
             string.Equals(sourceMarkup, markup, StringComparison.Ordinal)))
        {
            return;
        }

        SetAttr(element, FrameDocumentAttr,
            _subDocumentDoctype.GetValueOrDefault(subDocumentRoot, string.Empty) + markup);

        if (GetSubDocumentBaseUrl(source) is { Length: > 0 } baseUrl)
            SetAttr(element, FrameDocumentBaseAttr, baseUrl);
    }

    /// <summary>
    /// Projects what an <c>&lt;object&gt;</c> renders (HTML §4.8.7) and answers it: once its data
    /// has loaded as an image or a document, it renders that and not its fallback content, so its
    /// children are dropped from the projection, and the type the data came as is stamped
    /// (<see cref="DomBridgeUtils.ObjectTypeAttr"/>) for the renderer, which would otherwise decide
    /// from the markup alone. An object whose data failed, or is not loaded yet, is left as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Acid3's test 16 nests <c>support-a.png</c> (a 404), <c>support-b.png</c> (a page served as
    /// <c>text/html</c>, whose body is transparent) and <c>support-c.png</c> (an <c>image/png</c>)
    /// round the text "FAIL". The first shows its fallback, the second, and the second shows its
    /// page, which draws nothing, so neither the third nor the text renders. The renderer, which saw
    /// an object as an image only for a <c>data:image</c> URL and as a document only for an HTML
    /// <c>type</c> or extension, drew all three and the text above Acid3's heading.
    /// </para>
    /// <para>
    /// The <c>type</c> attribute is left alone: author selectors match it, Acid2's
    /// <c>#eyes-a object[type]</c> among them. The live object keeps its children, which scripts see.
    /// </para>
    /// </remarks>
    private ObjectContentKind ProjectObjectContent(DomElement element, DomElement source)
    {
        if (_browsingContexts.HasObjectLoadFailed(source) ||
            !_browsingContexts.TryGetObjectContent(source, out var content) ||
            content.Kind == ObjectContentKind.Fallback)
        {
            return ObjectContentKind.Fallback;
        }

        foreach (var child in element.ChildNodes.ToArray())
            element.RemoveChild(child);

        SetAttr(element, ObjectTypeAttr, content.Type);
        return content.Kind;
    }

    /// <summary>
    /// Whether Broiler.Layout's <c>FragmentTreeBuilder.TryLoadEmbeddedDocument</c> finds an
    /// object's document from its markup alone: an HTML <c>type</c>, or with none an HTML file
    /// extension, on a file it can read. It reads no network, and a <c>data:</c> URL it reads only
    /// as HTML.
    /// </summary>
    private bool RendererReadsObjectDocument(DomElement element, DomElement source)
    {
        if (!_browsingContexts.TryGetLocation(source, out var location) ||
            !location.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (element.GetAttribute("type") is { } type && !string.IsNullOrWhiteSpace(type))
        {
            var trimmed = type.Trim();
            return trimmed.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase);
        }

        var path = location.Split('?', '#')[0];
        return path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".xht", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The zoom half of serialization: bakes each element's used <c>zoom</c> into scaled lengths, and
/// emits scaled <c>::before</c>/<c>::after</c> overrides as bridge-owned author rules.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether anything in <paramref name="root"/>'s tree can give an element a used <c>zoom</c> other
    /// than 1: a <c>zoom</c> declaration, in a style sheet -- what it imports included -- or an inline
    /// style, whose value is not one that leaves the zoom as it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The zoom passes resolve every element's computed style to find its <c>zoom</c>, and a render
    /// projection is a document of its own, so that was two whole-document cascades for each
    /// projection. A window builds one for each change it shows and another for each layout a script
    /// or a hit test asks for: on html5test.com, whose sheet declares only <c>zoom: 1</c>, the old
    /// layout hack, they were a third of every pointer move. The user agent's own sheet sets no zoom.
    /// </para>
    /// <para>
    /// What a sheet imports is read through the document's stylesheet responses
    /// (<see cref="FetchExternalStylesheet"/>), which ask the network once. Any import used to count, as
    /// what it brought was not read here, and reCAPTCHA's demo page imports a font sheet: each of its
    /// projections, many a second, was baked.
    /// </para>
    /// </remarks>
    private bool MayUseZoom(DomElement root)
    {
        foreach (var element in root.Descendants().OfType<DomElement>().Prepend(root))
        {
            if (IsStyleSheetOwner(element) && SheetMayScale(element))
                return true;

            if (InlineStyleForRead(element).TryGetValue("zoom", out var inlineZoom) && MayScale(inlineZoom))
                return true;

            if (element.GetAttribute("style") is { } styleAttribute &&
                styleAttribute.Contains("zoom", StringComparison.OrdinalIgnoreCase) &&
                ParseStyle(styleAttribute).TryGetValue("zoom", out var attributeZoom) &&
                MayScale(attributeZoom))
            {
                return true;
            }
        }

        return false;

        // The sheet's own rules, or, when it imports, its text with each import's in place
        // (ExpandCssImports, as the projection inlines a <style>'s imports): the requests of the live
        // document the projected element stands for, against the sheet's own base URL.
        bool SheetMayScale(DomElement owner)
        {
            if (GetStyleElementCssText(owner) is not { Length: > 0 } css)
                return false;

            if (!HasLeadingImport(css))
                return css.Contains("zoom", StringComparison.OrdinalIgnoreCase) && DeclaresZoom(EnsureStyleSheetRulesCurrent(owner));

            var source = ResolveRenderSource(owner);
            var expanded = ExpandCssImports(
                css, GetStyleElementBaseUrl(owner), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0,
                _importsBeforePolicyMeta?.GetValueOrDefault(source), source);
            return expanded.Contains("zoom", StringComparison.OrdinalIgnoreCase) &&
                   DeclaresZoom(new CssParser().ParseStyleSheet(expanded).Rules);
        }

        static bool DeclaresZoom(IReadOnlyList<CssRule> rules)
        {
            foreach (var rule in rules)
            {
                var declarations = rule switch
                {
                    CssStyleRule styleRule => styleRule.Declarations,
                    CssAtRule atRule => atRule.Declarations,
                    _ => null,
                };

                if (declarations is not null)
                {
                    foreach (var declaration in declarations.Declarations)
                    {
                        if (declaration.Name.Equals("zoom", StringComparison.OrdinalIgnoreCase) &&
                            MayScale(declaration.Value.Text))
                        {
                            return true;
                        }
                    }
                }

                if (rule is CssAtRule { Rules.Count: > 0 } group && DeclaresZoom(group.Rules))
                    return true;
            }

            return false;
        }

        // A used zoom is the product of the specified ones (CssZoom.ResolveUsed), so only a value other
        // than these can change one. Anything else counts, a substitution among them.
        static bool MayScale(string value)
        {
            var specified = value.Trim();
            return !(specified.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
                     specified.Equals("100%", StringComparison.Ordinal) ||
                     specified.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                     specified.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
                     specified.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
                     specified.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
                     specified.Equals("revert-layer", StringComparison.OrdinalIgnoreCase) ||
                     double.TryParse(specified, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var factor) && factor == 1);
        }
    }

    /// <summary>
    /// The part of <see cref="ApplyZoomSerializationStyles"/> that is not about zoom, for a tree
    /// without any: each SVG element's presentation attributes from its computed style.
    /// </summary>
    private void ApplySvgSerializationAttributes(DomElement element)
    {
        if (IsText(element))
            return;

        if (ShouldApplySvgSerializationAttributes(element))
            ApplyZoomSerializationSvgAttributes(element, 1.0);

        foreach (var child in ChildElements(element))
            ApplySvgSerializationAttributes(child);
    }

    private void ApplyZoomPseudoSerializationOverrides(DomElement root)
    {
        // Every element's used zoom is 1, and a pseudo-element is overridden only where it is not.
        if (!MayUseZoom(root))
            return;

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

    /// <summary>
    /// An SVG length attribute scaled by the element's used zoom, or no answer at all when there
    /// is no scaled number this component can represent.
    /// </summary>
    /// <remarks>
    /// Every exit that produces a number formats it through <see cref="TryFormatScaledSvgLength"/>,
    /// which is where the refusal lives, because the scale is a multiplication: the zoom is finite
    /// (<c>ResolveUsedZoom</c> refuses one that is not) and the attribute may be too, and their
    /// product still need not be — at <c>zoom: 2</c>, <c>width="1e308"</c> used to serialize as
    /// <c>width="Infinity"</c>. Declining leaves the attribute exactly as the page wrote it, which
    /// is what this method already answers for a percentage, an unreadable token, or a unit whose
    /// zoom factor is 1.
    /// </remarks>
    private bool TryScaleSvgLengthToken(DomElement element, string value, double usedZoom, out string scaled)
    {
        scaled = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EndsWith('%'))
            return false;

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var unitlessNumber))
        {
            return TryFormatScaledSvgLength(unitlessNumber * usedZoom, string.Empty, out scaled);
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
                return TryFormatScaledSvgLength(number * unitPixels * usedZoom, string.Empty, out scaled);
            }

            var factor = ResolveSvgLengthZoomFactor(element, unit, usedZoom);
            if (Math.Abs(factor - 1.0) < ZoomSerializationEpsilon)
                return false;

            return TryFormatScaledSvgLength(number * factor, unit, out scaled);
        }

        return false;
    }

    /// <summary>
    /// The scaled length written back into the attribute, or no answer when it is not a number
    /// this component can represent. The single place the refusal above is read.
    /// </summary>
    private static bool TryFormatScaledSvgLength(double value, string unit, out string scaled)
    {
        if (!double.IsFinite(value))
        {
            scaled = string.Empty;
            return false;
        }

        scaled = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + unit;
        return true;
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
