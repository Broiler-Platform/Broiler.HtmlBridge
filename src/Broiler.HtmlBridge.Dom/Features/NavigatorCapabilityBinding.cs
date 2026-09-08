using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>navigator</c> members that report what the host machine can do rather than who the
/// browser is — <c>javaEnabled()</c>, <c>plugins</c>/<c>mimeTypes</c>/<c>pdfViewerEnabled</c>,
/// <c>getGamepads()</c>, <c>getBattery()</c> and <c>requestMediaKeySystemAccess()</c>. Pure static,
/// with no host contract: each answers from what the engine is, not from the document.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is a capability Broiler does not have, and every one of them has a specified
/// way of saying so — a list with nothing in it, a promise that rejects, a boolean that is false.
/// Saying it is the point: what was there before was not a negative answer but a TypeError.
/// <c>Array.from(navigator.plugins)</c> threw "Cannot convert undefined or null to object",
/// <c>navigator.javaEnabled()</c> and <c>navigator.getGamepads()</c> threw "undefined is not a
/// function", and each of those aborts the whole function that asked. Plugin and gamepad probes sit
/// in feature-detection preambles that run before anything else a script does, and
/// <c>getGamepads()</c> is called from inside animation frames, where the abort repeats every
/// frame.
/// </para>
/// <para>
/// Three of these — plugins, mime types and gamepads — answer with an empty list, which is a real
/// answer and not the same as the absence they replace: a page can iterate it, count it, and
/// conclude there is nothing there. Note that a suite scoring "did the page produce a value" will
/// still see nothing for those probes, because an empty list serializes as one; that is a property
/// of the measurement, not of the answer.
/// </para>
/// <para>
/// The module takes the realm its members are minted in rather than the script context it used to
/// raise its <c>NotSupportedError</c> against: a <c>DOMException</c> is built through the realm's own
/// <c>DOMException</c> global, which is the same object <c>DomBridge.ThrowDOMException</c> reached,
/// and everything else here is an ordinary member installation.
/// </para>
/// </remarks>
internal static class NavigatorCapabilityBinding
{
    /// <summary>
    /// Installs the capability members on <paramref name="navigator"/>.
    /// </summary>
    /// <param name="realm">The realm the members — and the rejected promise's DOMException — are minted in.</param>
    /// <param name="navigator">The <c>navigator</c> object being built.</param>
    public static void Install(IJsRealm realm, JsValue navigator)
    {
        // navigator.javaEnabled() (HTML §8.9) — specified to return false. Not "false because
        // Broiler has no Java": the method is a vestige whose only conforming answer is false.
        realm.DefineValue(navigator, "javaEnabled",
            realm.NewMethod("javaEnabled", static (in _) => JsValue.False, 0));

        // navigator.plugins / navigator.mimeTypes (HTML §8.9.1). HTML defines these as empty
        // whenever the user agent has no PDF viewer, which is the branch Broiler is on — the five
        // entries a Chromium reports are its bundled viewer, not a plugin system. `pdfViewerEnabled`
        // is the flag that decides between the two branches, so it is registered beside them rather
        // than left for a page to infer from the empty lists.
        realm.DefineValue(navigator, "plugins", BuildEmptyPluginArray(realm, "PluginArray"));
        realm.DefineValue(navigator, "mimeTypes", BuildEmptyPluginArray(realm, "MimeTypeArray"));
        realm.DefineValue(navigator, "pdfViewerEnabled", JsValue.False);

        // navigator.getGamepads() (Gamepad §2.2) — the gamepads currently connected. Broiler has no
        // gamepad input path, so none ever are, and an empty list is what the specification asks
        // for in that case.
        realm.DefineValue(navigator, "getGamepads",
            realm.NewMethod("getGamepads", (in _) => realm.NewArray(), 0));

        // navigator.getBattery() (Battery Status §4).
        realm.DefineValue(navigator, "getBattery",
            realm.NewMethod("getBattery", (in _) => GetBattery(realm), 0));

        // navigator.requestMediaKeySystemAccess() (EME §5).
        realm.DefineValue(navigator, "requestMediaKeySystemAccess",
            realm.NewMethod("requestMediaKeySystemAccess",
                (in call) => RequestMediaKeySystemAccess(in call), 2));
    }

    /// <summary>
    /// A <c>PluginArray</c>/<c>MimeTypeArray</c> with nothing in it. Array-like rather than an
    /// array: these are indexed <em>and</em> named collections, and <c>Array.from</c> — how a page
    /// most often reads one — needs only the <c>length</c>.
    /// </summary>
    private static JsValue BuildEmptyPluginArray(IJsRealm realm, string name)
    {
        var collection = realm.NewObject();

        realm.DefineValue(collection, "length", JsValue.Number(0));
        realm.DefineValue(collection, "item", NullMember(realm, "item", 1));
        realm.DefineValue(collection, "namedItem", NullMember(realm, "namedItem", 1));

        // refresh() exists only on PluginArray, and re-checks for newly installed plugins. There are
        // none to find, but a page that calls it before iterating must not lose the iteration.
        if (name == "PluginArray")
            realm.DefineValue(collection, "refresh", UndefinedMember(realm, "refresh", 0));

        return collection;
    }

    /// <summary>
    /// <c>navigator.getBattery()</c>. It resolves with a <c>BatteryManager</c> describing a system
    /// running on external power — which is what the Battery Status specification says to report
    /// when the implementation cannot see a battery: charging, fully charged, and with no time
    /// remaining to give. The alternative, rejecting, is reserved for a document that is not
    /// allowed to ask.
    /// </summary>
    private static JsValue GetBattery(IJsRealm realm)
    {
        var battery = realm.NewObject();

        realm.DefineValue(battery, "charging", JsValue.True);
        realm.DefineValue(battery, "chargingTime", JsValue.Number(0));
        realm.DefineValue(battery, "dischargingTime", JsValue.Number(double.PositiveInfinity));
        realm.DefineValue(battery, "level", JsValue.Number(1));

        // The four event-handler attributes, settable and never fired: none of the four values above
        // can change, so there is no change to deliver. A page assigns to them unconditionally.
        foreach (var handler in BatteryEventHandlers)
        {
            JsValue stored = JsValue.Null;
            realm.DefineAccessor(battery, handler,
                (in _) => stored,
                (in call) => stored = call.Length > 0 ? call[0] : JsValue.Null);
        }

        var promise = realm.NewPromise(out var resolve, out _);
        resolve(battery);
        return promise;
    }

    private static readonly string[] BatteryEventHandlers =
        ["onchargingchange", "onchargingtimechange", "ondischargingtimechange", "onlevelchange"];

    /// <summary>
    /// <c>navigator.requestMediaKeySystemAccess()</c>. Every key system is refused, because Broiler
    /// implements no Content Decryption Module — not even Clear Key. <c>NotSupportedError</c> is
    /// the rejection the specification defines for a key system the user agent does not support, so
    /// a player's existing <c>catch</c> takes its unencrypted path instead of waiting.
    /// </summary>
    private static JsValue RequestMediaKeySystemAccess(in JsCall call)
    {
        var realm = call.Realm;
        string keySystem = call.Length > 0 ? realm.ToJsString(call[0]) : string.Empty;
        var reason = BuildDomException(
            realm,
            $"The key system '{keySystem}' is not supported: no Content Decryption Module is available.",
            "NotSupportedError");

        var promise = realm.NewPromise(out _, out var reject);
        reject(reason);
        return promise;
    }

    /// <summary>
    /// A <c>DOMException</c> as a <em>value</em> — what a rejected promise carries, where
    /// <see cref="IJsCalls.DomError"/> raises the same object as an exception.
    /// </summary>
    /// <remarks>
    /// Built through the realm's own <c>DOMException</c> global, which the bridge's registration pass
    /// installs — the same lookup, the same argument order, and the same string fallback for a realm
    /// that does not have one yet.
    /// </remarks>
    private static JsValue BuildDomException(IJsRealm realm, string message, string name)
    {
        var constructor = realm.GetProperty(realm.Global, "DOMException");
        return constructor.IsFunction
            ? realm.Construct(constructor, [JsValue.String(message), JsValue.String(name)])
            : JsValue.String($"DOMException: {message} ({name})");
    }

    /// <summary>
    /// The realm's spelling of <c>DomBridge.NullFunction</c>/<c>UndefinedFunction</c> — an inert
    /// member answering <c>null</c> or <c>undefined</c>.
    /// </summary>
    /// <remarks>
    /// <c>NewConstructor</c> rather than <c>NewMethod</c> because the engine-built pair carries a
    /// prototype object and is therefore constructable, and preserving that is what makes this a
    /// refactor rather than a change. (WebIDL says an operation should not be constructable; that is
    /// a pre-existing deviation, and correcting it belongs in its own change.)
    /// </remarks>
    private static JsValue NullMember(IJsRealm realm, string name, int length = 0) =>
        realm.NewConstructor(name, static (in _) => JsValue.Null, length);

    /// <inheritdoc cref="NullMember"/>
    private static JsValue UndefinedMember(IJsRealm realm, string name, int length = 0) =>
        realm.NewConstructor(name, static (in _) => JsValue.Undefined, length);
}
