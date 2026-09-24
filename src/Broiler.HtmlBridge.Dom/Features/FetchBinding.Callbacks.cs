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
        var body = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : string.Empty;
        var (status, statusText, url, type, redirected, headers) = parseResponseInit(call[1]);
        return createResponse(body, status, statusText, url, type, redirected, headers);
    }


    private JsValue JsRegistrationJson114Core(ResponseInitParser parseResponseInit, ResponseFactory createResponse, in JsCall call)
    {
        var jsonBody = StringifyJson(call.Realm, call.Length > 0 ? call[0] : JsValue.Null);
        var (status, statusText, url, type, redirected, headers) = parseResponseInit(call[1]);
        if (!headers.ContainsKey("Content-Type"))
            headers["Content-Type"] = "application/json";
        return createResponse(jsonBody, status, statusText, url, type, redirected, headers);
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
        return createResponse(string.Empty, status, string.Empty, string.Empty, "basic", false, headers);
    }


    private JsValue JsRegistrationFetch120Core(JsPropertyStringGetter tryGetJsPropertyString, ObjectStringEntriesEnumerator enumerateObjectStringEntries, Func<JsValue, JsValue> createAbortErrorValue, ResponseFactory createResponse, in JsCall call)
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
        string? requestBody = null;
        string? inputMode = null, inputCredentials = null, inputRedirect = null;
        string? initMode = null, initCredentials = null, initRedirect = null;
        var requestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var signalValue = JsValue.Undefined;
        if (input.IsObject)
        {
            requestedUrl = tryGetJsPropertyString(input, "url", "href") ?? requestedUrl;
            method = tryGetJsPropertyString(input, "method");
            requestBody = tryGetJsPropertyString(input, "_bodyInit", "body");
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
            requestBody = tryGetJsPropertyString(opts, "body") ?? requestBody;
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

        // The outcome is settled before this returns — the send is synchronous — so the page gets a
        // real, already-settled Promise.
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
        // A real promise gets all of that from the engine. The capture pumps the microtask queue (a
        // plain `Promise.resolve().then(...)` callback runs), so settling through the real machinery
        // still delivers the callbacks. The realm hands back the settle functions rather than running
        // an executor, so the arms below are reached directly instead of through a callback that
        // only happened to run synchronously — see IJsJobs.NewPromise.
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
                requestBody,
                ModeOf(inputMode, initMode),
                CredentialsOf(inputCredentials, initCredentials),
                RedirectOf(inputRedirect, initRedirect));
        }
        catch (AuthorRequestError error)
        {
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
        try
        {
            using var message = CreateMessage(request);
            using var response = SendAuthorRequest(
                message, RequestContext.Fetch(client, request.Mode, request.Credentials, request.Redirect));
            resolve(ExposeResponse(response, createResponse, attempt, request.Method));
        }
        catch (Exception ex)
        {
            // A network error — no response at all, a CORS failure, a redirect the mode refused, the
            // loader's budget running out — is a TypeError. What it was is the host's to log and not
            // the page's to read: a CORS failure must tell a page no more than a refused connection.
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.fetch", $"Fetch error: {ex.Message}", ex);
            attempt.Failed(ex);
            reject(TypeErrorValue(realm, "Failed to fetch"));
        }

        return promise;
    }
}
