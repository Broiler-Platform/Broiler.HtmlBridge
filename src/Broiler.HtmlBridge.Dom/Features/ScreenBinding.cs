using System;
using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>window.screen</c> and the <c>Screen</c> interface (CSSOM View §4 and Screen Orientation API §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Prototype and accessors:</b> <c>Screen</c> is a Web IDL interface. Its members
/// (<c>width</c>, <c>height</c>, <c>availWidth</c>, <c>availHeight</c>, <c>availLeft</c>,
/// <c>availTop</c>, <c>colorDepth</c>, <c>pixelDepth</c>, <c>orientation</c>) are accessor
/// properties on <c>Screen.prototype</c>, not own properties of the <c>screen</c> instance.
/// The <c>screen</c> instance carries no own properties.
/// </para>
/// <para>
/// <b>Receiver checks:</b> Per Web IDL, calling any getter on <c>Screen.prototype</c> with an
/// incompatible receiver throws a <see cref="JsErrorKind.TypeError"/> ("Illegal invocation").
/// </para>
/// <para>
/// <b>Live host coupling:</b> Values are queried live from <see cref="IScreenHost"/>,
/// so window/viewport resizing dynamically updates the reported dimensions.
/// </para>
/// </remarks>
internal static class ScreenBinding
{
    private sealed class ScreenState(IScreenHost host)
    {
        public IScreenHost Host { get; } = host;
        public JsValue Orientation { get; set; } = JsValue.Missing;
    }

    private static readonly ConditionalWeakTable<object, ScreenState> Screens = new();

    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    public static JsValue Install(IJsRealm realm, JsValue window, IScreenHost host)
    {
        EnsureInterface(realm, window);

        var screenConstructor = realm.GetProperty(realm.Global, "Screen");
        var screenPrototype = screenConstructor.IsObject
            ? realm.GetProperty(screenConstructor, "prototype")
            : JsValue.Undefined;

        var screen = realm.NewObject();
        if (screenPrototype.IsObject)
        {
            realm.SetPrototype(screen, screenPrototype);
        }

        Screens.AddOrUpdate(IdentityOf(screen), new ScreenState(host));
        return screen;
    }

    private static void EnsureInterface(IJsRealm realm, JsValue window)
    {
        var existing = realm.GetProperty(realm.Global, "Screen");
        if (existing.IsObject)
        {
            if (window.IsObject && realm.GetProperty(window, "Screen").IsMissing)
                realm.SetProperty(window, "Screen", existing);
            return;
        }

        realm.EvaluateHostScript("""
            (function () {
                function Screen() { throw new TypeError("Illegal constructor"); }
                function ScreenOrientation() { throw new TypeError("Illegal constructor"); }

                if (typeof EventTarget === 'function') {
                    Object.setPrototypeOf(ScreenOrientation, EventTarget);
                    Object.setPrototypeOf(ScreenOrientation.prototype, EventTarget.prototype);
                }

                Object.defineProperty(Screen.prototype, Symbol.toStringTag, {
                    value: 'Screen', writable: false, enumerable: false, configurable: true
                });
                Object.defineProperty(ScreenOrientation.prototype, Symbol.toStringTag, {
                    value: 'ScreenOrientation', writable: false, enumerable: false, configurable: true
                });

                globalThis.Screen = Screen;
                globalThis.ScreenOrientation = ScreenOrientation;
            })();
            """, "interfaces:screen");

        var screenConstructor = realm.GetProperty(realm.Global, "Screen");
        if (screenConstructor.IsObject)
        {
            if (window.IsObject)
                realm.SetProperty(window, "Screen", screenConstructor);

            var prototype = realm.GetProperty(screenConstructor, "prototype");
            if (prototype.IsObject)
                InstallAccessors(realm, prototype);
        }

        var screenOrientationConstructor = realm.GetProperty(realm.Global, "ScreenOrientation");
        if (screenOrientationConstructor.IsObject && window.IsObject)
        {
            realm.SetProperty(window, "ScreenOrientation", screenOrientationConstructor);
        }
    }

    private static void InstallAccessors(IJsRealm realm, JsValue prototype)
    {
        Getter(realm, prototype, "availWidth", static (host, _) => JsValue.Number(host.ScreenAvailWidth));
        Getter(realm, prototype, "availHeight", static (host, _) => JsValue.Number(host.ScreenAvailHeight));
        Getter(realm, prototype, "width", static (host, _) => JsValue.Number(host.ScreenWidth));
        Getter(realm, prototype, "height", static (host, _) => JsValue.Number(host.ScreenHeight));
        Getter(realm, prototype, "colorDepth", static (host, _) => JsValue.Number(host.ScreenColorDepth));
        Getter(realm, prototype, "pixelDepth", static (host, _) => JsValue.Number(host.ScreenPixelDepth));
        Getter(realm, prototype, "availLeft", static (host, _) => JsValue.Number(host.ScreenAvailLeft));
        Getter(realm, prototype, "availTop", static (host, _) => JsValue.Number(host.ScreenAvailTop));

        realm.DefineAccessor(
            prototype,
            "orientation",
            static (in call) =>
            {
                var state = StateFor(in call, "orientation");
                if (state.Orientation.IsMissing)
                {
                    state.Orientation = ScreenOrientationBinding.Build(call.Realm, state.Host);
                }
                return state.Orientation;
            },
            null);
    }

    private static void Getter(IJsRealm realm, JsValue prototype, string name, Func<IScreenHost, IJsRealm, JsValue> read) =>
        realm.DefineAccessor(
            prototype,
            name,
            (in call) => read(StateFor(in call, name).Host, call.Realm),
            null);

    private static ScreenState StateFor(in JsCall call, string member)
    {
        if (call.This.IsObject && Screens.TryGetValue(IdentityOf(call.This), out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to read the '{member}' property from 'Screen': Illegal invocation");
    }
}
