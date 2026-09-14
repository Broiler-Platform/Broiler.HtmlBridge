using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Jseal.Providers;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The crossing between a JSEAL handle and the Broiler.JS object it carries, for the operations the
/// JSEAL contract cannot express yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is scaffolding, and it is meant to be deleted.</b> Two methods in this project use
/// it, and each calls itself a gap in the JSEAL contract rather than an unmigrated caller:
/// <c>SetAdoptedStyleSheets</c> in <c>DomBridge/ConstructedStyleSheets.cs</c> copies an assigned
/// array with the engine's own hole treatment, which <see cref="IJsRealm"/> cannot read back, and
/// <c>RetireIndex</c> in <c>Features/StyleSheetBinding.cs</c> deletes an index, which
/// <see cref="IJsMembers"/> has no member for. The only other caller is <c>JsInteropSeamTests</c>.
/// (This said the bridge was 250 engine-coupled files, half migrated, and that every use of this
/// class sat between a migrated binding and a caller still holding a <c>JSObject</c>.)
/// </para>
/// <para>
/// <b>The budget's number is NOT the count of those crossings, and reading it that way has misled
/// two pieces of work.</b> <c>eng/jseal-budget.json</c> counts occurrences of the engine's own
/// namespace as TEXT, and a crossing need not contain one: the file holding <c>RetireIndex</c> names
/// no engine type, while <c>ConstructedStyleSheets.cs</c> has two usings for its copy. (Spelled
/// around rather than out, because the metric would count this sentence too.) The commit that
/// deleted four unreachable members removed six crossings and moved the budget by eleven — the two
/// measure different things, and neither alone says how much is left. Count crossings with a grep
/// for a call to one of the three members below, since the class name alone also finds the comments
/// that mention it; count coupling with the script.
/// </para>
/// <para>
/// <b>It costs nothing at run time and it is not a conversion.</b> Under the Broiler.JS provider a
/// JSEAL object handle carries the engine's own <c>JSObject</c> — that is the provider's central
/// design rule, and it is what keeps wrapper identity and <c>el === el</c> working for an object
/// that crosses here.
/// </para>
/// <para>
/// <b>The weak tables are not among the things it keeps working.</b> Seven weak tables in this bridge
/// key on <see cref="JsValue.ObjectIdentity"/> — the reference the handle carries, which every
/// provider already makes canonical per object because handle equality is defined by it — and none
/// reaches it through this class: the reverse map in <c>Runtime/JsObjectRegistry.cs</c>, and the
/// stores behind Blob, NamedNodeMap, ElementInternals, Range, Selection and PermissionStatus
/// objects. The sub-window maps in <c>Runtime/BrowsingContextManager.cs</c> key on
/// <see cref="JsValue"/> itself, being strong maps emptied on demand rather than weak tables, so no
/// per-object table in this bridge names an engine type. (This paragraph used to count a
/// reference-key floor, at three tables and then six; there is none.)
/// </para>
/// <para>
/// So this is a cast, and the assertion it makes is that the handle came from a Broiler.JS realm.
/// <see cref="ToEngineObject"/> throws on a handle that carries no Broiler.JS object;
/// <see cref="ToEngineValue"/> answers <see langword="null"/> instead, and
/// <c>SetAdoptedStyleSheets</c> takes that as an empty list, so that crossing does not fail loudly.
/// (This said another engine would fail loudly at the first migrated binding, because the unmigrated
/// half of the bridge could not run on one.)
/// </para>
/// <para>
/// Nothing here reaches for a provider assembly. <c>JsProviderValue</c> lives in JSEAL itself, and the
/// engine types it hands back are ones this project already references — so migrating a file lowers
/// this project's engine-reference count without a new dependency arriving to replace it.
/// </para>
/// </remarks>
internal static class JsInterop
{
    /// <summary>
    /// The engine object behind a JSEAL handle, for an operation the contract cannot express:
    /// <c>RetireIndex</c> deletes an index through it. (This said "for an unmigrated caller".)
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The handle does not carry a Broiler.JS object — either it is a primitive, or the realm belongs
    /// to a different engine. Both mean the caller cannot do what it was about to do.
    /// </exception>
    internal static JSObject ToEngineObject(JsValue value) =>
        JsProviderValue.ReferenceOf(value) as JSObject
        ?? throw new InvalidOperationException(
            $"A JSEAL handle of kind {value.Kind} does not carry a Broiler.JS object. A migrated " +
            "binding produced a value the unmigrated half of the bridge cannot hold; either the realm " +
            "is not a Broiler.JS realm, or the binding returned a primitive where an object was expected.");

    /// <summary>
    /// The engine value behind a JSEAL handle, or <see langword="null"/> when it carries none: a
    /// primitive the handle holds itself, or another engine's value. This provider's symbols and
    /// BigInts do carry one. (This said "or null for a primitive".)
    /// </summary>
    internal static JSValue? ToEngineValue(JsValue value) => JsProviderValue.ReferenceOf(value) as JSValue;

    /// <summary>
    /// A JSEAL handle over an engine object: <c>SetAdoptedStyleSheets</c> hands back its engine-made
    /// array copy through it. (This said "for a migrated callee taking one from an unmigrated caller".)
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It answers the kind the provider would have answered, and it used to answer
    /// <see cref="JsValueKind.Object"/> for everything.</b> <c>JsProviderValue.Object</c> is the
    /// wrapper for an engine object that is <em>neither callable nor an Array exotic</em> — its own
    /// summary says so — while the provider's <c>BroilerJsMarshal.Wrap</c> tests for
    /// <c>JSFunction</c> and <c>JSArray</c> first. So a handle minted here and a handle minted by the
    /// provider over the same object disagreed about kind, and <see cref="JsValue"/> compares kind
    /// <em>before</em> reference: <c>a == b</c> was false for two handles on one object.
    /// </para>
    /// <para>
    /// <b>Nothing observed it yet, and the migration is what would have.</b> This paragraph used to
    /// name two places that could see it, and both are gone rather than guarded. <c>window.frames</c>
    /// was an engine array wrapped here and unwrapped again on the line that received it, and the realm
    /// mints it now. The inline <c>on*</c> handler, which the event-dispatch path tested
    /// <c>is JSFunction</c> before wrapping and the <c>onclick</c> getter wrapped behind a plain object
    /// test, is stored as a handle and handed back as one. They were never the only two: this method is
    /// still handed an array elsewhere, and a grep for its call sites is the census, not this sentence.
    /// What is not guarded is a map keyed on a handle, and there are several. A map whose keys are
    /// all kind <c>Object</c> never cared, since a handle minted here answered that kind even before
    /// the fix; one keyed on functions would have, and <c>CustomElementsBinding</c> files each
    /// definition under its constructor. What keeps every such map safe is where its handles come
    /// from: each key, and each handle looked up, arrives from a call frame or a realm member the
    /// provider filled, never through this method. (This named <c>EventTargetRegistry</c>'s maps as
    /// the only ones and said their invariant would end when a listener record became a handle. It
    /// is one now and nothing ended: a listener is an element of a list, never a key, and it too
    /// arrives from a call frame.) Fixing the kind here was still the cheap order.
    /// </para>
    /// <para>
    /// The test order matters and mirrors the provider's: <c>JSArray</c> derives from
    /// <c>JSObject</c>, and <c>JSFunction</c> is callable, so a plain <c>JSObject</c> arm placed
    /// first would swallow both.
    /// </para>
    /// </remarks>
    internal static JsValue FromEngineObject(JSObject value) => value switch
    {
        JSFunction function => JsProviderValue.Function(function),
        JSArray array => JsProviderValue.Array(array),
        _ => JsProviderValue.Object(value),
    };
}
