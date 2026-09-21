using System.Text;

using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The networking feature binding module.
/// It co-locates the whole <c>fetch</c> / <c>XMLHttpRequest</c> surface: the <c>fetch</c> polyfill and
/// its <c>Headers</c>/<c>Request</c>/<c>Response</c>/<c>FormData</c>/<c>Blob</c>/<c>AbortController</c>
/// helper objects, the <c>Response</c> static factories and the <c>XMLHttpRequest</c> polyfill. Host
/// I/O goes through the injected <see cref="ResourceLoader"/> — the "no feature callback constructs
/// an <c>HttpClient</c>" seam — and the other bridge couplings (the page URL used to resolve
/// <c>Response.redirect</c> relative URLs, the realm, and the blob/stream objects other modules own)
/// are reached through the narrow <see cref="IFetchHost"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), <c>ArrayBuffer</c> included: the
/// body readers mint one with <see cref="IJsValues.NewArrayBuffer"/>, as <c>StreamsBinding</c> and
/// <c>BlobBinding</c> do.
/// </para>
/// <para>
/// <b>Every member of these objects is installed as a constructable function, and that is preserved
/// rather than fixed.</b> The surface is built with plain functions throughout, so
/// <c>headers.get.prototype</c> is an object and <c>new headers.get()</c> does not throw, where a
/// browser's <c>Headers.prototype.get</c> is not constructable. That is a long-standing deviation,
/// and <see cref="IJsValues.NewConstructor"/> is the faithful spelling of it; switching to
/// <see cref="IJsValues.NewMethod"/> would be a behaviour change, not a tidy-up.
/// </para>
/// </remarks>
internal sealed partial class FetchBinding(IFetchHost host, ResourceLoader resources)
{
    private readonly IFetchHost _host = host;
    private readonly ResourceLoader _resources = resources;

    /// <summary>
    /// The realm's own <c>JSON.parse</c> and <c>JSON.stringify</c>, read once when the surface is
    /// installed.
    /// </summary>
    /// <remarks>
    /// These are the intrinsics, not whatever the page's <c>globalThis.JSON</c> holds. Reading them
    /// at registration — before a page's first script runs — is what keeps that true through a
    /// contract that has no JSON member: a page that later replaces <c>JSON</c> changes what its own
    /// code sees and not what a <c>response.json()</c> does.
    /// </remarks>
    private JsValue _jsonParse;

    private JsValue _jsonStringify;

    private delegate string? JsPropertyStringGetter(JsValue obj, params string[] names);

    private delegate IEnumerable<(string Key, string Value)> ObjectStringEntriesEnumerator(JsValue obj);

    private delegate (int status, string statusText, string url, string type, bool redirected, Dictionary<string, string> headers) ResponseInitParser(JsValue initValue);

    private delegate JsValue ResponseFactory(string body, int statusCode, string statusText,
        string responseUrl, string type, bool redirected, Dictionary<string, string> headers);

    /// <summary>Installs <c>fetch</c>/<c>Headers</c>/<c>Request</c>/<c>Response</c>/<c>FormData</c> and
    /// <c>XMLHttpRequest</c> on <paramref name="window"/> and the realm's global, returning the
    /// <c>fetch</c> function so the caller can register it among the window globals.</summary>
    internal JsValue Install(IJsRealm realm, JsValue window)
    {
        var json = realm.GetProperty(realm.Global, "JSON");
        _jsonParse = realm.GetProperty(json, "parse");
        _jsonStringify = realm.GetProperty(json, "stringify");

        // The response factories the callback cores take as delegates, bound to this realm.
        ResponseFactory createResponse = (body, statusCode, statusText, responseUrl, type, redirected, headers) =>
            CreateResponse(realm, body, statusCode, statusText, responseUrl, type, redirected, headers);
        ResponseInitParser parseResponseInit = init => ParseResponseInit(realm, init);

        var formDataCtor = realm.NewConstructor("FormData", (in call) => CreateFormDataObject(realm, call[0]), 1);
        var headersCtor = realm.NewConstructor("Headers", (in call) => CreateHeadersObject(realm, call[0]), 1);
        var requestCtor = realm.NewConstructor(
            "Request",
            // The first argument keeps its `undefined` default and the second its "not supplied" one:
            // an input that was never passed is coerced to the string "undefined" for the URL, while
            // an init that was never passed must not be read as an object.
            (in call) => CreateRequestObject(realm, call.Length > 0 ? call[0] : JsValue.Undefined, call[1]),
            2);
        var responseCtor = realm.NewConstructor("Response", (in call) => JsRegistrationResponse113Core(parseResponseInit, createResponse, in call), 2);
        realm.DefineConstructor(responseCtor, "json", 2, (in call) => JsRegistrationJson114Core(parseResponseInit, createResponse, in call));
        realm.DefineConstructor(
            responseCtor,
            "error",
            0,
            (in _) => CreateResponse(realm, string.Empty, 0, string.Empty, string.Empty, "error", false, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        realm.DefineConstructor(responseCtor, "redirect", 2, (in call) => JsRegistrationRedirect116Core(url => ResolveResponseRedirectUrl(realm, url), createResponse, in call));
        realm.DefineValue(window, "FormData", formDataCtor);
        realm.DefineValue(window, "Headers", headersCtor);
        realm.DefineValue(window, "Request", requestCtor);
        realm.DefineValue(window, "Response", responseCtor);
        realm.SetProperty(realm.Global, "FormData", formDataCtor);
        realm.SetProperty(realm.Global, "Headers", headersCtor);
        realm.SetProperty(realm.Global, "Request", requestCtor);
        realm.SetProperty(realm.Global, "Response", responseCtor);
        // fetch(url, options) — polyfill backed by the injected ResourceLoader
        var fetchFn = realm.NewConstructor(
            "fetch",
            (in call) => JsRegistrationFetch120Core(
                (obj, names) => TryGetJsPropertyString(realm, obj, names),
                obj => EnumerateObjectStringEntries(realm, obj),
                signal => CreateAbortErrorValue(realm, signal),
                createResponse,
                in call),
            1);
        realm.DefineValue(window, "fetch", fetchFn);
        // XMLHttpRequest — basic polyfill backed by fetch/the ResourceLoader
        RegisterXMLHttpRequest(realm);
        return fetchFn;
    }

    private IEnumerable<(string Key, string Value)> EnumerateObjectStringEntries(IJsRealm realm, JsValue obj)
    {
        foreach (var key in realm.OwnPropertyNames(obj))
        {
            var value = realm.GetProperty(obj, key);
            if (string.IsNullOrEmpty(key) || key[0] == '_' || value.IsFunction || value.IsUndefined || value.IsNull)
                continue;

            // The realm's ToString, not the handle's: this is the observable ECMAScript coercion
            // the engine's own ToString() performed here, and a header value that is an object
            // with a toString is entitled to it.
            yield return (key, realm.ToJsString(value));
        }
    }

    private string? TryGetJsPropertyString(IJsRealm realm, JsValue obj, params string[] names)
    {
        foreach (var name in names)
        {
            // IsNullish is the three tests this made before it coerced — absent (the engine's
            // indexer answered a CLR null, which is Missing here), undefined, and null.
            var value = realm.GetProperty(obj, name);
            if (!value.IsNullish)
                return realm.ToJsString(value);
        }

        return null;
    }

    // A settled native Promise for a body value that is already in hand — what
    // `response.text()`, `.json()`, `.arrayBuffer()`, `.blob()`, `.formData()` and a stream
    // reader's `read()` return.
    //
    // This used to be a hand-rolled object carrying one `then` that invoked the callback and
    // returned itself. Returning itself is what broke chaining: `.then(a).then(b)` ran `b`
    // against the ORIGINAL value rather than `a`'s result, so a mapping chain read the unmapped
    // value — a silently wrong answer rather than an error. It also had no `catch` and no
    // `finally` (so `.finally()` was a TypeError), was not `instanceof Promise`, and had no
    // rejection path at all, so a resolver that threw — a `.json()` over a malformed body,
    // say — threw synchronously out of `.then` instead of rejecting the promise.
    //
    // A real promise fixes all of it at once, and the engine's microtask queue is pumped in a
    // capture (a plain `Promise.resolve().then(...)` callback runs), so settling through the
    // real machinery still delivers the callback.
    //
    // The realm hands back the two settle functions rather than running an executor, so the
    // promise is settled here instead of inside a callback that only happened to run
    // synchronously — the difference `IJsJobs.NewPromise` exists to remove. The executor's `try`
    // came with that shape and is written out: a throwing `resolver` rejects, which is the
    // conforming outcome and what the JSON body readers rest on.
    private JsValue CreateThenable(IJsRealm realm, Func<JsValue> resolver)
    {
        var promise = realm.NewPromise(out var resolve, out var reject);

        try
        {
            resolve(resolver());
        }
        catch (JsEngineException ex) when (!ex.Thrown.IsMissing)
        {
            // Something the page threw — a toJSON, a getter — reaches the rejection as the value
            // it threw rather than as a description of it.
            reject(ex.Thrown);
        }
        catch (Exception ex)
        {
            reject(ErrorValue(realm, ex.Message));
        }

        return promise;
    }

    private JsValue CreateAbortErrorValue(IJsRealm realm, JsValue signalValue)
    {
        if (signalValue.IsObject)
        {
            var reason = realm.GetProperty(signalValue, "reason");
            if (!reason.IsNullish)
                return reason;
        }

        var error = realm.NewObject();
        realm.SetProperty(error, "name", JsValue.String("AbortError"));
        realm.SetProperty(error, "message", JsValue.String("The operation was aborted."));
        return error;
    }

    private (int status, string statusText, string url, string type, bool redirected, Dictionary<string, string> headers) ParseResponseInit(IJsRealm realm, JsValue initValue)
    {
        var status = 200;
        var statusText = string.Empty;
        var url = string.Empty;
        var type = "basic";
        var redirected = false;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (initValue.IsObject)
        {
            if (TryGetJsPropertyString(realm, initValue, "status") is string statusValue && int.TryParse(statusValue, out var parsedStatus))
                status = parsedStatus;
            statusText = TryGetJsPropertyString(realm, initValue, "statusText") ?? string.Empty;
            url = TryGetJsPropertyString(realm, initValue, "url") ?? string.Empty;
            type = TryGetJsPropertyString(realm, initValue, "type") ?? "basic";
            redirected = string.Equals(TryGetJsPropertyString(realm, initValue, "redirected"), "true", StringComparison.OrdinalIgnoreCase);

            var initHeaders = realm.GetProperty(initValue, "headers");
            if (initHeaders.IsObject)
            {
                foreach (var (key, value) in EnumerateObjectStringEntries(realm, initHeaders))
                    headers[key] = value;
            }
        }

        return (status, statusText, url, type, redirected, headers);
    }

    private string ResolveResponseRedirectUrl(IJsRealm realm, string redirectUrl)
    {
        if (string.IsNullOrWhiteSpace(redirectUrl))
            throw realm.Error(JsErrorKind.Error, "Failed to execute 'redirect' on 'Response': Invalid URL");

        // fetch uses the one shared resolver — absolute stays, relative resolves
        // against the page URL; an unresolvable URL is the spec's "Invalid URL" TypeError.
        return (UrlResolver.Resolve(redirectUrl, _host.PageUrl)
                ?? throw realm.Error(JsErrorKind.Error, "Failed to execute 'redirect' on 'Response': Invalid URL"))
            .AbsoluteUri;
    }

    /// <summary>The realm's <c>JSON.parse</c> over <paramref name="jsonText"/>, called with no receiver.</summary>
    private JsValue ParseJsonText(IJsRealm realm, string jsonText) =>
        realm.Invoke(_jsonParse, JsValue.Undefined, [JsValue.String(jsonText)]);

    /// <summary>
    /// The same, reporting a malformed body as the message <c>response.json()</c> has always
    /// rejected with rather than as the parser's own.
    /// </summary>
    private JsValue ParseResponseJsonText(IJsRealm realm, string jsonText)
    {
        try
        {
            return ParseJsonText(realm, jsonText);
        }
        catch (Exception ex)
        {
            throw realm.Error(JsErrorKind.Error, $"Failed to parse response body as JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// The realm's <c>JSON.stringify</c>, rendered the way the engine's host-side <c>Stringify</c>
    /// helper this replaced rendered it.
    /// </summary>
    /// <remarks>
    /// The helper wrote a string for every input: <c>undefined</c>, <c>null</c> and a non-finite
    /// number became the literal <c>null</c>, and a function became the empty string. The JavaScript
    /// operation instead answers <c>undefined</c> for the first and the last, so the two arms below
    /// are what keeps <c>Response.json(undefined)</c> a body of <c>"null"</c> — the string it was
    /// before — rather than the string <c>"undefined"</c>.
    /// </remarks>
    private string StringifyJson(IJsRealm realm, JsValue value)
    {
        var stringified = realm.Invoke(_jsonStringify, JsValue.Undefined, [value]);
        if (stringified.IsString)
            return stringified.AsString!;

        return value.IsFunction ? string.Empty : "null";
    }

    /// <summary>
    /// An <c>Error</c> to reject with, for a host failure that carries no JavaScript value of its own.
    /// </summary>
    /// <remarks>
    /// The promise executor used to do this itself — it caught anything the resolver threw and
    /// rejected with an error built from it. <see cref="IJsCalls.Error"/> hands back an exception to
    /// throw rather than a value to reject with, so the value is constructed here through the realm's
    /// own <c>Error</c>, which is what the engine's helper did too.
    /// </remarks>
    private static JsValue ErrorValue(IJsRealm realm, string message)
    {
        var error = realm.GetProperty(realm.Global, "Error");
        return error.IsFunction
            ? realm.Construct(error, [JsValue.String(message)])
            : JsValue.String(message);
    }

}
