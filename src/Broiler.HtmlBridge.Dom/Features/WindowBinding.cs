using System;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>window</c> and the <c>Window</c> interface (HTML §7.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Prototype and hierarchy:</b> <c>Window</c> is a Web IDL interface inheriting from <c>EventTarget</c>.
/// It cannot be invoked directly as a constructor or function; attempting to do so throws a
/// <see cref="JsErrorKind.TypeError"/> ("Illegal constructor").
/// </para>
/// <para>
/// <b>Global prototype chain:</b> Per HTML, <c>window</c> (the global object) inherits from
/// <c>Window.prototype</c>, which in turn inherits from <c>EventTarget.prototype</c>, so
/// <c>window instanceof Window</c> and <c>window instanceof EventTarget</c> hold.
/// </para>
/// </remarks>
internal static class WindowBinding
{
    public static void Install(IJsRealm realm, JsValue window)
    {
        EnsureInterface(realm, window);
    }

    private static void EnsureInterface(IJsRealm realm, JsValue window)
    {
        var existing = realm.GetProperty(realm.Global, "Window");
        if (existing.IsObject)
        {
            if (window.IsObject && realm.GetProperty(window, "Window").IsMissing)
                realm.SetProperty(window, "Window", existing);
            return;
        }

        realm.EvaluateHostScript("""
            (function () {
                function Window() { throw new TypeError("Illegal constructor"); }

                if (typeof EventTarget === 'function') {
                    Object.setPrototypeOf(Window, EventTarget);
                    Object.setPrototypeOf(Window.prototype, EventTarget.prototype);
                }

                Object.defineProperty(Window.prototype, Symbol.toStringTag, {
                    value: 'Window', writable: false, enumerable: false, configurable: true
                });

                globalThis.Window = Window;

                try {
                    Object.setPrototypeOf(globalThis, Window.prototype);
                } catch (e) {}
            })();
            """, "interfaces:window");

        var windowConstructor = realm.GetProperty(realm.Global, "Window");
        if (windowConstructor.IsObject)
        {
            if (window.IsObject)
                realm.SetProperty(window, "Window", windowConstructor);

            var prototype = realm.GetProperty(windowConstructor, "prototype");
            if (prototype.IsObject)
            {
                realm.SetPrototype(realm.Global, prototype);
                if (window.IsObject && window != realm.Global)
                {
                    realm.SetPrototype(window, prototype);
                }
            }
        }
    }
}
