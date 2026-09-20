using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// DOM traversal APIs — <c>TreeWalker</c>, <c>NodeIterator</c>,
/// <c>Range</c>, and the node-filter machinery.
/// </summary>
public sealed partial class DomBridge
{
    // -------- TreeWalker, NodeIterator, Range builders (extracted) --------
    // The TreeWalker/NodeIterator/Range construction and every Range callback live in the co-located
    // Broiler.HtmlBridge.Dom.Features.TraversalBinding feature module, which is migrated to JSEAL.
    //
    // Every builder call site — createRange/createTreeWalker/createNodeIterator on the main document
    // and on a sub-document — goes to the module; DomBridge/Hosts.Documents.cs asks _traversal for a
    // handle directly.

    // Call sites use the canonical Broiler.Dom.DomNode.CommonAncestorWith(b), which is
    // null-tolerant and returns null for nodes in different trees — matching the deleted helper.

    /// <summary>
    /// A CSSOM-View <c>DOMRect</c>-shaped object over a used-value rectangle, for
    /// <c>Range.getBoundingClientRect</c> and <c>Range.getClientRects</c>.
    /// </summary>
    /// <remarks>
    /// Eight data properties, enumerable, configurable and writable — the shape this has always
    /// installed. A real <c>DOMRect</c> carries them on its prototype as accessors; that is a
    /// separate change from this migration, and making it here would alter what
    /// <c>Object.getOwnPropertyNames</c> of a rect answers.
    /// </remarks>
    private JsValue CreateDomRectObject((double Left, double Top, double Width, double Height) rectData)
    {
        var rect = Realm.NewObject();
        Realm.DefineValue(rect, "x", JsValue.Number(rectData.Left));
        Realm.DefineValue(rect, "y", JsValue.Number(rectData.Top));
        Realm.DefineValue(rect, "top", JsValue.Number(rectData.Top));
        Realm.DefineValue(rect, "left", JsValue.Number(rectData.Left));
        Realm.DefineValue(rect, "right", JsValue.Number(rectData.Left + rectData.Width));
        Realm.DefineValue(rect, "bottom", JsValue.Number(rectData.Top + rectData.Height));
        Realm.DefineValue(rect, "width", JsValue.Number(rectData.Width));
        Realm.DefineValue(rect, "height", JsValue.Number(rectData.Height));
        return rect;
    }

    private List<(double Left, double Top, double Width, double Height)> GetClientRectsForRange(DomRange state)
    {
        var rects = new List<(double Left, double Top, double Width, double Height)>();
        if (state.Collapsed)
            return rects;

        foreach (var node in GetNodesInRange(state.StartContainer, state.StartOffset, state.EndContainer, state.EndOffset))
            CollectClientRectsForRangeNode(node, rects);

        return rects;
    }

    private void CollectClientRectsForRangeNode(DomNode node, List<(double Left, double Top, double Width, double Height)> rects)
    {
        // Character-data nodes contribute no client rect here (their text runs are measured
        // elsewhere); after this guard the node is an element.
        if (IsText(node) || IsComment(node) || node is not DomElement element)
            return;

        var display = GetComputedProps(element).GetValueOrDefault("display");
        if (string.Equals(display, "contents", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var child in ChildElements(element))
                CollectClientRectsForRangeNode(child, rects);

            return;
        }

        var rect = GetBoundingClientRectForDomElement(element, isRoot: false);
        if (rect.Width > 0 || rect.Height > 0)
            rects.Add(rect);
    }

    // The canonical DomCharacterData.Data setter publishes a CharacterData record to
    // DomDocument.Mutated (with its own value-changed guard), so the observer subscription delivers
    // it.
    private void SetCharacterData(DomNode target, string? newValue) => UpdateCharacterData(target, newValue);
}

/// <summary>
/// Compatibility entry points over the shared canonical-DOM selector matcher.
/// </summary>
public sealed partial class DomBridge
{
    // Per-bridge selector matcher: the `:checked` state
    // provider reads the per-bridge FormControl table, so the matcher (and MatchesSelector) is now an
    // instance owned by the bridge — was a process-static shared matcher over the static runtime table.
    // Initialized in the constructor (a field initializer cannot capture `this`).
    private readonly CssSelectorMatcher _selectorMatcher;

    internal bool MatchesSelector(
        DomElement element,
        string selector,
        DomElement? scope = null) =>
        _selectorMatcher.Matches(element, selector, scope);

    private sealed class BridgeSelectorStateProvider(DomBridge bridge) : ICssSelectorStateProvider
    {
        public bool? IsChecked(DomElement element)
        {
            if (element is not DomElement bridgeElement)
                return null;

            return bridge._formState.TryGetDirtyChecked(bridgeElement, out var value)
                ? value
                : null;
        }
    }
}

/// <summary>
/// Sibling partial of <c>SubDocuments.cs</c>: the generic HTML-fragment DOM-mutation helpers that
/// are not sub-document-specific. Covers per-node cache teardown
/// (<see cref="RemoveElementsRecursive"/>), <c>normalize()</c> text coalescing, indexed child
/// removal/insertion, and the <c>innerHTML</c> / <c>outerHTML</c> / <c>insertAdjacentHTML</c> /
/// child-node argument fragment builders.
/// </summary>
public sealed partial class DomBridge
{
    // Unregister the whole node subtree from the bridge's per-node caches when a sub-document root
    // is torn down (raw ChildNodes so canonical text/comment nodes are released too). There is no
    // register-on-build counterpart: node membership is read from the canonical tree.
    private void RemoveElementsRecursive(DomNode node)
    {
        _jsObjects.Remove(node);

        if (node is DomElement element)
            _styleSheetCache.Remove(element);

        foreach (var child in node.ChildNodes)
            RemoveElementsRecursive(child);
    }

    private void NormalizeNode(DomElement node)
    {
        // Text-node coalescing is delegated to canonical DomNode.Normalize(). Its internal
        // RemoveChild / Data-set operations publish canonical DomDocument.Mutated records, which drive
        // the bridge's MutationObserver / Range / NodeIterator (mutation-consolidation steps 1-2), so
        // normalize's removals adjust ranges/iterators and deliver observer records. The one
        // bridge-only side effect canonical has no equivalent for is the style-scope invalidation,
        // applied once for the normalized subtree.
        node.Normalize();
        InvalidateStyleScope(node);
    }

    // The isEqualNode binding delegates to canonical Broiler.Dom.DomNode.IsEqualNode(other). That
    // algorithm drops the bridge copy's element-level BridgeText comparison, which is a no-op on the
    // canonical tree (an element's NodeValue is null) — so it is behaviour-equivalent. NOTE: this
    // repo has no test pinning that equivalence.

    // The insertion parent is DomNode, not DomElement, so a canonical DomDocumentFragment can be
    // an insertion parent (fragment.appendChild/append/...). The style-scope invalidation is an
    // element-only concern, guarded accordingly; the onload firing below already guards on
    // `node is DomElement`. Behaviour for element parents is identical.
    private void InsertNodeAt(DomNode parent, DomNode node, int index)
    {
        if (ReferenceEquals(node, parent) || parent.IsDescendantOf(node))
            ThrowDOMException(Realm, "The new child element contains the parent.", "HierarchyRequestError");

        if (index < 0)
            index = 0;
        if (index > parent.ChildNodes.Count)
            index = parent.ChildNodes.Count;

        if (ParentEl(node) != null)
        {
            var oldParent = ParentEl(node);
            var oldIndex = ChildIndexOf(oldParent, node);
            if (oldIndex >= 0)
            {
                if (ReferenceEquals(oldParent, parent) && oldIndex < index)
                    index--;

                RemoveNthChild(oldParent, oldIndex);
            }
        }

        // A single canonical insert. The prior
        // SetParent(node, parent) appended node at the end first, so the InsertChildAt below then
        // re-moved it to `index` — firing spurious canonical add-at-end + remove records that the
        // canonical NodeIterator/CSS mutation subscribers observe. The move-block above already
        // detached node from any old parent, so InsertChildAt alone lands it at `index` with one
        // canonical ChildList(added) record.
        InsertChildAt(parent, index, node);
        if (parent is DomElement parentElement)
            InvalidateStyleScope(parentElement);

        // Only elements carry a TagName / fire onloads; a
        // canonical char-data node inserts with no sub-document side effects.
        if (node is DomElement insertedElement)
        {
            var insertedTag = insertedElement.TagName?.ToLowerInvariant();
            if (IsNestedBrowsingContextContainer(insertedTag))
                FireSubDocumentOnload(insertedElement);
            else
                FireDescendantOnloads(insertedElement);

            // A <link rel=stylesheet> only fetches once it is in the page's document, so insertion — not
            // createElement — is when its load event becomes due (HTML §4.2.4). The page's, not any
            // document's: FireStylesheetLinkLoad says why that is not isConnected.
            FireDescendantStylesheetLinkLoads(insertedElement);

            // An already-checked radio joining a group is the other way the "at most one checked"
            // invariant breaks — the property setter's own exclusivity walk never runs, because the
            // element was checked while it was still detached and in a group of one. Left alone, the
            // group held two checked members: a state no interaction can produce, and one that
            // submits two values for a single field.
            EnforceRadioGroupExclusivityForInsertion(insertedElement);
        }
    }

    /// <summary>
    /// The state-preserving reposition behind <c>Node.moveBefore()</c>: relocates
    /// <paramref name="node"/> under <paramref name="parent"/> before
    /// <paramref name="reference"/> without the disconnection <see cref="InsertNodeAt"/> performs.
    /// <para>
    /// The difference from <see cref="InsertNodeAt"/> is deliberate and is the entire point of the
    /// operation: no sub-document onload is fired, so a moved <c>&lt;iframe&gt;</c> does not
    /// reload.
    /// </para>
    /// <para>
    /// The move itself — including the pre-move validity steps and the pair of observer records —
    /// belongs to the canonical DOM and is delegated to <c>DomNode.MoveBefore</c>. What is left
    /// here is the bridge's own concern: marshalling the canonical <see cref="DomException"/> into
    /// a JavaScript <c>DOMException</c>, and invalidating the style scopes the reposition
    /// dirtied.
    /// </para>
    /// <para>
    /// WPT issue #1491 problem 27
    /// (<c>dom/nodes/moveBefore/preserve-render-blocking-style.html</c>).
    /// </para>
    /// </summary>
    private void MoveNodeBefore(DomNode parent, DomNode node, DomNode? reference)
    {
        // Read before the move, which is when it stops being the *old* parent.
        var oldParent = node.ParentNode;

        try
        {
            parent.MoveBefore(node, reference);
        }
        catch (DomException ex)
        {
            ThrowDOMException(Realm, ex.Message, ex.Name);
        }

        if (oldParent is DomElement oldParentElement && !ReferenceEquals(oldParent, parent))
            InvalidateStyleScope(oldParentElement);

        if (parent is DomElement parentElement)
            InvalidateStyleScope(parentElement);
    }

    private List<DomNode> BuildAdjacentHtmlNodes(DomElement contextElement, string html)
    {
        var nodes = new List<DomNode>();
        if (string.IsNullOrEmpty(html))
            return nodes;

        if (!TryBuildInnerHtmlFragmentContainer(contextElement, html, out var fragmentContainer))
            return nodes;

        // Move ALL children (raw ChildNodes) so text/comment
        // nodes in the parsed fragment survive. The detached nodes returned still belong to the
        // parser's private document until the caller inserts them, which adopts them.
        foreach (var child in fragmentContainer.ChildNodes.ToArray())
        {
            RemoveChildFrom(fragmentContainer, child);
            SetParent(child, null);
            nodes.Add(child);
        }

        return nodes;
    }

    private void SetElementInnerHtml(DomElement element, string html)
    {
        html ??= string.Empty;

        // A <template>'s children are its contents fragment, not its own child list (HTML §4.12.3),
        // so innerHTML writes the fragment. Writing the element instead left `content` holding the
        // markup from before the assignment, so a page that built a template dynamically and then
        // stamped it got the OLD markup with nothing to indicate the write had gone elsewhere.
        // The parsing context stays the element — the fragment has no tag to parse `<td>` against.
        DomNode target = IsTemplateElement(element) ? GetTemplateContent(element) : element;

        foreach (var child in target.ChildNodes.ToArray())
            RemoveElementsRecursive(child);

        ClearChildren(target);

        if (!string.IsNullOrEmpty(html) &&
            TryBuildInnerHtmlFragmentContainer(element, html, out var fragmentContainer))
        {
            // Move ALL children so parsed text/comment survive.
            foreach (var child in fragmentContainer.ChildNodes.ToArray())
            {
                // Single canonical move: AppendChild removes the child from the parsed fragment and
                // appends it to the target in one op. The prior SetParent(child, element) did the
                // same move, leaving the following AppendChild a no-op — redundant, not wrong.
                target.AppendChild(child);
            }

            // A template written into may itself contain templates, and those are the parser's to
            // divert exactly as the document's were.
            if (!ReferenceEquals(target, element))
                DivertTemplateContents(target);
        }

        ResetComputedStyleEngines();
        InvalidateStyleScope(element);
    }

    private void SetShadowRootInnerHtml(DomShadowRoot shadowRoot, string html)
    {
        html ??= string.Empty;

        foreach (var child in shadowRoot.ChildNodes.ToArray())
            RemoveElementsRecursive(child);

        ClearChildren(shadowRoot);

        var contextElement = shadowRoot.Host;
        if (!string.IsNullOrEmpty(html) &&
            TryBuildInnerHtmlFragmentContainer(contextElement, html, out var fragmentContainer))
        {
            foreach (var child in fragmentContainer.ChildNodes.ToArray())
                shadowRoot.AppendChild(child);

            DivertTemplateContents(shadowRoot);
        }

        ResetComputedStyleEngines();
        if (contextElement != null)
            InvalidateStyleScope(contextElement);
    }

    private void SetElementOuterHtml(DomElement element, string html)
    {
        html ??= string.Empty;

        var parent = ParentEl(element);
        if (parent == null)
            return;

        var index = ChildIndexOf(parent, element);
        if (index < 0)
            return;

        DomDocumentFragment? parsedContainer = null;
        if (!string.IsNullOrEmpty(html))
        {
            var parsingContext = parent.TagName.StartsWith('#')
                ? CreateBridgeElement("body")
                : parent;
            if (TryBuildInnerHtmlFragmentContainer(parsingContext, html, out var fragmentContainer))
                parsedContainer = fragmentContainer;
        }

        RemoveNthChild(parent, index);
        SetParent(element, null);

        if (parsedContainer != null)
        {
            var insertIndex = index;
            foreach (var child in parsedContainer.ChildNodes.ToArray())
            {
                // Single canonical insert (InsertChildAt moves child out of the parsed fragment and
                // into parent at insertIndex); the prior SetParent-append + reposition fired spurious
                // records.
                InsertChildAt(parent, insertIndex, child);
                insertIndex++;
            }
        }

        ResetComputedStyleEngines();
        InvalidateStyleScope(parent);
    }

    private static bool TryBuildInnerHtmlFragmentContainer(DomElement contextElement, string html, out DomDocumentFragment container) =>
        HtmlFragmentParsing.TryBuildFragment(contextElement, html, out container!);

}

/// <summary>
/// Custom element reactions from the canonical mutation stream: every document this bridge owns is
/// subscribed, and each child-list, attribute and adoption record is handed to the registry.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Turns the canonical mutation stream into reaction callbacks.
    /// </summary>
    /// <remarks>
    /// <c>DomDocument.Mutated</c> is raised synchronously at mutation time, which is what this needs:
    /// a browser runs <c>connectedCallback</c> before the statement after the <c>appendChild</c> that
    /// caused it. Building reactions on <c>MutationObserver</c> instead — the obvious reuse, since it
    /// already subscribes here — would have delivered every one of them a microtask late, and a
    /// component that reads its own DOM straight after inserting itself would have seen nothing.
    /// </remarks>
    private void SubscribeCustomElementReactions()
    {
        if (_customElementReactionsSubscribed)
            return;

        _customElementReactionsSubscribed = true;
        _document.Mutated += OnCustomElementRelevantMutation;
        foreach (var other in _browsingContextDocuments)
            other.Mutated += OnCustomElementRelevantMutation;
    }

    /// <summary>
    /// Subscribes a document this bridge minted for a detached browsing context, so a node adopted
    /// <em>into</em> it is heard.
    /// </summary>
    /// <remarks>
    /// Adoption publishes its record on the document the node is moving to, not the one it is leaving
    /// — so listening to the main document alone hears an adoption into the page and misses the
    /// symmetric one out of it. There is one registry across every document this bridge owns, so the
    /// same handler serves them all.
    /// </remarks>
    private void SubscribeBrowsingContextDocument(DomDocument document)
    {
        if (!_browsingContextDocuments.Add(document) || !_customElementReactionsSubscribed)
            return;

        document.Mutated += OnCustomElementRelevantMutation;
    }

    private readonly HashSet<DomDocument> _browsingContextDocuments = [];

    private bool _customElementReactionsSubscribed;

    private void OnCustomElementRelevantMutation(DomMutationRecord record)
    {
        if (_customElements is not { } registry)
            return;

        if (record.Type == DomMutationType.Adoption)
        {
            registry.OnAdoption(record.Target, DocumentValue(record.OldDocument), DocumentValue(record.NewDocument));
            return;
        }

        if (record.Type == DomMutationType.ChildList)
        {
            // Both lists are optional on the record, and a childList record normally carries only
            // one of them.
            registry.OnChildListMutation(
                record.AddedNodes ?? [],
                record.RemovedNodes ?? []);
            // A move changes a form-associated custom element's owner and can change its disabled
            // state (an ancestor fieldset), and both are computed from the tree rather than stored.
            registry.SyncFormState();
            return;
        }

        if (record.Type == DomMutationType.Attributes && record.Target is DomElement element &&
            record.AttributeName is { } attributeName)
        {
            registry.OnAttributeMutation(element, attributeName, record.OldValue);
            // `disabled`, `form` and `id` each move a form-associated custom element between states;
            // the sweep costs one pass over the elements a page actually upgraded.
            registry.SyncFormState();
        }
    }

    /// <summary>The JS object for a document node — the window's <c>document</c> for the page, its own
    /// wrapper for a document this bridge minted, and <c>null</c> for one that has neither.</summary>
    /// <remarks>
    /// Nothing here converts: the page's document is the bridge's document root and a minted
    /// document's wrapper is the registry's entry, both handles already held, so
    /// <c>evt.oldDocument === frameDoc</c> is preserved. The <c>null</c> for the page's own
    /// document before one is registered is kept deliberately: the root holds
    /// <see cref="JsValue.Missing"/> then, and this member answers a document or <c>null</c>, which is
    /// what a page can read.
    /// </remarks>
    private JsValue DocumentValue(DomDocument? document)
    {
        if (document is null)
            return JsValue.Null;

        if (ReferenceEquals(document, _document))
            return DocumentHandle.IsMissing ? JsValue.Null : DocumentHandle;

        return _jsObjects.TryGetDocument(document, out var wrapper) ? wrapper : JsValue.Null;
    }
}
