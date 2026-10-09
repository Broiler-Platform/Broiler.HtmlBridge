using System;
using System.Runtime.CompilerServices;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The worker global interfaces and prototypes (HTML §10.1–10.3 and Device Memory):
/// <c>WorkerGlobalScope</c>, <c>DedicatedWorkerGlobalScope</c>, <c>WorkerNavigator</c>,
/// and <c>WorkerLocation</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Prototype hierarchy:</b> <c>DedicatedWorkerGlobalScope</c> inherits from
/// <c>WorkerGlobalScope</c>, which inherits from <c>EventTarget</c> (if present) or
/// <c>Object.prototype</c>. The global object's prototype is set to
/// <c>DedicatedWorkerGlobalScope.prototype</c>, ensuring <c>self instanceof DedicatedWorkerGlobalScope</c>
/// and <c>self instanceof WorkerGlobalScope</c> are both true.
/// </para>
/// <para>
/// <b>WorkerNavigator:</b> Web IDL attributes (<c>userAgent</c>, <c>platform</c>,
/// <c>hardwareConcurrency</c>, <c>deviceMemory</c>, <c>language</c>, etc.) are accessor
/// properties on <c>WorkerNavigator.prototype</c>, not own properties of the <c>navigator</c> instance.
/// Incompatible receivers throw a <see cref="JsErrorKind.TypeError"/> ("Illegal invocation").
/// </para>
/// <para>
/// <b>Worker-only exposure:</b> These interfaces are installed only in worker realms,
/// never in window realms.
/// </para>
/// </remarks>
internal static class WorkerEnvironmentBinding
{
    private sealed class WorkerNavigatorState(string userAgent, int hardwareConcurrency, double deviceMemory)
    {
        public string AppCodeName => "Mozilla";
        public string AppName => "Netscape";
        public string Product => "Gecko";
        public string ProductSub => "20030107";
        public string UserAgent { get; } = userAgent;
        public string AppVersion => UserAgent.StartsWith("Mozilla/", StringComparison.Ordinal)
            ? UserAgent["Mozilla/".Length..]
            : UserAgent;
        public string Platform => "Win32";
        public string Vendor => "";
        public string VendorSub => "";
        public bool Webdriver => true;
        public string Language => "en-US";
        public bool OnLine => true;
        public int HardwareConcurrency { get; } = hardwareConcurrency;
        public double DeviceMemory { get; } = deviceMemory;
        public JsValue Languages { get; set; } = JsValue.Missing;
    }

    private sealed class WorkerScopeState(string name, JsValue workerGlobal, JsValue navigator, JsValue? location)
    {
        public string Name { get; } = name;
        public JsValue WorkerGlobal { get; } = workerGlobal;
        public JsValue Navigator { get; } = navigator;
        public JsValue? Location { get; } = location;
    }

    private static readonly ConditionalWeakTable<object, WorkerNavigatorState> Navigators = new();
    private static readonly ConditionalWeakTable<object, WorkerScopeState> Scopes = new();

    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    public static JsValue Install(IJsRealm realm, string workerName, JsValue? location)
    {
        EnsureInterfaces(realm);

        var global = realm.Global;

        // 1. WorkerNavigator setup
        var workerNavigatorConstructor = realm.GetProperty(global, "WorkerNavigator");
        var workerNavigatorPrototype = workerNavigatorConstructor.IsObject
            ? realm.GetProperty(workerNavigatorConstructor, "prototype")
            : JsValue.Undefined;

        var navigator = realm.NewObject();
        if (workerNavigatorPrototype.IsObject)
        {
            realm.SetPrototype(navigator, workerNavigatorPrototype);
            InstallNavigatorAccessors(realm, workerNavigatorPrototype);
        }

        Navigators.AddOrUpdate(IdentityOf(navigator), new WorkerNavigatorState(
            userAgent: global::Broiler.Net.Http.BroilerUserAgent.Value,
            hardwareConcurrency: NavigatorIdentityBinding.HardwareConcurrency,
            deviceMemory: NavigatorIdentityBinding.DeviceMemory));

        if (location is { IsObject: true } loc)
        {
            var workerLocationConstructor = realm.GetProperty(global, "WorkerLocation");
            if (workerLocationConstructor.IsObject)
            {
                var locProto = realm.GetProperty(workerLocationConstructor, "prototype");
                if (locProto.IsObject)
                    realm.SetPrototype(loc, locProto);
            }
        }

        // 2. WorkerGlobalScope and DedicatedWorkerGlobalScope accessors
        var workerGlobalScopeConstructor = realm.GetProperty(global, "WorkerGlobalScope");
        var workerGlobalScopePrototype = workerGlobalScopeConstructor.IsObject
            ? realm.GetProperty(workerGlobalScopeConstructor, "prototype")
            : JsValue.Undefined;

        var dedicatedWorkerGlobalScopeConstructor = realm.GetProperty(global, "DedicatedWorkerGlobalScope");
        var dedicatedWorkerGlobalScopePrototype = dedicatedWorkerGlobalScopeConstructor.IsObject
            ? realm.GetProperty(dedicatedWorkerGlobalScopeConstructor, "prototype")
            : JsValue.Undefined;

        Scopes.AddOrUpdate(IdentityOf(global), new WorkerScopeState(workerName, global, navigator, location));

        if (workerGlobalScopePrototype.IsObject)
        {
            InstallWorkerGlobalScopeAccessors(realm, workerGlobalScopePrototype);
        }

        if (dedicatedWorkerGlobalScopePrototype.IsObject)
        {
            InstallDedicatedWorkerGlobalScopeAccessors(realm, dedicatedWorkerGlobalScopePrototype);
        }

        // Expose navigator on global as an accessor delegating to the same instance
        realm.DefineAccessor(
            global,
            "navigator",
            static (in call) => ScopeStateFor(in call, "navigator").Navigator,
            null);

        return navigator;
    }

    private static void EnsureInterfaces(IJsRealm realm)
    {
        realm.EvaluateHostScript("""
            (function () {
                function WorkerGlobalScope() { throw new TypeError("Failed to construct 'WorkerGlobalScope': Illegal constructor"); }
                function DedicatedWorkerGlobalScope() { throw new TypeError("Failed to construct 'DedicatedWorkerGlobalScope': Illegal constructor"); }
                function WorkerNavigator() { throw new TypeError("Failed to construct 'WorkerNavigator': Illegal constructor"); }
                function WorkerLocation() { throw new TypeError("Failed to construct 'WorkerLocation': Illegal constructor"); }

                if (typeof EventTarget === 'function') {
                    Object.setPrototypeOf(WorkerGlobalScope, EventTarget);
                    Object.setPrototypeOf(WorkerGlobalScope.prototype, EventTarget.prototype);
                }

                Object.setPrototypeOf(DedicatedWorkerGlobalScope, WorkerGlobalScope);
                Object.setPrototypeOf(DedicatedWorkerGlobalScope.prototype, WorkerGlobalScope.prototype);

                Object.defineProperty(WorkerGlobalScope.prototype, Symbol.toStringTag, {
                    value: 'WorkerGlobalScope', writable: false, enumerable: false, configurable: true
                });
                Object.defineProperty(DedicatedWorkerGlobalScope.prototype, Symbol.toStringTag, {
                    value: 'DedicatedWorkerGlobalScope', writable: false, enumerable: false, configurable: true
                });
                Object.defineProperty(WorkerNavigator.prototype, Symbol.toStringTag, {
                    value: 'WorkerNavigator', writable: false, enumerable: false, configurable: true
                });
                Object.defineProperty(WorkerLocation.prototype, Symbol.toStringTag, {
                    value: 'WorkerLocation', writable: false, enumerable: false, configurable: true
                });

                globalThis.WorkerGlobalScope = WorkerGlobalScope;
                globalThis.DedicatedWorkerGlobalScope = DedicatedWorkerGlobalScope;
                globalThis.WorkerNavigator = WorkerNavigator;
                globalThis.WorkerLocation = WorkerLocation;

                try {
                    Object.setPrototypeOf(globalThis, DedicatedWorkerGlobalScope.prototype);
                } catch (e) {}
            })();
            """, "interfaces:worker-environment");

        // Ensure prototype is set at realm level as well
        var dedicatedWorkerGlobalScopeConstructor = realm.GetProperty(realm.Global, "DedicatedWorkerGlobalScope");
        if (dedicatedWorkerGlobalScopeConstructor.IsObject)
        {
            var dedicatedProto = realm.GetProperty(dedicatedWorkerGlobalScopeConstructor, "prototype");
            if (dedicatedProto.IsObject)
            {
                realm.SetPrototype(realm.Global, dedicatedProto);
            }
        }
    }

    private static void InstallNavigatorAccessors(IJsRealm realm, JsValue prototype)
    {
        NavigatorGetter(realm, prototype, "appCodeName", static state => JsValue.String(state.AppCodeName));
        NavigatorGetter(realm, prototype, "appName", static state => JsValue.String(state.AppName));
        NavigatorGetter(realm, prototype, "appVersion", static state => JsValue.String(state.AppVersion));
        NavigatorGetter(realm, prototype, "platform", static state => JsValue.String(state.Platform));
        NavigatorGetter(realm, prototype, "product", static state => JsValue.String(state.Product));
        NavigatorGetter(realm, prototype, "productSub", static state => JsValue.String(state.ProductSub));
        NavigatorGetter(realm, prototype, "userAgent", static state => JsValue.String(state.UserAgent));
        NavigatorGetter(realm, prototype, "vendor", static state => JsValue.String(state.Vendor));
        NavigatorGetter(realm, prototype, "vendorSub", static state => JsValue.String(state.VendorSub));
        NavigatorGetter(realm, prototype, "webdriver", static state => JsValue.Boolean(state.Webdriver));
        NavigatorGetter(realm, prototype, "language", static state => JsValue.String(state.Language));
        NavigatorGetter(realm, prototype, "onLine", static state => JsValue.Boolean(state.OnLine));
        NavigatorGetter(realm, prototype, "hardwareConcurrency", static state => JsValue.Number(state.HardwareConcurrency));
        NavigatorGetter(realm, prototype, "deviceMemory", static state => JsValue.Number(state.DeviceMemory));

        realm.DefineAccessor(
            prototype,
            "languages",
            static (in call) =>
            {
                var state = NavigatorStateFor(in call, "languages");
                if (state.Languages.IsMissing)
                {
                    state.Languages = call.Realm.NewArray([JsValue.String("en-US"), JsValue.String("en")]);
                }
                return state.Languages;
            },
            null);
    }

    private static void NavigatorGetter(IJsRealm realm, JsValue prototype, string name, Func<WorkerNavigatorState, JsValue> read) =>
        realm.DefineAccessor(
            prototype,
            name,
            (in call) => read(NavigatorStateFor(in call, name)),
            null);

    private static WorkerNavigatorState NavigatorStateFor(in JsCall call, string member)
    {
        if (call.This.IsObject && Navigators.TryGetValue(IdentityOf(call.This), out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to read the '{member}' property from 'WorkerNavigator': Illegal invocation");
    }

    private static void InstallWorkerGlobalScopeAccessors(IJsRealm realm, JsValue prototype)
    {
        realm.DefineAccessor(
            prototype,
            "navigator",
            static (in call) => ScopeStateFor(in call, "navigator").Navigator,
            null);

        realm.DefineAccessor(
            prototype,
            "self",
            static (in call) => ScopeStateFor(in call, "self").WorkerGlobal,
            null);

        realm.DefineAccessor(
            prototype,
            "location",
            static (in call) =>
            {
                var scope = ScopeStateFor(in call, "location");
                return scope.Location ?? JsValue.Undefined;
            },
            null);
    }

    private static void InstallDedicatedWorkerGlobalScopeAccessors(IJsRealm realm, JsValue prototype)
    {
        realm.DefineAccessor(
            prototype,
            "name",
            static (in call) => JsValue.String(ScopeStateFor(in call, "name").Name),
            null);
    }

    private static WorkerScopeState ScopeStateFor(in JsCall call, string member)
    {
        // For [Global] interface members, an unqualified or nullish call defaults to the global object.
        var target = call.This.IsNullish ? call.Realm.Global : call.This;
        if (target.IsObject && Scopes.TryGetValue(IdentityOf(target), out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to read the '{member}' property from 'WorkerGlobalScope': Illegal invocation");
    }
}
