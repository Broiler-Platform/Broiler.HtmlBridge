using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
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
    public static JSValue Send(JSObject? window, in Arguments a)
    {
        if (a.Length == 0 || a[0].IsNullOrUndefined)
            return JSBoolean.False;
        try
        {
            // Per sendBeacon semantics, failure to queue because no live fetch entry
            // point is available should return false instead of throwing.
            if (window[(KeyString)"fetch"] is not JSFunction currentFetch)
                return JSBoolean.False;
            var options = new JSObject();
            options[(KeyString)"method"] = new JSString("POST");
            options[(KeyString)"keepalive"] = JSBoolean.True;
            if (a.Length > 1 && !a[1].IsNullOrUndefined)
                options[(KeyString)"body"] = new JSString(a[1].ToString());
            currentFetch.InvokeFunction(new Arguments(currentFetch, a[0], options));
            return JSBoolean.True;
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.navigator.sendBeacon", $"sendBeacon error: {ex.Message}", ex);
            return JSBoolean.False;
        }
    }
}
