using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
    public static void Install(JSObject navigator)
    {
        navigator.FastAddValue("webkitTemporaryStorage", BuildStorageQuota(), JSPropertyAttributes.EnumerableConfigurableValue);
        navigator.FastAddValue("webkitPersistentStorage", BuildStorageQuota(), JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private static JSObject BuildStorageQuota()
    {
        var quota = new JSObject();

        quota.FastAddValue("queryUsageAndQuota",
            new DomFunction(QueryUsageAndQuota, "queryUsageAndQuota", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return quota;
    }

    private static JSValue QueryUsageAndQuota(in Arguments a)
    {
        if (a.Length > 0 && a[0].IsFunction)
            a[0].InvokeFunction(new Arguments(JSUndefined.Value, new JSNumber(0), new JSNumber(0)));

        return JSUndefined.Value;
    }
}
