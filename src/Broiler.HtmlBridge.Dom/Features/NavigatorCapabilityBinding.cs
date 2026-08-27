using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// </remarks>
internal static class NavigatorCapabilityBinding
{
    /// <summary>
    /// Installs the capability members on <paramref name="navigator"/>.
    /// </summary>
    /// <param name="navigator">The <c>navigator</c> object being built.</param>
    /// <param name="context">
    /// The realm whose <c>DOMException</c> constructor <c>requestMediaKeySystemAccess</c> rejects
    /// with.
    /// </param>
    public static void Install(JSObject navigator, JSContext context)
    {
        // navigator.javaEnabled() (HTML §8.9) — specified to return false. Not "false because
        // Broiler has no Java": the method is a vestige whose only conforming answer is false.
        navigator.FastAddValue("javaEnabled",
            new DomFunction((in _) => JSBoolean.False, "javaEnabled", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // navigator.plugins / navigator.mimeTypes (HTML §8.9.1). HTML defines these as empty
        // whenever the user agent has no PDF viewer, which is the branch Broiler is on — the five
        // entries a Chromium reports are its bundled viewer, not a plugin system. `pdfViewerEnabled`
        // is the flag that decides between the two branches, so it is registered beside them rather
        // than left for a page to infer from the empty lists.
        navigator.FastAddValue("plugins", BuildEmptyPluginArray("PluginArray"), JSPropertyAttributes.EnumerableConfigurableValue);
        navigator.FastAddValue("mimeTypes", BuildEmptyPluginArray("MimeTypeArray"), JSPropertyAttributes.EnumerableConfigurableValue);
        navigator.FastAddValue("pdfViewerEnabled", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);

        // navigator.getGamepads() (Gamepad §2.2) — the gamepads currently connected. Broiler has no
        // gamepad input path, so none ever are, and an empty list is what the specification asks
        // for in that case.
        navigator.FastAddValue("getGamepads",
            new DomFunction((in _) => new JSArray(), "getGamepads", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // navigator.getBattery() (Battery Status §4).
        navigator.FastAddValue("getBattery",
            new DomFunction((in _) => GetBattery(), "getBattery", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // navigator.requestMediaKeySystemAccess() (EME §5).
        navigator.FastAddValue("requestMediaKeySystemAccess",
            new DomFunction((in a) => RequestMediaKeySystemAccess(context, in a), "requestMediaKeySystemAccess", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// A <c>PluginArray</c>/<c>MimeTypeArray</c> with nothing in it. Array-like rather than an
    /// array: these are indexed <em>and</em> named collections, and <c>Array.from</c> — how a page
    /// most often reads one — needs only the <c>length</c>.
    /// </summary>
    private static JSObject BuildEmptyPluginArray(string name)
    {
        var collection = new JSObject();

        collection.FastAddValue("length", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        collection.FastAddValue("item", DomBridge.NullFunction("item", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        collection.FastAddValue("namedItem", DomBridge.NullFunction("namedItem", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // refresh() exists only on PluginArray, and re-checks for newly installed plugins. There are
        // none to find, but a page that calls it before iterating must not lose the iteration.
        if (name == "PluginArray")
            collection.FastAddValue("refresh", DomBridge.UndefinedFunction("refresh", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        return collection;
    }

    /// <summary>
    /// <c>navigator.getBattery()</c>. It resolves with a <c>BatteryManager</c> describing a system
    /// running on external power — which is what the Battery Status specification says to report
    /// when the implementation cannot see a battery: charging, fully charged, and with no time
    /// remaining to give. The alternative, rejecting, is reserved for a document that is not
    /// allowed to ask.
    /// </summary>
    private static JSValue GetBattery()
    {
        var battery = new JSObject();

        battery.FastAddValue("charging", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        battery.FastAddValue("chargingTime", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        battery.FastAddValue("dischargingTime", new JSNumber(double.PositiveInfinity), JSPropertyAttributes.EnumerableConfigurableValue);
        battery.FastAddValue("level", new JSNumber(1), JSPropertyAttributes.EnumerableConfigurableValue);

        // The four event-handler attributes, settable and never fired: none of the four values above
        // can change, so there is no change to deliver. A page assigns to them unconditionally.
        foreach (var handler in BatteryEventHandlers)
        {
            JSValue stored = JSNull.Value;
            battery.FastAddProperty(handler,
                new DomFunction((in _) => stored, "get " + handler),
                new DomFunction((in a) => stored = a.Length > 0 ? a[0] : JSNull.Value, "set " + handler),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        return new JSPromise((resolve, _) => resolve(battery));
    }

    private static readonly string[] BatteryEventHandlers =
        ["onchargingchange", "onchargingtimechange", "ondischargingtimechange", "onlevelchange"];

    /// <summary>
    /// <c>navigator.requestMediaKeySystemAccess()</c>. Every key system is refused, because Broiler
    /// implements no Content Decryption Module — not even Clear Key. <c>NotSupportedError</c> is
    /// the rejection the specification defines for a key system the user agent does not support, so
    /// a player's existing <c>catch</c> takes its unencrypted path instead of waiting.
    /// </summary>
    private static JSValue RequestMediaKeySystemAccess(JSContext context, in Arguments a)
    {
        string keySystem = a.Length > 0 ? a[0].ToString() : string.Empty;
        var reason = BuildDomException(
            context,
            $"The key system '{keySystem}' is not supported: no Content Decryption Module is available.",
            "NotSupportedError");

        return new JSPromise((_, reject) => reject(reason));
    }

    /// <summary>
    /// A <c>DOMException</c> as a <em>value</em> — what a rejected promise carries, where
    /// <c>DomBridge.ThrowDOMException</c> raises the same object as an exception.
    /// </summary>
    private static JSValue BuildDomException(JSContext context, string message, string name) =>
        context["DOMException"] is Broiler.JavaScript.BuiltIns.Function.JSFunction constructor
            ? constructor.CreateInstance(new Arguments(constructor, new JSString(message), new JSString(name)))
            : new JSString($"DOMException: {message} ({name})");
}
