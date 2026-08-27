using System.Runtime.CompilerServices;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>Selection</c> (Selection API) and the <c>window.getSelection()</c> / <c>document.getSelection()</c>
/// pair that reach it.
/// </summary>
/// <remarks>
/// <para>
/// Neither existed, so <c>window.getSelection</c> was <c>undefined</c> and the bare <c>Selection</c>
/// was a <c>ReferenceError</c> — the kind that aborts the script rather than the statement. That is
/// worse than it sounds for this API in particular: the copy-to-clipboard idiom every page shares is
/// <c>sel.removeAllRanges(); sel.addRange(range)</c>, and <c>window.getSelection().toString()</c> is
/// how a page reads what the user picked, so the name is reached by ordinary pages and not only by
/// editors.
/// </para>
/// <para>
/// <b>What this is and is not.</b> Broiler has no user input, so it has no <em>user</em> selection —
/// and that is exactly the state a browser is in on a freshly loaded page: <c>rangeCount</c> is
/// <c>0</c>, <c>type</c> is <c>"None"</c>, <c>anchorNode</c> is <c>null</c>. Everything a script then
/// does to it — <c>addRange</c>, <c>collapse</c>, <c>extend</c>, <c>setBaseAndExtent</c>,
/// <c>selectAllChildren</c>, <c>deleteFromDocument</c> — has an answer that does not depend on a
/// user, and that scripted half is what is implemented here. What is absent is the half that has no
/// answer without one: nothing ever populates the selection on its own, no <c>selectionchange</c>
/// fires from input, and the selection is not painted.
/// </para>
/// <para>
/// <b>Two members are deliberately left out</b> rather than stubbed: <c>modify()</c>, which moves the
/// selection by character/word/line and so needs the text-segmentation model this engine does not
/// have, and <c>getComposedRanges()</c>, which is about shadow-tree composition. Absent is the honest
/// signal — a page feature-detecting either takes its fallback, where a stub would claim a movement
/// that silently does nothing.
/// </para>
/// <para>
/// Every expectation is Chromium's measured answer over one probe corpus run against both engines.
/// Two of them are not what the specification's wording suggests: a node or range belonging to
/// <em>another</em> tree is silently <b>ignored</b> by <c>addRange</c>, <c>collapse</c>,
/// <c>selectAllChildren</c> and <c>setBaseAndExtent</c> rather than throwing — and "another tree"
/// includes a detached one, so <c>collapse</c> into a node not yet inserted does nothing; while an
/// out-of-range offset or a doctype in that same argument <em>does</em> throw, so the validation
/// happens before the tree test rather than after.
/// </para>
/// </remarks>
internal sealed partial class TraversalBinding
{
    /// <summary><c>Selection.prototype</c>, once the interface is registered.</summary>
    private JSObject? _selectionPrototype;

    /// <summary>
    /// The one <c>Selection</c> object per document, so <c>window.getSelection() ===
    /// document.getSelection()</c> and two calls answer the same object — which a browser guarantees
    /// and which a page relies on when it stashes the selection and comes back to it.
    /// </summary>
    private readonly Dictionary<DomNode, JSObject> _selections = [];

    private readonly ConditionalWeakTable<JSObject, SelectionState> _selectionStates = new();

    /// <summary>
    /// What a selection holds: the document it belongs to, the one range it may carry, and which end
    /// of that range the focus is at.
    /// </summary>
    /// <remarks>
    /// The range is held as <em>the page's own object</em> when the page supplied one, which is what
    /// makes <c>sel.getRangeAt(0) === r</c> true after <c>sel.addRange(r)</c> and what makes
    /// <c>sel.toString()</c> follow a later edit of that range. A selection carries at most one range:
    /// a second <c>addRange</c> is ignored, as it is in Chromium.
    /// </remarks>
    private sealed class SelectionState(DomNode documentRoot)
    {
        public DomNode DocumentRoot { get; } = documentRoot;
        public JSObject? RangeObject { get; set; }
        public BridgeDomRange? Range { get; set; }

        /// <summary>Whether the focus is at the range's <em>start</em> — the state
        /// <c>extend</c> and <c>setBaseAndExtent</c> can produce and a range alone cannot express.</summary>
        public bool Backwards { get; set; }

        public void Clear()
        {
            RangeObject = null;
            Range = null;
            Backwards = false;
        }
    }

    // -------- Registration --------

    /// <summary>Registers the <c>Selection</c> interface and installs its members. Called from
    /// <see cref="RegisterRangeInterface"/>, after <c>Range</c> exists for it to hand back.</summary>
    private void RegisterSelectionInterface(JSContext context)
    {
        context.Eval("""
            (function () {
                // Not constructible: a selection comes from getSelection(), never from `new`.
                function Selection() { throw new TypeError('Illegal constructor'); }
                Object.defineProperty(Selection.prototype, Symbol.toStringTag, {
                    value: 'Selection', writable: false, enumerable: false, configurable: true
                });
                globalThis.Selection = Selection;
            })();
            """);

        if (context.Eval("Selection") is not JSObject constructor ||
            constructor[(KeyString)"prototype"] is not JSObject prototype)
            return;

        _selectionPrototype = prototype;

        SelectionGetter(prototype, "anchorNode", (s, host) => NodeOrNull(host, Anchor(s).Node));
        SelectionGetter(prototype, "anchorOffset", static (s, _) => new JSNumber(Anchor(s).Offset));
        SelectionGetter(prototype, "focusNode", (s, host) => NodeOrNull(host, Focus(s).Node));
        SelectionGetter(prototype, "focusOffset", static (s, _) => new JSNumber(Focus(s).Offset));
        // The legacy aliases, which a browser still carries and older code still reads.
        SelectionGetter(prototype, "baseNode", (s, host) => NodeOrNull(host, Anchor(s).Node));
        SelectionGetter(prototype, "baseOffset", static (s, _) => new JSNumber(Anchor(s).Offset));
        SelectionGetter(prototype, "extentNode", (s, host) => NodeOrNull(host, Focus(s).Node));
        SelectionGetter(prototype, "extentOffset", static (s, _) => new JSNumber(Focus(s).Offset));
        SelectionGetter(prototype, "isCollapsed", static (s, _) =>
            s.Range is null || s.Range.Collapsed ? JSBoolean.True : JSBoolean.False);
        SelectionGetter(prototype, "rangeCount", static (s, _) => new JSNumber(s.Range is null ? 0 : 1));
        SelectionGetter(prototype, "type", static (s, _) => new JSString(
            s.Range is null ? "None" : s.Range.Collapsed ? "Caret" : "Range"));
        SelectionGetter(prototype, "direction", static (s, _) => new JSString(
            s.Range is null ? "none" : s.Backwards ? "backward" : "forward"));

        SelectionMethod(prototype, "getRangeAt", 1, SelectionGetRangeAt);
        SelectionMethod(prototype, "addRange", 1, SelectionAddRange);
        SelectionMethod(prototype, "removeRange", 1, SelectionRemoveRange);
        SelectionMethod(prototype, "removeAllRanges", 0, SelectionRemoveAllRanges);
        // `empty()` is the same operation under its older name; a browser has both.
        SelectionMethod(prototype, "empty", 0, SelectionRemoveAllRanges);
        SelectionMethod(prototype, "collapse", 1, (SelectionState s, in Arguments a) => SelectionCollapse(s, in a, "collapse"));
        SelectionMethod(prototype, "setPosition", 1, (SelectionState s, in Arguments a) => SelectionCollapse(s, in a, "setPosition"));
        SelectionMethod(prototype, "collapseToStart", 0, (SelectionState s, in Arguments a) => SelectionCollapseToEnd(s, "collapseToStart", toStart: true));
        SelectionMethod(prototype, "collapseToEnd", 0, (SelectionState s, in Arguments a) => SelectionCollapseToEnd(s, "collapseToEnd", toStart: false));
        SelectionMethod(prototype, "extend", 1, SelectionExtend);
        SelectionMethod(prototype, "setBaseAndExtent", 4, SelectionSetBaseAndExtent);
        SelectionMethod(prototype, "selectAllChildren", 1, SelectionSelectAllChildren);
        SelectionMethod(prototype, "containsNode", 1, SelectionContainsNode);
        SelectionMethod(prototype, "deleteFromDocument", 0, SelectionDeleteFromDocument);
        SelectionMethod(prototype, "toString", 0, static (SelectionState s, in Arguments a) =>
            new JSString(s.Range is null ? string.Empty : RangeText(s.Range)));
    }

    /// <summary>
    /// The <c>getSelection()</c> both <c>window</c> and <c>document</c> expose. A document with no
    /// browsing context — a <c>createDocument</c>/<c>createHTMLDocument</c> result — has no selection
    /// and answers <c>null</c>, which is the browser's answer and not an omission.
    /// </summary>
    internal JSValue GetSelection(DomNode? documentRoot = null)
    {
        var root = documentRoot ?? _host.DocumentNode;
        if (!ReferenceEquals(root, _host.DocumentNode) && !_host.HasBrowsingContext(root))
            return JSNull.Value;

        if (_selections.TryGetValue(root, out var existing))
            return existing;

        var selection = new JSObject();
        _selectionStates.Add(selection, new SelectionState(root));
        if (_selectionPrototype is { } prototype)
            selection.BasePrototypeObject = prototype;
        _selections[root] = selection;
        return selection;
    }

    // -------- Member plumbing --------

    private delegate JSValue SelectionOperation(SelectionState state, in Arguments a);

    private void SelectionMethod(JSObject prototype, string name, int length, SelectionOperation body) =>
        prototype.FastAddValue(
            name,
            new DomFunction((in a) => body(SelectionFor(in a, name), in a), name, length),
            JSPropertyAttributes.EnumerableConfigurableValue);

    private void SelectionGetter(JSObject prototype, string name, Func<SelectionState, ITraversalHost, JSValue> read) =>
        prototype.FastAddProperty(
            name,
            new DomFunction((in a) => read(SelectionFor(in a, name), _host), $"get {name}"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

    private SelectionState SelectionFor(in Arguments a, string member)
    {
        if (a.This is JSObject receiver && _selectionStates.TryGetValue(receiver, out var state))
            return state;

        return JSException.ThrowTypeError<SelectionState>(
            $"Failed to execute '{member}' on 'Selection': Illegal invocation");
    }

    private static JSValue NodeOrNull(ITraversalHost host, DomNode? node) =>
        node is null ? JSNull.Value : host.ToJSObject(node);

    /// <summary>The end the selection was anchored at — the range's start unless <c>extend</c> put the
    /// focus there instead.</summary>
    private static (DomNode? Node, int Offset) Anchor(SelectionState state) =>
        state.Range is not { } range ? (null, 0)
        : state.Backwards ? (range.EndContainer, range.EndOffset)
        : (range.StartContainer, range.StartOffset);

    private static (DomNode? Node, int Offset) Focus(SelectionState state) =>
        state.Range is not { } range ? (null, 0)
        : state.Backwards ? (range.StartContainer, range.StartOffset)
        : (range.EndContainer, range.EndOffset);

    /// <summary>Whether a node belongs to the tree this selection covers. A node in another document
    /// — or in no document at all, which a freshly created element is — does not.</summary>
    private static bool InSelectionTree(SelectionState state, DomNode node) =>
        ReferenceEquals(node.GetRootNode(), state.DocumentRoot);

    /// <summary>
    /// The range this selection writes through, minted on first use. Reusing the object keeps
    /// <c>getRangeAt(0)</c> stable across a <c>collapse</c>/<c>extend</c> sequence, and when the page
    /// supplied the range through <c>addRange</c> the selection goes on writing through that one — as
    /// a browser does, the selection's range being the range rather than a copy of it.
    /// </summary>
    private BridgeDomRange SelectionRange(SelectionState state)
    {
        if (state.Range is { } existing)
            return existing;

        var rangeObject = BuildRange(state.DocumentRoot);
        var range = (BridgeDomRange)_rangeStates.GetValue(
            rangeObject, _ => throw new InvalidOperationException("A range built here is always registered."));
        state.RangeObject = rangeObject;
        state.Range = range;
        state.Backwards = false;
        return range;
    }

    // -------- Operations --------

    private JSValue SelectionGetRangeAt(SelectionState state, in Arguments a)
    {
        var index = ToUnsignedLong(a.Length > 0 ? a[0] : null);
        if (state.RangeObject is not { } rangeObject || index != 0)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute 'getRangeAt' on 'Selection': {index} is not a valid index.",
                "IndexSizeError");
            return JSUndefined.Value;
        }

        return rangeObject;
    }

    private JSValue SelectionAddRange(SelectionState state, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject candidate ||
            !_rangeStates.TryGetValue(candidate, out var boundaries) || boundaries is not BridgeDomRange range)
            return JSException.ThrowTypeError<JSValue>(
                "Failed to execute 'addRange' on 'Selection': parameter 1 is not of type 'Range'.");

        // A selection holds one range, and a second addRange is dropped rather than replacing it.
        if (state.Range is not null || !InSelectionTree(state, range.StartContainer))
            return JSUndefined.Value;

        state.RangeObject = candidate;
        state.Range = range;
        state.Backwards = false;
        return JSUndefined.Value;
    }

    private JSValue SelectionRemoveRange(SelectionState state, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject candidate ||
            !_rangeStates.TryGetValue(candidate, out var boundaries) || boundaries is not BridgeDomRange)
            return JSException.ThrowTypeError<JSValue>(
                "Failed to execute 'removeRange' on 'Selection': parameter 1 is not of type 'Range'.");

        // Only the range this selection is actually holding; another range with the same boundaries
        // is a different range, and removing it removes nothing.
        if (ReferenceEquals(state.RangeObject, candidate))
            state.Clear();
        return JSUndefined.Value;
    }

    private static JSValue SelectionRemoveAllRanges(SelectionState state, in Arguments a)
    {
        state.Clear();
        return JSUndefined.Value;
    }

    /// <summary><c>collapse(node, offset)</c> and its newer name <c>setPosition</c>. A <c>null</c>
    /// node empties the selection rather than failing, which is the one argument that is not a
    /// <c>TypeError</c>.</summary>
    private JSValue SelectionCollapse(SelectionState state, in Arguments a, string member)
    {
        if (a.Length > 0 && a[0].IsNull)
        {
            state.Clear();
            return JSUndefined.Value;
        }

        var node = NodeArgument(in a, 0, member, "Selection");
        var offset = ValidateBoundary(node, ToUnsignedLong(a.Length > 1 ? a[1] : null), member, "Selection");
        if (!InSelectionTree(state, node))
            return JSUndefined.Value;

        var range = SelectionRange(state);
        range.SetStart(node, offset);
        range.SetEnd(node, offset);
        state.Backwards = false;
        return JSUndefined.Value;
    }

    private JSValue SelectionCollapseToEnd(SelectionState state, string member, bool toStart)
    {
        if (state.Range is not { } range)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute '{member}' on 'Selection': there is no selection.",
                "InvalidStateError");
            return JSUndefined.Value;
        }

        var node = toStart ? range.StartContainer : range.EndContainer;
        var offset = toStart ? range.StartOffset : range.EndOffset;
        range.SetStart(node, offset);
        range.SetEnd(node, offset);
        state.Backwards = false;
        return JSUndefined.Value;
    }

    /// <summary>
    /// Moves the focus, keeping the anchor. This is the one operation a bare <c>Range</c> cannot
    /// express: when the new focus lands before the anchor the range still runs low-to-high, and the
    /// selection remembers that its focus is at the low end.
    /// </summary>
    private JSValue SelectionExtend(SelectionState state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "extend", "Selection");
        if (state.Range is null)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'extend' on 'Selection': This Selection object doesn't have any Ranges.",
                "InvalidStateError");
            return JSUndefined.Value;
        }

        var offset = ValidateBoundary(node, ToUnsignedLong(a.Length > 1 ? a[1] : null), "extend", "Selection");
        if (!InSelectionTree(state, node))
            return JSUndefined.Value;

        var (anchorNode, anchorOffset) = Anchor(state);
        SetSelectionBoundaries(state, anchorNode!, anchorOffset, node, offset);
        return JSUndefined.Value;
    }

    private JSValue SelectionSetBaseAndExtent(SelectionState state, in Arguments a)
    {
        var anchorNode = NodeArgument(in a, 0, "setBaseAndExtent", "Selection");
        var anchorOffset = ValidateBoundary(
            anchorNode, ToUnsignedLong(a.Length > 1 ? a[1] : null), "setBaseAndExtent", "Selection");
        var focusNode = NodeArgument(in a, 2, "setBaseAndExtent", "Selection");
        var focusOffset = ValidateBoundary(
            focusNode, ToUnsignedLong(a.Length > 3 ? a[3] : null), "setBaseAndExtent", "Selection");

        if (!InSelectionTree(state, anchorNode) || !InSelectionTree(state, focusNode))
            return JSUndefined.Value;

        SetSelectionBoundaries(state, anchorNode, anchorOffset, focusNode, focusOffset);
        return JSUndefined.Value;
    }

    /// <summary>Points the range at the two boundaries low-to-high and records which end the focus is
    /// at.</summary>
    private void SetSelectionBoundaries(
        SelectionState state, DomNode anchorNode, int anchorOffset, DomNode focusNode, int focusOffset)
    {
        var backwards =
            DomRange.CompareBoundaryPoints(focusNode, focusOffset, anchorNode, anchorOffset) < 0;

        var range = SelectionRange(state);
        // Set the low boundary first: setStart past the current end (or setEnd before the current
        // start) collapses the range onto the new point, which would lose the other boundary.
        if (backwards)
        {
            range.SetStart(focusNode, focusOffset);
            range.SetEnd(anchorNode, anchorOffset);
        }
        else
        {
            range.SetStart(anchorNode, anchorOffset);
            range.SetEnd(focusNode, focusOffset);
        }

        state.Backwards = backwards;
    }

    private JSValue SelectionSelectAllChildren(SelectionState state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "selectAllChildren", "Selection");
        if (node is DomDocumentType)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute 'selectAllChildren' on 'Selection': The node provided is of type '{NodeNameOf(node)}'.",
                "InvalidNodeTypeError");
            return JSUndefined.Value;
        }

        if (!InSelectionTree(state, node))
            return JSUndefined.Value;

        var range = SelectionRange(state);
        range.SetStart(node, 0);
        range.SetEnd(node, node.ChildNodes.Count);
        state.Backwards = false;
        return JSUndefined.Value;
    }

    /// <summary>
    /// Whether the node lies inside the selection — wholly, or partly when
    /// <paramref name="a"/>'s second argument allows it. The default is <c>false</c>: "contains" means
    /// the whole node unless the caller says otherwise.
    /// </summary>
    private JSValue SelectionContainsNode(SelectionState state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "containsNode", "Selection");
        var allowPartial = a.Length > 1 && a[1].BooleanValue;

        if (state.Range is not { } range || !ReferenceEquals(node.GetRootNode(), range.StartContainer.GetRootNode()))
            return JSBoolean.False;

        // A node with no parent that shares the range's root is the root itself, which contains the
        // whole selection.
        if (node.ParentNode is not { } parent)
            return JSBoolean.True;

        var index = DomBridge.ChildIndexOf(parent, node);
        var contained = allowPartial
            ? DomRange.CompareBoundaryPoints(range.StartContainer, range.StartOffset, parent, index + 1) < 0 &&
              DomRange.CompareBoundaryPoints(parent, index, range.EndContainer, range.EndOffset) < 0
            : DomRange.CompareBoundaryPoints(range.StartContainer, range.StartOffset, parent, index) <= 0 &&
              DomRange.CompareBoundaryPoints(parent, index + 1, range.EndContainer, range.EndOffset) <= 0;
        return contained ? JSBoolean.True : JSBoolean.False;
    }

    private static JSValue SelectionDeleteFromDocument(SelectionState state, in Arguments a)
    {
        state.Range?.DeleteContents();
        return JSUndefined.Value;
    }
}
