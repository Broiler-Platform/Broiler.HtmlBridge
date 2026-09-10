using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM traversal / Range feature binding — <c>TreeWalker</c>, <c>NodeIterator</c>,
/// <c>Range</c>, the node-filter machinery and <c>document.createComment</c>. This is the first
/// co-located feature module of the HtmlBridge complexity-reduction roadmap Phase 3: the
/// registration for the feature and every handler that implements it now live together in one file
/// with semantic names, reachable and testable without loading the whole <c>DomBridge</c>
/// implementation. The module owns the traversal-scoped state (the weak range and selection
/// registries) and depends only on the narrow <see cref="ITraversalHost"/> contract plus the
/// assembly's neutral static DOM-tree helpers on <c>DomBridge</c> (which Phase 4 promotes to
/// <c>Broiler.Dom</c>).
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): objects and functions are minted
/// through the realm, an argument frame arrives as a <see cref="JsCall"/>, and a <c>DOMException</c>
/// is raised with <see cref="IJsCalls.DomError"/> rather than by hand-constructing one against the
/// context's <c>DOMException</c> global — which is what the provider now does on this module's
/// behalf, with the identical fallback.
/// </remarks>
internal sealed partial class TraversalBinding(ITraversalHost host)
{
    private readonly ITraversalHost _host = host;

    // -------- Registration --------

    /// <summary>
    /// Installs the traversal surface on a document object: the <c>NodeFilter</c> constants plus
    /// <c>createTreeWalker</c>, <c>createNodeIterator</c>, <c>createRange</c> and
    /// <c>createComment</c>.
    /// </summary>
    internal void RegisterDocumentApis(JsValue document)
    {
        var realm = _host.Realm;

        // NodeFilter constants
        var nodeFilter = realm.NewObject();
        realm.DefineValue(nodeFilter, "FILTER_ACCEPT", JsValue.Number(1));
        realm.DefineValue(nodeFilter, "FILTER_REJECT", JsValue.Number(2));
        realm.DefineValue(nodeFilter, "FILTER_SKIP", JsValue.Number(3));
        realm.DefineValue(nodeFilter, "SHOW_ALL", JsValue.Number(0xFFFFFFFF));
        realm.DefineValue(nodeFilter, "SHOW_ELEMENT", JsValue.Number(0x1));
        realm.DefineValue(nodeFilter, "SHOW_ATTRIBUTE", JsValue.Number(0x2));
        realm.DefineValue(nodeFilter, "SHOW_TEXT", JsValue.Number(0x4));
        realm.DefineValue(nodeFilter, "SHOW_CDATA_SECTION", JsValue.Number(0x8));
        realm.DefineValue(nodeFilter, "SHOW_ENTITY_REFERENCE", JsValue.Number(0x10));
        realm.DefineValue(nodeFilter, "SHOW_ENTITY", JsValue.Number(0x20));
        realm.DefineValue(nodeFilter, "SHOW_PROCESSING_INSTRUCTION", JsValue.Number(0x40));
        realm.DefineValue(nodeFilter, "SHOW_COMMENT", JsValue.Number(0x80));
        realm.DefineValue(nodeFilter, "SHOW_DOCUMENT", JsValue.Number(0x100));
        realm.DefineValue(nodeFilter, "SHOW_DOCUMENT_TYPE", JsValue.Number(0x200));
        realm.DefineValue(nodeFilter, "SHOW_DOCUMENT_FRAGMENT", JsValue.Number(0x400));
        realm.DefineValue(nodeFilter, "SHOW_NOTATION", JsValue.Number(0x800));
        realm.SetProperty(realm.Global, "NodeFilter", nodeFilter);

        // document.createTreeWalker(root, whatToShow, filter)
        realm.DefineValue(document, "createTreeWalker", realm.NewMethod("createTreeWalker", CreateTreeWalker, 3));
        // document.createNodeIterator(root, whatToShow, filter)
        realm.DefineValue(document, "createNodeIterator", realm.NewMethod("createNodeIterator", CreateNodeIterator, 3));
        // document.createRange()
        realm.DefineValue(document, "createRange", realm.NewMethod("createRange", (in _) => BuildRange(), 0));
        // document.createComment(data)
        realm.DefineValue(document, "createComment", realm.NewMethod("createComment", CreateComment, 1));

        // window.getSelection() and document.getSelection(), which answer the same object — the
        // window is the global here, so one function installed in both places is what a browser has.
        // The Selection interface itself is registered with the other DOM interface constructors;
        // this is only reachable from page script, which runs after that.
        var getSelection = realm.NewMethod("getSelection", (in _) => SelectionObject(), 0);
        realm.DefineValue(document, "getSelection", getSelection);
        realm.SetProperty(realm.Global, "getSelection", getSelection);
    }

    private JsValue CreateTreeWalker(in JsCall call)
    {
        // A plain Error, not a TypeError: it is what this site has always thrown, and the arity and
        // the type failure are reported the same way for the same reason.
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createTreeWalker': 1 argument required.");
        if (!call[0].IsObject)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createTreeWalker': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindElement(call[0]);
        if (rootEl == null)
            return JsValue.Null;
        var whatToShow = WhatToShowArgument(in call);
        return BuildTreeWalker(rootEl, whatToShow, FilterArgument(in call));
    }

    private JsValue CreateNodeIterator(in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createNodeIterator': 1 argument required.");
        if (!call[0].IsObject)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createNodeIterator': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindElement(call[0]);
        if (rootEl == null)
            return JsValue.Null;
        var whatToShow = WhatToShowArgument(in call);
        return BuildNodeIterator(rootEl, whatToShow, FilterArgument(in call));
    }

    /// <summary>
    /// The <c>whatToShow</c> bitmask: absent, <c>null</c> or <c>undefined</c> means SHOW_ALL. The
    /// conversion is the realm's <c>ToNumber</c>, because a page may pass the mask as a string and
    /// the engine's own numeric view of an argument — which this site read directly before — is that
    /// coercion.
    /// </summary>
    private static int WhatToShowArgument(in JsCall call) =>
        call.Length > 1 && !call[1].IsNullish
            ? unchecked((int)(uint)call.Realm.ToNumber(call[1]))
            : unchecked((int)0xFFFFFFFF);

    /// <summary>
    /// The <c>NodeFilter</c>: a bare function, or the <c>acceptNode</c> of a filter object, or
    /// nothing.
    /// </summary>
    private static JsValue FilterArgument(in JsCall call)
    {
        if (call.Length <= 2)
            return JsValue.Missing;
        if (call[2].IsFunction)
            return call[2];

        return call[2].IsObject ? call.Realm.GetProperty(call[2], "acceptNode") : JsValue.Missing;
    }

    private JsValue CreateComment(in JsCall call)
    {
        // ToJsString, not the handle's rendering: an object argument runs its own toString here, and
        // that is the coercion a page observes.
        var data = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        return _host.CreateCommentNode(data);
    }

    // -------- TreeWalker / NodeIterator / Range builders --------

    /// <summary>
    /// Returns <c>1</c> (ACCEPT), <c>2</c> (REJECT) or <c>3</c> (SKIP) for <paramref name="el"/>
    /// against the <paramref name="whatToShow"/> bitmask and the optional
    /// <paramref name="filterFn"/>.
    /// </summary>
    private int ApplyFilter(DomNode el, int whatToShow, JsValue filterFn)
    {
        var nodeType = (int)el.NodeType;
        var showBit = nodeType switch
        {
            1 => 0x1,    // SHOW_ELEMENT
            3 => 0x4,    // SHOW_TEXT
            8 => 0x80,   // SHOW_COMMENT
            9 => 0x100,  // SHOW_DOCUMENT
            11 => 0x400, // SHOW_DOCUMENT_FRAGMENT
            _ => 0x0
        };
        if ((whatToShow & showBit) == 0) return 3; // FILTER_SKIP

        if (filterFn.IsFunction)
        {
            // Per DOM Level 2 Traversal spec, exceptions thrown by NodeFilter
            // callbacks must propagate to the caller — they must NOT be swallowed.
            // The filter is its own receiver, as it was when the engine was invoked directly.
            var realm = _host.Realm;
            var result = realm.Invoke(filterFn, filterFn, [_host.WrapNode(el)]);
            // Handle boolean return: true → 1 (ACCEPT), false → 2 (REJECT)
            if (result.IsBoolean)
                return result.AsBoolean ? 1 : 2;
            return (int)realm.ToNumber(result);
        }
        return 1; // FILTER_ACCEPT
    }

    /// <summary>Builds a DOM <c>TreeWalker</c> object.</summary>
    internal JsValue BuildTreeWalker(DomElement root, int whatToShow, JsValue filterFn)
    {
        var realm = _host.Realm;
        var tw = realm.NewObject();
        var walker = new DomTreeWalker(root,
            (DomWhatToShow)(uint)whatToShow,
            node => (DomFilterResult)ApplyFilter(node, whatToShow, filterFn));

        realm.DefineValue(tw, "root", _host.WrapNode(root));

        realm.DefineAccessor(tw, "currentNode",
            (in _) => _host.WrapNode(walker.CurrentNode),
            (in call) =>
            {
                if (call.Length > 0 && call[0].IsObject &&
                    _host.FindNode(call[0]) is { } node)
                {
                    walker.CurrentNode = node;
                }
                return JsValue.Undefined;
            });

        realm.DefineValue(tw, "whatToShow", JsValue.Number(whatToShow));

        realm.DefineValue(tw, "parentNode",
            realm.NewMethod("parentNode", (in _) => ToTraversalJsValue(walker.ParentNode())));
        realm.DefineValue(tw, "firstChild",
            realm.NewMethod("firstChild", (in _) => ToTraversalJsValue(walker.FirstChild())));
        realm.DefineValue(tw, "lastChild",
            realm.NewMethod("lastChild", (in _) => ToTraversalJsValue(walker.LastChild())));
        realm.DefineValue(tw, "nextSibling",
            realm.NewMethod("nextSibling", (in _) => ToTraversalJsValue(walker.NextSibling())));
        realm.DefineValue(tw, "previousSibling",
            realm.NewMethod("previousSibling", (in _) => ToTraversalJsValue(walker.PreviousSibling())));
        // nextNode() — depth-first pre-order traversal forward
        realm.DefineValue(tw, "nextNode",
            realm.NewMethod("nextNode", (in _) => ToTraversalJsValue(walker.NextNode())));
        // previousNode() — depth-first pre-order traversal backward
        realm.DefineValue(tw, "previousNode",
            realm.NewMethod("previousNode", (in _) => ToTraversalJsValue(walker.PreviousNode())));

        return tw;
    }

    // RF-BRIDGE-1c Phase F (F3c part 2c): a TreeWalker/NodeIterator result may be a text/comment
    // node (SHOW_TEXT/SHOW_COMMENT), so convert any non-null node — not just elements — to its JS
    // wrapper.
    private JsValue ToTraversalJsValue(DomNode? node) => node is not null ? _host.WrapNode(node) : JsValue.Null;

    /// <summary>Builds a DOM <c>NodeIterator</c> object.</summary>
    internal JsValue BuildNodeIterator(DomElement root, int whatToShow, JsValue filterFn)
    {
        var realm = _host.Realm;
        var iter = realm.NewObject();
        // Canonical DomNodeIterator self-subscribes to root.OwnerDocument.Mutated and runs the DOM
        // §6.1 pre-removal reference-node adjustment itself, so the bridge keeps no registry.
        var iterator = new DomNodeIterator(root,
            (DomWhatToShow)(uint)whatToShow,
            node => (DomFilterResult)ApplyFilter(node, whatToShow, filterFn));

        realm.DefineValue(iter, "root", _host.WrapNode(root));

        realm.DefineValue(iter, "whatToShow", JsValue.Number(whatToShow));

        realm.DefineAccessor(iter, "referenceNode",
            (in _) => ToTraversalJsValue(iterator.ReferenceNode), null);

        realm.DefineAccessor(iter, "pointerBeforeReferenceNode",
            (in _) => JsValue.Boolean(iterator.PointerBeforeReferenceNode), null);

        realm.DefineValue(iter, "nextNode",
            realm.NewMethod("nextNode", (in _) => ToTraversalJsValue(iterator.NextNode())));
        realm.DefineValue(iter, "previousNode",
            realm.NewMethod("previousNode", (in _) => ToTraversalJsValue(iterator.PreviousNode())));
        realm.DefineValue(iter, "detach",
            realm.NewMethod("detach", (in _) =>
            {
                iterator.Dispose();
                return JsValue.Undefined;
            }));

        return iter;
    }

    /// <summary>
    /// Builds a DOM <c>Range</c> object. The <paramref name="documentRoot"/> is the document node
    /// that owns this range (main or sub-document); defaults to the main document root.
    /// </summary>
    /// <remarks>
    /// The object carries no members of its own: they live on <c>Range.prototype</c> and
    /// <c>AbstractRange.prototype</c>, and its boundaries are held in
    /// <see cref="_rangeStates"/> under the object itself — see
    /// <see cref="RegisterRangeInterface"/>. So <c>Object.getOwnPropertyNames</c> of a range is
    /// empty, as it is in a browser, and the 29 own properties this used to install are gone.
    /// </remarks>
    internal JsValue BuildRange(DomNode? documentRoot = null)
    {
        var realm = _host.Realm;
        var range = realm.NewObject();
        var docRoot = documentRoot ?? _host.DocumentNode;
        // The range self-subscribes to its document's DomDocument.Mutated (trackMutations) and runs
        // the DOM "removing steps" itself, so the bridge keeps no active-range registry.
        _rangeStates.Add(IdentityOf(range), new BridgeDomRange(_host, docRoot));

        // Before the interface is registered there is no prototype to point at, so the range is left
        // unlinked rather than failing. There is no such range on the normal path: createRange and
        // `new Range()` are both reachable only from page script, which runs after registration.
        if (_rangePrototype is { } prototype)
            realm.SetPrototype(range, prototype);

        return range;
    }

    /// <summary>
    /// The identity a weak per-object registry keys on: the reference the handle carries.
    /// </summary>
    /// <remarks>
    /// <b>This used to unwrap to the engine's own object, on the reasoning that a
    /// <see cref="JsValue"/> is a struct and so cannot be a
    /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/> key.</b> The
    /// struct is not the key; the reference it carries is, and
    /// <see cref="JsValue.ObjectIdentity"/> is that reference. It is the same instance this table
    /// was keyed on before, under the one provider that could reach it - so nothing about the
    /// answers changes - and it is now an instance every provider supplies. The keys stay weak, so a
    /// range or a selection the page has dropped is not kept alive by the registry, nor is its
    /// mutation subscription.
    /// </remarks>
    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    /// <summary>
    /// The bridge's live <c>Range</c> boundary store and content-operation engine — the canonical
    /// <see cref="DomRange"/> with the node-creation seams overridden so content operations mint
    /// bridge nodes through <see cref="ITraversalHost"/>: <c>#document-fragment</c> result
    /// fragments and clones that carry host runtime state, all registered so the host's
    /// <c>WrapNode</c> can wrap them. Constructed <c>trackMutations: true</c> — the range
    /// self-subscribes to its document's <see cref="DomDocument.Mutated"/> and runs the DOM
    /// "removing steps" (boundary adjustment) itself, uniformly with NodeIterator, now that the
    /// bridge no longer drives a separate notification channel.
    /// </summary>
    private sealed class BridgeDomRange(ITraversalHost host, DomNode root)
        : DomRange(root, trackMutations: true), IRangeBoundaries
    {
        protected override DomNode CreateResultFragment() => host.CreateRangeResultFragment();

        protected override DomNode CloneForRange(DomNode node, bool deep) => host.CloneRangeNode(node, deep);

        protected override DomText CreateTextForRange(string data) => host.CreateRangeTextNode(data);

        protected override DomRange CreateSubRange(DomNode root) => new BridgeDomRange(host, root);
    }
}
