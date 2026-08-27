using System.Text.RegularExpressions;
using Broiler.Dom;
using Broiler.CSS.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>DomBridge.cs</c> (Phase 3 ratchet, 2026-07-17) to keep the
/// facade under the 750-line guard: initial HTML/doctype ingestion and inline-style parsing.
/// <see cref="ParseHtml"/> rebuilds the canonical document from an HTML string (clearing prior
/// runtime state, parsing the doctype, running the shared tree builder and reparenting into
/// <c>DocumentElement</c>); <see cref="ParseStyle"/> / <see cref="IsAcceptableInlineValue"/> apply
/// the shared CSS declaration-validation error recovery to inline <c>style</c> values. Pure
/// partial-class relocation — no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    private static readonly System.Text.RegularExpressions.Regex DocTypePattern = DocTypePatternRegex();

    private void ParseHtml(string html)
    {
        // Multithreading roadmap item #17. Everything from here to BuildDocumentTree is CPU the
        // document's sub-resource fetches could have been overlapping with, and until now they did
        // not start until the parse had finished and the sheet list had been collected. Start the
        // speculative scan first, so its worker is issuing requests while this thread tears the old
        // document down and builds the new one.
        StartSpeculativePreloadScan(html);

        // Parse/re-parse tears down and rebuilds the live tree wholesale (ClearChildren + build);
        // these are document-construction mutations, not script mutations, so suppress observer
        // delivery for them (matching the prior explicit channel, which parse never drove).
        using var mutationSuppression = SuppressMutationDelivery();
        // P2.2: one call clears both wrapper maps. Re-parse now also releases stale sub-document
        // wrappers (keyed by detached roots that no lookup can reach again) — observably
        // equivalent to before, but it stops them lingering until disposal.
        _jsObjects.Clear();
        ClearComputedPropsCache();
        // Clear the document first so the doctype/<html> re-append below satisfies canonical
        // DomDocument ordering (doctype must precede the document element).
        ClearChildren(_document);
        // A re-parse is a new document generation: drop the prior document's timers, listeners,
        // observers and message ports so re-attaching leaves no state from the previous document
        // (HtmlBridge complexity-reduction roadmap Phase 2, P2.1).
        ClearRuntimeSessionState();
        // A re-parsed document is a new generation: release the prior document's headless
        // layout view (and its renderer container) so geometry is document-scoped.
        DisposeLayoutView();

        // Publish the document's quirks mode for the render that follows on this
        // thread. Layout (which on the HTML-string path holds no back-reference to
        // the document) reads it while sizing the root/body boxes for the
        // quirks-mode fill-viewport behaviour. Every WPT render runs through this
        // parse before laying out, so the flag is set for the render that matters.
        Layout.DocumentModeContext.CurrentQuirksMode =
            Layout.DocumentModeContext.IsQuirksHtml(html);

        // Use WHATWG-aligned tokeniser & tree builder (shared HtmlDocumentParser). The parser
        // now also yields the canonical <!DOCTYPE> node (name + PUBLIC/SYSTEM identifiers), so the
        // bridge no longer re-parses it with its own regex. Add it as the document's first child
        // (before <html>, appended below — canonical DomDocument requires doctype-before-element).
        var (docElement, doctype, allElements, title) = BuildDocumentTree(html);
        if (doctype != null)
            _document.AppendChild(doctype);
        Title = title;
        ClearChildren(DocumentElement);
        // RF-BRIDGE-1c Phase F (F3c part 2d): reparent ALL children (raw ChildNodes) so any
        // text/comment nodes directly under the parsed <html> survive — no-op on the old
        // homogeneous tree where every child was an element.
        foreach (var child in docElement.ChildNodes.ToArray())
        {
            SetParent(child, DocumentElement);
            DocumentElement.AppendChild(child);
        }

        // Copy attributes from the parsed <html> element to DocumentElement
        // so that attributes like lang="en", dir="rtl", etc. are preserved
        // during serialization.
        if (!string.IsNullOrEmpty(docElement.Id))
            DocumentElement.Id = docElement.Id;
        if (!string.IsNullOrEmpty(docElement.ClassName))
            DocumentElement.ClassName = docElement.ClassName;
        foreach (var attribute in docElement.Attributes.Values)
            SetAttr(DocumentElement, attribute.QualifiedName, attribute.Value);
        foreach (var kv in InlineStyle(docElement))
            InlineStyle(DocumentElement)[kv.Key] = kv.Value;


        // Connect DocumentElement to the canonical document (after the doctype) so that
        // document.firstChild works and structural pseudo-classes correctly detect the document
        // root boundary.
        if (!_document.ChildNodes.Contains(DocumentElement))
            _document.AppendChild(DocumentElement);

        AttachDeclarativeShadowRoots(DocumentElement);

        // After the shadow-root pass has consumed the templates that declare one: every
        // remaining <template>'s children move into its contents fragment, which is where
        // HTML §4.12.3 has the parser put them. Order matters — a declarative shadow root's
        // template is not inert and its children belong in the shadow tree, not in a fragment.
        DivertTemplateContents(DocumentElement);


        // Stylesheet discovery is document-scoped and lazy through the shared
        // CssStyleEngine. A rebuilt document must not retain the prior engines.
        ResetComputedStyleEngines();
    }

    /// <summary>
    /// HTML §4.12.3: turns every <c>&lt;template shadowrootmode="open|closed"&gt;</c> into a real
    /// shadow root on its parent — the declarative counterpart of <c>attachShadow()</c>. The template
    /// contributes its children to the new root and is then dropped from the tree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The spec does this inside the tree builder, as the template's end tag is seen. Doing it as a
    /// pass over the finished tree is equivalent for a parsed document — nothing observes the
    /// intermediate state, because script has not run yet — and keeps the shared
    /// <c>HtmlDocumentParser</c> free of a bridge-only concept.
    /// </para>
    /// <para>
    /// Only the shadow root is new here: everything downstream of it (styling the shadow tree,
    /// flattening the <c>#shadow-root</c> wrapper for the renderer, capturing it in a view
    /// transition) is the same machinery <c>attachShadow()</c> already drove, which is why
    /// imperative shadow content rendered while declarative content did not.
    /// </para>
    /// </remarks>
    private void AttachDeclarativeShadowRoots(DomElement root)
    {
        // Depth-first with an explicit stack: a shadow tree may itself contain declarative shadow
        // roots, and those templates only become reachable once their content has been moved.
        var pending = new Stack<DomElement>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var element = pending.Pop();

            for (var index = element.ChildNodes.Count - 1; index >= 0; index--)
            {
                if (element.ChildNodes[index] is not DomElement child)
                    continue;

                if (!TryTakeDeclarativeShadowRoot(element, child, index, out var shadowRoot))
                {
                    pending.Push(child);
                    continue;
                }

                pending.Push(shadowRoot);
            }
        }
    }

    /// <summary>
    /// When <paramref name="child"/> is a declarative shadow-root template of <paramref name="host"/>,
    /// moves its children into a freshly attached shadow root, removes the template, and returns the
    /// root. Returns <see langword="false"/> for anything else, leaving the tree untouched.
    /// </summary>
    private bool TryTakeDeclarativeShadowRoot(
        DomElement host, DomElement child, int childIndex, out DomElement shadowRoot)
    {
        shadowRoot = null!;

        if (!string.Equals(child.TagName, "template", StringComparison.OrdinalIgnoreCase))
            return false;

        var mode = child.GetAttribute("shadowrootmode")?.Trim();
        if (!string.Equals(mode, "open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "closed", StringComparison.OrdinalIgnoreCase))
            return false;

        // "Only the first declarative shadow root wins": a second template on the same host is an
        // ordinary inert <template>, per the spec's already-has-a-shadow-root check.
        if (GetShadowRoot(host) is not null)
            return false;

        shadowRoot = ((Dom.Features.IShadowDomHost)this).AttachShadowRoot(host, mode!.ToLowerInvariant());

        foreach (var content in child.ChildNodes.ToArray())
        {
            SetParent(content, shadowRoot);
            shadowRoot.AppendChild(content);
        }

        RemoveNthChild(host, childIndex);
        return true;
    }

    /// <summary>
    /// Parses a CSS inline style string (e.g. <c>"color: red; font-size: 12px"</c>)
    /// into a property→value dictionary. Implements CSS error recovery: when the
    /// same property is declared multiple times, invalid values are discarded so
    /// the last <em>valid</em> value wins (per CSS 2.1 §4.2 / CSS Syntax §5).
    /// </summary>
    /// <param name="reportDrops">
    /// When <c>true</c>, declarations rejected by
    /// <see cref="CSS.Dom.CssDeclarationValidator.IsAcceptableDeclarationValue"/>
    /// are surfaced through
    /// <see cref="CSS.Dom.CssEngineDiagnostics.DeclarationRejected"/>
    /// (diagnostic #1b). The bridge rewrites the serialized <c>style</c> attribute
    /// from the survivors of this filter (see <c>PrepareCanonicalDocumentForRendering</c>),
    /// so a dropped inline declaration vanishes before the renderer's own style engine
    /// can report it — this is the only place such drops are observable. Set it only at
    /// inline-style <em>ingestion</em> sites that write <c>InlineStyle(element)</c> (so the drop
    /// reaches the rendered output); leave it off for query/bookkeeping re-parses and for
    /// stylesheet-rule / descriptor parsing (cascade drops the style engine already reports).
    /// </param>
    internal static Dictionary<string, string> ParseStyle(string styleValue, bool reportDrops = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new CSS.CssParser().ParseDeclarations(styleValue);
        foreach (var declaration in declarations.Declarations)
        {
            var prop = declaration.Name;
            // Validate the importance-stripped value against the shared CSS.Dom
            // declaration table (the same closed-keyword error-recovery the cascade
            // uses), then re-attach the "!important" suffix the bridge-owned
            // declaration map carries as part of the string value.
            var rawValue = declaration.Value.Text;

            if (CssDeclarationValidator.IsAcceptableDeclarationValue(prop, rawValue))
            {
                var val = declaration.Important ? rawValue + " !important" : rawValue;
                result[prop] = val;
                // Map vendor-prefixed property to unprefixed equivalent (TODO-G9)
                var unprefixed = CssPropertyNames.StripVendorPrefix(prop);
                if (unprefixed != prop && !result.ContainsKey(unprefixed))
                    result[unprefixed] = val;
            }
            else if (reportDrops)
            {
                // Report the raw value (without any synthetic " !important" suffix) so
                // inline drops aggregate identically to the engine's stylesheet drops.
                CssEngineDiagnostics.DeclarationRejected?.Invoke(prop, rawValue);
            }
        }
        return result;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an acceptable declared value for
    /// <paramref name="property"/> per the shared <see cref="CSS.Dom.CssDeclarationValidator"/> —
    /// the same closed-keyword error-recovery the inline-style <em>attribute</em> path
    /// (<see cref="ParseStyle"/>) applies. A live <c>CSSStyleDeclaration</c> per-property setter
    /// (<c>el.style.color = …</c>, <c>setProperty(…)</c>, <c>cssFloat = …</c>) must <em>reject</em>
    /// an invalid value rather than store it, matching the attribute path (where
    /// <c>el.style = "color: bogus"</c> already drops the declaration) and CSSOM error handling.
    /// The value may carry a trailing <c>!important</c>, which is stripped before validation;
    /// unknown and custom (<c>--*</c>) properties are always accepted (the validator's default).
    /// </summary>
    internal static bool IsAcceptableInlineValue(string property, string value) =>
        CssDeclarationValidator.IsAcceptableDeclarationValue(property, CssPriority.Strip(value));

    [GeneratedRegex(@"<!DOCTYPE\s+(\w+)(?:\s+PUBLIC\s+""([^""]*)""(?:\s+""([^""]*)"")?)?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex DocTypePatternRegex();
}
