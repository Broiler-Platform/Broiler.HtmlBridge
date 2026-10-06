using System.Net.Http;

using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>navigator.sendBeacon(url, data)</c>, on the fetch binding's own core.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not through <c>window.fetch</c>.</b> The beacon used to read the page-visible <c>fetch</c>
/// property and call it, so a page that replaced <c>window.fetch</c> saw — and could answer — every
/// beacon a library sent, and the request carried whatever that function chose. It now goes straight
/// to the loader, as the calling document's request, through the same header and response gates as
/// <c>fetch()</c>.
/// </para>
/// <para>
/// <b>The Beacon request.</b> A <c>POST</c> with credentials <c>include</c>, for the calling document
/// (the frame whose script is running, when the bridge can tell). Its mode follows the body: a body
/// whose <c>Content-Type</c> is not CORS-safelisted (a <c>Blob</c> typed <c>application/json</c>, say)
/// is a <c>cors</c> request and preflighted; a string, a <c>URLSearchParams</c>, an <c>ArrayBuffer</c>
/// or a view of one, an untyped <c>Blob</c> or no body at all is <c>no-cors</c>. A URL that does not
/// parse, or is not HTTP(S), is a <c>TypeError</c>. The body is Fetch's "extract a body"
/// (<see cref="ExtractBody"/>).
/// </para>
/// <para>
/// <b>Sent synchronously</b>, as it always has been here: the call returns once the request has
/// been answered or has failed. <c>true</c> means it was sent — a network error after that is not the
/// page's to observe, as the Beacon API specifies — and <c>false</c> that it could not be queued
/// because the body could not be read.
/// </para>
/// </remarks>
internal sealed partial class FetchBinding
{
    /// <summary>The <c>sendBeacon</c> method's body; see the type remarks.</summary>
    internal JsValue SendBeacon(in JsCall call)
    {
        if (call.Length == 0 || call[0].IsNullish)
            return JsValue.False;

        var realm = call.Realm;
        var requestedUrl = realm.ToJsString(call[0]);
        if (UrlResolver.Resolve(requestedUrl, _host.FetchBaseUrl) is not { } url ||
            url.Scheme is not ("http" or "https"))
            throw realm.Error(JsErrorKind.TypeError, $"Failed to execute 'sendBeacon' on 'Navigator': '{requestedUrl}' is not an HTTP(S) URL.");

        byte[]? body = null;
        string? contentType = null;
        try
        {
            if (call.Length > 1 && !call[1].IsNullish)
                (body, contentType) = ExtractBody(realm, call[1]);
        }
        catch (Exception ex)
        {
            // A body whose own toString threw: nothing was queued.
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.navigator.sendBeacon", $"sendBeacon error: {ex.Message}", ex);
            return JsValue.False;
        }

        var mode = contentType is null || FetchHeaders.IsCorsSafelistedRequestHeader("Content-Type", contentType)
            ? RequestMode.NoCors
            : RequestMode.Cors;

        var attempt = ResourceTrace.Begin(ResourceTraceKind.Fetch, url.AbsoluteUri);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, url);
            if (body is not null)
            {
                message.Content = new ByteArrayContent(body);
                if (contentType is not null)
                    message.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }

            var client = _host.FetchClient;
            using var response = SendAuthorRequest(
                message, RequestContext.Fetch(client, mode, CredentialsMode.Include),
                new ResourceTimingRequest("beacon", client, _host.ResourceTimings));
            attempt.Completed(null, response.StatusCode, response.Message.Content.Headers.ContentType?.MediaType, "POST");
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.navigator.sendBeacon", $"sendBeacon request failed: {ex.Message}", ex);
            attempt.Failed(ex);
        }

        return JsValue.True;
    }
}
