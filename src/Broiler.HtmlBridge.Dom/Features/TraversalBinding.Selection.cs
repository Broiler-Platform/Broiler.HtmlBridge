using System.Runtime.CompilerServices;
using Broiler.HtmlBridge.Jseal;
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
    private JsValue? _selectionPrototype;

    /// <summary>
    /// The one <c>Selection</c> object per document, so <c>window.getSelection() ===
    /// document.getSelection()</c> and two calls answer the same object — which a browser guarantees
    /// and which a page relies on when it stashes the selection and comes back to it.
    /// </summary>
    private readonly Dictionary<DomNode, JsValue> _selections = [];

    private readonly ConditionalWeakTable<object, SelectionState> _selectionStates = new();

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
        public JsValue? RangeObject { get; set; }
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
    private void RegisterSelectionInterface()
    {
        var realm = _host.Realm;

        realm.EvaluateHostScript("""
            (function () {
                // Not constructible: a selection comes from getSelection(), never from `new`.
                function Selection() { throw new TypeError('Illegal constructor'); }
                Object.defineProperty(Selection.prototype, Symbol.toStringTag, {
                    value: 'Selection', writable: false, enumerable: false, configurable: true
                });
                globalThis.Selection = Selection;
            })();
            """, "interface:selection");

        var constructor = realm.EvaluateHostScript("Selection", "probe:Selection");
        if (!constructor.IsObject)
            return;

        var prototype = realm.GetProperty(constructor, "prototype");
        if (!prototype.IsObject)
            return;

        _selectionPrototype = prototype;

        SelectionGetter(prototype, "anchorNode", (s, host) => NodeOrNull(host, Anchor(s).Node));
        SelectionGetter(prototype, "anchorOffset", static (s, _) => JsValue.Number(Anchor(s).Offset));
        SelectionGetter(prototype, "focusNode", (s, host) => NodeOrNull(host, Focus(s).Node));
        SelectionGetter(prototype, "focusOffset", static (s, _) => JsValue.Number(Focus(s).Offset));
        // The legacy aliases, which a browser still carries and older code still reads.
        SelectionGetter(prototype, "baseNode", (s, host) => NodeOrNull(host, Anchor(s).Node));
        SelectionGetter(prototype, "baseOffset", static (s, _) => JsValue.Number(Anchor(s).Offset));
        SelectionGetter(prototype, "extentNode", (s, host) => NodeOrNull(host, Focus(s).Node));
        SelectionGetter(prototype, "extentOffset", static (s, _) => JsValue.Number(Focus(s).Offset));
        SelectionGetter(prototype, "isCollapsed", static (s, _) =>
            JsValue.Boolean(s.Range is null || s.Range.Collapsed));
        SelectionGetter(prototype, "rangeCount", static (s, _) => JsValue.Number(s.Range is null ? 0 : 1));
        SelectionGetter(prototype, "type", static (s, _) => JsValue.String(
            s.Range is null ? "None" : s.Range.Collapsed ? "Caret" : "Range"));
        SelectionGetter(prototype, "direction", static (s, _) => JsValue.String(
            s.Range is null ? "none" : s.Backwards ? "backward" : "forward"));

        SelectionMethod(prototype, "getRangeAt", 1, SelectionGetRangeAt);
        SelectionMethod(prototype, "addRange", 1, SelectionAddRange);
        SelectionMethod(prototype, "removeRange", 1, SelectionRemoveRange);
        SelectionMethod(prototype, "removeAllRanges", 0, SelectionRemoveAllRanges);
        // `empty()` is the same operation under its older name; a browser has both.
        SelectionMethod(prototype, "empty", 0, SelectionRemoveAllRanges);
        SelectionMethod(prototype, "collapse", 1, (SelectionState s, in JsCall call) => SelectionCollapse(s, in call, "collapse"));
        SelectionMethod(prototype, "setPosition", 1, (SelectionState s, in JsCall call) => SelectionCollapse(s, in call, "setPosition"));
        SelectionMethod(prototype, "collapseToStart", 0, (SelectionState s, in JsCall call) => SelectionCollapseToEnd(s, "collapseToStart", toStart: true));
        SelectionMethod(prototype, "collapseToEnd", 0, (SelectionState s, in JsCall call) => SelectionCollapseToEnd(s, "collapseToEnd", toStart: false));
        SelectionMethod(prototype, "extend", 1, SelectionExtend);
        SelectionMethod(prototype, "setBaseAndExtent", 4, SelectionSetBaseAndExtent);
        SelectionMethod(prototype, "selectAllChildren", 1, SelectionSelectAllChildren);
        SelectionMethod(prototype, "containsNode", 1, SelectionContainsNode);
        SelectionMethod(prototype, "deleteFromDocument", 0, SelectionDeleteFromDocument);
        SelectionMethod(prototype, "toString", 0, static (SelectionState s, in JsCall call) =>
            JsValue.String(s.Range is null ? string.Empty : RangeText(s.Range)));
    }

    /// <summary>
    /// The <c>getSelection()</c> both <c>window</c> and <c>document</c> expose. A document with no
    /// browsing context — a <c>createDocument</c>/<c>createHTMLDocument</c> result — has no selection
    /// and answers <c>null</c>, which is the browser's answer and not an omission.
    /// </summary>
    internal JsValue SelectionObject(DomNode? documentRoot = null)
    {
        var realm = _host.Realm;
        var root = documentRoot ?? _host.DocumentNode;
        if (!ReferenceEquals(root, _host.DocumentNode) && !_host.HasBrowsingContext(root))
            return JsValue.Null;

        if (_selections.TryGetValue(root, out var existing))
            return existing;

        var selection = realm.NewObject();
        _selectionStates.Add(IdentityOf(selection), new SelectionState(root));
        if (_selectionPrototype is { } prototype)
            realm.SetPrototype(selection, prototype);
        _selections[root] = selection;
        return selection;
    }

    /// <summary>
    /// <see cref="SelectionObject"/> as an engine value, for the one caller that still holds one:
    /// <c>DomBridge.SubDocumentHost.cs</c>'s <c>ISubDocumentHost.GetSelection</c>, which is another
    /// group's file this round. It is a cast and not a conversion — the handle carries the engine's
    /// own object — and it goes when that seam migrates.
    /// </summary>
    internal Broiler.JavaScript.Runtime.JSValue GetSelection(DomNode? documentRoot = null)
    {
        var selection = SelectionObject(documentRoot);
        return selection.IsObject
            ? Runtime.JsInterop.ToEngineObject(selection)
            : Broiler.JavaScript.BuiltIns.Null.JSNull.Value;
    }

    // -------- Member plumbing --------

    private delegate JsValue SelectionOperation(SelectionState state, in JsCall call);

    private void SelectionMethod(JsValue prototype, string name, int length, SelectionOperation body) =>
        _host.Realm.DefineValue(
            prototype,
            name,
            _host.Realm.NewMethod(name, (in call) => body(SelectionFor(in call, name), in call), length));

    private void SelectionGetter(JsValue prototype, string name, Func<SelectionState, ITraversalHost, JsValue> read) =>
        _host.Realm.DefineAccessor(
            prototype,
            name,
            (in call) => read(SelectionFor(in call, name), _host),
            null);

    private SelectionState SelectionFor(in JsCall call, string member)
    {
        if (call.This.IsObject && _selectionStates.TryGetValue(IdentityOf(call.This), out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Selection': Illegal invocation");
    }

    private static JsValue NodeOrNull(ITraversalHost host, DomNode? node) =>
        node is null ? JsValue.Null : host.WrapNode(node);

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
            IdentityOf(rangeObject), _ => throw new InvalidOperationException("A range built here is always registered."));
        state.RangeObject = rangeObject;
        state.Range = range;
        state.Backwards = false;
        return range;
    }

    // -------- Operations --------

    private JsValue SelectionGetRangeAt(SelectionState state, in JsCall call)
    {
        var index = ToUnsignedLong(call.Realm, call[0]);
        if (state.RangeObject is not { } rangeObject || index != 0)
            throw call.Realm.DomError(
                "IndexSizeError",
                $"Failed to execute 'getRangeAt' on 'Selection': {index} is not a valid index.");

        return rangeObject;
    }

    private JsValue SelectionAddRange(SelectionState state, in JsCall call)
    {
        if (!call[0].IsObject ||
            !_rangeStates.TryGetValue(IdentityOf(call[0]), out var boundaries) || boundaries is not BridgeDomRange range)
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                "Failed to execute 'addRange' on 'Selection': parameter 1 is not of type 'Range'.");

        // A selection holds one range, and a second addRange is dropped rather than replacing it.
        if (state.Range is not null || !InSelectionTree(state, range.StartContainer))
            return JsValue.Undefined;

        state.RangeObject = call[0];
        state.Range = range;
        state.Backwards = false;
        return JsValue.Undefined;
    }

    private JsValue SelectionRemoveRange(SelectionState state, in JsCall call)
    {
        if (!call[0].IsObject ||
            !_rangeStates.TryGetValue(IdentityOf(call[0]), out var boundaries) || boundaries is not BridgeDomRange)
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                "Failed to execute 'removeRange' on 'Selection': parameter 1 is not of type 'Range'.");

        // Only the range this selection is actually holding; another range with the same boundaries
        // is a different range, and removing it removes nothing. JsValue's `==` is JavaScript's
        // `===`, which for two object handles is the reference test this used to spell out.
        if (state.RangeObject is { } held && held == call[0])
            state.Clear();
        return JsValue.Undefined;
    }

    private static JsValue SelectionRemoveAllRanges(SelectionState state, in JsCall call)
    {
        state.Clear();
        return JsValue.Undefined;
    }

    /// <summary><c>collapse(node, offset)</c> and its newer name <c>setPosition</c>. A <c>null</c>
    /// node empties the selection rather than failing, which is the one argument that is not a
    /// <c>TypeError</c>.</summary>
    private JsValue SelectionCollapse(SelectionState state, in JsCall call, string member)
    {
        if (call.Length > 0 && call[0].IsNull)
        {
            state.Clear();
            return JsValue.Undefined;
        }

        var node = NodeArgument(in call, 0, member, "Selection");
        var offset = ValidateBoundary(node, ToUnsignedLong(call.Realm, call[1]), member, "Selection");
        if (!InSelectionTree(state, node))
            return JsValue.Undefined;

        var range = SelectionRange(state);
        range.SetStart(node, offset);
        range.SetEnd(node, offset);
        state.Backwards = false;
        return JsValue.Undefined;
    }

    private JsValue SelectionCollapseToEnd(SelectionState state, string member, bool toStart)
    {
        if (state.Range is not { } range)
            throw _host.Realm.DomError(
                "InvalidStateError",
                $"Failed to execute '{member}' on 'Selection': there is no selection.");

        var node = toStart ? range.StartContainer : range.EndContainer;
        var offset = toStart ? range.StartOffset : range.EndOffset;
        range.SetStart(node, offset);
        range.SetEnd(node, offset);
        state.Backwards = false;
        return JsValue.Undefined;
    }

    /// <summary>
    /// Moves the focus, keeping the anchor. This is the one operation a bare <c>Range</c> cannot
    /// express: when the new focus lands before the anchor the range still runs low-to-high, and the
    /// selection remembers that its focus is at the low end.
    /// </summary>
    private JsValue SelectionExtend(SelectionState state, in JsCall call)
    {
        var node = NodeArgument(in call, 0, "extend", "Selection");
        if (state.Range is null)
            throw call.Realm.DomError(
                "InvalidStateError",
                "Failed to execute 'extend' on 'Selection': This Selection object doesn't have any Ranges.");

        var offset = ValidateBoundary(node, ToUnsignedLong(call.Realm, call[1]), "extend", "Selection");
        if (!InSelectionTree(state, node))
            return JsValue.Undefined;

        var (anchorNode, anchorOffset) = Anchor(state);
        SetSelectionBoundaries(state, anchorNode!, anchorOffset, node, offset);
        return JsValue.Undefined;
    }

    private JsValue SelectionSetBaseAndExtent(SelectionState state, in JsCall call)
    {
        var anchorNode = NodeArgument(in call, 0, "setBaseAndExtent", "Selection");
        var anchorOffset = ValidateBoundary(
            anchorNode, ToUnsignedLong(call.Realm, call[1]), "setBaseAndExtent", "Selection");
        var focusNode = NodeArgument(in call, 2, "setBaseAndExtent", "Selection");
        var focusOffset = ValidateBoundary(
            focusNode, ToUnsignedLong(call.Realm, call[3]), "setBaseAndExtent", "Selection");

        if (!InSelectionTree(state, anchorNode) || !InSelectionTree(state, focusNode))
            return JsValue.Undefined;

        SetSelectionBoundaries(state, anchorNode, anchorOffset, focusNode, focusOffset);
        return JsValue.Undefined;
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

    private JsValue SelectionSelectAllChildren(SelectionState state, in JsCall call)
    {
        var node = NodeArgument(in call, 0, "selectAllChildren", "Selection");
        if (node is DomDocumentType)
            throw call.Realm.DomError(
                "InvalidNodeTypeError",
                $"Failed to execute 'selectAllChildren' on 'Selection': The node provided is of type '{NodeNameOf(node)}'.");

        if (!InSelectionTree(state, node))
            return JsValue.Undefined;

        var range = SelectionRange(state);
        range.SetStart(node, 0);
        range.SetEnd(node, node.ChildNodes.Count);
        state.Backwards = false;
        return JsValue.Undefined;
    }

    /// <summary>
    /// Whether the node lies inside the selection — wholly, or partly when
    /// <paramref name="call"/>'s second argument allows it. The default is <c>false</c>: "contains"
    /// means the whole node unless the caller says otherwise.
    /// </summary>
    private JsValue SelectionContainsNode(SelectionState state, in JsCall call)
    {
        var node = NodeArgument(in call, 0, "containsNode", "Selection");
        var allowPartial = call.Length > 1 && call[1].AsBoolean;

        if (state.Range is not { } range || !ReferenceEquals(node.GetRootNode(), range.StartContainer.GetRootNode()))
            return JsValue.False;

        // A node with no parent that shares the range's root is the root itself, which contains the
        // whole selection.
        if (node.ParentNode is not { } parent)
            return JsValue.True;

        var index = DomBridge.ChildIndexOf(parent, node);
        var contained = allowPartial
            ? DomRange.CompareBoundaryPoints(range.StartContainer, range.StartOffset, parent, index + 1) < 0 &&
              DomRange.CompareBoundaryPoints(parent, index, range.EndContainer, range.EndOffset) < 0
            : DomRange.CompareBoundaryPoints(range.StartContainer, range.StartOffset, parent, index) <= 0 &&
              DomRange.CompareBoundaryPoints(parent, index + 1, range.EndContainer, range.EndOffset) <= 0;
        return JsValue.Boolean(contained);
    }

    private static JsValue SelectionDeleteFromDocument(SelectionState state, in JsCall call)
    {
        state.Range?.DeleteContents();
        return JsValue.Undefined;
    }
}
