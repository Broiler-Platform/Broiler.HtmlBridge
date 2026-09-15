using Broiler.Dom.Html;
using Broiler.Dom;
using Broiler.CSS;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// DOM → HTML serialisation — converts the in-memory DOM tree back to
/// an HTML string after JavaScript execution.
/// Uses shared serialization helpers from Broiler.HTML.Dom.
/// </summary>
public sealed partial class DomBridge
{
    // ------------------------------------------------------------------
    //  DOM → HTML serialisation
    // ------------------------------------------------------------------

    private readonly Dictionary<DomElement, Dictionary<string, string>> _zoomSpecifiedStyleCache = [];

    /// <summary>
    /// Serialises the current DOM tree back to an HTML string.
    /// Call this after JavaScript execution to obtain the modified page
    /// content for re-rendering.
    /// </summary>
    public string SerializeToHtml()
    {
        var projection = CreateRenderProjection();
        var root = projection.Document.DocumentElement;
        if (root is null)
            return EmptyDocumentHtml;

        return HtmlSerializer.Serialize(
            root,
            CreateSerializationAdapter(projection.SourceFor),
            new HtmlSerializationOptions(
                IncludeHtmlDoctype: SelectsStandardsMode(),
                MaximumDepth: MaxSerializationDepth,
                EncodeTextNodes: false,
                NewLineAfterDoctype: true));
    }

    /// <summary>
    /// Whether the document's doctype selects standards mode, which is what the emitted
    /// <c>&lt;!DOCTYPE html&gt;</c> above carries to whoever re-parses this string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be an unconditional <c>true</c>, and that silently destroyed quirks mode for
    /// every consumer of the serialised page. The renderer re-derives the mode from the string it
    /// is handed (<c>DocumentModeContext.IsQuirksHtml</c>, via
    /// <c>HtmlContainerInt.SetHtmlWithStyleSet</c>), so a doctype-less document came back out with
    /// a doctype and rendered as standards — no quirk could ever fire on the WPT path, whose whole
    /// `quirks/` directory is doctype-less by construction.
    /// </para>
    /// <para>
    /// The mode is what round-trips, not the doctype's text: <c>IsQuirksHtml</c> keys off the
    /// doctype's *name* alone, so a public/system identifier (the XHTML doctypes the CSS2.1
    /// <c>.xht</c> tests carry) selects standards through the bare form just as it did through the
    /// original — those identifiers were already dropped here and still are. A doctype whose name
    /// is not <c>html</c> selects quirks, so it is correctly serialised as no doctype at all.
    /// </para>
    /// </remarks>
    private bool SelectsStandardsMode() =>
        _document.DocumentType is { } doctype
        && doctype.Name.Equals("html", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The document's current element child — what the canvas actually renders — or
    /// <c>null</c> once the document has none.
    /// <para>
    /// <see cref="DocumentElement"/> is captured when the bridge is constructed and never
    /// tracks tree mutation, so after <c>document.documentElement.remove()</c> it still
    /// points at the now-detached <c>&lt;html&gt;</c>. Serializing that subtree kept
    /// rendering the removed document's background and text, where the spec leaves a
    /// document with no element child and therefore nothing to paint (WPT
    /// <c>html/rendering/…/Document-documentElement-remove-clears-content</c>: a red
    /// <c>&lt;html&gt;</c> that removes itself on load must end up blank). Reading the live
    /// tree here also picks up a <em>replaced</em> document element, which the captured
    /// field would likewise have missed.
    /// </para>
    /// </summary>
    private DomElement? RenderedDocumentElement => GetDocumentElement(_document);

    /// <summary>
    /// Returns an isolated document prepared for direct renderer consumption.
    /// Bridge-owned style and form-control state is reflected into the projection;
    /// the script-visible canonical <see cref="Document"/> is not changed.
    /// </summary>
    public DomDocument GetRenderDocument() => CreateRenderProjection().Document;


    /// <summary>
    /// Serializes the element's authoritative inline-style dict back into its canonical
    /// <c>style=</c> attribute in CSSOM serialization form (shorthand-first, <c>"; "</c>-joined),
    /// removing the attribute when the dict is empty. This is the single inline-style write-through
    /// (Phase 4 item 2): it runs at serialization (<see cref="ReflectRenderState"/>) and after every
    /// script <c>element.style</c> mutation, so a JS style mutation and <c>getAttribute("style")</c>
    /// observe the same state. Uses the node-model <see cref="DomBridgeUtils.SetAttr"/>/<see cref="DomBridgeUtils.RemoveAttr"/> (not
    /// the JS <c>setAttribute</c> binding), so there is no reparse loop back into the dict.
    /// </summary>
    private void SyncStyleAttributeFromInlineStyle(DomElement element)
    {
        // Merge the baked overlay in: at serialize time (ReflectRenderState) the style= attribute must
        // reflect the resolved bakes, exactly as it did when bakes lived in the inline-style dict. At
        // script time the overlay is empty, so this equals the base dict (byte-identical write-through).
        var style = EffectiveInlineStyle(element);
        if (style.Count == 0)
        {
            RemoveAttr(element, "style");
            return;
        }

        var styleText = string.Join(
            "; ",
            style
                .OrderBy(kv => HtmlSerializer.IsShorthandProperty(kv.Key) ? 0 : 1)
                .Select(static kv => $"{kv.Key}: {kv.Value}"));
        if (!TryGetAttribute(element, "style", out var currentStyle) ||
            !string.Equals(currentStyle, styleText, StringComparison.Ordinal))
        {
            SetAttr(element, "style", styleText);
        }
    }

    private void ReflectRenderState(DomElement element)
    {
        if (!IsText(element) && !element.TagName.StartsWith('#'))
        {
            SyncStyleAttributeFromInlineStyle(element);

            ReflectFormControlValue(element);
        }

        foreach (var child in ChildElements(element))
            ReflectRenderState(child);
    }

    /// <summary>
    /// Writes a value a script set into the markup, each control into the place HTML keeps its
    /// value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The IDL <c>value</c> and the content attribute are different things — the attribute is the
    /// default, the property is the current value — and a browser has no reason to reconcile them.
    /// Here there is one: serializing is the only way the current value leaves the bridge, and a
    /// form submission is built by re-parsing what comes out. A value that does not reach the markup
    /// is a value the server never sees.
    /// </para>
    /// <para>
    /// So each control is reflected into its own place, and they are three different places: an
    /// <c>input</c>'s <c>value</c> attribute, a <c>textarea</c>'s child text (HTML §4.10.11 — it has
    /// no <c>value</c> attribute for a write to land in), and, for a <c>select</c>, the
    /// <c>selected</c> attribute moving to the option it chose. <c>TryGet</c> answers "did a script
    /// set this", so a control the page never touched is left exactly as it was authored.
    /// </para>
    /// <para>
    /// This runs over the render projection rather than the live tree, so rewriting a textarea's
    /// children here does not disturb the document the page is still scripting.
    /// </para>
    /// </remarks>
    private void ReflectFormControlValue(DomElement element)
    {
        var state = FormControlStateFor(element);

        if (element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase))
        {
            if (state.Value.TryGet(out var inputValue) && inputValue is string inputString)
                SetAttr(element, "value", inputString);
            return;
        }

        if (element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase))
        {
            if (state.Value.TryGet(out var areaValue) && areaValue is string areaString &&
                !string.Equals(GetElementTextContent(element), areaString, StringComparison.Ordinal))
            {
                SetElementTextContent(element, areaString);
            }

            return;
        }

        if (element.TagName.Equals("select", StringComparison.OrdinalIgnoreCase) &&
            state.SelectedIndex.TryGet(out var indexValue) && indexValue is int selectedIndex)
        {
            // The same walk the select binding selects through, so "which option is the third one"
            // has one answer rather than two that can disagree about nested optgroups.
            var options = Dom.Features.SelectBinding.CollectSelectOptions(element);
            for (var index = 0; index < options.Count; index++)
            {
                if (index == selectedIndex)
                    SetAttr(options[index], "selected", string.Empty);
                else if (HasAttr(options[index], "selected"))
                    RemoveAttr(options[index], "selected");
            }
        }
    }

    private string SerializeElementToHtml(DomElement element) => SerializeNodeToHtml(element);

    /// <summary>Serializes one node of any kind — the adapter already covers text, comments,
    /// doctypes and fragments.</summary>
    private string SerializeNodeToHtml(DomNode node) =>
        HtmlSerializer.Serialize(node, CreateSerializationAdapter(),
            new HtmlSerializationOptions(MaximumDepth: MaxSerializationDepth, EncodeTextNodes: false));

    /// <summary><c>innerHTML</c>'s read side: every child, not only the element ones. The
    /// <c>OfType&lt;DomElement&gt;()</c> this filtered with is a leftover from the facade era, when a
    /// text child was a string on its parent's element record rather than a node.</summary>
    private string SerializeChildrenToHtml(DomElement element) =>
        string.Concat(SerializationChildrenOf(element).Select(SerializeNodeToHtml));

    private void ApplySerializationTransforms(DomElement root)
    {
        RemoveRenderCommentNodes(root);
        ApplyCssomStyleSheetMutations(root);
        InlineStyleSheetImports(root);
        ApplyAdoptedStyleSheets(root);
        // A shadow root's <style> is serialized inline as a global rule, so its ordinary
        // selectors must be restricted to that root's own tree — otherwise a shadow tree's
        // `div { background: red }` repaints every div in the page. Runs BEFORE the :host
        // pass, which rewrites the keyword this one keys off.
        ScopeShadowTreeSelectors(root);
        // A shadow root's <style> is serialized inline as a global rule, so its :host
        // selectors must be rewritten to target that root's own host — otherwise the
        // renderer's lenient :host matching paints every element.
        ScopeShadowHostSelectors(root);
        // The renderer does not model ::part, so an outer rule addressing a shadow part reached
        // nothing. Runs while shadow-root ancestry is still intact, so only real parts are stamped.
        RewriteShadowPartSelectors(root);
        // A host's light-DOM children render only where a <slot> assigns them; with no slot in
        // the shadow tree they generate no boxes.
        HideUnslottedShadowHostChildren(root);
        // A frame loaded from `src` has no attribute its live document round-trips through, so a
        // scripted one is stamped onto its container here. See DomBridge.FrameDocumentProjection.cs.
        ProjectScriptedFrameDocuments(root);
        ApplyBaseHrefToStyleUrls(root);
        ApplyMetaColorScheme(root);
        // Zoom baking is applied by the callers (GetRenderDocument/SerializeToHtml) before this,
        // so it can be reverted on the geometry-snapshot path; pseudo/progress below depend on
        // the baked sizes and must run after it.
        ApplyZoomPseudoSerializationOverrides(root);
        // A Web Animation targeting a pseudo-element has no node to bake onto, so it is emitted as
        // an author rule instead.
        ApplyAnimatedPseudoSerializationOverrides(root);
        ApplyProgressLikeSerializationPlaceholders(root);
        // Flatten the #shadow-root wrapper LAST: it is not an HTML element, so the renderer would
        // paint its serialized "<#shadow-root>" open tag as literal text. It must run after every
        // pass that reads shadow-tree ancestry — ApplyMetaColorScheme in particular skips a
        // <meta name=color-scheme> inside a shadow tree, and unwrapping first moved that meta into
        // the document tree and wrongly darkened the canvas.
        UnwrapShadowRootsForRender(root);
    }

    /// <summary>
    /// Reflects an <c>&lt;meta name="color-scheme"&gt;</c> onto the root element's used
    /// <c>color-scheme</c> so the canvas backdrop honours it (WPT
    /// <c>html/semantics/…/meta-color-scheme-*</c>, <c>css/mediaqueries/prefers-color-scheme-*-with-meta-*</c>).
    /// <para>
    /// HTML §4.2.5.3 makes the meta a page-level default for the root's color scheme, which the
    /// renderer already turns into the dark UA canvas (<c>rgb(18,18,18)</c>) when it reads a
    /// <c>color-scheme</c> of <c>dark</c> off the <c>&lt;html&gt;</c> box. Nothing translated the
    /// meta into that property, so <c>&lt;meta name=color-scheme content=dark&gt;</c> painted the
    /// default light canvas while an equivalent <c>:root { color-scheme: dark }</c> painted dark.
    /// </para>
    /// <para>
    /// Author CSS wins: the meta is applied only when the root declares no <c>color-scheme</c> of
    /// its own, so <c>:root { color-scheme: light }</c> beside <c>&lt;meta … content=dark&gt;</c>
    /// stays light. The first meta in tree order with a valid <c>content</c> value supplies the
    /// value (see <see cref="DomBridgeUtils.FindMetaColorScheme"/>); the renderer's own token parsing then selects
    /// dark or light (an unrecognised but syntactically valid value contributes neither and falls
    /// back to light, matching the invalid-value cases in the spec's test suite). Written to the
    /// baked overlay, so it reaches
    /// the renderer and the serialized <c>style=</c> without polluting the script-observable inline
    /// style.
    /// </para>
    /// </summary>
    private void ApplyMetaColorScheme(DomElement root)
    {
        var metaValue = FindMetaColorScheme(root);
        if (metaValue is null)
            return;

        // Author color-scheme on the root takes precedence over the meta default. Read the
        // cascaded declared value (not the used value), so an explicit author declaration of any
        // kind suppresses the meta while an absent one lets it through.
        if (BuildSpecifiedStyleMap(root).TryGetValue("color-scheme", out var declared) &&
            !string.IsNullOrWhiteSpace(declared))
        {
            return;
        }

        BakedInlineStyle(root)["color-scheme"] = metaValue;
    }

    /// <summary>
    /// Bakes CSSOM rule-model mutations into the render-bound document. Script
    /// <c>insertRule</c>/<c>deleteRule</c> mutates the shared rule list held in the
    /// style element's runtime state — never its text node — so the serialized HTML
    /// handed to the renderer still carried the original author text and the mutation
    /// was invisible to layout and paint, while <c>getComputedStyle</c> (which reads
    /// the model through <see cref="GetStyleElementCssText"/>) already observed it.
    /// A script that styles the page purely through the CSSOM therefore rendered as
    /// if it had never run — the WPT <c>css/cssom</c> insertRule family. Replacing the
    /// text node with the serialized model closes that gap, so the renderer and the
    /// CSSOM agree on one stylesheet.
    /// <para>
    /// Runs inside <see cref="ApplySerializationTransforms"/> on the isolated render projection,
    /// so the rewrite delivers no live-document observer records. It is idempotent: the text it
    /// writes reparses to the same rules, and the reparse
    /// clears <see cref="Dom.Runtime.StyleSheetRuntimeState.RulesMutated"/>, so a second pass is a
    /// no-op. JS-visible <c>innerHTML</c>/<c>outerHTML</c> serialize without the
    /// transforms and still expose the author text.
    /// </para>
    /// <para>
    /// Only <c>&lt;style&gt;</c> elements are baked. A mutated <c>&lt;link rel=stylesheet&gt;</c>
    /// has no text node to carry the model — inlining it into a <c>&lt;style&gt;</c> would
    /// silently re-base its relative <c>url()</c>s onto the document — so those
    /// mutations stay renderer-invisible until linked sheets are modelled properly.
    /// </para>
    /// </summary>
    private void ApplyCssomStyleSheetMutations(DomElement element)
    {
        if (!IsText(element) &&
            element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase) &&
            StyleSheetStateFor(element).RulesMutated)
        {
            // Read the effective text before rewriting: GetStyleElementCssText compares
            // the element's current source text against the model's parse source, and
            // would discard the mutations if the text node had already been replaced.
            SetElementTextContent(element, GetStyleElementCssText(element));
        }

        foreach (var child in ChildElements(element))
            ApplyCssomStyleSheetMutations(child);
    }

    private void ApplyZoomPseudoSerializationOverrides(DomElement root)
    {
        var rules = new List<string>();
        int pseudoIndex = 0;
        CollectZoomPseudoSerializationOverrides(root, 1.0, rules, ref pseudoIndex);
        InjectBridgeStyleRules(root, rules);
    }

    /// <summary>
    /// Appends a bridge-owned <c>&lt;style&gt;</c> carrying <paramref name="rules"/> to the render
    /// document — in <c>&lt;head&gt;</c> when there is one, else first in the root. A no-op for an
    /// empty rule list, so a document that needs no overrides serializes byte-identically.
    /// </summary>
    private void InjectBridgeStyleRules(DomElement root, List<string> rules)
    {
        if (rules.Count == 0)
            return;

        var styleElement = CreateBridgeElement("style");
        SetElementTextContent(styleElement, string.Join(Environment.NewLine, rules));

        var head = FindFirstElementByTagName(root, "head");
        if (head != null)
        {
            SetParent(styleElement, head);
            head.AppendChild(styleElement);
            return;
        }

        SetParent(styleElement, root);
        InsertChildAt(root, 0, styleElement);
    }

    /// <summary>
    /// Emits the values baked by <c>element.animate(…, { pseudoElement })</c> as author rules, so a
    /// Web Animation on a pseudo-element reaches the renderer through the ordinary cascade.
    /// <para>
    /// A pseudo-element has no node, so the element-inline bake <c>animate()</c> normally performs
    /// has nowhere to land, and the animation was silently dropped: WPT
    /// <c>css/css-pseudo/backdrop-animate-002</c> (issue #1538 problem 11) animates
    /// <c>::backdrop</c> to a 10%-opacity green and got the UA modal scrim instead. Its own
    /// reference writes the same declarations as CSS and already rendered correctly, which is what
    /// says the gap is the API and not the pseudo-element.
    /// </para>
    /// <para>
    /// <c>!important</c> matches the zoom overrides above and the cascade position an animation
    /// actually has: an animation's effect outranks author declarations, and this is the only lever
    /// a serialized rule has to say so.
    /// </para>
    /// </summary>
    private void ApplyAnimatedPseudoSerializationOverrides(DomElement root)
    {
        // Nothing to emit unless animate() has actually targeted a pseudo-element, and the walk below
        // is over the whole document — so gate it on the flag rather than paying for a tree walk on
        // every serialization of every page. `RunTestWithTimeout_GridTemplateColumnsCrash…` is a
        // 6-second budget on a pathological document and caught the unguarded version.
        if (!_hasAnimatedPseudoStyles)
            return;

        var rules = new List<string>();
        int pseudoIndex = 0;
        CollectAnimatedPseudoSerializationOverrides(root, rules, ref pseudoIndex);
        InjectBridgeStyleRules(root, rules);
    }

    private void CollectAnimatedPseudoSerializationOverrides(DomElement element, List<string> rules, ref int pseudoIndex)
    {
        if (!IsText(element) && !element.TagName.StartsWith('#'))
        {
            foreach (var pseudoElement in AnimatedPseudoElementsOf(element))
            {
                if (AnimatedPseudoStyle(element, pseudoElement) is not { Count: > 0 } properties)
                    continue;

                if (string.IsNullOrWhiteSpace(element.Id))
                    element.Id = $"broiler-animated-pseudo-{++pseudoIndex}";

                var declarations = properties
                    .Select(property => $"{property.Key}: {property.Value} !important");
                rules.Add($"#{element.Id}{pseudoElement} {{ {string.Join("; ", declarations)}; }}");
            }
        }

        foreach (var child in ChildElements(element))
            CollectAnimatedPseudoSerializationOverrides(child, rules, ref pseudoIndex);
    }

    private void CollectZoomPseudoSerializationOverrides(DomElement element, double parentZoom, List<string> rules, ref int pseudoIndex)
    {
        if (IsText(element))
            return;

        var props = GetComputedProps(element);
        var specifiedZoom = props.GetValueOrDefault("zoom");
        var usedZoom = CssZoom.ResolveUsed(specifiedZoom, parentZoom);

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

    private void ApplyProgressLikeSerializationPlaceholders(DomElement element)
    {
        if (IsText(element))
            return;

        foreach (var child in ChildElements(element).ToList())
            ApplyProgressLikeSerializationPlaceholders(child);

        var tag = element.TagName.ToLowerInvariant();
        if (tag is not ("progress" or "meter"))
            return;

        var props = GetComputedProps(element);
        var width = props.GetValueOrDefault("width");
        var height = props.GetValueOrDefault("height");
        var writingMode = props.GetValueOrDefault("writing-mode") ?? "horizontal-tb";
        var direction = props.GetValueOrDefault("direction") ?? "ltr";
        var vertical = CssWritingMode.IsVertical(writingMode);
        var reverseInline = string.Equals(direction, "rtl", StringComparison.OrdinalIgnoreCase);
        var ratio = HtmlElementQueries.ResolveProgressLikeValueRatio(element, tag);

        BakedInlineStyle(element)["display"] = "inline-block";
        BakedInlineStyle(element)["box-sizing"] = "border-box";
        BakedInlineStyle(element)["position"] = "relative";
        BakedInlineStyle(element)["overflow"] = "hidden";
        BakedInlineStyle(element)["padding"] = "0";
        BakedInlineStyle(element)["border"] = "1px solid #767676";
        BakedInlineStyle(element)["background-color"] = tag == "meter" ? "#e6e6e6" : "#f0f0f0";
        BakedInlineStyle(element)["vertical-align"] = "middle";
        if (!string.IsNullOrWhiteSpace(width) && !string.Equals(width, "auto", StringComparison.OrdinalIgnoreCase))
            BakedInlineStyle(element)["width"] = width;
        if (!string.IsNullOrWhiteSpace(height) && !string.Equals(height, "auto", StringComparison.OrdinalIgnoreCase))
            BakedInlineStyle(element)["height"] = height;

        ClearChildren(element);

        var fill = CreateBridgeElement("div");
        SetParent(fill, element);
        BakedInlineStyle(fill)["position"] = "absolute";
        BakedInlineStyle(fill)["background-color"] = tag == "meter" ? "#4caf50" : "#0a84ff";

        var fillExtent = vertical
            ? ReadPixelLength(height, DefaultProgressLikeTrackLengthPx) * ratio
            : ReadPixelLength(width, DefaultProgressLikeTrackLengthPx) * ratio;
        var fillExtentPx = $"{fillExtent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}px";
        if (vertical)
        {
            BakedInlineStyle(fill)["left"] = "0";
            BakedInlineStyle(fill)["right"] = "0";
            BakedInlineStyle(fill)[reverseInline ? "bottom" : "top"] = "0";
            BakedInlineStyle(fill)["height"] = fillExtentPx;
        }
        else
        {
            BakedInlineStyle(fill)["top"] = "0";
            BakedInlineStyle(fill)["bottom"] = "0";
            BakedInlineStyle(fill)[reverseInline ? "right" : "left"] = "0";
            BakedInlineStyle(fill)["width"] = fillExtentPx;
        }

        element.AppendChild(fill);
    }

    private void ApplyZoomSerializationStyles(DomElement element, double parentZoom)
    {
        if (IsText(element))
            return;

        var props = GetComputedProps(element);
        var specifiedZoom = props.GetValueOrDefault("zoom");
        var usedZoom = CssZoom.ResolveUsed(specifiedZoom, parentZoom);

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


    // RF-BRIDGE-1c Phase F (F3c part 2c): the serialization adapter is over canonical DomNode so
    // text/comment children serialize; construction mints them as DomText/DomComment. GetKind keys
    // text/comment off NodeType (IsText/IsComment, canonical char-data) and the doctype/fragment
    // kinds off the canonical node types; everything else is an element. GetName/GetAttributes/
    // GetStyles are only invoked for element/doctype nodes (see HtmlSerializer.Append), so their
    // Broiler.Dom.DomElement narrowing is always satisfied.
    private HtmlSerializationAdapter<DomNode> CreateSerializationAdapter(
        Func<DomElement, DomElement?>? sourceResolver = null) => new(
        GetKind: static node =>
            IsText(node) ? HtmlSerializationNodeKind.Text
            : IsComment(node) ? HtmlSerializationNodeKind.Comment
            : node is DomDocumentType ? HtmlSerializationNodeKind.DocumentType
            : node is DomDocumentFragment ? HtmlSerializationNodeKind.Fragment
            : HtmlSerializationNodeKind.Element,
        GetName: static node => node is DomDocumentType docType ? docType.Name
            : node is DomElement element ? element.TagName : string.Empty,
        // A materialised nested-browsing-context document is no longer an in-tree child (P4.4b
        // severed the #subdoc-root element); it is referenced off its <iframe>/<object>/<frame>
        // container and rasterised in isolation (srcdoc content round-trips via the srcdoc
        // attribute), so it can never appear in ChildNodes and needs no serialization skip.
        // Not node.ChildNodes: a <template> serializes its fragment (see TemplateContents.cs).
        GetChildren: SerializationChildrenOf,
        GetAttributes: node => node is DomElement element
            ? GetSerializableAttributes(element, sourceResolver?.Invoke(element))
            : [],
        GetStyles: node => node is DomElement element
            ? EffectiveInlineStyle(element).OrderBy(kv => HtmlSerializer.IsShorthandProperty(kv.Key) ? 0 : 1)
            : [],
        // RF-BRIDGE-1c Phase F (F3c part 2d): text nodes serialize with the same HTML escaping the
        // former element-store textContent path applied — except inside raw-text elements
        // (script/style/…), whose character data must stay literal. The bridge serializes with
        // EncodeTextNodes:false, so GetText returns the already-escaped form. Comments stay raw.
        GetText: static node => node switch
        {
            DomText text => IsRawTextSerializationParent(text)
                ? text.Data
                : HtmlSerializer.Encode(text.Data),
            DomCharacterData other => other.Data,
            _ => BridgeText(node),
        },
        // Phase 4 item 3: the parallel InnerHtml string is gone — raw-text content is always a
        // canonical DomText child, serialized via GetChildren/GetText above. No raw fallback.
        GetRawInnerHtml: static _ => null);

    private IEnumerable<KeyValuePair<string, string>> GetSerializableAttributes(
        DomElement element,
        DomElement? sourceElement = null)
    {
        // A customized built-in created by its constructor or by createElement's `is` option has an
        // is value and no `is` attribute, and HTML §13.3 serializes it — before the other attributes
        // — precisely so the markup can be re-parsed into the same element. Measured against
        // Chromium: getAttribute('is') is null while outerHTML reads <button is="fancy-b">.
        if (_customElements?.SerializedIsValue(element) is { Length: > 0 } isValue && !HasAttr(element, "is"))
            yield return new("is", isValue);

        if (!string.IsNullOrEmpty(element.Id))
            yield return new("id", element.Id);
        if (!string.IsNullOrEmpty(element.ClassName))
            yield return new("class", element.ClassName);

        // Set by a script, so the attribute below is the stale one and is skipped rather than
        // emitted alongside it. See the matching reflection in ReflectRenderState.
        var scriptSetValue =
            element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
            FormControlStateFor(element).Value.TryGet(out var idlValue) &&
            idlValue is string idlString
                ? idlString
                : null;

        // The same question for an option, whose selectedness is decided by its select rather than
        // by itself. `null` means no script has chosen, and the authored attribute stands.
        var scriptSetSelected = ScriptChosenOptionSelected(element);

        var serializedSrcDoc = TrySerializeCurrentSrcDoc(element, sourceElement);
        foreach (var attribute in element.Attributes.Values)
        {
            var name = attribute.QualifiedName;
            var value = attribute.Value;
            if (name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("class", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (scriptSetValue is not null && name.Equals("value", StringComparison.OrdinalIgnoreCase))
                continue;

            if (scriptSetSelected is not null && name.Equals("selected", StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new(
                name,
                name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase) && serializedSrcDoc is not null
                    ? serializedSrcDoc
                    : value);
        }

        if (scriptSetValue is not null)
            yield return new("value", scriptSetValue);

        if (scriptSetSelected is true)
            yield return new("selected", string.Empty);
    }

    /// <summary>
    /// Whether a script has decided this option's selectedness, and how — <c>null</c> when it is not
    /// an option, or when its select carries no index a script chose, in which case the authored
    /// <c>selected</c> attribute is still the answer.
    /// </summary>
    /// <remarks>
    /// An option is the one element whose serialized state is not its own: <c>select.value = x</c>
    /// writes an index on the <i>select</i>, and which option that makes selected is a question only
    /// the select can answer. So the option is asked about its ancestor, through the same option walk
    /// the select binding selects with.
    /// </remarks>
    private bool? ScriptChosenOptionSelected(DomElement element)
    {
        if (!element.TagName.Equals("option", StringComparison.OrdinalIgnoreCase))
            return null;

        DomElement? select = null;
        for (DomNode? node = element.ParentNode; node is not null; node = node.ParentNode)
        {
            if (node is DomElement candidate && candidate.TagName.Equals("select", StringComparison.OrdinalIgnoreCase))
            {
                select = candidate;
                break;
            }
        }

        if (select is null ||
            !FormControlStateFor(select).SelectedIndex.TryGet(out var stored) ||
            stored is not int chosen)
        {
            return null;
        }

        return Dom.Features.SelectBinding.CollectSelectOptions(select).IndexOf(element) == chosen;
    }

    private string? TrySerializeCurrentSrcDoc(DomElement element, DomElement? sourceElement)
    {
        if (!string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase) ||
            !HasAttr(element, "srcdoc"))
        {
            return null;
        }

        var subDocumentRoot = GetContentDocument(sourceElement ?? element);
        if (subDocumentRoot == null || subDocumentRoot.ChildNodes.Count == 0)
            return null;

        return RenderedSubDocumentMarkup(subDocumentRoot);
    }

}
