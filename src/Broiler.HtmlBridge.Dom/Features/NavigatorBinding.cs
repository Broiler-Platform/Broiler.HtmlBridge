using System;
using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Host contract for <see cref="NavigatorBinding"/>.
/// </summary>
internal interface INavigatorHost
{
    JsValue SendBeacon(in JsCall call);
}

/// <summary>
/// <c>window.navigator</c> and the <c>Navigator</c> interface (HTML §8.9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Prototype and accessors:</b> <c>Navigator</c> is a Web IDL interface. Its standard members
/// are accessor properties on <c>Navigator.prototype</c>, not own properties of the <c>navigator</c> instance.
/// The <c>navigator</c> instance carries no own properties.
/// </para>
/// <para>
/// <b>Receiver checks:</b> Per Web IDL, calling any getter or method on <c>Navigator.prototype</c> with an
/// incompatible receiver throws a <see cref="JsErrorKind.TypeError"/> ("Illegal invocation").
/// </para>
/// </remarks>
internal static class NavigatorBinding
{
    internal sealed class NavigatorState(
        INavigatorHost host,
        string userAgent,
        bool cookiesEnabled)
    {
        public INavigatorHost Host { get; } = host;
        public string UserAgent { get; } = userAgent;
        public bool CookiesEnabled { get; } = cookiesEnabled;
        public JsValue Plugins { get; set; } = JsValue.Missing;
        public JsValue MimeTypes { get; set; } = JsValue.Missing;
        public JsValue Languages { get; set; } = JsValue.Missing;
        public JsValue Storage { get; set; } = JsValue.Missing;
        public JsValue Permissions { get; set; } = JsValue.Missing;
        public JsValue UserAgentData { get; set; } = JsValue.Missing;
        public JsValue WebkitTemporaryStorage { get; set; } = JsValue.Missing;
        public JsValue WebkitPersistentStorage { get; set; } = JsValue.Missing;
        public JsValue MediaDevices { get; set; } = JsValue.Missing;
    }

    private static readonly ConditionalWeakTable<object, NavigatorState> Navigators = new();

    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    public static JsValue Install(
        IJsRealm realm,
        JsValue window,
        INavigatorHost host,
        string userAgent,
        bool cookiesEnabled)
    {
        EnsureInterface(realm, window, userAgent);

        var navigatorConstructor = realm.GetProperty(realm.Global, "Navigator");
        var navigatorPrototype = navigatorConstructor.IsObject
            ? realm.GetProperty(navigatorConstructor, "prototype")
            : JsValue.Undefined;

        var navigator = realm.NewObject();
        if (navigatorPrototype.IsObject)
        {
            realm.SetPrototype(navigator, navigatorPrototype);
        }

        Navigators.AddOrUpdate(IdentityOf(navigator), new NavigatorState(host, userAgent, cookiesEnabled));
        return navigator;
    }

    private static void EnsureInterface(IJsRealm realm, JsValue window, string userAgent)
    {
        var existing = realm.GetProperty(realm.Global, "Navigator");
        if (existing.IsObject)
        {
            if (window.IsObject && realm.GetProperty(window, "Navigator").IsMissing)
                realm.SetProperty(window, "Navigator", existing);
            return;
        }

        realm.EvaluateHostScript("""
            (function () {
                function Navigator() { throw new TypeError("Illegal constructor"); }

                Object.defineProperty(Navigator.prototype, Symbol.toStringTag, {
                    value: 'Navigator', writable: false, enumerable: false, configurable: true
                });

                globalThis.Navigator = Navigator;
            })();
            """, "interfaces:navigator");

        var navigatorConstructor = realm.GetProperty(realm.Global, "Navigator");
        if (navigatorConstructor.IsObject)
        {
            if (window.IsObject)
                realm.SetProperty(window, "Navigator", navigatorConstructor);

            var prototype = realm.GetProperty(navigatorConstructor, "prototype");
            if (prototype.IsObject)
            {
                InstallAccessors(realm, prototype, window, userAgent);
            }
        }
    }

    private static void InstallAccessors(IJsRealm realm, JsValue prototype, JsValue window, string userAgent)
    {
        // NavigatorID
        Getter(realm, prototype, "appCodeName", static _ => JsValue.String("Mozilla"));
        Getter(realm, prototype, "appName", static _ => JsValue.String("Netscape"));
        Getter(realm, prototype, "appVersion", static state =>
        {
            var ua = state.UserAgent;
            return JsValue.String(ua.StartsWith("Mozilla/", StringComparison.Ordinal) ? ua["Mozilla/".Length..] : ua);
        });
        Getter(realm, prototype, "platform", static _ => JsValue.String("Win32"));
        Getter(realm, prototype, "product", static _ => JsValue.String("Gecko"));
        Getter(realm, prototype, "productSub", static _ => JsValue.String("20030107"));
        Getter(realm, prototype, "userAgent", static state => JsValue.String(state.UserAgent));
        Getter(realm, prototype, "vendor", static _ => JsValue.String(string.Empty));
        Getter(realm, prototype, "vendorSub", static _ => JsValue.String(string.Empty));

        // NavigatorLanguage
        Getter(realm, prototype, "language", static _ => JsValue.String("en-US"));
        realm.DefineAccessor(
            prototype,
            "languages",
            static (in call) =>
            {
                var state = StateFor(in call, "languages");
                if (state.Languages.IsMissing)
                {
                    state.Languages = call.Realm.NewArray([JsValue.String("en-US"), JsValue.String("en")]);
                }
                return state.Languages;
            },
            null);

        // NavigatorOnLine
        Getter(realm, prototype, "onLine", static _ => JsValue.True);

        // NavigatorCookies
        Getter(realm, prototype, "cookieEnabled", static state => JsValue.Boolean(state.CookiesEnabled));

        // NavigatorConcurrentHardware
        Getter(realm, prototype, "hardwareConcurrency", static _ => JsValue.Number(NavigatorIdentityBinding.HardwareConcurrency));

        // Device Memory
        Getter(realm, prototype, "deviceMemory", static _ => JsValue.Number(NavigatorIdentityBinding.DeviceMemory));

        // WebDriver
        Getter(realm, prototype, "webdriver", static _ => JsValue.True);

        // Touch
        Getter(realm, prototype, "maxTouchPoints", static _ => JsValue.Number(0));

        // NavigatorPlugins & capabilities
        realm.DefineAccessor(
            prototype,
            "plugins",
            static (in call) =>
            {
                var state = StateFor(in call, "plugins");
                if (state.Plugins.IsMissing)
                {
                    state.Plugins = DomCollectionBinding.PluginArray(call.Realm, static () => []);
                }
                return state.Plugins;
            },
            null);

        realm.DefineAccessor(
            prototype,
            "mimeTypes",
            static (in call) =>
            {
                var state = StateFor(in call, "mimeTypes");
                if (state.MimeTypes.IsMissing)
                {
                    state.MimeTypes = DomCollectionBinding.MimeTypeArray(call.Realm, static () => []);
                }
                return state.MimeTypes;
            },
            null);

        Getter(realm, prototype, "pdfViewerEnabled", static _ => JsValue.False);
        Method(realm, prototype, "javaEnabled", 0, static (in call) =>
        {
            StateForMethod(in call, "javaEnabled");
            return JsValue.False;
        });
        Method(realm, prototype, "getGamepads", 0, static (in call) =>
        {
            StateForMethod(in call, "getGamepads");
            return call.Realm.NewArray();
        });
        Method(realm, prototype, "getBattery", 0, static (in call) =>
        {
            StateForMethod(in call, "getBattery");
            return NavigatorCapabilityBinding.GetBattery(call.Realm);
        });
        Method(realm, prototype, "requestMediaKeySystemAccess", 2, static (in call) =>
        {
            StateForMethod(in call, "requestMediaKeySystemAccess");
            return NavigatorCapabilityBinding.RequestMediaKeySystemAccess(in call);
        });

        // Beacon
        Method(realm, prototype, "sendBeacon", 2, static (in call) =>
        {
            var state = StateForMethod(in call, "sendBeacon");
            return state.Host.SendBeacon(in call);
        });

        // Quota
        realm.DefineAccessor(
            prototype,
            "webkitTemporaryStorage",
            static (in call) =>
            {
                var state = StateFor(in call, "webkitTemporaryStorage");
                if (state.WebkitTemporaryStorage.IsMissing)
                {
                    state.WebkitTemporaryStorage = StorageQuotaBinding.BuildStorageQuota(call.Realm);
                }
                return state.WebkitTemporaryStorage;
            },
            null);

        realm.DefineAccessor(
            prototype,
            "webkitPersistentStorage",
            static (in call) =>
            {
                var state = StateFor(in call, "webkitPersistentStorage");
                if (state.WebkitPersistentStorage.IsMissing)
                {
                    state.WebkitPersistentStorage = StorageQuotaBinding.BuildStorageQuota(call.Realm);
                }
                return state.WebkitPersistentStorage;
            },
            null);

        // Surfaces (storage, permissions, userAgentData)
        NavigatorSurfacesBinding.EnsureInterfaces(realm, userAgent);

        realm.DefineAccessor(
            prototype,
            "storage",
            static (in call) =>
            {
                var state = StateFor(in call, "storage");
                if (state.Storage.IsMissing)
                {
                    state.Storage = NavigatorSurfacesBinding.BuildStorage(call.Realm);
                }
                return state.Storage;
            },
            null);

        realm.DefineAccessor(
            prototype,
            "permissions",
            static (in call) =>
            {
                var state = StateFor(in call, "permissions");
                if (state.Permissions.IsMissing)
                {
                    state.Permissions = NavigatorSurfacesBinding.BuildPermissions(call.Realm);
                }
                return state.Permissions;
            },
            null);

        realm.DefineAccessor(
            prototype,
            "userAgentData",
            static (in call) =>
            {
                var state = StateFor(in call, "userAgentData");
                if (state.UserAgentData.IsMissing)
                {
                    state.UserAgentData = NavigatorSurfacesBinding.BuildUserAgentData(call.Realm, state.UserAgent);
                }
                return state.UserAgentData;
            },
            null);

        // MediaDevices & MediaDeviceInfo
        MediaDevicesBinding.EnsureInterfaces(realm, window);

        realm.DefineAccessor(
            prototype,
            "mediaDevices",
            static (in call) =>
            {
                var state = StateFor(in call, "mediaDevices");
                if (state.MediaDevices.IsMissing)
                {
                    state.MediaDevices = MediaDevicesBinding.BuildMediaDevices(call.Realm);
                }
                return state.MediaDevices;
            },
            null);

        // Legacy getUserMedia
        Method(realm, prototype, "getUserMedia", 3, static (in call) =>
        {
            StateForMethod(in call, "getUserMedia");
            if (call.Length == 0)
                return JsValue.Undefined;

            if (call.Length >= 3 && call[2].IsFunction)
            {
                var err = MediaDevicesBinding.BuildDomException(call.Realm, "Requested device not found", "NotFoundError");
                call.Realm.Invoke(call[2], JsValue.Undefined, [err]);
            }
            return JsValue.Undefined;
        });
    }

    private static void Getter(IJsRealm realm, JsValue prototype, string name, Func<NavigatorState, JsValue> read) =>
        realm.DefineAccessor(
            prototype,
            name,
            (in call) => read(StateFor(in call, name)),
            null);

    private static void Method(IJsRealm realm, JsValue prototype, string name, int length, JsNativeFunction body) =>
        realm.DefineMethod(prototype, name, length, body);

    private static NavigatorState StateFor(in JsCall call, string member)
    {
        if (call.This.IsObject && call.This.ObjectIdentity is { } id && Navigators.TryGetValue(id, out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to read the '{member}' property from 'Navigator': Illegal invocation");
    }

    private static NavigatorState StateForMethod(in JsCall call, string member)
    {
        if (call.This.IsObject && call.This.ObjectIdentity is { } id && Navigators.TryGetValue(id, out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Navigator': Illegal invocation");
    }
}
