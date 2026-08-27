using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Json;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Response</c> static factories (<c>new Response</c> / <c>Response.json</c> /
/// <c>Response.redirect</c>) and the <c>fetch</c> network call, moved out of the bridge's shared
/// registration callbacks into the co-located <see cref="FetchBinding"/> module (P3.11). The fetch
/// implementation performs its host I/O through the injected <see cref="Broiler.HtmlBridge.Dom.Runtime.ResourceLoader"/>.
/// </summary>
internal sealed partial class FetchBinding
{
    private JSValue JsRegistrationResponse113Core(ResponseInitParser parseResponseInit, ResponseFactory createResponse, in Arguments a)
    {
        var body = a.Length > 0 && !a[0].IsUndefined && !a[0].IsNull ? a[0].ToString() : string.Empty;
        var (status, statusText, url, type, redirected, headers) = parseResponseInit(a.Length > 1 ? a[1] : null);
        return createResponse(body, status, statusText, url, type, redirected, headers);
    }


    private JSValue JsRegistrationJson114Core(ResponseInitParser parseResponseInit, ResponseFactory createResponse, in Arguments a)
    {
        var jsonBody = JSJSON.Stringify(a.Length > 0 ? a[0] : JSNull.Value);
        var (status, statusText, url, type, redirected, headers) = parseResponseInit(a.Length > 1 ? a[1] : null);
        if (!headers.ContainsKey("Content-Type"))
            headers["Content-Type"] = "application/json";
        return createResponse(jsonBody, status, statusText, url, type, redirected, headers);
    }


    private JSValue JsRegistrationRedirect116Core(Func<string, string> resolveResponseRedirectUrl, ResponseFactory createResponse, in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'redirect' on 'Response': 1 argument required.");
        var status = 302;
        if (a.Length > 1 && int.TryParse(a[1].ToString(), out var parsedStatus))
            status = parsedStatus;
        if (status is not (301 or 302 or 303 or 307 or 308))
            throw new JSException("Failed to execute 'redirect' on 'Response': Invalid status code");
        var resolvedUrl = resolveResponseRedirectUrl(a[0].ToString());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Location"] = resolvedUrl
        };
        return createResponse(string.Empty, status, string.Empty, string.Empty, "basic", false, headers);
    }


    private JSValue JsRegistrationFetch120Core(JsPropertyStringGetter tryGetJsPropertyString, ObjectStringEntriesEnumerator enumerateObjectStringEntries, Func<JSValue, JSValue> createAbortErrorValue, ResponseFactory createResponse, in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'fetch': 1 argument required.");
        var requestedUrl = a[0].ToString();
        if (a[0] is JSObject requestInput)
        {
            requestedUrl = tryGetJsPropertyString(requestInput, "url", "href") ?? requestedUrl;
        }

        // §5.4 of Fetch: the input is parsed against the entry settings object's base URL. This is
        // the same one shared resolver Response.redirect uses (Phase 7 item 4) — an absolute URL is
        // kept, a relative or root-relative one resolves against the page. Without it a root-relative
        // target went to HttpClient verbatim as a relative request URI, which is an
        // InvalidOperationException out of PrepareRequestMessage rather than a request. That is not
        // an exotic spelling: google.com beacons its timing to `/gen_204?atyp=i&…` through
        // navigator.sendBeacon, which delegates to this fetch, and so does every XMLHttpRequest
        // opened on a path (the XHR polyfill calls fetch(this._url)).
        var resolvedUri = UrlResolver.Resolve(requestedUrl, _host.PageUrl);
        var fetchUrl = resolvedUri?.AbsoluteUri ?? requestedUrl;

        JSValue responseObj = new JSObject();
        // Parse options (method, headers, body)
        var method = "GET";
        string? requestBody = null;
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        JSValue signalValue = JSUndefined.Value;
        if (a[0] is JSObject requestObject)
        {
            method = (tryGetJsPropertyString(requestObject, "method") ?? method).ToUpperInvariant();
            requestBody = tryGetJsPropertyString(requestObject, "_bodyInit", "body");
            if (requestObject[(KeyString)"signal"] is { } requestSignal && !requestSignal.IsUndefined && !requestSignal.IsNull)
                signalValue = requestSignal;
            if (requestObject[(KeyString)"headers"] is JSObject requestHeadersObject)
            {
                foreach (var (key, value) in enumerateObjectStringEntries(requestHeadersObject))
                    requestHeaders[key] = value;
            }
        }

        if (a.Length > 1 && a[1] is JSObject opts)
        {
            method = (tryGetJsPropertyString(opts, "method") ?? method).ToUpperInvariant();
            requestBody = tryGetJsPropertyString(opts, "body") ?? requestBody;
            if (opts[(KeyString)"signal"] is { } optionsSignal && !optionsSignal.IsUndefined && !optionsSignal.IsNull)
                signalValue = optionsSignal;
            if (opts[(KeyString)"headers"] is JSObject optionsHeadersObject)
            {
                foreach (var (key, value) in enumerateObjectStringEntries(optionsHeadersObject))
                    requestHeaders[key] = value;
            }
        }

        var rejected = false;
        var rejectedValue = JSUndefined.Value;
        if (signalValue is JSObject signalObject && signalObject[(KeyString)"aborted"].BooleanValue)
        {
            rejected = true;
            rejectedValue = createAbortErrorValue(signalValue);
        }

        // Traced here rather than inside the loader, for two reasons: the body is already decoded at
        // this level (asking the loader would decode the same bytes twice), and an aborted request —
        // which never reaches the loader — must not be recorded as a request that was made. Begun
        // before the try so a failure is recorded with the time it took to fail. Off by default; see
        // ResourceTrace.
        var attempt = rejected ? default : ResourceTrace.Begin(ResourceTraceKind.Fetch, fetchUrl);

        try
        {
            if (!rejected && resolvedUri == null)
            {
                // A URL that will not parse against the page's base is the spec's "network error"
                // for this binding's error model, which reports every failed fetch as an error
                // Response rather than throwing. Throwing here would abort the whole calling script,
                // which for a relative URL is a far worse outcome than one failed request.
                RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.fetch",
                    $"Fetch error: '{requestedUrl}' is not an absolute URL and does not resolve against the page URL '{_host.PageUrl}'.",
                    new JSException("Failed to parse URL"));
                attempt.Failed($"'{requestedUrl}' does not resolve against the page URL");
                responseObj = createResponse(string.Empty, 0, "Invalid URL", requestedUrl, "error", false,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            }
            else if (!rejected)
            {
                var request = new HttpRequestMessage(new HttpMethod(method), fetchUrl);
                if (requestBody != null)
                    request.Content = CreateRequestContent(requestBody, requestHeaders);
                foreach (var kv in requestHeaders)
                {
                    // Content-Type already travelled with the body, above.
                    if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (request.Headers.TryAddWithoutValidation(kv.Key, kv.Value))
                        continue;

                    // What HttpRequestMessage.Headers refuses is a *content* header:
                    // Content-Language, Content-Disposition, Content-Encoding and the rest belong to
                    // the body in this API, not the request. TryAddWithoutValidation reports the
                    // refusal by returning false, and this loop used to discard that without a word,
                    // so a page that set one watched it vanish between fetch and the wire. Retrying
                    // against the content is what actually sends them.
                    //
                    // Content-Length is the deliberate exception and stays dropped. The framework
                    // derives it from the body it is about to write, and an author value *replaces*
                    // that derived one rather than being rejected — a page claiming a length its body
                    // does not have would leave the server waiting for bytes that never arrive, which
                    // is a worse failure than the dropped header. Fetch forbids the header to authors
                    // for the same reason.
                    if (!string.Equals(kv.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        request.Content?.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                }

                var response = _resources.SendAsync(request).GetAwaiter().GetResult();
                var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                var statusCode = (int)response.StatusCode;
                attempt.Completed(
                    body,
                    statusCode,
                    response.Content.Headers.ContentType?.MediaType,
                    method);
                var allHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in response.Headers)
                    allHeaders[h.Key] = string.Join(", ", h.Value);
                if (response.Content.Headers != null)
                {
                    foreach (var h in response.Content.Headers)
                        allHeaders[h.Key] = string.Join(", ", h.Value);
                }

                responseObj = createResponse(body, statusCode, response.ReasonPhrase ?? string.Empty, fetchUrl, "basic", false, allHeaders);
            }
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.fetch", $"Fetch error: {ex.Message}", ex);
            attempt.Failed(ex);
            responseObj = createResponse(string.Empty, 0, ex.Message, fetchUrl, "error", false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        }

        // The outcome is settled by this point — an aborted request set `rejected`, everything else
        // produced a `responseObj` — so hand it back as a real, already-settled Promise.
        //
        // This used to be a hand-rolled object with a `then` and a `catch` that each invoked their
        // callback and returned THEMSELVES. Four things were wrong with it, and returning `this` was
        // the worst: `.then(a).then(b)` ran `b` against the original Response rather than `a`'s result,
        // so a mapping chain — the ordinary `fetch(u).then(r => r.json()).then(useData)` shape — handed
        // the second callback the Response instead of the parsed body. That is a silently wrong value,
        // not an error. Beyond it: `.then`'s second (onRejected) argument was ignored entirely;
        // `.finally` did not exist, so calling it was a TypeError; and a callback that threw was caught
        // and logged rather than rejecting the derived promise, so an error inside a handler vanished.
        // The object was also not `instanceof Promise`, which feature-detecting code checks.
        //
        // A real JSPromise gets all of that from the engine. The capture pumps the microtask queue (a
        // plain `Promise.resolve().then(...)` callback runs), so settling through the real machinery
        // still delivers the callbacks.
        return new JSPromise((resolve, reject) =>
        {
            if (rejected)
                reject(rejectedValue);
            else
                resolve(responseObj);
        });
    }

    /// <summary>
    /// Builds the request body, carrying the author's <c>Content-Type</c> across as a header value
    /// rather than as a bare media type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a page sets is a full header value, and pages routinely put parameters in it. The body
    /// used to be built as <c>new StringContent(body, Encoding.UTF8, contentTypeHeader)</c>, whose
    /// third parameter takes the media type <em>alone</em>: it validates through
    /// <c>MediaTypeHeaderValue</c>, so a <c>;</c> anywhere in the value throws
    /// <c>FormatException("The format of value '…' is invalid.")</c> — and an empty value throws
    /// <c>ArgumentException</c>. That is caught below and turned into an error <c>Response</c>, so
    /// the symptom is not a crash but a request that is never sent at all, for every POST whose
    /// content type is spelled the ordinary way: google.com's start page posts
    /// <c>application/x-www-form-urlencoded;charset=utf-8</c> (its XHR sets that through
    /// <c>setRequestHeader</c>, which the polyfill hands to <c>fetch</c> as <c>opts.headers</c>), and
    /// a <c>multipart/form-data; boundary=…</c> or <c>text/plain; charset=UTF-8</c> post failed the
    /// same way.
    /// </para>
    /// <para>
    /// Parsing into <see cref="HttpContentHeaders.ContentType"/> keeps those parameters. The bytes
    /// stay UTF-8 regardless of the charset the author wrote, which is what Fetch §body extraction
    /// says for a string body — the parameter travels in the header, it does not pick the encoding.
    /// A value too malformed to parse is sent verbatim instead of costing the whole request, and
    /// with no <c>Content-Type</c> at all the default stays the spec's <c>text/plain;charset=UTF-8</c>.
    /// </para>
    /// </remarks>
    private static StringContent CreateRequestContent(string requestBody, Dictionary<string, string> requestHeaders)
    {
        var content = new StringContent(requestBody, Encoding.UTF8);
        if (!requestHeaders.TryGetValue("Content-Type", out var contentType) || string.IsNullOrWhiteSpace(contentType))
            return content;

        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            content.Headers.ContentType = parsed;
        }
        else
        {
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        return content;
    }
}
