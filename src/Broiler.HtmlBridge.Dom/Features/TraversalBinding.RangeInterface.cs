using System.Runtime.CompilerServices;
using Broiler.HtmlBridge.Jseal;
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
/// <para>
/// <b>The JavaScript half is host script, not guest source.</b> The two IIFEs below are written by
/// this repository and ship with it, so they run through <see cref="IJsSource.EvaluateHostScript"/>
/// and are not subject to the page's Content-Security-Policy — which is the distinction JSEAL draws
/// and the reason a realm may serve these while refusing the page its own <c>eval</c>.
/// </para>
/// </remarks>
internal sealed partial class TraversalBinding
{
    /// <summary>
    /// <c>Range.prototype</c>, once the interface is registered. A range built before registration
    /// (there is none on the normal path — <c>createRange</c> is reachable only from page script) is
    /// left unlinked rather than failing.
    /// </summary>
    private JsValue? _rangePrototype;

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
    /// <para>
    /// The key is the object identity behind the handle (see <see cref="IdentityOf"/>), because a
    /// <see cref="JsValue"/> is a struct and a weak table needs a reference.
    /// </para>
    /// </remarks>
    private readonly ConditionalWeakTable<object, IRangeBoundaries> _rangeStates = new();

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
    /// <remarks>
    /// <b>It took an engine context until its one caller was migrated, and never read it.</b> The
    /// parameter's own doc comment said it stayed only so that call site needed no edit while it
    /// was another group's file, and that it would go when that file moved. It has, so it did —
    /// this module reaches the realm through <see cref="ITraversalHost.Realm"/> and always did.
    /// </remarks>
    internal void RegisterRangeInterface()
    {
        var realm = _host.Realm;

        // The host halves of the two constructors, reached only from the JavaScript below: they are
        // captured into a closure and deleted from the global, so a page cannot mint one out of band.
        realm.SetProperty(realm.Global, "__broilerCreateRange", realm.NewMethod("createRange", (in _) => BuildRange(), 0));
        realm.SetProperty(realm.Global, "__broilerCreateStaticRange", realm.NewMethod("createStaticRange", BuildStaticRange, 1));

        realm.EvaluateHostScript("""
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
            """, "interface:range");

        // Read the two interface objects back by evaluating their names rather than through the
        // global's property lookup: they are published with `globalThis.X = …` from inside the
        // closure above, not as top-level declarations.
        var rangeConstructor = realm.EvaluateHostScript("Range", "probe:Range");
        var abstractRangeConstructor = realm.EvaluateHostScript("AbstractRange", "probe:AbstractRange");
        if (!rangeConstructor.IsObject || !abstractRangeConstructor.IsObject)
            return;

        var rangePrototype = realm.GetProperty(rangeConstructor, "prototype");
        var abstractRangePrototype = realm.GetProperty(abstractRangeConstructor, "prototype");
        if (!rangePrototype.IsObject || !abstractRangePrototype.IsObject)
            return;

        _rangePrototype = rangePrototype;

        var staticRangeConstructor = realm.EvaluateHostScript("StaticRange", "probe:StaticRange");
        var staticRangePrototype = staticRangeConstructor.IsObject
            ? realm.GetProperty(staticRangeConstructor, "prototype")
            : JsValue.Undefined;
        _staticRangePrototype = staticRangePrototype.IsObject ? staticRangePrototype : null;

        InstallAbstractRangeMembers(abstractRangePrototype);
        InstallRangeMembers(rangePrototype);
        RegisterSelectionInterface();
    }

    /// <summary><c>StaticRange.prototype</c>, once the interface is registered.</summary>
    private JsValue? _staticRangePrototype;

    /// <summary>
    /// <c>new StaticRange(init)</c> — DOM §4.4. The four members are all required, and neither
    /// container may be a doctype; the offsets are deliberately <em>not</em> range-checked, because a
    /// static range is allowed to be invalid (that is what makes it cheap enough for an input event
    /// to carry one).
    /// </summary>
    private JsValue BuildStaticRange(in JsCall call)
    {
        var realm = call.Realm;
        var init = call[0].IsObject ? call[0] : JsValue.Missing;

        var startContainer = RequiredNodeMember(realm, init, "startContainer");
        var startOffset = RequiredOffsetMember(realm, init, "startOffset");
        var endContainer = RequiredNodeMember(realm, init, "endContainer");
        var endOffset = RequiredOffsetMember(realm, init, "endOffset");

        if (startContainer is DomDocumentType || endContainer is DomDocumentType)
            throw realm.DomError(
                "InvalidNodeTypeError",
                "Failed to construct 'StaticRange': Neither startContainer nor endContainer can be a DocumentType or Attribute node.");

        var staticRange = realm.NewObject();
        _rangeStates.Add(
            IdentityOf(staticRange),
            new StaticRangeBoundaries(startContainer, (int)startOffset, endContainer, (int)endOffset));
        if (_staticRangePrototype is { } prototype)
            realm.SetPrototype(staticRange, prototype);
        return staticRange;
    }

    private DomNode RequiredNodeMember(IJsRealm realm, JsValue init, string member)
    {
        if (init.IsObject && realm.GetProperty(init, member) is { IsObject: true } candidate &&
            _host.FindNode(candidate) is { } node)
            return node;

        throw realm.Error(
            JsErrorKind.TypeError,
            $"Failed to construct 'StaticRange': Failed to read the '{member}' property from " +
            "'StaticRangeInit': Required member is undefined.");
    }

    private static uint RequiredOffsetMember(IJsRealm realm, JsValue init, string member)
    {
        // No init at all reads as an absent member, which is the same failure as an undefined one —
        // the CLR-null the property read used to answer and an explicit `undefined` were already
        // treated identically here, and Missing is what the former stands as now.
        var value = init.IsObject ? realm.GetProperty(init, member) : JsValue.Missing;
        if (value.IsMissing || value.IsUndefined)
            throw realm.Error(
                JsErrorKind.TypeError,
                $"Failed to construct 'StaticRange': Failed to read the '{member}' property from " +
                "'StaticRangeInit': Required member is undefined.");

        return ToUnsignedLong(realm, value);
    }

    /// <summary>The five boundary attributes DOM §4.5 gives <c>AbstractRange</c>.</summary>
    private void InstallAbstractRangeMembers(JsValue prototype)
    {
        Getter(prototype, "startContainer", (state, host) => host.WrapNode(state.StartContainer));
        Getter(prototype, "startOffset", static (state, _) => JsValue.Number(state.StartOffset));
        Getter(prototype, "endContainer", (state, host) => host.WrapNode(state.EndContainer));
        Getter(prototype, "endOffset", static (state, _) => JsValue.Number(state.EndOffset));
        Getter(prototype, "collapsed", static (state, _) => JsValue.Boolean(state.Collapsed));
    }

    /// <summary>
    /// <c>Range</c>'s own attribute and its operations, including the CSSOM-View geometry pair and
    /// the HTML fragment-parsing extension.
    /// </summary>
    private void InstallRangeMembers(JsValue prototype)
    {
        // Range's own attribute rather than AbstractRange's, so it reads the live range — a
        // StaticRange has no common ancestor to report and is not asked for one.
        _host.Realm.DefineAccessor(
            prototype,
            "commonAncestorContainer",
            (in call) => RangeGetCommonAncestorContainer(StateFor(in call, "commonAncestorContainer")),
            null);

        Method(prototype, "setStart", 2, RangeSetStart);
        Method(prototype, "setEnd", 2, RangeSetEnd);
        Method(prototype, "setStartBefore", 1, (BridgeDomRange state, in JsCall call) => RangeSetBoundaryToSibling(state, in call, "setStartBefore", start: true, after: false));
        Method(prototype, "setStartAfter", 1, (BridgeDomRange state, in JsCall call) => RangeSetBoundaryToSibling(state, in call, "setStartAfter", start: true, after: true));
        Method(prototype, "setEndBefore", 1, (BridgeDomRange state, in JsCall call) => RangeSetBoundaryToSibling(state, in call, "setEndBefore", start: false, after: false));
        Method(prototype, "setEndAfter", 1, (BridgeDomRange state, in JsCall call) => RangeSetBoundaryToSibling(state, in call, "setEndAfter", start: false, after: true));
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

    private delegate JsValue RangeOperation(BridgeDomRange state, in JsCall call);

    private void Method(JsValue prototype, string name, int length, RangeOperation body) =>
        _host.Realm.DefineValue(
            prototype,
            name,
            _host.Realm.NewMethod(name, (in call) => body(StateFor(in call, name), in call), length));

    /// <summary>
    /// An <c>AbstractRange</c> attribute. It reads <see cref="IRangeBoundaries"/> rather than a live
    /// range, so the same getter serves a <c>Range</c> and a <c>StaticRange</c>.
    /// </summary>
    /// <remarks>
    /// The realm mints the accessor function, names it <c>get name</c> and makes it
    /// non-constructable — the three things the bridge's own function type did at this call site
    /// before. A <see langword="null"/> setter is still how read-only is spelled.
    /// </remarks>
    private void Getter(JsValue prototype, string name, Func<IRangeBoundaries, ITraversalHost, JsValue> read) =>
        _host.Realm.DefineAccessor(
            prototype,
            name,
            (in call) => read(BoundariesFor(in call, name), _host),
            null);

    /// <summary>
    /// The boundaries behind the receiver, or a <c>TypeError</c> — a member held on the prototype can
    /// be called on anything, and a browser answers "Illegal invocation" for a receiver that is not a
    /// range.
    /// </summary>
    private IRangeBoundaries BoundariesFor(in JsCall call, string member)
    {
        if (call.This.IsObject && _rangeStates.TryGetValue(IdentityOf(call.This), out var boundaries))
            return boundaries;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Range': Illegal invocation");
    }

    /// <summary>
    /// The <em>live</em> range behind the receiver. A <c>StaticRange</c> reaches this only through a
    /// borrowed <c>Range.prototype</c> method, which is an illegal invocation for the same reason any
    /// other foreign receiver is: it has no tree to mutate.
    /// </summary>
    private BridgeDomRange StateFor(in JsCall call, string member) =>
        BoundariesFor(in call, member) as BridgeDomRange
        ?? throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Range': Illegal invocation");
}
