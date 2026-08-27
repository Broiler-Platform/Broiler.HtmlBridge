using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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

    /// <summary>
    /// The per-query state behind a <c>PermissionStatus</c>. Only the name varies — the state is
    /// <c>"denied"</c> for every capability this engine has.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JSObject, JSString> StatusNames = new();

    public static void Install(JSObject navigator, JSContext context, string userAgent)
    {
        context.Eval("""
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
            """);

        var storage = InstanceOf(context, "StorageManager", out var storagePrototype);
        var permissions = InstanceOf(context, "Permissions", out var permissionsPrototype);
        var userAgentData = InstanceOf(context, "NavigatorUAData", out var userAgentDataPrototype);
        if (storage is null || permissions is null || userAgentData is null ||
            storagePrototype is null || permissionsPrototype is null || userAgentDataPrototype is null ||
            context["PermissionStatus"] is not JSObject statusConstructor ||
            statusConstructor[(KeyString)"prototype"] is not JSObject statusPrototype)
            return;

        InstallStorageManager(storagePrototype);
        InstallPermissions(permissionsPrototype, statusPrototype, context);
        InstallUserAgentData(userAgentDataPrototype, userAgent);

        Add(navigator, "storage", storage);
        Add(navigator, "permissions", permissions);
        Add(navigator, "userAgentData", userAgentData);
    }

    // -------- StorageManager --------

    private static void InstallStorageManager(JSObject prototype)
    {
        // estimate() — the origin's quota-managed usage and quota. Both zero: nothing is stored
        // because none of the backends this interface counts exists. localStorage, sessionStorage
        // and document.cookie all work and have never been counted here by any browser.
        Method(prototype, "estimate", 0, static (in Arguments _) =>
        {
            var estimate = new JSObject();
            estimate.FastAddValue("usage", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
            estimate.FastAddValue("quota", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
            return Resolved(estimate);
        });

        // persisted() / persist() — whether the origin's storage is exempt from eviction, and a
        // request to make it so. False and false: there is no storage to persist, and a persist()
        // that resolved true would promise durability for nothing.
        Method(prototype, "persisted", 0, static (in Arguments _) => Resolved(JSBoolean.False));
        Method(prototype, "persist", 0, static (in Arguments _) => Resolved(JSBoolean.False));
    }

    // -------- Permissions --------

    private static void InstallPermissions(JSObject prototype, JSObject statusPrototype, JSContext context)
    {
        Method(prototype, "query", 1, (in Arguments a) =>
        {
            var name = a.Length > 0 && a[0] is JSObject descriptor && descriptor[(KeyString)"name"] is { } requested
                ? requested.ToString()
                : string.Empty;

            if (!PermissionNames.Contains(name))
            {
                // Rejected rather than thrown, and a TypeError rather than a denial: the enum is
                // validated before the permission is looked at, so a typo is reported as a typo.
                return Rejected(context,
                    "Failed to execute 'query' on 'Permissions': Failed to read the 'name' property " +
                    $"from 'PermissionDescriptor': The provided value '{name}' is not a valid enum value " +
                    "of type PermissionName.");
            }

            var status = new JSObject { BasePrototypeObject = statusPrototype };
            StatusNames.Add(status, new JSString(name));
            return Resolved(status);
        });

        Getter(statusPrototype, "name", static status =>
            StatusNames.TryGetValue(status, out var name) ? name : new JSString(string.Empty));

        // Denied, for every capability. Broiler grants none of them and has no surface to prompt on,
        // so "prompt" — which is what a browser answers before the user has been asked — would
        // promise a dialog that never comes. This is the state Notification.permission already
        // reports, for the same reason.
        Getter(statusPrototype, "state", static _ => new JSString("denied"));

        // The state never changes, so this handler is never called — which is the correct behaviour
        // rather than a missing one. It is present because a page assigns to it unconditionally.
        statusPrototype.FastAddValue("onchange",
            JavaScript.BuiltIns.Null.JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
    }

    // -------- NavigatorUAData --------

    /// <summary>
    /// Installs the User-Agent Client Hints members, every one derived from
    /// <paramref name="userAgent"/> so the structured identity and the string cannot disagree.
    /// </summary>
    private static void InstallUserAgentData(JSObject prototype, string userAgent)
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
        Getter(prototype, "brands", _ => BrandList(brand, majorVersion));
        Getter(prototype, "mobile", static _ => JSBoolean.False);
        Getter(prototype, "platform", _ => new JSString(platform));

        // One GREASE brand is what a browser adds here to keep sites from hard-coding the list.
        // Broiler reports its own brand and nothing else: an invented second entry would be a claim
        // about a product that does not exist, and the anti-ossification argument is a browser-market
        // one rather than a correctness one.
        Method(prototype, "toJSON", 0, (in Arguments _) => LowEntropyObject(brand, majorVersion, platform));

        Method(prototype, "getHighEntropyValues", 1, (in Arguments a) =>
        {
            var result = LowEntropyObject(brand, majorVersion, platform);
            var hints = RequestedHints(a);

            // Each hint is answered from the user agent string or from a fact about this engine.
            // A hint that is not asked for is absent, which is the interface's own shape: the
            // caller names what it wants and gets exactly that.
            if (hints.Contains("architecture"))
                result.FastAddValue("architecture", new JSString("x86"), JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("bitness"))
                result.FastAddValue("bitness", new JSString(is64Bit ? "64" : "32"), JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("model"))
                result.FastAddValue("model", new JSString(string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("platformVersion"))
                result.FastAddValue("platformVersion", new JSString(platformVersion), JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("uaFullVersion"))
                result.FastAddValue("uaFullVersion", new JSString(version), JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("fullVersionList"))
                result.FastAddValue("fullVersionList", BrandList(brand, version), JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("wow64"))
                result.FastAddValue("wow64", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
            if (hints.Contains("formFactors"))
            {
                result.FastAddValue("formFactors",
                    new JSArray([new JSString("Desktop")]), JSPropertyAttributes.EnumerableConfigurableValue);
            }

            return Resolved(result);
        });
    }

    private static JSObject LowEntropyObject(string brand, string majorVersion, string platform)
    {
        var result = new JSObject();
        result.FastAddValue("brands", BrandList(brand, majorVersion), JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue("mobile", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue("platform", new JSString(platform), JSPropertyAttributes.EnumerableConfigurableValue);
        return result;
    }

    private static JSArray BrandList(string brand, string version)
    {
        var entry = new JSObject();
        entry.FastAddValue("brand", new JSString(brand), JSPropertyAttributes.EnumerableConfigurableValue);
        entry.FastAddValue("version", new JSString(version), JSPropertyAttributes.EnumerableConfigurableValue);
        return new JSArray([entry]);
    }

    private static HashSet<string> RequestedHints(in Arguments a)
    {
        var hints = new HashSet<string>(StringComparer.Ordinal);
        if (a.Length == 0 || a[0] is not JSObject list)
            return hints;

        var length = list[(KeyString)"length"] is { } lengthValue ? (int)lengthValue.DoubleValue : 0;
        for (var index = 0; index < length; index++)
        {
            if (list[(uint)index] is { } hint && !hint.IsUndefined && !hint.IsNull)
                hints.Add(hint.ToString());
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
    private static JSObject? InstanceOf(JSContext context, string interfaceName, out JSObject? prototype)
    {
        prototype = context[interfaceName] is JSObject constructor
            ? constructor[(KeyString)"prototype"] as JSObject
            : null;

        return prototype is null ? null : new JSObject { BasePrototypeObject = prototype };
    }

    private static void Method(JSObject prototype, string name, int length, JSFunctionDelegate body) =>
        prototype.FastAddValue(name, new DomFunction(body, name, length),
            JSPropertyAttributes.EnumerableConfigurableValue);

    private static void Getter(JSObject prototype, string name, Func<JSObject, JSValue> read) =>
        prototype.FastAddProperty(
            name,
            new DomFunction((in a) => a.This is JSObject receiver ? read(receiver) : JSUndefined.Value, $"get {name}"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

    private static JSValue Resolved(JSValue value) => new JSPromise((resolve, _) => resolve(value));

    private static JSValue Rejected(JSContext context, string message)
    {
        var error = context["TypeError"] is JavaScript.BuiltIns.Function.JSFunction typeError
            ? typeError.CreateInstance(new Arguments(typeError, new JSString(message)))
            : new JSString($"TypeError: {message}");

        return new JSPromise((_, reject) => reject(error));
    }

    private static void Add(JSObject navigator, string name, JSValue value) =>
        navigator.FastAddValue(name, value, JSPropertyAttributes.EnumerableConfigurableValue);
}
