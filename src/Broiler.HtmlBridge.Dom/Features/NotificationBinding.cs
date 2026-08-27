using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// </remarks>
internal static class NotificationBinding
{
    /// <summary>The one permission state Broiler can be in — see the class remarks.</summary>
    private const string Permission = "denied";

    /// <summary>
    /// Builds the <c>Notification</c> interface object. It is a <see cref="JSFunction"/> rather
    /// than a <c>DomFunction</c> because pages legitimately <c>new</c> it.
    /// </summary>
    public static JSFunction Build()
    {
        var constructor = new JSFunction(NewNotification, "Notification", 2);

        constructor.FastAddValue("permission",
            new JSString(Permission), JSPropertyAttributes.EnumerableConfigurableValue);

        constructor.FastAddValue("requestPermission",
            new DomFunction(RequestPermission, "requestPermission", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // The maximum number of actions a notification may carry. Zero is the honest count for a
        // notification that is never displayed, and it is a value the specification expects to vary
        // by user agent, so a page reading it is already prepared for zero.
        constructor.FastAddValue("maxActions",
            new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);

        return constructor;
    }

    /// <summary>
    /// <c>Notification.requestPermission()</c>. It resolves — it does not reject — with the
    /// permission that resulted, which here is the one it started as. Both call styles are
    /// supported: the promise the current specification returns, and the legacy callback argument
    /// that older code still passes.
    /// </summary>
    private static JSValue RequestPermission(in Arguments a)
    {
        var permission = new JSString(Permission);

        if (a.Length > 0 && a[0].IsFunction)
            a[0].InvokeFunction(new Arguments(JSUndefined.Value, permission));

        return new JSPromise((resolve, _) => resolve(permission));
    }

    private static JSValue NewNotification(in Arguments a)
    {
        var notification = new JSObject();

        notification.FastAddValue("title",
            a.Length > 0 ? new JSString(a[0].ToString()) : new JSString(string.Empty),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // The options a caller passed are reflected back, because that is what the interface's
        // attributes are: a notification reports the values it was constructed with.
        var options = a.Length > 1 && a[1] is JSObject given ? given : new JSObject();
        foreach (var (name, fallback) in ReflectedOptions)
        {
            var value = options[(KeyString)name];
            notification.FastAddValue(name,
                value is JSUndefined ? fallback : value,
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        JSValue onError = JSNull.Value;
        notification.FastAddProperty("onerror",
            new DomFunction((in _) => onError, "get onerror"),
            new DomFunction((in b) => onError = b.Length > 0 ? b[0] : JSNull.Value, "set onerror"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        notification.FastAddValue("close",
            DomBridge.UndefinedFunction("close", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return notification;
    }

    /// <summary>
    /// The constructor options that are also readable attributes on the notification, with the
    /// value each has when it was not supplied.
    /// </summary>
    private static readonly (string Name, JSValue Fallback)[] ReflectedOptions =
    [
        ("body", new JSString(string.Empty)),
        ("tag", new JSString(string.Empty)),
        ("icon", new JSString(string.Empty)),
        ("lang", new JSString(string.Empty)),
        ("dir", new JSString("auto")),
        ("data", JSNull.Value),
        ("silent", JSNull.Value),
    ];
}
