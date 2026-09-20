using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JSeal;
using Broiler.JSeal.Providers;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The crossing between a JSEAL handle and the Broiler.JS object it carries, for the operations the
/// JSEAL contract cannot express yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is scaffolding, and it is meant to be deleted.</b> Two methods in this project use
/// it, and each calls itself a gap in the JSEAL contract rather than an unmigrated caller:
/// <c>SetAdoptedStyleSheets</c> in <c>DomBridge/ComputedStyle.cs</c> copies an assigned
/// array with the engine's own hole treatment, which <see cref="IJsRealm"/> cannot read back, and
/// <c>RetireIndex</c> in <c>Features/StyleSheetBinding.cs</c> deletes an index, which
/// <see cref="IJsMembers"/> has no member for. The only other caller is <c>JsInteropSeamTests</c>.
/// </para>
/// <para>
/// <b>It costs nothing at run time and it is not a conversion.</b> Under the Broiler.JS provider a
/// JSEAL object handle carries the engine's own <c>JSObject</c> — that is the provider's central
/// design rule, and it is what keeps wrapper identity and <c>el === el</c> working for an object
/// that crosses here.
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
    /// <c>RetireIndex</c> deletes an index through it.
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
    /// BigInts do carry one.
    /// </summary>
    internal static JSValue? ToEngineValue(JsValue value) => JsProviderValue.ReferenceOf(value) as JSValue;

    /// <summary>
    /// A JSEAL handle over an engine object: <c>SetAdoptedStyleSheets</c> hands back its engine-made
    /// array copy through it.
    /// </summary>
    /// <remarks>
    /// The test order matters and mirrors the provider's: <c>JSArray</c> derives from
    /// <c>JSObject</c>, and <c>JSFunction</c> is callable, so a plain <c>JSObject</c> arm placed
    /// first would swallow both.
    /// </remarks>
    internal static JsValue FromEngineObject(JSObject value) => value switch
    {
        JSFunction function => JsProviderValue.Function(function),
        JSArray array => JsProviderValue.Array(array),
        _ => JsProviderValue.Object(value),
    };
}
