using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Notification</c> interface (Notifications API §2) as an engine with no notification
/// surface can honestly provide it: the permission is <c>denied</c>, and a notification that is
/// constructed anyway is never shown. Pure static, with no host contract — nothing here reaches
/// bridge state.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why "denied" and not "default".</b> <c>default</c> means the user has not been asked yet, and
/// it is an invitation: a page that sees it calls <c>requestPermission()</c> and waits for a prompt
/// that Broiler has no way to show. <c>denied</c> is the terminal state, and it is the true one —
/// there is no display Broiler could put a notification on, so no sequence of calls could ever end
/// with one being shown. A page that reads it takes its no-notifications path immediately instead
/// of waiting on a permission flow that cannot complete.
/// </para>
/// <para>
/// The constructor still exists, and still does not throw: the specification does not make
/// construction conditional on the permission — a denied notification is created and then simply
/// not shown — and a page that constructs one inside a handler would otherwise lose the rest of
/// that handler to a <c>ReferenceError</c>. <c>onerror</c> is settable and stays null: the page
/// already knows from <c>Notification.permission</c> that nothing will be displayed, so there is no
/// failure to report that it was not told before it asked.
/// </para>
/// <para>
/// The interface object and everything on it are minted through the realm its registration site
/// hands over. The two argument reads coerce with the realm's <c>ToJsString</c>, which is the
/// observable ECMAScript <c>ToString</c> the engine's <c>ToString()</c> ran here before.
/// </para>
/// </remarks>
internal static class NotificationBinding
{
    /// <summary>The one permission state Broiler can be in — see the class remarks.</summary>
    private const string Permission = "denied";

    /// <summary>
    /// Builds the <c>Notification</c> interface object. It is a constructor rather than a plain
    /// method because pages legitimately <c>new</c> it.
    /// </summary>
    public static JsValue Build(IJsRealm realm)
    {
        var constructor = realm.NewConstructor("Notification", (in call) => NewNotification(in call), 2);

        realm.DefineValue(constructor, "permission", JsValue.String(Permission));

        realm.DefineValue(constructor, "requestPermission",
            realm.NewMethod("requestPermission", RequestPermission, 1));

        // The maximum number of actions a notification may carry. Zero is the honest count for a
        // notification that is never displayed, and it is a value the specification expects to vary
        // by user agent, so a page reading it is already prepared for zero.
        realm.DefineValue(constructor, "maxActions", JsValue.Number(0));

        return constructor;
    }

    /// <summary>
    /// <c>Notification.requestPermission()</c>. It resolves — it does not reject — with the
    /// permission that resulted, which here is the one it started as. Both call styles are
    /// supported: the promise the current specification returns, and the legacy callback argument
    /// that older code still passes.
    /// </summary>
    private static JsValue RequestPermission(in JsCall call)
    {
        var realm = call.Realm;
        var permission = JsValue.String(Permission);

        if (call[0].IsFunction)
            realm.Invoke(call[0], JsValue.Undefined, [permission]);

        var promise = realm.NewPromise(out var resolve, out _);
        resolve(permission);
        return promise;
    }

    private static JsValue NewNotification(in JsCall call)
    {
        var realm = call.Realm;
        var notification = realm.NewObject();

        realm.DefineValue(notification, "title",
            call.Length > 0 ? JsValue.String(realm.ToJsString(call[0])) : JsValue.String(string.Empty));

        // The options a caller passed are reflected back, because that is what the interface's
        // attributes are: a notification reports the values it was constructed with.
        var options = call[1].IsObject ? call[1] : realm.NewObject();
        foreach (var (name, fallback) in ReflectedOptions)
        {
            var value = realm.GetProperty(options, name);
            realm.DefineValue(notification, name, value.IsUndefined || value.IsMissing ? fallback : value);
        }

        JsValue onError = JsValue.Null;
        realm.DefineAccessor(notification, "onerror",
            (in _) => onError,
            (in set) => onError = set.Length > 0 ? set[0] : JsValue.Null);

        realm.DefineValue(notification, "close",
            realm.NewConstructor("close", static (in _) => JsValue.Undefined, 0));

        return notification;
    }

    /// <summary>
    /// The constructor options that are also readable attributes on the notification, with the
    /// value each has when it was not supplied.
    /// </summary>
    private static readonly (string Name, JsValue Fallback)[] ReflectedOptions =
    [
        ("body", JsValue.String(string.Empty)),
        ("tag", JsValue.String(string.Empty)),
        ("icon", JsValue.String(string.Empty)),
        ("lang", JsValue.String(string.Empty)),
        ("dir", JsValue.String("auto")),
        ("data", JsValue.Null),
        ("silent", JsValue.Null),
    ];
}
