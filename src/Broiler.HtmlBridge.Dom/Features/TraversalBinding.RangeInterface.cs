using System.Runtime.CompilerServices;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>AbstractRange</c> and <c>Range</c> as real interfaces (DOM §4.5), with their members on the
/// interface prototypes rather than copied onto every range object.
/// </summary>
/// <remarks>
/// <para>
/// <c>Range</c> did not exist as a global at all: <c>typeof Range</c> was <c>"undefined"</c>, so
/// <c>new Range()</c> and <c>r instanceof Range</c> were both a <c>ReferenceError</c> — the kind
/// that aborts the whole script, not just the line — and <c>document.createRange()</c> handed back a
/// plain object whose <c>constructor.name</c> was <c>"Object"</c> and whose 29 members were its own
/// properties.
/// </para>
/// <para>
/// <b>The boundary getters go on <c>AbstractRange</c>, not on <c>Range</c>.</b> That is not a
/// decoration: <c>startContainer</c>, <c>startOffset</c>, <c>endContainer</c>, <c>endOffset</c> and
/// <c>collapsed</c> are <c>AbstractRange</c>'s, and a browser's <c>Range.prototype</c> genuinely does
/// not carry them — measured, not assumed. A page that walks <c>Range.prototype</c>'s own property
/// names and finds them there would be reading a shape no browser has.
/// </para>
/// <para>
/// <b>Members on the prototype is the point.</b> Every other DOM wrapper in this bridge still
/// installs its interface as own properties of each object — the open half of track 6's wrapper item
/// — so <c>Object.getOwnPropertyNames(node)</c> lists the whole interface and
/// <c>Text.prototype.splitText</c> is <c>undefined</c>. A range is where that stops: the state lives
/// in <see cref="TraversalBinding._rangeStates"/>, keyed weakly by the range object, so a prototype
/// method can find its own boundaries from its receiver and there is nothing left to put on the
/// instance. <c>Object.getOwnPropertyNames(document.createRange())</c> is <c>[]</c>, as it is in a
/// browser.
/// </para>
/// <para>
/// Reaching a range's state through the receiver is also what makes an illegal invocation —
/// <c>Range.prototype.setStart.call({}, node, 0)</c> — a <c>TypeError</c> rather than a crash or a
/// silent wrong answer.
/// </para>
/// </remarks>
internal sealed partial class TraversalBinding
{
    /// <summary>
    /// <c>Range.prototype</c>, once the interface is registered. A range built before registration
    /// (there is none on the normal path — <c>createRange</c> is reachable only from page script) is
    /// left unlinked rather than failing.
    /// </summary>
    private JSObject? _rangePrototype;

    /// <summary>
    /// The boundaries behind each range object, so a prototype method can find them from its
    /// receiver. Weak, so a range a page has dropped is not kept alive by this table — and neither is
    /// its mutation subscription on the document.
    /// </summary>
    /// <remarks>
    /// Holds both kinds of <c>AbstractRange</c>: a live <see cref="BridgeDomRange"/> and a
    /// <see cref="StaticRangeBoundaries"/>, which is the whole difference between the two interfaces
    /// — one tracks the tree and one is four values captured at construction. The five getters on
    /// <c>AbstractRange.prototype</c> read either; every operation on <c>Range.prototype</c> demands
    /// the live one, so <c>Range.prototype.setStart.call(staticRange, …)</c> is the same
    /// <c>TypeError</c> as calling it on any other foreign receiver.
    /// </remarks>
    private readonly ConditionalWeakTable<JSObject, IRangeBoundaries> _rangeStates = new();

    /// <summary>The four boundary values every <c>AbstractRange</c> has, live or static.</summary>
    private interface IRangeBoundaries
    {
        DomNode StartContainer { get; }
        int StartOffset { get; }
        DomNode EndContainer { get; }
        int EndOffset { get; }
        bool Collapsed { get; }
    }

    /// <summary>
    /// A <c>StaticRange</c>'s boundaries: captured once and never adjusted, which is the point of the
    /// interface — a live range would move under a tree mutation and this must not.
    /// </summary>
    private sealed record StaticRangeBoundaries(
        DomNode StartContainer, int StartOffset, DomNode EndContainer, int EndOffset) : IRangeBoundaries
    {
        public bool Collapsed =>
            ReferenceEquals(StartContainer, EndContainer) && StartOffset == EndOffset;
    }

    /// <summary>
    /// Registers the two interface globals and installs every member on their prototypes. Runs once
    /// per context, with the other DOM interface constructors.
    /// </summary>
    internal void RegisterRangeInterface(JSContext context)
    {
        // The host halves of the two constructors, reached only from the JavaScript below: they are
        // captured into a closure and deleted from the global, so a page cannot mint one out of band.
        context["__broilerCreateRange"] = new DomFunction((in _) => BuildRange(), "createRange", 0);
        context["__broilerCreateStaticRange"] =
            new DomFunction((in a) => BuildStaticRange(in a), "createStaticRange", 1);

        context.Eval("""
            (function () {
                var create = __broilerCreateRange;
                var createStatic = __broilerCreateStaticRange;
                delete globalThis.__broilerCreateRange;
                delete globalThis.__broilerCreateStaticRange;

                // Not constructible, exactly as in a browser: AbstractRange is the base Range and
                // StaticRange share, never something a page builds.
                function AbstractRange() { throw new TypeError('Illegal constructor'); }

                // `new Range()` is a real constructor (DOM §4.5), and it is the one interface here
                // that is: it returns a range over the document with both boundaries at (document, 0).
                // Called without `new` it throws, which is what a browser does for every DOM
                // interface object.
                function Range() {
                    if (!new.target)
                        throw new TypeError("Failed to construct 'Range': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
                    return create();
                }

                Object.setPrototypeOf(Range.prototype, AbstractRange.prototype);

                // Web IDL constants: on the interface object and on its prototype, non-writable and
                // non-configurable. A browser has them in both places, which is what lets both
                // `Range.START_TO_START` and `someRange.START_TO_START` read 0.
                var names = ['START_TO_START', 'START_TO_END', 'END_TO_END', 'END_TO_START'];
                for (var i = 0; i < names.length; i++) {
                    var descriptor = { value: i, writable: false, enumerable: true, configurable: false };
                    Object.defineProperty(Range, names[i], descriptor);
                    Object.defineProperty(Range.prototype, names[i], descriptor);
                }

                Object.defineProperty(Range.prototype, Symbol.toStringTag, {
                    value: 'Range', writable: false, enumerable: false, configurable: true
                });

                // The other AbstractRange subclass (DOM §4.4). It carries no members of its own — the
                // four boundary values and `collapsed` come from AbstractRange — so its prototype is
                // just a constructor, as it is in a browser.
                function StaticRange(init) {
                    if (!new.target)
                        throw new TypeError("Failed to construct 'StaticRange': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
                    return createStatic(init);
                }

                Object.setPrototypeOf(StaticRange.prototype, AbstractRange.prototype);
                Object.defineProperty(StaticRange.prototype, Symbol.toStringTag, {
                    value: 'StaticRange', writable: false, enumerable: false, configurable: true
                });

                globalThis.AbstractRange = AbstractRange;
                globalThis.Range = Range;
                globalThis.StaticRange = StaticRange;
            })();
            """);

        // Read the two interface objects back by evaluating their names rather than through the
        // context indexer: they are published with `globalThis.X = …` from inside the closure above,
        // not as top-level declarations.
        if (context.Eval("Range") is not JSObject rangeConstructor ||
            rangeConstructor[(KeyString)"prototype"] is not JSObject rangePrototype ||
            context.Eval("AbstractRange") is not JSObject abstractRangeConstructor ||
            abstractRangeConstructor[(KeyString)"prototype"] is not JSObject abstractRangePrototype)
            return;

        _rangePrototype = rangePrototype;
        _staticRangePrototype =
            (context.Eval("StaticRange") as JSObject)?[(KeyString)"prototype"] as JSObject;
        InstallAbstractRangeMembers(abstractRangePrototype);
        InstallRangeMembers(rangePrototype);
        RegisterSelectionInterface(context);
    }

    /// <summary><c>StaticRange.prototype</c>, once the interface is registered.</summary>
    private JSObject? _staticRangePrototype;

    /// <summary>
    /// <c>new StaticRange(init)</c> — DOM §4.4. The four members are all required, and neither
    /// container may be a doctype; the offsets are deliberately <em>not</em> range-checked, because a
    /// static range is allowed to be invalid (that is what makes it cheap enough for an input event
    /// to carry one).
    /// </summary>
    private JSValue BuildStaticRange(in Arguments a)
    {
        var init = a.Length > 0 ? a[0] as JSObject : null;

        var startContainer = RequiredNodeMember(init, "startContainer");
        var startOffset = RequiredOffsetMember(init, "startOffset");
        var endContainer = RequiredNodeMember(init, "endContainer");
        var endOffset = RequiredOffsetMember(init, "endOffset");

        if (startContainer is DomDocumentType || endContainer is DomDocumentType)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to construct 'StaticRange': Neither startContainer nor endContainer can be a DocumentType or Attribute node.",
                "InvalidNodeTypeError");
            return JSUndefined.Value;
        }

        var staticRange = new JSObject();
        _rangeStates.Add(
            staticRange,
            new StaticRangeBoundaries(startContainer, (int)startOffset, endContainer, (int)endOffset));
        if (_staticRangePrototype is { } prototype)
            staticRange.BasePrototypeObject = prototype;
        return staticRange;
    }

    private DomNode RequiredNodeMember(JSObject? init, string member)
    {
        if (init?[(KeyString)member] is JSObject candidate &&
            _host.FindDomNodeByJSObject(candidate) is { } node)
            return node;

        return JSException.ThrowTypeError<DomNode>(
            $"Failed to construct 'StaticRange': Failed to read the '{member}' property from " +
            "'StaticRangeInit': Required member is undefined.");
    }

    private static uint RequiredOffsetMember(JSObject? init, string member)
    {
        var value = init?[(KeyString)member];
        if (value is null || value.IsUndefined)
            return JSException.ThrowTypeError<uint>(
                $"Failed to construct 'StaticRange': Failed to read the '{member}' property from " +
                "'StaticRangeInit': Required member is undefined.");

        return ToUnsignedLong(value);
    }

    /// <summary>The five boundary attributes DOM §4.5 gives <c>AbstractRange</c>.</summary>
    private void InstallAbstractRangeMembers(JSObject prototype)
    {
        Getter(prototype, "startContainer", (state, host) => host.ToJSObject(state.StartContainer));
        Getter(prototype, "startOffset", static (state, _) => new JSNumber(state.StartOffset));
        Getter(prototype, "endContainer", (state, host) => host.ToJSObject(state.EndContainer));
        Getter(prototype, "endOffset", static (state, _) => new JSNumber(state.EndOffset));
        Getter(prototype, "collapsed", static (state, _) => state.Collapsed ? JSBoolean.True : JSBoolean.False);
    }

    /// <summary>
    /// <c>Range</c>'s own attribute and its operations, including the CSSOM-View geometry pair and
    /// the HTML fragment-parsing extension.
    /// </summary>
    private void InstallRangeMembers(JSObject prototype)
    {
        // Range's own attribute rather than AbstractRange's, so it reads the live range — a
        // StaticRange has no common ancestor to report and is not asked for one.
        prototype.FastAddProperty(
            "commonAncestorContainer",
            new DomFunction(
                (in a) => RangeGetCommonAncestorContainer(StateFor(in a, "commonAncestorContainer")),
                "get commonAncestorContainer"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

        Method(prototype, "setStart", 2, RangeSetStart);
        Method(prototype, "setEnd", 2, RangeSetEnd);
        Method(prototype, "setStartBefore", 1, (BridgeDomRange state, in Arguments a) => RangeSetBoundaryToSibling(state, in a, "setStartBefore", start: true, after: false));
        Method(prototype, "setStartAfter", 1, (BridgeDomRange state, in Arguments a) => RangeSetBoundaryToSibling(state, in a, "setStartAfter", start: true, after: true));
        Method(prototype, "setEndBefore", 1, (BridgeDomRange state, in Arguments a) => RangeSetBoundaryToSibling(state, in a, "setEndBefore", start: false, after: false));
        Method(prototype, "setEndAfter", 1, (BridgeDomRange state, in Arguments a) => RangeSetBoundaryToSibling(state, in a, "setEndAfter", start: false, after: true));
        // `collapse(toStart)` is optional, so Web IDL gives it length 0 rather than 1.
        Method(prototype, "collapse", 0, RangeCollapse);
        Method(prototype, "selectNode", 1, RangeSelectNode);
        Method(prototype, "selectNodeContents", 1, RangeSelectNodeContents);
        Method(prototype, "compareBoundaryPoints", 2, RangeCompareBoundaryPoints);
        Method(prototype, "deleteContents", 0, RangeDeleteContents);
        Method(prototype, "extractContents", 0, RangeExtractContents);
        Method(prototype, "cloneContents", 0, RangeCloneContents);
        Method(prototype, "insertNode", 1, RangeInsertNode);
        Method(prototype, "surroundContents", 1, RangeSurroundContents);
        Method(prototype, "cloneRange", 0, RangeCloneRange);
        Method(prototype, "detach", 0, RangeDetach);
        Method(prototype, "isPointInRange", 2, RangeIsPointInRange);
        Method(prototype, "comparePoint", 2, RangeComparePoint);
        Method(prototype, "intersectsNode", 1, RangeIntersectsNode);
        Method(prototype, "toString", 0, RangeToString);
        Method(prototype, "getBoundingClientRect", 0, RangeGetBoundingClientRect);
        Method(prototype, "getClientRects", 0, RangeGetClientRects);
        Method(prototype, "createContextualFragment", 1, RangeCreateContextualFragment);
    }

    private delegate JSValue RangeOperation(BridgeDomRange state, in Arguments a);

    private void Method(JSObject prototype, string name, int length, RangeOperation body) =>
        prototype.FastAddValue(
            name,
            new DomFunction((in a) => body(StateFor(in a, name), in a), name, length),
            JSPropertyAttributes.EnumerableConfigurableValue);

    /// <summary>
    /// An <c>AbstractRange</c> attribute. It reads <see cref="IRangeBoundaries"/> rather than a live
    /// range, so the same getter serves a <c>Range</c> and a <c>StaticRange</c>.
    /// </summary>
    private void Getter(JSObject prototype, string name, Func<IRangeBoundaries, ITraversalHost, JSValue> read) =>
        prototype.FastAddProperty(
            name,
            new DomFunction((in a) => read(BoundariesFor(in a, name), _host), $"get {name}"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

    /// <summary>
    /// The boundaries behind the receiver, or a <c>TypeError</c> — a member held on the prototype can
    /// be called on anything, and a browser answers "Illegal invocation" for a receiver that is not a
    /// range.
    /// </summary>
    private IRangeBoundaries BoundariesFor(in Arguments a, string member)
    {
        if (a.This is JSObject receiver && _rangeStates.TryGetValue(receiver, out var boundaries))
            return boundaries;

        return JSException.ThrowTypeError<IRangeBoundaries>(
            $"Failed to execute '{member}' on 'Range': Illegal invocation");
    }

    /// <summary>
    /// The <em>live</em> range behind the receiver. A <c>StaticRange</c> reaches this only through a
    /// borrowed <c>Range.prototype</c> method, which is an illegal invocation for the same reason any
    /// other foreign receiver is: it has no tree to mutate.
    /// </summary>
    private BridgeDomRange StateFor(in Arguments a, string member) =>
        BoundariesFor(in a, member) as BridgeDomRange
        ?? JSException.ThrowTypeError<BridgeDomRange>(
            $"Failed to execute '{member}' on 'Range': Illegal invocation");
}
