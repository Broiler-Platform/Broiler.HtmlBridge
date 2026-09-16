using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element shadow-DOM linkage (a host's
    // shadow root, a root's host, and the root's mode) was the Shadow slot of the process-static
    // ElementRuntimeState table; it is now a per-bridge instance table, owned by the session's bridge.
    // Still element-keyed, so it GCs with the element and the cloneNode copy (see CloneDomElement) is
    // preserved. The former static GetShadowRoot / GetShadowHost helpers became instance methods (all
    // their callers were already on the bridge instance), so no cross-class host threading was needed.
    private readonly ConditionalWeakTable<DomElement, ShadowRuntimeState> _shadowRuntimeStates = [];

    private ShadowRuntimeState ShadowStateFor(DomElement element) =>
        _shadowRuntimeStates.GetValue(element, static _ => new ShadowRuntimeState());

    private DomElement? GetShadowRoot(DomElement element)
    {
        if (ShadowStateFor(element).Root.TryGet(out var rawShadowRoot) &&
            rawShadowRoot is DomElement root)
        {
            return root;
        }

        return null;
    }

    private DomElement? GetShadowHost(DomElement? shadowRoot)
    {
        if (shadowRoot != null &&
            string.Equals(shadowRoot.TagName, "#shadow-root", StringComparison.Ordinal) &&
            ShadowStateFor(shadowRoot).Host.TryGet(out var rawHost) &&
            rawHost is DomElement host)
        {
            return host;
        }

        return null;
    }

    // Walk to the absolute root. For a connected node this is the canonical DomDocument (Phase 4: the
    // document root is the DomDocument, not a #document wrapper element); a detached subtree roots to its
    // topmost node. Phase 4 item 4/5: this is exactly canonical DomNode.GetRootNode(), so delegate to it
    // rather than re-implement the `while ParentNode` climb.
    private DomNode GetTreeRoot(DomNode node) => node.GetRootNode();

    /// <summary>
    /// The wrapper a <c>getRootNode()</c> answers with, or <see cref="JsValue.Null"/> when the
    /// document has none yet.
    /// </summary>
    /// <remarks>
    /// <b>It answered a <c>JSValue</c> until its one caller stopped needing one, and between them
    /// they were a round trip.</b> This built an engine value; <c>WrapRootNode</c> tested it with
    /// <c>is JSObject</c> and wrapped it straight back into a handle. Both halves are the same
    /// object either way — a handle carries the engine's own — so the conversion was work with no
    /// observer, and removing it removes two engine references rather than relocating them.
    /// <para>
    /// <b>The <see cref="JsValue.Null"/> arm is not the same as an absent one and is kept
    /// deliberately.</b> <c>DocumentHandle</c> answers <see cref="JsValue.Missing"/> before a
    /// document wrapper exists, which is "the bridge has not registered a document yet" — a state no
    /// page can observe. What a page asking <c>getRootNode()</c> in that window must see is
    /// <c>null</c>, which is what the engine's own null meant here, so the translation is explicit.
    /// </para>
    /// </remarks>
    private JsValue ToJSRootNode(DomNode root)
    {
        if (ReferenceEquals(root, _document))
            return DocumentHandle.IsMissing ? JsValue.Null : DocumentHandle;

        // A severed sub-document root is a canonical DomDocument (P4.4b); WrapNode resolves it to
        // its document wrapper via the document-wrapper map, so no #subdoc-root special case remains.
        return WrapNode(root);
    }

    private DomElement? GetSlotHost(DomElement slot) => GetShadowHost(FindContainingShadowRoot(slot));

    private DomElement? FindAssignedSlot(DomElement root, DomElement node)
    {
        foreach (var child in ChildElements(root))
        {
            if (IsText(child))
                continue;

            if (string.Equals(child.TagName, "slot", StringComparison.OrdinalIgnoreCase) &&
                SlotAcceptsNode(child, node))
            {
                return child;
            }

            var nested = FindAssignedSlot(child, node);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private DomElement? GetAssignedSlot(DomElement element)
    {
        if (IsText(element) || ParentEl(element) == null)
            return null;

        var shadowRoot = GetShadowRoot(ParentEl(element));
        return shadowRoot != null ? FindAssignedSlot(shadowRoot, element) : null;
    }

    private DomElement? GetScrollTraversalParent(DomElement element)
    {
        var assignedSlot = GetAssignedSlot(element);
        if (assignedSlot != null)
            return assignedSlot;

        var parent = ParentEl(element);
        return GetShadowHost(parent) ?? parent;
    }
}

/// <summary>
/// Render-time flattening of a shadow host's light-DOM children (DOM §4.2.2 "slottable" /
/// CSS Scoping 1 §3.3).
/// <para>
/// A host's light-DOM children do not render in their own right: they render only where a
/// <c>&lt;slot&gt;</c> in the shadow tree assigns them. The serializer emits the host's light
/// children <em>and</em> its <c>#shadow-root</c> side by side, so a shadow tree with no
/// <c>&lt;slot&gt;</c> at all still painted the light children — the WPT
/// <c>css/css-shadow</c> family's standard "FAIL" text, which the references never show
/// (<c>css-scoping-shadow-root-hides-children</c>, and the leftover text inside the green box
/// of <c>css-scoping-shadow-host-functional-rule</c>).
/// </para>
/// <para>
/// This handles the unambiguous half: when the shadow tree exposes no slot, nothing can be
/// assigned, so the host's light children generate no boxes and are dropped from the
/// render-bound tree. A shadow tree that <em>does</em> contain a slot is left alone — placing
/// each child at its assigned slot is full slot flattening, which this does not attempt.
/// Only the render-bound serialization is affected; the live DOM and JS-visible
/// <c>innerHTML</c> still expose the light children.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Drops the light-DOM children of every shadow host whose shadow tree contains no
    /// <c>&lt;slot&gt;</c>. A no-op for a document with no shadow roots.
    /// </summary>
    private void HideUnslottedShadowHostChildren(DomElement root)
    {
        var shadowRoots = root.Descendants()
            .OfType<DomElement>()
            .Where(e => string.Equals(e.TagName, "#shadow-root", StringComparison.Ordinal))
            .ToList();

        foreach (var shadowRoot in shadowRoots)
        {
            var host = ParentEl(shadowRoot);
            if (host == null)
                continue;

            // A slot somewhere in this shadow tree can assign the light children — leave them
            // in place rather than dropping content the author expects to see.
            if (ShadowTreeHasSlot(shadowRoot))
                continue;

            foreach (var child in host.ChildNodes.ToArray())
            {
                if (ReferenceEquals(child, shadowRoot))
                    continue;

                host.RemoveChild(child);
            }
        }
    }

    /// <summary>
    /// Replaces each <c>#shadow-root</c> element with its children, in place, for the
    /// render-bound tree.
    /// <para>
    /// <c>#shadow-root</c> is a bridge-internal container, not an HTML element, and it is
    /// serialized literally as <c>&lt;#shadow-root&gt;</c>. Per the HTML tokenizer a <c>&lt;</c>
    /// followed by anything that is not an ASCII letter (or <c>!</c>, <c>/</c>, <c>?</c>) is not a
    /// tag at all — it is emitted as character data — so the renderer painted the literal string
    /// "&lt;#shadow-root&gt;" inside every shadow host. Flattening the wrapper away puts the
    /// shadow content directly in the host, which is what the renderer should draw.
    /// </para>
    /// <para>
    /// Deliberately limited to shadow trees with no <c>&lt;slot&gt;</c> — the same set
    /// <see cref="HideUnslottedShadowHostChildren"/> acts on. Flattening the wrapper moves the
    /// shadow content into the host, which changes the ancestor chain that slot assignment and
    /// scroll-container resolution walk; restricting it to slotless trees leaves every
    /// slot-bearing shadow tree byte-identical to its previous rendering, so this cannot disturb
    /// slot behaviour. Slotless trees have no assignment to preserve, so the flatten is safe
    /// there and is what the <c>css/css-shadow</c> <c>:host</c> reftests need.
    /// </para>
    /// <para>
    /// Runs after the other shadow passes, which locate content by the <c>#shadow-root</c>
    /// wrapper. Only the render-bound serialization is affected.
    /// </para>
    /// </summary>
    private void UnwrapShadowRootsForRender(DomElement root)
    {
        var shadowRoots = root.Descendants()
            .OfType<DomElement>()
            .Where(e => string.Equals(e.TagName, "#shadow-root", StringComparison.Ordinal))
            .Where(e => !ShadowTreeHasSlot(e))
            .ToList();

        // Innermost first, so unwrapping a nested root is not disturbed by its ancestor moving.
        shadowRoots.Reverse();

        foreach (var shadowRoot in shadowRoots)
        {
            var host = ParentEl(shadowRoot);
            if (host == null)
                continue;

            var index = ChildIndexOf(host, shadowRoot);
            if (index < 0)
                continue;

            RemoveNthChild(host, index);
            SetParent(shadowRoot, null);

            foreach (var child in shadowRoot.ChildNodes.ToArray())
            {
                InsertChildAt(host, index, child);
                index++;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="shadowRoot"/>'s tree contains a <c>&lt;slot&gt;</c>, not
    /// descending into a nested <c>#shadow-root</c> (whose slots assign that root's own host's
    /// children, not this one's).
    /// </summary>
    private bool ShadowTreeHasSlot(DomElement shadowRoot)
    {
        foreach (var child in ChildElements(shadowRoot))
        {
            if (IsText(child))
                continue;

            if (string.Equals(child.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            if (child.TagName.Equals("slot", StringComparison.OrdinalIgnoreCase))
                return true;

            if (ShadowTreeHasSlot(child))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Render-time scoping of a shadow tree's <em>own</em> style rules (CSS Scoping 1 §3.3,
/// "selectors in a shadow tree only match elements in that tree").
/// <para>
/// A shadow root's <c>&lt;style&gt;</c> is serialized inline into the render document, so the
/// renderer sees its rules as ordinary global rules with no provenance —
/// <c>&lt;style&gt;div { background: red }&lt;/style&gt;</c> inside a shadow root repainted
/// <em>every</em> <c>div</c> in the page. That is the whole failure of
/// <c>css/css-shadow/css-scoping-shadow-with-rules-no-style-leak</c> and
/// <c>shadow-link-rel-stylesheet-no-style-leak</c> (both render the light-DOM "FAIL" box red
/// where the reference is green), and — combined with a <c>:dir()</c> that matched
/// everything — it is what painted the whole canvas for
/// <c>shadow-directionality-001/002</c>.
/// </para>
/// <para>
/// <see cref="ScopeShadowHostSelectors"/> already solved the mirror-image problem for
/// <c>:host</c>, and this reuses its shape: stamp the elements, rewrite the selectors.
/// </para>
/// <list type="bullet">
///   <item>every element <em>in</em> the shadow tree gets
///         <c>data-broiler-shadow-scope="N"</c> (the host does not — it belongs to the outer
///         tree and is reachable only through <c>:host</c>);</item>
///   <item>each selector in that root's styles gets <c>[data-broiler-shadow-scope="N"]</c>
///         appended to its <em>subject</em> compound, so the rule can only ever apply to an
///         element of this tree.</item>
/// </list>
/// <para>
/// <b>Why the subject compound only, and not every compound.</b> Scoping the subject is what
/// stops the leak: a rule whose subject is outside the tree cannot apply. Scoping *every*
/// compound would additionally require each ancestor in a descendant selector to be in the
/// same tree — more faithful to the spec, but it adds one attribute selector per compound and
/// so changes specificity <em>unevenly</em> between rules of the same sheet: <c>div span</c>
/// (0,0,2) and <c>.foo</c> (0,1,0) invert their cascade order once they become
/// <c>div[s] span[s]</c> (0,2,2) and <c>.foo[s]</c> (0,2,0). Scoping only the subject adds
/// exactly (0,1,0) to every rule in the sheet, so their order relative to each other is
/// preserved exactly, and the sheet as a whole outranks page rules that reach into the tree —
/// which is the correct direction, since per spec those page rules should not match at all.
/// </para>
/// <para>
/// Left deliberately untouched: a compound containing <c>:host</c>/<c>:host-context</c> (the
/// host is not in the tree; <see cref="ScopeShadowHostSelectors"/> owns it, and runs after
/// this so it still sees the author's keyword), and one containing <c>::slotted</c>/<c>::part</c>
/// (whose subject is a light-DOM node, not a member of this tree). <c>@keyframes</c>,
/// <c>@font-face</c> and friends are copied verbatim — their blocks hold keyframe selectors and
/// descriptors, not selectors — while the conditional group rules (<c>@media</c>,
/// <c>@supports</c>, <c>@container</c>, <c>@layer</c>, <c>@scope</c>, <c>@starting-style</c>)
/// are recursed into, because those do hold style rules.
/// </para>
/// <para>
/// Only the render-bound serialization is affected; the live CSSOM and JS-visible
/// <c>innerHTML</c> still expose the author's original selector text.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Restricts every shadow root's own style rules to that root's tree. A no-op for a document
    /// with no shadow roots, and for a shadow tree that carries no <c>&lt;style&gt;</c>.
    /// </summary>
    /// <summary>
    /// Whether a shadow root has ever been attached in this document. Both this pass and its
    /// <c>:host</c> sibling look for <c>#shadow-root</c> by walking every descendant, so a document
    /// that has no shadow DOM at all should not pay for the walk on every serialization —
    /// <c>RunTestWithTimeout_GridTemplateColumnsCrash…</c>, a 6-second budget on a pathological
    /// document, is what caught the unguarded version.
    /// </summary>
    private bool _hasShadowRoots;

    private void ScopeShadowTreeSelectors(DomElement root)
    {
        if (!_hasShadowRoots)
            return;

        var index = 0;

        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            var token = index.ToString(CultureInfo.InvariantCulture);
            index++;

            var styles = new List<DomElement>();
            CollectStylesInShadowRoot(element, styles);
            if (styles.Count == 0)
                continue;

            var scoped = false;
            foreach (var style in styles)
            {
                var original = GetStyleElementCssText(style);
                var rewritten = ScopeSelectorsToShadowTree(original, token);
                if (string.Equals(rewritten, original, StringComparison.Ordinal))
                    continue;

                SetElementTextContent(style, rewritten);
                scoped = true;
            }

            if (scoped)
                StampShadowTreeScope(element, token);
        }
    }

    /// <summary>
    /// Stamps every element of <paramref name="shadowRoot"/>'s tree with the scope marker, not
    /// descending into a nested <c>#shadow-root</c> — an inner tree is its own scope, and an
    /// outer tree's rules must not reach into it either.
    /// </summary>
    private void StampShadowTreeScope(DomElement shadowRoot, string token)
    {
        foreach (var child in ChildElements(shadowRoot))
        {
            if (IsText(child) || child.TagName.StartsWith('#'))
                continue;

            SetAttr(child, ShadowScopeAttr, token);
            StampShadowTreeScope(child, token);
        }
    }
}

/// <summary>
/// Render-time scoping of <c>:host</c> selectors (CSS Scoping 1 §3.1).
/// <para>
/// A shadow root's <c>&lt;style&gt;</c> is serialized inline into the render document, so the
/// renderer sees its rules as ordinary global rules. The renderer's selector matcher lists
/// <c>host</c>/<c>host-context</c> as recognised-but-unmodelled pseudo-classes, which are
/// deliberately lenient and therefore match <em>every</em> element — so a single
/// <c>:host { background: red }</c> in any shadow tree painted the whole document red (the
/// <c>css/css-shadow</c> reftest family, e.g. <c>css-scoping-shadow-host-functional-rule</c>,
/// whose five hosts must all end up green).
/// </para>
/// <para>
/// The renderer cannot fix this by itself: it has no rule provenance, so it cannot tell which
/// shadow tree a rule came from — and because the rules are global, one host's
/// <c>:host(other-host)</c> rule would still match a <em>different</em> host. That knowledge
/// exists only here, where the owning <c>#shadow-root</c> is known, so rewrite each
/// <c>:host</c> selector to target exactly that root's host element:
/// </para>
/// <list type="bullet">
///   <item><c>:host</c> → <c>[data-broiler-shadow-host="N"]</c></item>
///   <item><c>:host(&lt;compound&gt;)</c> → <c>&lt;compound&gt;[data-broiler-shadow-host="N"]</c>,
///         so the host must match the compound <em>and</em> be this root's host</item>
///   <item><c>:host(&lt;complex&gt;)</c> (an argument containing a combinator, e.g.
///         <c>:host(div host-3)</c>) is an invalid selector per the grammar
///         (<c>&lt;compound-selector&gt;</c> only) and must match nothing</item>
///   <item><c>:host-context(...)</c> is not modelled; it is neutralised to a never-matching
///         selector rather than left to match everything</item>
/// </list>
/// <para>
/// Matching the compound itself is delegated to the renderer, which already supports type,
/// class, id and attribute selectors — so <c>:host(host-2.foo#bar[name=baz])</c> needs no
/// selector engine here. Only the render-bound serialization is affected; the live CSSOM and
/// JS-visible <c>innerHTML</c> still expose the author's <c>:host</c> text.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Rewrites <c>:host</c> selectors in every shadow root's <c>&lt;style&gt;</c> so they
    /// target that root's host element instead of matching every element. A no-op for a
    /// document with no shadow roots, and for a shadow tree whose styles use no <c>:host</c>.
    /// </summary>
    private void ScopeShadowHostSelectors(DomElement root)
    {
        var index = 0;

        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            var host = ParentEl(element);
            if (host == null)
                continue;

            var token = index.ToString(CultureInfo.InvariantCulture);
            index++;

            var styles = new List<DomElement>();
            CollectStylesInShadowRoot(element, styles);

            var stamped = false;
            foreach (var style in styles)
            {
                var original = GetStyleElementCssText(style);
                var rewritten = RewriteHostSelectors(original, token);
                if (string.Equals(rewritten, original, StringComparison.Ordinal))
                    continue;

                if (!stamped)
                {
                    SetAttr(host, ShadowHostAttr, token);
                    stamped = true;
                }

                SetElementTextContent(style, rewritten);
            }
        }
    }

    /// <summary>
    /// Collects the <c>&lt;style&gt;</c> elements belonging to <paramref name="shadowRoot"/>,
    /// without descending into a nested <c>#shadow-root</c> (whose styles are scoped to their
    /// own host by that root's own pass).
    /// </summary>
    private void CollectStylesInShadowRoot(DomElement shadowRoot, List<DomElement> styles)
    {
        foreach (var child in ChildElements(shadowRoot))
        {
            if (IsText(child))
                continue;

            if (string.Equals(child.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            if (child.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                styles.Add(child);

            CollectStylesInShadowRoot(child, styles);
        }
    }
}

/// <summary>
/// Render-time projection of <c>::part()</c> selectors (CSS Shadow Parts 1).
/// <para>
/// A shadow tree is serialized into the render document as ordinary descendants of its host, and
/// the renderer's selector matcher does not model <c>::part</c> — so an outer
/// <c>::part(p2) { background: yellow; width: 100px; height: 100px }</c> reached nothing and the
/// part rendered with no author styling at all. That is why WPT
/// <c>css/css-view-transitions/auto-name-from-id-shadow</c> (issue #1544 problem 11, 0.6% match)
/// showed only its green light-DOM item: the yellow one is a shadow part with no size and no
/// colour of its own.
/// </para>
/// <para>
/// Each <c>::part()</c> rule gets a renderer-readable twin, injected as one extra bridge sheet:
/// </para>
/// <list type="bullet">
///   <item>every element inside a shadow root that carries a <c>part</c> attribute is stamped
///         <c>data-broiler-part="&lt;its part list&gt;"</c>, so only a real shadow part is
///         addressable — a <c>part</c> attribute on a light-DOM element is inert and must stay so;</item>
///   <item><c>&lt;prefix&gt;::part(a b)</c> is re-emitted as
///         <c>&lt;prefix&gt; [data-broiler-part~="a"][data-broiler-part~="b"]</c>, every ident in
///         the argument being required per the spec's <c>&lt;ident&gt;+</c> grammar;</item>
///   <item>a bare <c>::part(a)</c>, which Chromium accepts as <c>*::part(a)</c>, becomes just the
///         attribute compound.</item>
/// </list>
/// <para>
/// <b>Why a twin sheet rather than rewriting the author's rule in place.</b> The author's
/// <c>::part(</c> text is load-bearing elsewhere: <c>AppendOuterPartRules</c> scans for it to lift
/// those rules into a shadow tree's computed-style scope, which is how <c>getComputedStyle</c> — and
/// so a view transition looking for <c>view-transition-name</c> — sees them at all. Rewriting the
/// text away silently broke that path (<c>Outer_Part_Rules_Reach_Into_The_Shadow_Tree</c> caught it).
/// Adding a sheet leaves every author selector exactly as written.
/// </para>
/// <para>
/// <b>Two deliberate approximations.</b> The descendant combinator is looser than the spec's "part
/// of the shadow tree whose host is the originating element": it also reaches parts of a
/// <em>nested</em> shadow tree, which really require <c>exportparts</c> to be addressable. Encoding
/// the host relationship needs selector-engine support that does not exist here. And the twin's
/// specificity is that of its attribute compound, where <c>::part()</c> is specced as a
/// pseudo-element; the twins are injected after the author sheets, so between themselves their
/// author order still decides ties.
/// </para>
/// <para>
/// Only the render-bound serialization is affected; the live CSSOM and JS-visible
/// <c>innerHTML</c> still expose the author's <c>::part</c> text.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Stamps every shadow part in the tree and injects a renderer-readable twin of each
    /// <c>::part()</c> rule. A no-op for a document with no <c>::part</c> rules.
    /// </summary>
    private void RewriteShadowPartSelectors(DomElement root)
    {
        var styles = new List<DomElement>();
        CollectStyleElementsForParts(root, styles);
        if (styles.Count == 0)
            return;

        var rules = new List<string>();
        foreach (var style in styles)
            CollectPartRuleTwins(GetStyleElementCssText(style), rules, ShadowPartAttr, prefixlessOnly: false);

        if (rules.Count == 0)
            return;

        foreach (var shadowRoot in root.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(shadowRoot.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            foreach (var element in shadowRoot.Descendants().OfType<DomElement>())
            {
                if (TryGetAttribute(element, "part", out var part) && !string.IsNullOrWhiteSpace(part))
                    SetAttr(element, ShadowPartAttr, part);
            }
        }

        InjectBridgeStyleRules(root, rules);
    }

    /// <summary>Collects every <c>&lt;style&gt;</c> in the document, shadow trees included — a
    /// shadow tree's own styles may address the parts of a tree nested inside it.</summary>
    private void CollectStyleElementsForParts(DomElement element, List<DomElement> styles)
    {
        foreach (var child in ChildElements(element))
        {
            if (IsText(child))
                continue;

            if (child.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                styles.Add(child);

            CollectStyleElementsForParts(child, styles);
        }
    }
}
