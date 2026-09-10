using System.Runtime.CompilerServices;

using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Three of <c>navigator</c>'s object-valued surfaces: <c>storage</c> (<c>StorageManager</c>),
/// <c>permissions</c> (<c>Permissions</c> and <c>PermissionStatus</c>) and <c>userAgentData</c>
/// (<c>NavigatorUAData</c>).
/// </summary>
/// <remarks>
/// <para>
/// These are whole APIs rather than values, so each needed its own decision — the roadmap's test is
/// whether a present object answers a page's <c>'x' in navigator</c> detection <em>more</em>
/// misleadingly than absence does, which is what kept <c>speechSynthesis</c> and
/// <c>navigator.bluetooth</c> out. These three pass it, and for the same reason in each case: the
/// question the interface exists to answer is one Broiler can answer truthfully.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b><c>navigator.storage</c></b> reports quota-managed storage — IndexedDB, the Cache API, the
/// origin private file system. Broiler implements none of them, so the honest estimate is
/// <c>{usage: 0, quota: 0}</c> and the honest persistence answer is <see langword="false"/>. That is
/// the same pair the already-present <c>navigator.webkitTemporaryStorage</c> reports, which is the
/// deprecated interface for the same question; the two would have disagreed by one being absent.
/// <c>getDirectory()</c> is deliberately <em>not</em> here: the origin private file system's
/// feature-detect is exactly <c>'getDirectory' in navigator.storage</c>, and there is no file system
/// to hand back.
/// </description></item>
/// <item><description>
/// <b><c>navigator.permissions</c></b> asks whether a permission-gated capability is available.
/// Broiler grants none of them and will not prompt, so every query answers <c>"denied"</c> — a real,
/// specified state, and the one <c>Notification.permission</c> already reports for the single
/// capability that had an answer at all. Note this differs from Chromium's measured <c>"prompt"</c>,
/// and deliberately: <c>"prompt"</c> promises a dialog that this engine has no surface to show.
/// </description></item>
/// <item><description>
/// <b><c>navigator.userAgentData</c></b> is identity, and identity is the one thing the bridge
/// already reports carefully — every member here is derived from the single
/// <c>BroilerUserAgent.Value</c> string, so the structured form and the string cannot disagree. That
/// derivation is the whole argument for including it: a site that reads both is entitled to one
/// answer.
/// </description></item>
/// </list>
/// <para>
/// <b>The three that stay absent, and why.</b> <c>navigator.connection</c> claims the user agent can
/// report the connection's quality — <c>effectiveType</c>, <c>rtt</c>, <c>downlink</c> — and Broiler
/// measures none of it, so any value would be an invention rather than a negative answer; there is
/// no "no connection information" state in the interface. <c>navigator.mediaDevices</c> and
/// <c>navigator.mediaCapabilities</c> are media surfaces whose capability decisions belong with the
/// rest of media rather than here.
/// </para>
/// <para>
/// The members live on the interface prototypes, and each of the three is a singleton, so no
/// per-instance state is needed for them at all; only a <c>PermissionStatus</c>, of which there is
/// one per query, carries its own.
/// </para>
/// <para>
/// <b>The four interface objects are declared in host-authored JavaScript.</b> That is the one
/// <see cref="IJsSource.EvaluateHostScript"/> site in this file, and it is host script by every test
/// the contract states: the source is written here, ships with this repository, and is not subject to
/// the page's content policy. It is script rather than four <c>NewConstructor</c> calls because what
/// it declares is the <em>illegal-constructor</em> throw each interface object must perform, which is
/// three lines of JavaScript and would be four host functions plus their prototypes otherwise.
/// </para>
/// </remarks>
internal static class NavigatorSurfacesBinding
{
    /// <summary>
    /// The <c>PermissionName</c> values a query accepts. Anything else is a <c>TypeError</c>, which
    /// is what a browser does — the enum is validated before the permission is looked at, so a typo
    /// is reported as a typo rather than as a denial.
    /// </summary>
    private static readonly HashSet<string> PermissionNames = new(StringComparer.Ordinal)
    {
        "accelerometer", "ambient-light-sensor", "background-fetch", "background-sync", "bluetooth",
        "camera", "clipboard-read", "clipboard-write", "display-capture", "geolocation", "gyroscope",
        "idle-detection", "local-fonts", "magnetometer", "microphone", "midi", "nfc", "notifications",
        "payment-handler", "periodic-background-sync", "persistent-storage", "push",
        "screen-wake-lock", "speaker-selection", "storage-access", "system-wake-lock",
        "top-level-storage-access", "window-management", "xr-spatial-tracking",
    };

    public static void Install(IJsRealm realm, JsValue navigator, string userAgent)
    {
        realm.EvaluateHostScript("""
            (function () {
                // None of the four is constructible: they come from navigator, and from query().
                function StorageManager() { throw new TypeError("Failed to construct 'StorageManager': Illegal constructor"); }
                function Permissions() { throw new TypeError("Failed to construct 'Permissions': Illegal constructor"); }
                function PermissionStatus() { throw new TypeError("Failed to construct 'PermissionStatus': Illegal constructor"); }
                function NavigatorUAData() { throw new TypeError("Failed to construct 'NavigatorUAData': Illegal constructor"); }
                globalThis.StorageManager = StorageManager;
                globalThis.Permissions = Permissions;
                globalThis.PermissionStatus = PermissionStatus;
                globalThis.NavigatorUAData = NavigatorUAData;
            })();
            """, "polyfill:navigator-surfaces");

        var hasStorage = TryInstanceOf(realm, "StorageManager", out var storage, out var storagePrototype);
        var hasPermissions = TryInstanceOf(realm, "Permissions", out var permissions, out var permissionsPrototype);
        var hasUserAgentData = TryInstanceOf(realm, "NavigatorUAData", out var userAgentData, out var userAgentDataPrototype);
        if (!hasStorage || !hasPermissions || !hasUserAgentData ||
            !TryPrototypeOf(realm, "PermissionStatus", out var statusPrototype))
            return;

        InstallStorageManager(realm, storagePrototype);
        InstallPermissions(realm, permissionsPrototype, statusPrototype);
        InstallUserAgentData(realm, userAgentDataPrototype, userAgent);

        Add(realm, navigator, "storage", storage);
        Add(realm, navigator, "permissions", permissions);
        Add(realm, navigator, "userAgentData", userAgentData);
    }

    // -------- StorageManager --------

    private static void InstallStorageManager(IJsRealm realm, JsValue prototype)
    {
        // estimate() — the origin's quota-managed usage and quota. Both zero: nothing is stored
        // because none of the backends this interface counts exists. localStorage, sessionStorage
        // and document.cookie all work and have never been counted here by any browser.
        Method(realm, prototype, "estimate", 0, static (in call) =>
        {
            var estimate = call.Realm.NewObject();
            call.Realm.DefineValue(estimate, "usage", JsValue.Number(0));
            call.Realm.DefineValue(estimate, "quota", JsValue.Number(0));
            return Resolved(call.Realm, estimate);
        });

        // persisted() / persist() — whether the origin's storage is exempt from eviction, and a
        // request to make it so. False and false: there is no storage to persist, and a persist()
        // that resolved true would promise durability for nothing.
        Method(realm, prototype, "persisted", 0, static (in call) => Resolved(call.Realm, JsValue.False));
        Method(realm, prototype, "persist", 0, static (in call) => Resolved(call.Realm, JsValue.False));
    }

    // -------- Permissions --------

    /// <summary>
    /// Installs <c>query()</c> and the two <c>PermissionStatus</c> accessors.
    /// </summary>
    /// <remarks>
    /// <b>The per-status name is held in a weak table keyed by the status object, and it is weak
    /// again.</b> It became a plain dictionary on the reasoning that a handle cannot key a
    /// <c>ConditionalWeakTable</c> - a <see cref="JsValue"/> is a struct, and such a table needs a
    /// class key - which cost a page that queries permissions in a loop one entry per query for the
    /// life of its document. The struct was never the key: <see cref="JsValue.ObjectIdentity"/> is
    /// the reference the handle carries, which a provider already has to make canonical per object.
    /// The remark that stood here closed "it goes away when a status can carry host state of its
    /// own", and this is that, arriving from the other direction.
    /// </remarks>
    private static void InstallPermissions(IJsRealm realm, JsValue prototype, JsValue statusPrototype)
    {
        var statusNames = new ConditionalWeakTable<object, string>();

        Method(realm, prototype, "query", 1, (in call) =>
        {
            var callRealm = call.Realm;

            // The descriptor's `name` is coerced through the realm, not rendered from the handle: a
            // page may pass anything with a toString, and what it stringifies to is the enum value
            // being asked for.
            var descriptor = call[0];
            var name = descriptor.IsObject && callRealm.GetProperty(descriptor, "name") is { IsMissing: false } requested
                ? callRealm.ToJsString(requested)
                : string.Empty;

            if (!PermissionNames.Contains(name))
            {
                // Rejected rather than thrown, and a TypeError rather than a denial: the enum is
                // validated before the permission is looked at, so a typo is reported as a typo.
                return Rejected(callRealm,
                    "Failed to execute 'query' on 'Permissions': Failed to read the 'name' property " +
                    $"from 'PermissionDescriptor': The provided value '{name}' is not a valid enum value " +
                    "of type PermissionName.");
            }

            var status = callRealm.NewObject();
            callRealm.SetPrototype(status, statusPrototype);
            statusNames.AddOrUpdate(
                status.ObjectIdentity ?? throw new InvalidOperationException(
                    "a PermissionStatus registry was keyed on a handle that is not an object"),
                name);
            return Resolved(callRealm, status);
        });

        Getter(realm, statusPrototype, "name", status =>
            JsValue.String(
                status.ObjectIdentity is { } identity && statusNames.TryGetValue(identity, out var name)
                    ? name
                    : string.Empty));

        // Denied, for every capability. Broiler grants none of them and has no surface to prompt on,
        // so "prompt" — which is what a browser answers before the user has been asked — would
        // promise a dialog that never comes. This is the state Notification.permission already
        // reports, for the same reason.
        Getter(realm, statusPrototype, "state", static _ => JsValue.String("denied"));

        // The state never changes, so this handler is never called — which is the correct behaviour
        // rather than a missing one. It is present because a page assigns to it unconditionally.
        realm.DefineValue(statusPrototype, "onchange", JsValue.Null);
    }

    // -------- NavigatorUAData --------

    /// <summary>
    /// Installs the User-Agent Client Hints members, every one derived from
    /// <paramref name="userAgent"/> so the structured identity and the string cannot disagree.
    /// </summary>
    private static void InstallUserAgentData(IJsRealm realm, JsValue prototype, string userAgent)
    {
        var (brand, version) = ProductFrom(userAgent);
        var majorVersion = version.Split('.')[0];
        var platform = PlatformFrom(userAgent);
        var platformVersion = PlatformVersionFrom(userAgent);
        var is64Bit = userAgent.Contains("x64", StringComparison.Ordinal) ||
                      userAgent.Contains("Win64", StringComparison.Ordinal) ||
                      userAgent.Contains("x86_64", StringComparison.Ordinal);

        // The low-entropy trio, readable without a permission. `brands` carries the major version
        // only, which is what makes it low-entropy; the full version is behind
        // getHighEntropyValues.
        //
        // Each getter mints its answer in the realm the read is happening in — which is this one, and
        // is why the accessor takes the receiver rather than closing over a realm handed in here.
        GetterInRealm(realm, prototype, "brands", (callRealm, _) => BrandList(callRealm, brand, majorVersion));
        Getter(realm, prototype, "mobile", static _ => JsValue.False);
        Getter(realm, prototype, "platform", _ => JsValue.String(platform));

        // One GREASE brand is what a browser adds here to keep sites from hard-coding the list.
        // Broiler reports its own brand and nothing else: an invented second entry would be a claim
        // about a product that does not exist, and the anti-ossification argument is a browser-market
        // one rather than a correctness one.
        Method(realm, prototype, "toJSON", 0,
            (in call) => LowEntropyObject(call.Realm, brand, majorVersion, platform));

        Method(realm, prototype, "getHighEntropyValues", 1, (in call) =>
        {
            var callRealm = call.Realm;
            var result = LowEntropyObject(callRealm, brand, majorVersion, platform);
            var hints = RequestedHints(in call);

            // Each hint is answered from the user agent string or from a fact about this engine.
            // A hint that is not asked for is absent, which is the interface's own shape: the
            // caller names what it wants and gets exactly that.
            if (hints.Contains("architecture"))
                callRealm.DefineValue(result, "architecture", JsValue.String("x86"));
            if (hints.Contains("bitness"))
                callRealm.DefineValue(result, "bitness", JsValue.String(is64Bit ? "64" : "32"));
            if (hints.Contains("model"))
                callRealm.DefineValue(result, "model", JsValue.String(string.Empty));
            if (hints.Contains("platformVersion"))
                callRealm.DefineValue(result, "platformVersion", JsValue.String(platformVersion));
            if (hints.Contains("uaFullVersion"))
                callRealm.DefineValue(result, "uaFullVersion", JsValue.String(version));
            if (hints.Contains("fullVersionList"))
                callRealm.DefineValue(result, "fullVersionList", BrandList(callRealm, brand, version));
            if (hints.Contains("wow64"))
                callRealm.DefineValue(result, "wow64", JsValue.False);
            if (hints.Contains("formFactors"))
            {
                callRealm.DefineValue(result, "formFactors",
                    callRealm.NewArray([JsValue.String("Desktop")]));
            }

            return Resolved(callRealm, result);
        });
    }

    private static JsValue LowEntropyObject(IJsRealm realm, string brand, string majorVersion, string platform)
    {
        var result = realm.NewObject();
        realm.DefineValue(result, "brands", BrandList(realm, brand, majorVersion));
        realm.DefineValue(result, "mobile", JsValue.False);
        realm.DefineValue(result, "platform", JsValue.String(platform));
        return result;
    }

    private static JsValue BrandList(IJsRealm realm, string brand, string version)
    {
        var entry = realm.NewObject();
        realm.DefineValue(entry, "brand", JsValue.String(brand));
        realm.DefineValue(entry, "version", JsValue.String(version));
        return realm.NewArray([entry]);
    }

    private static HashSet<string> RequestedHints(in JsCall call)
    {
        var hints = new HashSet<string>(StringComparer.Ordinal);
        if (call.Length == 0 || !call[0].IsObject)
            return hints;

        var realm = call.Realm;
        var list = call[0];

        // `length` is read and coerced rather than assumed: the argument is a sequence<DOMString> in
        // WebIDL, so an array-like with a string length is a legitimate caller.
        var length = (int)realm.ToNumber(realm.GetProperty(list, "length"));
        for (var index = 0; index < length; index++)
        {
            var hint = realm.GetIndex(list, (uint)index);

            // A hole, a null and an undefined are all skipped, as before — IsNullish is the three of
            // them in one question.
            if (!hint.IsNullish)
                hints.Add(realm.ToJsString(hint));
        }

        return hints;
    }

    /// <summary>
    /// The product token and its version — <c>Broiler/1.0</c> in the string this engine reports.
    /// The last token wins, which is where every user agent puts the product that is actually
    /// speaking.
    /// </summary>
    private static (string Brand, string Version) ProductFrom(string userAgent)
    {
        var product = userAgent.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(token => token.Contains('/') && !token.StartsWith("Mozilla/", StringComparison.Ordinal));

        if (product is null)
            return ("Broiler", "1.0");

        var separator = product.IndexOf('/');
        return (product[..separator], product[(separator + 1)..]);
    }

    /// <summary>
    /// The UA-CH platform name for the platform token the user agent string carries. It follows the
    /// string rather than the host machine deliberately: <c>navigator.platform</c> already reports
    /// <c>Win32</c> from the same claim, and a site reading both is entitled to one answer.
    /// </summary>
    private static string PlatformFrom(string userAgent) =>
        userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
        : userAgent.Contains("Mac OS X", StringComparison.Ordinal) ? "macOS"
        : userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
        : userAgent.Contains("Linux", StringComparison.Ordinal) ? "Linux"
        : "Unknown";

    /// <summary>
    /// The platform version, from the same token. <c>Windows NT 10.0</c> is UA-CH's <c>10.0.0</c> —
    /// the mapping a browser applies, not an invented third component; an unrecognised platform
    /// answers the empty string, which is the interface's own "not known".
    /// </summary>
    private static string PlatformVersionFrom(string userAgent)
    {
        const string marker = "Windows NT ";
        var start = userAgent.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;

        var rest = userAgent[(start + marker.Length)..];
        var end = rest.IndexOfAny([';', ')', ' ']);
        var ntVersion = end < 0 ? rest : rest[..end];
        return ntVersion.Length == 0 ? string.Empty : $"{ntVersion}.0";
    }

    // -------- plumbing --------

    /// <summary>Mints the one instance of a singleton interface, linked to its prototype.</summary>
    private static bool TryInstanceOf(IJsRealm realm, string interfaceName, out JsValue instance, out JsValue prototype)
    {
        instance = JsValue.Undefined;
        if (!TryPrototypeOf(realm, interfaceName, out prototype))
            return false;

        // A fresh ordinary object re-pointed at the interface prototype — what the engine-typed
        // object initialiser with its base-prototype assignment said before, in the realm's
        // vocabulary.
        instance = realm.NewObject();
        realm.SetPrototype(instance, prototype);
        return true;
    }

    /// <summary>The <c>prototype</c> object of a global interface function, if it has one.</summary>
    private static bool TryPrototypeOf(IJsRealm realm, string interfaceName, out JsValue prototype)
    {
        prototype = JsValue.Undefined;

        var constructor = realm.GetProperty(realm.Global, interfaceName);
        if (!constructor.IsObject)
            return false;

        var candidate = realm.GetProperty(constructor, "prototype");
        if (!candidate.IsObject)
            return false;

        prototype = candidate;
        return true;
    }

    private static void Method(IJsRealm realm, JsValue prototype, string name, int length, JsNativeFunction body) =>
        realm.DefineValue(prototype, name, realm.NewMethod(name, body, length));

    /// <summary>
    /// A read-only accessor on an interface prototype, whose getter is handed the receiver.
    /// </summary>
    /// <remarks>
    /// A non-object receiver answers <c>undefined</c> rather than throwing, which is what the
    /// engine-typed receiver test did: these are read off the singleton instances, and a page that
    /// calls the getter on something else gets nothing rather than an exception it did not provoke.
    /// The realm names the function <c>get {name}</c> and makes it non-constructable itself.
    /// </remarks>
    private static void Getter(IJsRealm realm, JsValue prototype, string name, Func<JsValue, JsValue> read) =>
        realm.DefineAccessor(prototype, name, (in call) => call.This.IsObject ? read(call.This) : JsValue.Undefined, null);

    /// <summary>As <see cref="Getter"/>, for an answer that has to be minted in the calling realm.</summary>
    private static void GetterInRealm(IJsRealm realm, JsValue prototype, string name, Func<IJsRealm, JsValue, JsValue> read) =>
        realm.DefineAccessor(prototype, name,
            (in call) => call.This.IsObject ? read(call.Realm, call.This) : JsValue.Undefined, null);

    /// <summary>A promise already fulfilled with <paramref name="value"/>.</summary>
    /// <remarks>
    /// The realm hands back the settle functions rather than running an executor, so the promise is
    /// resolved here instead of inside a callback that only happened to run synchronously — the
    /// difference <see cref="IJsJobs.NewPromise"/> exists to remove.
    /// </remarks>
    private static JsValue Resolved(IJsRealm realm, JsValue value)
    {
        var promise = realm.NewPromise(out var resolve, out _);
        resolve(value);
        return promise;
    }

    private static JsValue Rejected(IJsRealm realm, string message)
    {
        var typeError = realm.GetProperty(realm.Global, "TypeError");
        var error = typeError.IsFunction
            ? realm.Construct(typeError, [JsValue.String(message)])
            : JsValue.String($"TypeError: {message}");

        var promise = realm.NewPromise(out _, out var reject);
        reject(error);
        return promise;
    }

    private static void Add(IJsRealm realm, JsValue navigator, string name, JsValue value) =>
        realm.DefineValue(navigator, name, value);
}
