using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Range</c> operations for <see cref="TraversalBinding"/> — the bodies behind the members
/// <see cref="TraversalBinding.InstallRangeMembers"/> puts on <c>Range.prototype</c>. Split from the
/// primary module file only so no single source file exceeds the 750-line guideline; this is the
/// same class.
/// </summary>
/// <remarks>
/// <para>
/// <b>These raise the exceptions DOM §4.5 names.</b> They used to be uniformly lenient: an
/// out-of-range offset was clamped into the node instead of raising <c>IndexSizeError</c>, a
/// non-<c>Node</c> or missing argument returned <c>undefined</c> instead of raising
/// <c>TypeError</c>, <c>selectNode</c> on a parentless node was a silent no-op, an invalid
/// <c>compareBoundaryPoints</c> comparison method and a source range in another tree both answered
/// <c>0</c>, and <c>insertNode</c> happily put a doctype inside a paragraph. Leniency of that shape
/// does not make a page work — it makes the range quietly point somewhere else and the wrongness
/// surface later, in the content operation, as a wrong extraction rather than a caught error.
/// </para>
/// <para>
/// Every expectation is Chromium's measured answer over one probe corpus run against both engines,
/// not a reading of the grammar, which is what pins the cases where the specification's wording and
/// a browser's behaviour part company: <c>setStart(node, -1)</c> is an <c>IndexSizeError</c> and not
/// a <c>TypeError</c> (Web IDL converts <c>-1</c> to <c>4294967295</c>, which is then merely too
/// large), <c>compareBoundaryPoints(3.7, r)</c> is accepted (the same conversion truncates it to
/// <c>END_TO_START</c>) while <c>compareBoundaryPoints(4, r)</c> is a <c>NotSupportedError</c>, and
/// <c>surroundContents</c> splits into <c>InvalidNodeTypeError</c> for a doctype and
/// <c>HierarchyRequestError</c> for a text node.
/// </para>
/// </remarks>
internal sealed partial class TraversalBinding
{
    // -------- Attributes --------

    private JSValue RangeGetCommonAncestorContainer(DomRange state)
    {
        // CommonAncestorWith returns null for boundaries in different trees, preserving the lenient
        // JSNull result; the canonical DomRange.CommonAncestorContainer would throw.
        var ancestor = state.StartContainer.CommonAncestorWith(state.EndContainer);
        return ancestor != null ? _host.ToJSObject(ancestor) : JSNull.Value;
    }

    // -------- Geometry (CSSOM View) --------

    private JSValue RangeGetBoundingClientRect(BridgeDomRange state, in Arguments _)
    {
        var rects = _host.GetClientRectsForRange(state);
        return _host.CreateDomRectObject(UnionClientRects(rects));
    }

    private JSValue RangeGetClientRects(BridgeDomRange state, in Arguments _)
    {
        var rects = _host.GetClientRectsForRange(state);
        if (rects.Count == 0)
            return new JSArray();
        return new JSArray([.. rects.Select(rect => (JSValue)_host.CreateDomRectObject(rect))]);
    }

    // -------- Argument and boundary validation --------

    /// <summary>
    /// The node behind argument <paramref name="index"/>, or a <c>TypeError</c>. A missing argument
    /// and one that is not a <c>Node</c> are the same failure to a browser — "parameter 1 is not of
    /// type 'Node'" — and both used to return <c>undefined</c> here, leaving the range untouched and
    /// the caller none the wiser.
    /// </summary>
    private DomNode NodeArgument(in Arguments a, int index, string member, string interfaceName = "Range")
    {
        if (index < a.Length && a[index] is JSObject candidate &&
            _host.FindDomNodeByJSObject(candidate) is { } node)
            return node;

        return JSException.ThrowTypeError<DomNode>(
            $"Failed to execute '{member}' on '{interfaceName}': parameter {index + 1} is not of type 'Node'.");
    }

    /// <summary>
    /// Web IDL's <c>unsigned long</c> conversion for a range offset: <c>NaN</c> and the infinities
    /// become <c>0</c>, everything else truncates and wraps modulo 2^32 — which is why a negative
    /// offset is reported as a very large one rather than rejected as negative.
    /// </summary>
    private static uint ToUnsignedLong(JSValue? value)
    {
        var number = value is null ? 0 : value.DoubleValue;
        if (double.IsNaN(number) || double.IsInfinity(number))
            return 0;

        var wrapped = Math.Truncate(number) % 4294967296.0;
        if (wrapped < 0)
            wrapped += 4294967296.0;
        return (uint)wrapped;
    }

    /// <summary>
    /// The node name a DOM exception message reports — the same value <c>nodeName</c> answers, which
    /// is what a browser puts in "The node provided is of type 'html'".
    /// </summary>
    private static string NodeNameOf(DomNode node) => node switch
    {
        DomDocumentType doctype => doctype.Name,
        DomText => "#text",
        DomComment => "#comment",
        DomDocumentFragment => "#document-fragment",
        DomDocument => "#document",
        DomElement element => element.TagName.ToUpperInvariant(),
        _ => node.NodeType.ToString(),
    };

    /// <summary>A node's length (DOM §4.4): zero for a doctype, the data length for character data,
    /// the child count otherwise.</summary>
    private static int NodeLength(DomNode node) => node switch
    {
        DomDocumentType => 0,
        DomCharacterData characterData => characterData.Data.Length,
        _ => node.ChildNodes.Count,
    };

    /// <summary>
    /// The "set the start/end of a range" preconditions: a doctype is never a boundary container,
    /// and an offset past the container's length is an <c>IndexSizeError</c> rather than something to
    /// clamp.
    /// </summary>
    private int ValidateBoundary(DomNode node, uint offset, string member, string interfaceName = "Range")
    {
        if (node is DomDocumentType)
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute '{member}' on '{interfaceName}': The node provided is of type '{NodeNameOf(node)}'.",
                "InvalidNodeTypeError");

        var length = NodeLength(node);
        if (offset > (uint)length)
            DomBridge.ThrowDOMException(
                _host.JsContext,
                node is DomCharacterData || interfaceName == "Selection"
                    ? $"Failed to execute '{member}' on '{interfaceName}': The offset {offset} is larger than the node's length ({length})."
                    : $"Failed to execute '{member}' on '{interfaceName}': There is no child at offset {offset}.",
                "IndexSizeError");

        return (int)offset;
    }

    // -------- Boundary operations --------

    private JSValue RangeSetStart(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "setStart");
        state.SetStart(node, ValidateBoundary(node, ToUnsignedLong(a.Length > 1 ? a[1] : null), "setStart"));
        return JSUndefined.Value;
    }

    private JSValue RangeSetEnd(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "setEnd");
        state.SetEnd(node, ValidateBoundary(node, ToUnsignedLong(a.Length > 1 ? a[1] : null), "setEnd"));
        return JSUndefined.Value;
    }

    /// <summary>
    /// The four sibling-relative setters. They differ only in which boundary they move and whether
    /// the offset is the node's index or one past it, so one body serves all four — and all four owe
    /// the caller <c>InvalidNodeTypeError</c> when the node has no parent to be positioned within.
    /// </summary>
    private JSValue RangeSetBoundaryToSibling(BridgeDomRange state, in Arguments a, string member, bool start, bool after)
    {
        var node = NodeArgument(in a, 0, member);
        // Phase 4 item 1 (P4.4a): a boundary node's parent may be a canonical DomDocument (a regime-B
        // createDocument root) — a valid boundary container that is not a DomElement, so use the raw
        // ParentNode (ParentEl nulled out a non-element parent and wrongly threw here).
        if (node.ParentNode is not { } parent)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute '{member}' on 'Range': the given Node has no parent.",
                "InvalidNodeTypeError");
            return JSUndefined.Value;
        }

        var offset = DomBridge.ChildIndexOf(parent, node) + (after ? 1 : 0);
        if (start)
            state.SetStart(parent, offset);
        else
            state.SetEnd(parent, offset);
        return JSUndefined.Value;
    }

    private static JSValue RangeCollapse(BridgeDomRange state, in Arguments a)
    {
        state.Collapse(a.Length > 0 && a[0].BooleanValue);
        return JSUndefined.Value;
    }

    private JSValue RangeSelectNode(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "selectNode");
        if (node.ParentNode is null)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'selectNode' on 'Range': the given Node has no parent.",
                "InvalidNodeTypeError");
            return JSUndefined.Value;
        }

        state.SelectNode(node);
        return JSUndefined.Value;
    }

    private JSValue RangeSelectNodeContents(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "selectNodeContents");
        try
        {
            state.SelectNodeContents(node);
        }
        catch (DomException ex)
        {
            // A doctype has no contents to select. Uncaught, the canonical exception reached the page
            // as a bare Error carrying a .NET stack trace instead of a DOMException.
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute 'selectNodeContents' on 'Range': The node provided is of type '{NodeNameOf(node)}'.",
                ex.Name);
        }

        return JSUndefined.Value;
    }

    // -------- Content operations --------

    private JSValue RangeCloneContents(BridgeDomRange state, in Arguments a) =>
        _host.ToJSObject(state.CloneContents());

    private JSValue RangeExtractContents(BridgeDomRange state, in Arguments a) =>
        _host.ToJSObject(state.ExtractContents());

    private static JSValue RangeDeleteContents(BridgeDomRange state, in Arguments a)
    {
        state.DeleteContents();
        return JSUndefined.Value;
    }

    private JSValue RangeInsertNode(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "insertNode");
        if (node is DomDocumentType)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute 'insertNode' on 'Range': Nodes of type '{NodeNameOf(node)}' may not be inserted inside nodes of type '{NodeNameOf(state.StartContainer)}'.",
                "HierarchyRequestError");
            return JSUndefined.Value;
        }

        try
        {
            state.InsertNode(node);
        }
        catch (DomException ex)
        {
            DomBridge.ThrowDOMException(_host.JsContext, ex.Message, ex.Name);
        }

        return JSUndefined.Value;
    }

    private JSValue RangeSurroundContents(BridgeDomRange state, in Arguments a)
    {
        var newParent = NodeArgument(in a, 0, "surroundContents");

        // The document root is now a canonical DomDocument (P4.6) and sub-document roots are severed
        // canonical DomDocuments (P4.4b) — neither is a DomElement — so a non-element new parent is
        // rejected here by node kind rather than by the former #document / #subdoc-root sentinel
        // guard. A browser splits the rejection two ways, which is what these two arms are.
        if (newParent is DomDocumentType or DomDocument or DomDocumentFragment)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute 'surroundContents' on 'Range': The node provided is of type '{NodeNameOf(newParent)}'.",
                "InvalidNodeTypeError");
            return JSUndefined.Value;
        }

        if (newParent is not DomElement newParentElement)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'surroundContents' on 'Range': This node type does not support this method.",
                "HierarchyRequestError");
            return JSUndefined.Value;
        }

        // The canonical algorithm handles the partial-non-text (InvalidStateError, incl. comment
        // boundaries) check, the extract, and the wrap.
        try
        {
            state.SurroundContents(newParentElement);
        }
        catch (DomException ex)
        {
            DomBridge.ThrowDOMException(_host.JsContext, ex.Message, ex.Name);
        }

        return JSUndefined.Value;
    }

    /// <summary>
    /// A copy with the same boundaries and the same root — the root matters, because a range created
    /// in a frame's document must clone into that document and not into the containing page's. The
    /// former implementation minted the clone against the main document and set its boundaries by
    /// calling the JS <c>setStart</c>/<c>setEnd</c> it looked up off the object; both go away with
    /// the members now on the prototype and the state held here.
    /// </summary>
    private JSValue RangeCloneRange(BridgeDomRange state, in Arguments a)
    {
        var clone = BuildRange(state.Root);
        if (_rangeStates.TryGetValue(clone, out var boundaries) && boundaries is BridgeDomRange cloneState)
        {
            cloneState.SetStart(state.StartContainer, state.StartOffset);
            cloneState.SetEnd(state.EndContainer, state.EndOffset);
        }

        return clone;
    }

    /// <summary>
    /// A no-op that a page may still call: <c>detach()</c> was DOM Level 2's way of releasing a
    /// range, and DOM §4.5 keeps the method so old code does not break while specifying that it does
    /// nothing. The range stays usable afterwards, which is the observable part.
    /// </summary>
    private static JSValue RangeDetach(BridgeDomRange state, in Arguments a) => JSUndefined.Value;

    // -------- Comparison --------

    private JSValue RangeCompareBoundaryPoints(BridgeDomRange state, in Arguments a)
    {
        if (a.Length < 2)
            return JSException.ThrowTypeError<JSValue>(
                "Failed to execute 'compareBoundaryPoints' on 'Range': 2 arguments required, but only " +
                $"{a.Length} present.");

        // A StaticRange is deliberately not accepted here: the operation is Range-to-Range, and a
        // static range's boundaries may be invalid by construction.
        if (a[1] is not JSObject sourceRangeObject ||
            !_rangeStates.TryGetValue(sourceRangeObject, out var sourceBoundaries) ||
            sourceBoundaries is not BridgeDomRange source)
            return JSException.ThrowTypeError<JSValue>(
                "Failed to execute 'compareBoundaryPoints' on 'Range': parameter 2 is not of type 'Range'.");

        // Web IDL `unsigned short`: 3.7 truncates to END_TO_START and is accepted; -1 wraps to 65535
        // and is not. Only the four named methods are in range.
        var how = ToUnsignedLong(a[0]) % 65536;
        if (how > 3)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'compareBoundaryPoints' on 'Range': The comparison method provided must be one of " +
                "'START_TO_START', 'START_TO_END', 'END_TO_END', or 'END_TO_START'.",
                "NotSupportedError");
            return new JSNumber(0);
        }

        if (!ReferenceEquals(state.StartContainer.GetRootNode(), source.StartContainer.GetRootNode()))
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'compareBoundaryPoints' on 'Range': The source range is in a different document than this range.",
                "WrongDocumentError");
            return new JSNumber(0);
        }

        var (thisContainer, thisOffset, otherContainer, otherOffset) = how switch
        {
            0 => (state.StartContainer, state.StartOffset, source.StartContainer, source.StartOffset),
            1 => (state.EndContainer, state.EndOffset, source.StartContainer, source.StartOffset),
            2 => (state.EndContainer, state.EndOffset, source.EndContainer, source.EndOffset),
            _ => (state.StartContainer, state.StartOffset, source.EndContainer, source.EndOffset),
        };

        return new JSNumber(
            DomRange.CompareBoundaryPoints(thisContainer, thisOffset, otherContainer, otherOffset));
    }

    /// <summary>
    /// Whether a point is before (<c>-1</c>), within (<c>0</c>) or after (<c>1</c>) the range. The
    /// three point/node predicates below share their preconditions, which is what
    /// <see cref="ValidatePoint"/> holds.
    /// </summary>
    private JSValue RangeComparePoint(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "comparePoint");
        var offset = ToUnsignedLong(a.Length > 1 ? a[1] : null);

        if (!ReferenceEquals(node.GetRootNode(), state.StartContainer.GetRootNode()))
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'comparePoint' on 'Range': The node provided and the Range are not in the same tree.",
                "WrongDocumentError");
            return new JSNumber(0);
        }

        var point = ValidatePoint(node, offset, "comparePoint");
        if (DomRange.CompareBoundaryPoints(node, point, state.StartContainer, state.StartOffset) < 0)
            return new JSNumber(-1);
        if (DomRange.CompareBoundaryPoints(node, point, state.EndContainer, state.EndOffset) > 0)
            return new JSNumber(1);
        return new JSNumber(0);
    }

    private JSValue RangeIsPointInRange(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "isPointInRange");
        var offset = ToUnsignedLong(a.Length > 1 ? a[1] : null);

        // A point in another tree is simply not in the range — this one answers false where
        // comparePoint throws, because "is it inside?" has an answer and "where is it?" does not.
        if (!ReferenceEquals(node.GetRootNode(), state.StartContainer.GetRootNode()))
            return JSBoolean.False;

        var point = ValidatePoint(node, offset, "isPointInRange");
        var inside =
            DomRange.CompareBoundaryPoints(node, point, state.StartContainer, state.StartOffset) >= 0 &&
            DomRange.CompareBoundaryPoints(node, point, state.EndContainer, state.EndOffset) <= 0;
        return inside ? JSBoolean.True : JSBoolean.False;
    }

    private JSValue RangeIntersectsNode(BridgeDomRange state, in Arguments a)
    {
        var node = NodeArgument(in a, 0, "intersectsNode");
        if (!ReferenceEquals(node.GetRootNode(), state.StartContainer.GetRootNode()))
            return JSBoolean.False;

        // A node with no parent that shares the range's root is the root itself, and the range is
        // inside it — so it intersects. (A *detached* node fails the root test above instead, which
        // is why this arm is true rather than false.)
        if (node.ParentNode is not { } parent)
            return JSBoolean.True;

        var offset = DomBridge.ChildIndexOf(parent, node);
        var intersects =
            DomRange.CompareBoundaryPoints(parent, offset, state.EndContainer, state.EndOffset) < 0 &&
            DomRange.CompareBoundaryPoints(parent, offset + 1, state.StartContainer, state.StartOffset) > 0;
        return intersects ? JSBoolean.True : JSBoolean.False;
    }

    /// <summary>The two checks a point argument owes: not inside a doctype, and not past the node's
    /// length.</summary>
    private int ValidatePoint(DomNode node, uint offset, string member)
    {
        if (node is DomDocumentType)
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute '{member}' on 'Range': The node provided is of type '{NodeNameOf(node)}'.",
                "InvalidNodeTypeError");

        var length = NodeLength(node);
        if (offset > (uint)length)
            DomBridge.ThrowDOMException(
                _host.JsContext,
                $"Failed to execute '{member}' on 'Range': The offset {offset} is larger than the node's length ({length}).",
                "IndexSizeError");

        return (int)offset;
    }

    // -------- HTML fragment parsing --------

    /// <summary>
    /// <c>createContextualFragment</c> (HTML §3.5): parse a markup string as if it were being written
    /// into the range's start node, and hand back the result as a document fragment. It is how a page
    /// turns a string into nodes with the *surrounding* element's content model applied, which
    /// <c>innerHTML</c> on a detached container cannot do.
    /// </summary>
    private JSValue RangeCreateContextualFragment(BridgeDomRange state, in Arguments a)
    {
        var html = a.Length > 0 && !a[0].IsUndefined ? a[0].ToString() : string.Empty;

        // The parsing context is the start node if it is an element, otherwise its parent element.
        // `html` is excluded deliberately: parsing into it would run the "before head" rules and
        // discard ordinary flow content, so HTML §3.5 substitutes a body element, and so does this.
        var context = state.StartContainer as DomElement ?? state.StartContainer.ParentNode as DomElement;
        if (context is null || string.Equals(context.LocalName, "html", StringComparison.OrdinalIgnoreCase))
            context = state.StartContainer.OwnerDocument?.Body ?? _host.CreateBridgeElement("body");

        var fragment = _host.CreateRangeResultFragment();
        foreach (var node in _host.ParseHtmlFragment(context, html))
            fragment.AppendChild(node);

        return _host.ToJSObject(fragment);
    }

    // -------- Stringifier --------

    private static JSValue RangeToString(BridgeDomRange state, in Arguments a) =>
        new JSString(RangeText(state));

    /// <summary>
    /// The text a range selects. Shared with <c>Selection.toString()</c>, so the selection and the
    /// range it holds can never disagree about their own text.
    /// </summary>
    private static string RangeText(DomRange state)
    {
        // A range within a single Comment node stringifies to the selected substring — a deliberate
        // deviation from the DOM §4.5 stringifier, which is Text-only (a Comment range would yield
        // ""), retained for Acid3 Test 11. Every other case delegates to the canonical spec-correct
        // Range stringifier (Broiler.Dom.DomRange.ToString), which the bridge's former CollectRangeText
        // copy shadowed — and shadowed with a bug: it omitted the end-container Text node's head.
        if (ReferenceEquals(state.StartContainer, state.EndContainer) && DomBridge.IsComment(state.StartContainer))
        {
            var text = DomBridge.BridgeText(state.StartContainer);
            var s = Math.Max(0, Math.Min(state.StartOffset, text.Length));
            var e = Math.Max(s, Math.Min(state.EndOffset, text.Length));
            return text.Substring(s, e - s);
        }

        return state.ToString();
    }

    private static (double Left, double Top, double Width, double Height) UnionClientRects(
        IReadOnlyList<(double Left, double Top, double Width, double Height)> rects)
    {
        if (rects.Count == 0)
            return (0, 0, 0, 0);

        var left = rects[0].Left;
        var top = rects[0].Top;
        var right = rects[0].Left + rects[0].Width;
        var bottom = rects[0].Top + rects[0].Height;

        for (var i = 1; i < rects.Count; i++)
        {
            var (Left, Top, Width, Height) = rects[i];
            left = Math.Min(left, Left);
            top = Math.Min(top, Top);
            right = Math.Max(right, Left + Width);
            bottom = Math.Max(bottom, Top + Height);
        }

        return (left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
