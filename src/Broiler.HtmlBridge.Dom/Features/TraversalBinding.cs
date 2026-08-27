using System.Text;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM traversal / Range feature binding — <c>TreeWalker</c>, <c>NodeIterator</c>,
/// <c>Range</c>, the node-filter machinery and <c>document.createComment</c>. This is the first
/// co-located feature module of the HtmlBridge complexity-reduction roadmap Phase 3: the
/// registration for the feature and every handler that implements it now live together in one file
/// with semantic names, reachable and testable without loading the whole <c>DomBridge</c>
/// implementation. The module owns the traversal-scoped state (the weak active-range and
/// active-node-iterator registries) and depends only on the narrow <see cref="ITraversalHost"/>
/// contract plus the assembly's neutral static DOM-tree helpers on <c>DomBridge</c> (which Phase 4
/// promotes to <c>Broiler.Dom</c>).
/// </summary>
internal sealed partial class TraversalBinding(ITraversalHost host)
{
    private readonly ITraversalHost _host = host;

    // -------- Registration --------

    /// <summary>
    /// Installs the traversal surface on a document object: the <c>NodeFilter</c> constants plus
    /// <c>createTreeWalker</c>, <c>createNodeIterator</c>, <c>createRange</c> and
    /// <c>createComment</c>.
    /// </summary>
    internal void RegisterDocumentApis(JSContext context, JSObject document)
    {
        // NodeFilter constants
        var nodeFilter = new JSObject();
        nodeFilter.FastAddValue("FILTER_ACCEPT", new JSNumber(1), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("FILTER_REJECT", new JSNumber(2), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("FILTER_SKIP", new JSNumber(3), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_ALL", new JSNumber(0xFFFFFFFF), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_ELEMENT", new JSNumber(0x1), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_ATTRIBUTE", new JSNumber(0x2), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_TEXT", new JSNumber(0x4), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_CDATA_SECTION", new JSNumber(0x8), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_ENTITY_REFERENCE", new JSNumber(0x10), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_ENTITY", new JSNumber(0x20), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_PROCESSING_INSTRUCTION", new JSNumber(0x40), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_COMMENT", new JSNumber(0x80), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_DOCUMENT", new JSNumber(0x100), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_DOCUMENT_TYPE", new JSNumber(0x200), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_DOCUMENT_FRAGMENT", new JSNumber(0x400), JSPropertyAttributes.EnumerableConfigurableValue);
        nodeFilter.FastAddValue("SHOW_NOTATION", new JSNumber(0x800), JSPropertyAttributes.EnumerableConfigurableValue);
        context["NodeFilter"] = nodeFilter;

        // document.createTreeWalker(root, whatToShow, filter)
        document.FastAddValue(
            "createTreeWalker",
            new DomFunction((in a) => CreateTreeWalker(in a), "createTreeWalker", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);
        // document.createNodeIterator(root, whatToShow, filter)
        document.FastAddValue(
            "createNodeIterator",
            new DomFunction((in a) => CreateNodeIterator(in a), "createNodeIterator", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);
        // document.createRange()
        document.FastAddValue(
            "createRange",
            new DomFunction((in _) => BuildRange(), "createRange", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        // document.createComment(data)
        document.FastAddValue(
            "createComment",
            new DomFunction((in a) => CreateComment(in a), "createComment", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // window.getSelection() and document.getSelection(), which answer the same object — the
        // window is the global here, so one function installed in both places is what a browser has.
        // The Selection interface itself is registered with the other DOM interface constructors;
        // this is only reachable from page script, which runs after that.
        var getSelection = new DomFunction((in _) => GetSelection(), "getSelection", 0);
        document.FastAddValue(
            "getSelection", getSelection, JSPropertyAttributes.EnumerableConfigurableValue);
        context["getSelection"] = getSelection;
    }

    private JSValue CreateTreeWalker(in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'createTreeWalker': 1 argument required.");
        if (a[0] is not JSObject rootObj)
            throw new JSException("Failed to execute 'createTreeWalker': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindDomElementByJSObject(rootObj);
        if (rootEl == null)
            return JSNull.Value;
        var whatToShow = a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined ? unchecked((int)(uint)a[1].DoubleValue) : unchecked((int)0xFFFFFFFF);
        var filterFn = a.Length > 2 && a[2] is JSFunction f ? f : (a.Length > 2 && a[2] is JSObject filterObj ? filterObj[(KeyString)"acceptNode"] as JSFunction : null);
        return BuildTreeWalker(rootEl, whatToShow, filterFn);
    }

    private JSValue CreateNodeIterator(in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'createNodeIterator': 1 argument required.");
        if (a[0] is not JSObject rootObj)
            throw new JSException("Failed to execute 'createNodeIterator': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindDomElementByJSObject(rootObj);
        if (rootEl == null)
            return JSNull.Value;
        var whatToShow = a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined ? unchecked((int)(uint)a[1].DoubleValue) : unchecked((int)0xFFFFFFFF);
        var filterFn = a.Length > 2 && a[2] is JSFunction f ? f : (a.Length > 2 && a[2] is JSObject filterObj ? filterObj[(KeyString)"acceptNode"] as JSFunction : null);
        return BuildNodeIterator(rootEl, whatToShow, filterFn);
    }

    private JSValue CreateComment(in Arguments a)
    {
        var data = a.Length > 0 ? a[0].ToString() : string.Empty;
        return _host.CreateCommentNode(data);
    }

    // -------- TreeWalker / NodeIterator / Range builders --------

    /// <summary>
    /// Returns <c>1</c> (ACCEPT), <c>2</c> (REJECT) or <c>3</c> (SKIP) for <paramref name="el"/>
    /// against the <paramref name="whatToShow"/> bitmask and the optional
    /// <paramref name="filterFn"/>.
    /// </summary>
    private int ApplyFilter(DomNode el, int whatToShow, JSFunction? filterFn)
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

        if (filterFn != null)
        {
            // Per DOM Level 2 Traversal spec, exceptions thrown by NodeFilter
            // callbacks must propagate to the caller — they must NOT be swallowed.
            var result = filterFn.InvokeFunction(new Arguments(filterFn, _host.ToJSObject(el)));
            // Handle boolean return: true → 1 (ACCEPT), false → 2 (REJECT)
            if (result.IsBoolean)
                return result.BooleanValue ? 1 : 2;
            return (int)result.DoubleValue;
        }
        return 1; // FILTER_ACCEPT
    }

    /// <summary>Builds a DOM <c>TreeWalker</c> object.</summary>
    internal JSObject BuildTreeWalker(DomElement root, int whatToShow, JSFunction? filterFn)
    {
        var tw = new JSObject();
        var walker = new DomTreeWalker(root,
            (DomWhatToShow)(uint)whatToShow,
            node => (DomFilterResult)ApplyFilter(node, whatToShow, filterFn));

        tw.FastAddValue("root",
            _host.ToJSObject(root),
            JSPropertyAttributes.EnumerableConfigurableValue);

        tw.FastAddProperty("currentNode",
            new DomFunction((in a) => _host.ToJSObject(walker.CurrentNode), "get currentNode"),
            new DomFunction((in a) =>
            {
                if (a.Length > 0 && a[0] is JSObject nodeObject &&
                    _host.FindDomNodeByJSObject(nodeObject) is { } node)
                {
                    walker.CurrentNode = node;
                }
                return JSUndefined.Value;
            }, "set currentNode"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        tw.FastAddValue("whatToShow",
            new JSNumber(whatToShow),
            JSPropertyAttributes.EnumerableConfigurableValue);

        tw.FastAddValue("parentNode",
            new DomFunction((in a) => ToTraversalJsValue(walker.ParentNode()), "parentNode", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        tw.FastAddValue("firstChild",
            new DomFunction((in a) => ToTraversalJsValue(walker.FirstChild()), "firstChild", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        tw.FastAddValue("lastChild",
            new DomFunction((in a) => ToTraversalJsValue(walker.LastChild()), "lastChild", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        tw.FastAddValue("nextSibling",
            new DomFunction((in a) => ToTraversalJsValue(walker.NextSibling()), "nextSibling", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        tw.FastAddValue("previousSibling",
            new DomFunction((in a) => ToTraversalJsValue(walker.PreviousSibling()), "previousSibling", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        // nextNode() — depth-first pre-order traversal forward
        tw.FastAddValue("nextNode",
            new DomFunction((in a) => ToTraversalJsValue(walker.NextNode()), "nextNode", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        // previousNode() — depth-first pre-order traversal backward
        tw.FastAddValue("previousNode",
            new DomFunction((in a) => ToTraversalJsValue(walker.PreviousNode()), "previousNode", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return tw;
    }

    // RF-BRIDGE-1c Phase F (F3c part 2c): a TreeWalker/NodeIterator result may be a text/comment
    // node (SHOW_TEXT/SHOW_COMMENT), so convert any non-null node — not just elements — to its JS
    // wrapper.
    private JSValue ToTraversalJsValue(DomNode? node) => node is not null ? _host.ToJSObject(node) : JSNull.Value;

    /// <summary>Builds a DOM <c>NodeIterator</c> object.</summary>
    internal JSObject BuildNodeIterator(DomElement root, int whatToShow, JSFunction? filterFn)
    {
        var iter = new JSObject();
        // Canonical DomNodeIterator self-subscribes to root.OwnerDocument.Mutated and runs the DOM
        // §6.1 pre-removal reference-node adjustment itself, so the bridge keeps no registry.
        var iterator = new DomNodeIterator(root,
            (DomWhatToShow)(uint)whatToShow,
            node => (DomFilterResult)ApplyFilter(node, whatToShow, filterFn));

        iter.FastAddValue("root",
            _host.ToJSObject(root),
            JSPropertyAttributes.EnumerableConfigurableValue);

        iter.FastAddValue("whatToShow",
            new JSNumber(whatToShow),
            JSPropertyAttributes.EnumerableConfigurableValue);

        iter.FastAddProperty("referenceNode",
            new DomFunction((in a) => ToTraversalJsValue(iterator.ReferenceNode), "get referenceNode"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        iter.FastAddProperty("pointerBeforeReferenceNode",
            new DomFunction((in a) => iterator.PointerBeforeReferenceNode ? JSBoolean.True : JSBoolean.False, "get pointerBeforeReferenceNode"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        iter.FastAddValue("nextNode",
            new DomFunction((in a) => ToTraversalJsValue(iterator.NextNode()), "nextNode", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        iter.FastAddValue("previousNode",
            new DomFunction((in a) => ToTraversalJsValue(iterator.PreviousNode()), "previousNode", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        iter.FastAddValue("detach",
            new DomFunction((in a) =>
            {
                iterator.Dispose();
                return JSUndefined.Value;
            }, "detach", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

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
    internal JSObject BuildRange(DomNode? documentRoot = null)
    {
        var range = new JSObject();
        var docRoot = documentRoot ?? _host.DocumentNode;
        // The range self-subscribes to its document's DomDocument.Mutated (trackMutations) and runs
        // the DOM "removing steps" itself, so the bridge keeps no active-range registry.
        _rangeStates.Add(range, new BridgeDomRange(_host, docRoot));

        // Before the interface is registered there is no prototype to point at, so the range is left
        // unlinked rather than failing. There is no such range on the normal path: createRange and
        // `new Range()` are both reachable only from page script, which runs after registration.
        if (_rangePrototype is { } prototype)
            range.BasePrototypeObject = prototype;

        return range;
    }

    /// <summary>
    /// The bridge's live <c>Range</c> boundary store and content-operation engine — the canonical
    /// <see cref="DomRange"/> with the node-creation seams overridden so content operations mint
    /// bridge nodes through <see cref="ITraversalHost"/>: <c>#document-fragment</c> result
    /// fragments and clones that carry host runtime state, all registered so the host's
    /// <c>ToJSObject</c> can wrap them. Constructed <c>trackMutations: true</c> — the range
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
