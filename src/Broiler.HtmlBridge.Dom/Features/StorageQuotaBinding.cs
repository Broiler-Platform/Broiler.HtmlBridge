using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>navigator.webkitTemporaryStorage</c> and <c>navigator.webkitPersistentStorage</c> — the
/// legacy Quota Management interface, whose one surviving member is
/// <c>queryUsageAndQuota(success, error)</c>. Pure static, with no host contract.
/// </summary>
/// <remarks>
/// <para>
/// These report the origin's <em>quota-managed</em> storage: IndexedDB, the Cache API, the origin
/// private file system. Broiler implements none of them, so the honest pair of numbers is
/// <c>(0, 0)</c> — nothing is stored, and nothing may be. That is not the same statement as "the
/// page has no storage": <c>localStorage</c>, <c>sessionStorage</c> and <c>document.cookie</c> all
/// work, and none of them has ever been counted by this interface in any browser.
/// </para>
/// <para>
/// The interface is deprecated in favour of <c>navigator.storage.estimate()</c>, and is here
/// because pages still call it: reading <c>navigator.webkitTemporaryStorage.queryUsageAndQuota</c>
/// threw "Cannot get property queryUsageAndQuota of undefined", which aborts the function that
/// asked rather than telling it there is no quota. When the quota-managed backends land, both
/// callbacks report from them and <c>navigator.storage.estimate()</c> joins them here.
/// </para>
/// <para>
/// The success callback is invoked synchronously. A browser answers on a later task, and a caller
/// that depends on the difference is already broken — but note this is why a page must not assume
/// its own <c>then</c> has been installed by the time the callback runs.
/// </para>
/// </remarks>
internal static class StorageQuotaBinding
{
    /// <summary>
    /// Installs both storage-quota objects on <paramref name="navigator"/>. They are separate
    /// objects because they are separate storage types — a page may hold a reference to one — even
    /// though both currently report the same pair.
    /// </summary>
    /// <param name="realm">The realm the two objects and their method belong to.</param>
    /// <param name="navigator">The navigator to install them on.</param>
    public static void Install(IJsRealm realm, JsValue navigator)
    {
        realm.DefineValue(navigator, "webkitTemporaryStorage", BuildStorageQuota(realm));
        realm.DefineValue(navigator, "webkitPersistentStorage", BuildStorageQuota(realm));
    }

    private static JsValue BuildStorageQuota(IJsRealm realm)
    {
        var quota = realm.NewObject();

        realm.DefineValue(quota, "queryUsageAndQuota",
            realm.NewMethod("queryUsageAndQuota", QueryUsageAndQuota, 2));

        return quota;
    }

    /// <summary>
    /// <c>queryUsageAndQuota(success, error)</c> — reports zero used of zero available, to the
    /// success callback if one was passed. The error callback is never reached; there is no failure
    /// this can report.
    /// </summary>
    /// <remarks>
    /// The success callback is called with <c>undefined</c> as its receiver and the two numbers as
    /// its arguments — the same call the engine was making directly before, now asked of the realm so
    /// that the callback runs under whatever a provider needs around a re-entry into script.
    /// </remarks>
    private static JsValue QueryUsageAndQuota(in JsCall call)
    {
        if (call.Length > 0 && call[0].IsFunction)
        {
            call.Realm.Invoke(call[0], JsValue.Undefined, [JsValue.Number(0), JsValue.Number(0)]);
        }

        return JsValue.Undefined;
    }
}
