using Broiler.Dom.Html;
using Broiler.Dom;
using Broiler.CSS;

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

    private const int MaxSerializationDepth = 100_000;
    private const double ZoomSerializationEpsilon = 0.0001;
    private const double DefaultProgressLikeTrackLengthPx = 120;
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
    /// Serialization of a document with no element child: the doctype alone, matching the
    /// blank reference these tests compare against. The doctype is unconditional here for
    /// the same reason <see cref="HtmlSerializationOptions.IncludeHtmlDoctype"/> is set on
    /// the normal path.
    /// </summary>
    private const string EmptyDocumentHtml = "<!DOCTYPE html>\n";

    /// <summary>
    /// Returns an isolated document prepared for direct renderer consumption.
    /// Bridge-owned style and form-control state is reflected into the projection;
    /// the script-visible canonical <see cref="Document"/> is not changed.
    /// </summary>
    public DomDocument GetRenderDocument() => CreateRenderProjection().Document;

    /// <summary>
    /// Whether the element-<c>zoom</c> serialization bake (<see cref="ApplyZoomSerializationStyles"/>)
    /// runs. The bake and the engine used-value model (<c>Broiler.Layout.Engine.NativeZoom</c>,
    /// increments 1–5) are mutually exclusive: running both double-counts <c>zoom</c>, running neither
    /// drops it. The engine flag is <c>[ThreadStatic]</c> and set on the layout thread; the bake mutates
    /// the DOM thread-independently. So the bake is skipped exactly when the engine model is enabled on
    /// this thread — the increment-6 cutover switch. Default (flag off) the bake runs, byte-identical to
    /// before this gate.
    /// </summary>
    private static bool ZoomBakeActive => !Broiler.Layout.Engine.NativeZoom.Enabled;


    /// <summary>
    /// Serializes the element's authoritative inline-style dict back into its canonical
    /// <c>style=</c> attribute in CSSOM serialization form (shorthand-first, <c>"; "</c>-joined),
    /// removing the attribute when the dict is empty. This is the single inline-style write-through
    /// (Phase 4 item 2): it runs at serialization (<see cref="ReflectRenderState"/>) and after every
    /// script <c>element.style</c> mutation, so a JS style mutation and <c>getAttribute("style")</c>
    /// observe the same state. Uses the node-model <see cref="SetAttr"/>/<see cref="RemoveAttr"/> (not
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

            if (element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
                !HasAttr(element, "value") &&
                FormControlStateFor(element).Value.TryGet(out var idlValue) &&
                idlValue is string { Length: > 0 } idlString)
            {
                SetAttr(element, "value", idlString);
            }
        }

        foreach (var child in ChildElements(element))
            ReflectRenderState(child);
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
    /// value (see <see cref="FindMetaColorScheme"/>); the renderer's own token parsing then selects
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
    /// The <c>content</c> of the first <c>&lt;meta name="color-scheme"&gt;</c> in tree order whose
    /// <c>content</c> is a valid CSS <c>&lt;'color-scheme'&gt;</c> value, or <c>null</c> when there
    /// is none.
    /// <para>
    /// A meta contributes when its <c>name</c> is <c>color-scheme</c>, even if it also carries an
    /// <c>http-equiv</c> attribute (WPT <c>http-equiv-and-name-1</c>): the name metadata is still
    /// processed. A meta whose <c>content</c> is absent, empty, or not a valid color-scheme value
    /// (e.g. the comma-separated <c>light,dark</c>) is skipped, so selection continues to the first
    /// meta that <em>does</em> parse — "first valid applies" (WPT
    /// <c>meta-color-scheme-first-valid-applies</c>).
    /// </para>
    /// </summary>
    private static string? FindMetaColorScheme(DomElement root)
    {
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!element.TagName.Equals("meta", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGetAttribute(element, "name", out var name) ||
                !name.Trim().Equals("color-scheme", StringComparison.OrdinalIgnoreCase))
                continue;
            // A meta in a shadow tree is not in the document tree (HTML §4.2.5.3), so it
            // contributes no document-level color scheme (WPT
            // meta-color-scheme-single-value-in-shadow-tree).
            if (FindContainingShadowRoot(element) != null)
                continue;
            if (TryGetAttribute(element, "content", out var content) &&
                IsValidColorSchemeValue(content))
            {
                return content.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="content"/> is a valid CSS <c>&lt;'color-scheme'&gt;</c> value:
    /// a whitespace-separated list of CSS identifiers (<c>normal</c>, <c>light</c>, <c>dark</c>,
    /// <c>only</c>, or a custom ident). Any other character — most notably a comma, as in the
    /// invalid <c>light,dark</c> — makes the value unparseable, so the meta is ignored per CSS
    /// Color Adjust §2.
    /// </summary>
    private static bool IsValidColorSchemeValue(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        foreach (var token in tokens)
            foreach (var ch in token)
                if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch > 0x7F))
                    return false;

        return true;
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
    /// clears <see cref="StyleSheetRuntimeState.RulesMutated"/>, so a second pass is a
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

    /// <summary>
    /// Drops comment nodes from the render-bound document. Comments never render,
    /// but the shared serializer emits them as <c>&lt;!--…--&gt;</c>, which splits
    /// an otherwise-contiguous run of text around a comment (e.g.
    /// <c>"\n&lt;!-- c --&gt;\n"</c> between block siblings) into two separate text
    /// nodes when the canonical HTML is re-parsed for layout. CSS white-space
    /// processing then collapses each run independently, yielding a spurious extra
    /// space between elements (and an uncollapsed leading space at the start of a
    /// block) that shifts all following content — a common cause of the WPT
    /// "MissingContent" pixel mismatches in comment-heavy tests. Removing the
    /// comment nodes lets the surrounding text re-parse as a single node so the run
    /// collapses as the spec requires. Runs only inside <see cref="ApplySerializationTransforms"/>,
    /// so JS-visible <c>innerHTML</c>/<c>outerHTML</c> (which serialize without it)
    /// still expose the comments.
    /// </summary>
    private static void RemoveRenderCommentNodes(DomElement element)
    {
        for (int i = element.ChildNodes.Count - 1; i >= 0; i--)
        {
            var child = element.ChildNodes[i];
            if (IsComment(child))
            {
                RemoveNthChild(element, i);
                continue;
            }

            if (child is DomElement childElement)
                RemoveRenderCommentNodes(childElement);
        }
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

    private static DomElement? FindFirstElementByTagName(DomElement root, string tagName)
    {
        if (string.Equals(root.TagName, tagName, StringComparison.OrdinalIgnoreCase))
            return root;

        foreach (var child in ChildElements(root))
        {
            var match = FindFirstElementByTagName(child, tagName);
            if (match != null)
                return match;
        }

        return null;
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



    private static double ReadPixelLength(string? rawValue, double fallback)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallback;

        var trimmed = rawValue.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];

        return double.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
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

    private static bool ShouldApplySvgSerializationAttributes(DomElement element)
    {
        var tag = element.TagName.ToLowerInvariant();
        return tag is "svg" or "defs" or "path" or "rect" or "line" or "text" or "textpath" or "polygon" or "polyline";
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

    private static readonly HashSet<string> ZoomPreferSpecifiedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "width",
        "height",
        "min-width",
        "min-height",
        "max-width",
        "max-height"
    };

    private static readonly string[] ZoomScaledSerializationProperties =
    [
        "width", "height", "min-width", "min-height", "max-width", "max-height",
        "top", "right", "bottom", "left",
        "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding-top", "padding-right", "padding-bottom", "padding-left",
        "scroll-margin-top", "scroll-margin-right", "scroll-margin-bottom", "scroll-margin-left",
        "scroll-padding-top", "scroll-padding-right", "scroll-padding-bottom", "scroll-padding-left",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "stroke-width",
        "font-size", "line-height", "letter-spacing", "word-spacing", "text-indent",
        "border-radius", "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius",
        "outline-width", "outline-offset",
        "column-width", "column-height", "column-gap"
    ];


    // RF-BRIDGE-1c Phase F (F3c part 2c): the serialization adapter is over canonical DomNode so
    // text/comment children serialize once construction flips to DomText/DomComment. GetKind keys
    // text/comment off NodeType (holds for facade and canonical char-data) and the doctype/fragment
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

    /// <summary>Whether <paramref name="node"/>'s parent is an HTML raw-text element whose text
    /// content is serialized literally (not HTML-escaped). The standard raw-text element set is
    /// owned by <see cref="SharedHtmlSerializer.RawTextElements"/> (§13.3); this bridge predicate
    /// only applies it to the node's parent. (RF-BRIDGE-1c Phase F, F3c part 2d.)</summary>
    private static bool IsRawTextSerializationParent(DomNode node) =>
        node.ParentNode is DomElement parent &&
        HtmlSerializer.IsRawTextElement(parent.TagName);

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

            yield return new(
                name,
                name.Equals("srcdoc", StringComparison.OrdinalIgnoreCase) && serializedSrcDoc is not null
                    ? serializedSrcDoc
                    : value);
        }

        if (element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
            !HasAttr(element, "value") &&
            FormControlStateFor(element).Value.TryGet(out var idlValue) &&
            idlValue is string { Length: > 0 } idlString)
        {
            yield return new("value", idlString);
        }
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
