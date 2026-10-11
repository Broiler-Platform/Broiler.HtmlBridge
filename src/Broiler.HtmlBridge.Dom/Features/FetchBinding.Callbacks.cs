using System.Text;

using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Response</c> static factories (<c>new Response</c> / <c>Response.json</c> /
/// <c>Response.redirect</c>) and the <c>fetch</c> network call, in the co-located
/// <see cref="FetchBinding"/> module. The fetch
/// implementation performs its host I/O through the injected <see cref="Broiler.HtmlBridge.Dom.Runtime.ResourceLoader"/>.
/// </summary>
internal sealed partial class FetchBinding
{
    private JsValue JsRegistrationResponse113Core(ResponseInitParser parseResponseInit, ResponseFactory createResponse, in JsCall call)
    {
        // Fetch's "extract a body"; its type is the Content-Type unless the init named one (measured:
        // new Response('x') is text/plain;charset=UTF-8, new Response(bytes) has none).
        var body = call.Length > 0 && !call[0].IsNullish ? ExtractBody(call.Realm, call[0]) : new BodyWithType([], null);
        var (status, statusText, url, type, redirected, headers) = parseResponseInit(call[1]);
        if (body.ContentType is { } contentType && !headers.ContainsKey("Content-Type"))
            headers["Content-Type"] = contentType;
        return createResponse(body.Bytes, status, statusText, url, type, redirected, headers);
    }


    private JsValue JsRegistrationJson114Core(ResponseInitParser parseResponseInit, ResponseFactory createResponse, in JsCall call)
    {
        var jsonBody = StringifyJson(call.Realm, call.Length > 0 ? call[0] : JsValue.Null);
        var (status, statusText, url, type, redirected, headers) = parseResponseInit(call[1]);
        if (!headers.ContainsKey("Content-Type"))
            headers["Content-Type"] = "application/json";
        return createResponse(Encoding.UTF8.GetBytes(jsonBody), status, statusText, url, type, redirected, headers);
    }


    private JsValue JsRegistrationRedirect116Core(Func<string, string> resolveResponseRedirectUrl, ResponseFactory createResponse, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'redirect' on 'Response': 1 argument required.");
        var status = 302;
        if (call.Length > 1 && int.TryParse(call.Realm.ToJsString(call[1]), out var parsedStatus))
            status = parsedStatus;
        if (status is not (301 or 302 or 303 or 307 or 308))
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'redirect' on 'Response': Invalid status code");
        var resolvedUrl = resolveResponseRedirectUrl(call.Realm.ToJsString(call[0]));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Location"] = resolvedUrl
        };
        return createResponse([], status, string.Empty, string.Empty, "basic", false, headers);
    }


    /// <param name="initiatorType">What the fetch is to Resource Timing: <c>fetch</c>, or <c>xmlhttprequest</c> for the XMLHttpRequest polyfill's sends.</param>
    private JsValue JsRegistrationFetch120Core(JsPropertyStringGetter tryGetJsPropertyString, ObjectStringEntriesEnumerator enumerateObjectStringEntries, Func<JsValue, JsValue> createAbortErrorValue, ResponseFactory createResponse, string initiatorType, bool synchronous, in JsCall call)
    {
        var realm = call.Realm;

        if (call.Length == 0)
            throw realm.Error(JsErrorKind.Error, "Failed to execute 'fetch': 1 argument required.");
        var requestedUrl = realm.ToJsString(call[0]);
        var input = call[0];

        // What the page supplied: the input's members first (a Request, or a URL object's href),
        // then init's, which win. Nothing is checked yet — PrepareAuthorRequest applies Fetch's
        // "new Request" rules to the merged values, so a Request object and an init object are held
        // to the same ones.
        string? method = null;
        BodyWithType? requestBody = null;
        string? inputMode = null, inputCredentials = null, inputRedirect = null;
        string? initMode = null, initCredentials = null, initRedirect = null;
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var signalValue = JsValue.Undefined;
        if (input.IsObject)
        {
            requestedUrl = tryGetJsPropertyString(input, "url", "href") ?? requestedUrl;
            method = tryGetJsPropertyString(input, "method");
            // A Request's body is the one it was made with; its headers already carry its type.
            if (RequestBodyOf(input) is { } stored)
                requestBody = new BodyWithType(stored.BufferedBytes ?? [], null);
            inputMode = tryGetJsPropertyString(input, "mode");
            inputCredentials = tryGetJsPropertyString(input, "credentials");
            inputRedirect = tryGetJsPropertyString(input, "redirect");
            var requestSignal = realm.GetProperty(input, "signal");
            if (!requestSignal.IsNullish)
                signalValue = requestSignal;
            var requestHeadersObject = realm.GetProperty(input, "headers");
            if (requestHeadersObject.IsObject)
            {
                foreach (var (key, value) in enumerateObjectStringEntries(requestHeadersObject))
                    requestHeaders[key] = value;
            }
        }

        if (call.Length > 1 && call[1].IsObject)
        {
            var opts = call[1];
            method = tryGetJsPropertyString(opts, "method") ?? method;
            var initBody = realm.GetProperty(opts, "body");
            if (!initBody.IsNullish)
                requestBody = ExtractBody(realm, initBody);
            initMode = tryGetJsPropertyString(opts, "mode");
            initCredentials = tryGetJsPropertyString(opts, "credentials");
            initRedirect = tryGetJsPropertyString(opts, "redirect");
            var optionsSignal = realm.GetProperty(opts, "signal");
            if (!optionsSignal.IsNullish)
                signalValue = optionsSignal;
            var optionsHeadersObject = realm.GetProperty(opts, "headers");
            if (optionsHeadersObject.IsObject)
            {
                foreach (var (key, value) in enumerateObjectStringEntries(optionsHeadersObject))
                    requestHeaders[key] = value;
            }
        }

        // The body's own type when the page set none (Fetch's "new Request" steps).
        if (requestBody?.ContentType is { } bodyType && !requestHeaders.ContainsKey("Content-Type"))
            requestHeaders["Content-Type"] = bodyType;

        // Network completion settles this promise on the owning event loop.
        var promise = realm.NewPromise(out var resolve, out var reject);

        if (signalValue.IsObject && realm.GetProperty(signalValue, "aborted").AsBoolean)
        {
            reject(createAbortErrorValue(signalValue));
            return promise;
        }

        // The calling document — the frame whose script is running, when the bridge can tell — is the
        // request's client, and its base is what a relative URL resolves against (§5.4 of Fetch: the
        // entry settings object's base URL). Without that resolution a root-relative target went to
        // HttpClient verbatim as a relative request URI, which is an InvalidOperationException rather
        // than a request — and that is not an exotic spelling: google.com beacons its timing to
        // `/gen_204?atyp=i&…`, and every XMLHttpRequest opened on a path reaches this core.
        var client = _host.FetchClient;
        AuthorRequest request;
        try
        {
            request = PrepareAuthorRequest(
                requestedUrl,
                _host.FetchBaseUrl,
                method,
                requestHeaders,
                requestBody?.Bytes,
                ModeOf(inputMode, initMode),
                CredentialsOf(inputCredentials, initCredentials),
                RedirectOf(inputRedirect, initRedirect));
        }
        catch (AuthorRequestError error)
        {
            if (synchronous) throw realm.Error(JsErrorKind.TypeError, error.Message);
            // Fetch's own TypeError: what the page asked for cannot be a request at all. Rejected
            // rather than thrown, as fetch() does — a throw would abort the whole calling script.
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.fetch", $"Fetch refused: {error.Message}");
            reject(TypeErrorValue(realm, $"Failed to execute 'fetch' on 'Window': {error.Message}"));
            return promise;
        }

        // Traced here rather than inside the loader, for two reasons: the body is already decoded at
        // this level (asking the loader would decode the same bytes twice), and a refused or aborted
        // request — which never reaches the loader — must not be recorded as a request that was made.
        // Begun before the try so a failure is recorded with the time it took to fail. Off by default;
        // see ResourceTrace.
        var attempt = ResourceTrace.Begin(ResourceTraceKind.Fetch, request.Url.AbsoluteUri);
        var timing = new DeferredResourceTiming(_host.ResourceTimings);
        var context = RequestContext.Fetch(client, request.Mode, request.Credentials, request.Redirect);
        var timingRequest = new ResourceTimingRequest(initiatorType, client, timing);
        if (synchronous)
        {
            try
            {
                using var message = CreateMessage(request);
                using var response = SendAuthorRequest(message, context, timingRequest);
                var data = ReadResponseAsync(response, attempt, request.Method).GetAwaiter().GetResult();
                timing.Release();
                return data.Expose(createResponse);
            }
            catch (Exception ex)
            {
                attempt.Failed(ex);
                throw realm.Error(JsErrorKind.TypeError, "Failed to fetch");
            }
        }

        var settled = false;
        var inDocument = _host.CaptureFetchCompletion();
        var abortListener = JsValue.Undefined;
        void RemoveAbortListener()
        {
            if (!abortListener.IsFunction) return;
            var listener = abortListener;
            abortListener = JsValue.Undefined;
            try
            {
                var remove = realm.GetProperty(signalValue, "removeEventListener");
                if (remove.IsFunction)
                    realm.Invoke(remove, signalValue, [JsValue.String("abort"), listener]);
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.fetch", $"Abort listener cleanup failed: {ex.Message}");
            }
        }

        StreamingStoredBody? activeBody = null;
        var cancel = _eventLoop.StartAsync(async token =>
        {
            using var message = CreateMessage(request);
            var result = await _resources.SendHeadersAsync(message, context, timingRequest, token, blocksCaller: false).ConfigureAwait(false);
            var head = ExtractResponseHead(result.Response, request.Method);
            return new FetchResultHead(result, head);
        }, (outcome, error) => inDocument(() =>
        {
            if (settled)
            {
                outcome?.Dispose();
                return;
            }
            settled = true;
            if (error is not null)
            {
                RemoveAbortListener();
                RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.fetch", $"Fetch error: {error.Message}", error);
                attempt.Failed(error);
                reject(TypeErrorValue(realm, "Failed to fetch"));
                return;
            }
            try
            {
                var result = outcome.Result;
                var head = outcome.Head;
                var streamingBody = new StreamingStoredBody(result, head.ContentType, head.HasNoBody);
                activeBody = streamingBody;
                var exposed = CreateResponse(realm, streamingBody, head.Status, head.StatusText, head.Url, head.Type, head.Redirected, head.Headers);
                if (initiatorType != "fetch" || head.HasNoBody)
                {
                    RemoveAbortListener();
                    timing.Release();
                }
                else
                {
                    ReleaseOnBodyRead(exposed, () =>
                    {
                        timing.Release();
                    });
                }
                resolve(exposed);
            }
            catch (Exception ex)
            {
                RemoveAbortListener();
                outcome?.Dispose();
                RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.fetch", $"Response error: {ex.Message}", ex);
                reject(TypeErrorValue(realm, "Failed to fetch"));
            }
        }), abandoned: () =>
        {
            settled = true;
            RemoveAbortListener();
        });

        if (signalValue.IsObject)
        {
            var add = realm.GetProperty(signalValue, "addEventListener");
            if (add.IsFunction)
            {
                abortListener = realm.NewMethod("abort", (in _) =>
                {
                    if (!settled)
                    {
                        settled = true;
                        cancel();
                        RemoveAbortListener();
                        reject(createAbortErrorValue(signalValue));
                    }
                    else
                    {
                        activeBody?.Dispose();
                        RemoveAbortListener();
                    }
                    return JsValue.Undefined;
                }, 0);
                realm.Invoke(add, signalValue, [JsValue.String("abort"), abortListener]);
            }
        }

        return promise;
    }
}
