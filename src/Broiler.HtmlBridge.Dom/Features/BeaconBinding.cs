using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>navigator.sendBeacon(url, data)</c>, co-located as an HtmlBridge feature module (Phase 3).
/// It queues a keep-alive <c>POST</c> by delegating to the window's own <c>fetch</c>, returning
/// <c>false</c> (never throwing) when the request cannot be queued — per the Beacon spec. It reads
/// only the supplied <c>window</c> object and routes errors to <see cref="RenderLogger"/>, touching
/// no bridge instance state, so — like <c>ConsoleBinding</c> / <c>CryptoBinding</c> — it is a pure
/// static class with no host contract. Previously the bridge's
/// <c>JsRegistrationSendBeacon124Core</c> in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class BeaconBinding
{
    /// <summary>
    /// Sends a beacon by delegating to <c>window.fetch</c> with <c>method: POST</c> and
    /// <c>keepalive: true</c>. Returns <c>true</c> when the request was queued, <c>false</c> when no
    /// data was supplied, no <c>fetch</c> entry point is available, or the delegation threw.
    /// </summary>
    /// <param name="window">The window whose <c>fetch</c> performs the request.</param>
    /// <param name="call">The call frame — its realm is what reads and builds the values below.</param>
    public static JsValue Send(JsValue window, in JsCall call)
    {
        if (call.Length == 0 || call[0].IsNullish)
            return JsValue.False;
        try
        {
            var realm = call.Realm;

            // Per sendBeacon semantics, failure to queue because no live fetch entry
            // point is available should return false instead of throwing.
            var currentFetch = realm.GetProperty(window, "fetch");
            if (!currentFetch.IsFunction)
                return JsValue.False;

            // Ordinary [[Set]]s, as before, rather than property definitions: an options bag is a
            // plain object the page never sees, and the two differ only if something on
            // Object.prototype intercepts one of these names — which is the page's business, and was
            // its business before this migration too.
            var options = realm.NewObject();
            realm.SetProperty(options, "method", JsValue.String("POST"));
            realm.SetProperty(options, "keepalive", JsValue.True);
            if (call.Length > 1 && !call[1].IsNullish)
            {
                // ToJsString, not the handle's rendering: the body argument is commonly an object
                // (a URLSearchParams, a page's own payload wrapper) whose own `toString` is what
                // decides the bytes sent. That coercion is observable, so it stays the realm's.
                realm.SetProperty(options, "body", JsValue.String(realm.ToJsString(call[1])));
            }

            // The receiver is `fetch` itself, which is what the engine-typed call site passed as
            // this call's `this` before.
            realm.Invoke(currentFetch, currentFetch, [call[0], options]);
            return JsValue.True;
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.navigator.sendBeacon", $"sendBeacon error: {ex.Message}", ex);
            return JsValue.False;
        }
    }
}
