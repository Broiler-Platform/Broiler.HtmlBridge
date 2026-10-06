using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

/// <summary>
/// What a window hears when its document's fragment changes -- <c>location.hash = x</c> and the other
/// spellings, a link into the page the host followed, a frame's own -- as HTML's "navigate to a fragment"
/// fires it: <c>popstate</c> at once, <c>hashchange</c> in a later task.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>hashchange</c> fired inside the assignment that caused it</b>, before the next statement of the
/// script that wrote <c>location.hash</c>: a router that sets the hash and then records it as its own
/// heard the change first and took it for the user's. And no <c>popstate</c> fired at all, so a router
/// listening for that -- the History API's own event, which a fragment navigation fires too -- never
/// heard a link into the page.
/// </para>
/// <para>
/// <b>As Chromium fires them, measured.</b> <c>popstate</c> (state <c>null</c>, not bubbling, not
/// cancelable) fires inside the navigation, and <c>history.state</c> is <c>null</c> after it.
/// <c>hashchange</c> is queued as a task: it runs after the script's microtasks and takes its turn with
/// the timers -- after a <c>setTimeout(…, 0)</c> set before the navigation, before one set after it --
/// one per navigation, each with its own <c>oldURL</c> and <c>newURL</c>. A navigation to the fragment
/// already in hand fires neither. A frame's fragment navigation fires both at the frame's window and
/// none at the page's.
/// </para>
/// <para>
/// <c>history.length</c> stays at one: the bridge keeps no session history, and a count that grew would
/// tell a page that <c>history.back()</c> -- which does nothing here -- had somewhere to go.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The page's History object, whose state a fragment navigation clears.
    private JsValue _historyObject;

    /// <summary>The page's fragment changed from <paramref name="oldUrl"/> to <paramref name="newUrl"/>: <c>popstate</c> now, <c>hashchange</c> later.</summary>
    private void PageFragmentChanged(string oldUrl, string newUrl)
    {
        if (_realm is null)
            return;

        // The entry a fragment navigation makes carries no state.
        if (_historyObject.IsObject)
            Realm.DefineValue(_historyObject, "state", JsValue.Null);

        FireFragmentEvent("popstate", () => DispatchWindowEvent(NewPopStateEvent()));
        _eventLoop.QueueTask(() =>
        {
            if (_realm is not null)
                FireFragmentEvent("hashchange", () => DispatchWindowEvent(NewHashChangeEvent(oldUrl, newUrl)));
        });
    }

    /// <summary>
    /// The fragment of <paramref name="container"/>'s document changed: <c>popstate</c> now and
    /// <c>hashchange</c> later at the frame's window, as the frame's script -- unless the frame shows
    /// another document by then.
    /// </summary>
    private void FrameFragmentChanged(DomElement container, string oldUrl, string newUrl)
    {
        if (_realm is null || !_browsingContexts.TryGetSubWindow(container, out var window) || !window.IsObject)
            return;

        var document = GetContentDocument(container);
        FireFragmentEvent("popstate", () => RunWithWindowContext(window, () => _eventDispatch.DispatchEventOnWindow(window, NewPopStateEvent())));
        _eventLoop.QueueTask(() =>
        {
            if (_realm is null || !container.IsConnected || !ReferenceEquals(GetContentDocument(container), document) ||
                !_browsingContexts.TryGetSubWindow(container, out var current) || current != window)
            {
                return;
            }

            FireFragmentEvent("hashchange",
                () => RunWithWindowContext(window, () => _eventDispatch.DispatchEventOnWindow(window, NewHashChangeEvent(oldUrl, newUrl))));
        });
    }

    /// <summary>
    /// Fires one of the two. A failure is logged and swallowed: in a browser it belongs to the dispatch,
    /// not to the <c>location.hash = x</c> that caused it, and letting it out would abort that script.
    /// </summary>
    private static void FireFragmentEvent(string type, Action fire)
    {
        try
        {
            fire();
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.location", $"Error firing {type}: {ex.Message}", ex);
        }
    }

    private JsValue NewPopStateEvent()
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, "popstate", bubbles: false, cancelable: false, composed: false, InterfacePrototype(realm, "PopStateEvent"));
        Define(realm, evt, "state", JsValue.Null);
        Define(realm, evt, "hasUAVisualTransition", JsValue.False);
        return evt;
    }

    private JsValue NewHashChangeEvent(string oldUrl, string newUrl)
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, "hashchange", bubbles: false, cancelable: false, composed: false, InterfacePrototype(realm, "HashChangeEvent"));
        Define(realm, evt, "oldURL", JsValue.String(oldUrl));
        Define(realm, evt, "newURL", JsValue.String(newUrl));
        return evt;
    }
}
