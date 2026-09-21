using Broiler.JSeal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Installs a JavaScript-visible method on <paramref name="target"/>, minted through
    /// <paramref name="realm"/>.
    /// <para>
    /// <b>The member name is spelled once.</b> A JS function carries its own <c>name</c> as well as
    /// the property key it is reached by, and the bridge wants those to agree at every site — so the
    /// longhand <c>realm.DefineValue(target, "x", realm.NewMethod("x", body, n))</c> spells <c>x</c>
    /// twice and nothing but a typo can make the two differ. Here there is one string to get right.
    /// </para>
    /// <para>
    /// <paramref name="length"/> is the WebIDL operation's declared arity, which a page reads as
    /// <c>fn.length</c>. The overload without it mints a zero-arity function, which is
    /// <see cref="IJsValues.NewMethod"/>'s own default.
    /// </para>
    /// <para>
    /// <see cref="JsPropertyFlags.Default"/> is enumerable, configurable and writable — what the
    /// instance properties were and what Web IDL asks for on a prototype, so moving a member to a
    /// prototype changes only its <i>location</i>. That is why no ordinary call site spells
    /// <paramref name="flags"/>; the ones that do are saying something deliberate, as
    /// <c>localStorage</c>'s non-enumerable methods do.
    /// </para>
    /// </summary>
    internal static void DefineMethod(
        this IJsRealm realm, JsValue target, string name, int length, JsNativeFunction body,
        JsPropertyFlags flags = JsPropertyFlags.Default) =>
        realm.DefineValue(target, name, realm.NewMethod(name, body, length), flags);

    /// <inheritdoc cref="DefineMethod(IJsRealm, JsValue, string, int, JsNativeFunction, JsPropertyFlags)"/>
    internal static void DefineMethod(
        this IJsRealm realm, JsValue target, string name, JsNativeFunction body,
        JsPropertyFlags flags = JsPropertyFlags.Default) =>
        realm.DefineValue(target, name, realm.NewMethod(name, body), flags);

    /// <summary>
    /// Installs a JavaScript-visible interface object — something a page may legitimately <c>new</c> —
    /// on <paramref name="target"/>, spelling its name once as
    /// <see cref="DefineMethod(IJsRealm, JsValue, string, int, JsNativeFunction, JsPropertyFlags)"/> does.
    /// <para>
    /// A constructor is not a method: <see cref="IJsValues.NewConstructor"/> mints a prototype and its
    /// <c>constructor</c> back-reference, which a plain operation must not pay for. Reach for this only
    /// where <c>new</c> is part of the interface.
    /// </para>
    /// </summary>
    internal static void DefineConstructor(
        this IJsRealm realm, JsValue target, string name, int length, JsNativeFunction body,
        JsPropertyFlags flags = JsPropertyFlags.Default) =>
        realm.DefineValue(target, name, realm.NewConstructor(name, body, length), flags);

    /// <inheritdoc cref="DefineConstructor(IJsRealm, JsValue, string, int, JsNativeFunction, JsPropertyFlags)"/>
    internal static void DefineConstructor(
        this IJsRealm realm, JsValue target, string name, JsNativeFunction body,
        JsPropertyFlags flags = JsPropertyFlags.Default) =>
        realm.DefineValue(target, name, realm.NewConstructor(name, body), flags);
}
