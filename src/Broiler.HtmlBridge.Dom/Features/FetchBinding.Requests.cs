using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.JSeal;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The script-facing safety gates every request a page authors passes through — <c>fetch()</c>, the
/// <c>XMLHttpRequest</c> polyfill (which calls the same core) and <c>navigator.sendBeacon</c> — and
/// the filter every response passes through on its way back to the page.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the page may say.</b> Fetch's "new Request" steps, reduced to what this binding carries:
/// the <c>mode</c>, <c>credentials</c> and <c>redirect</c> enums (a <c>navigate</c> mode is a
/// <c>TypeError</c>), the method (a token, never <c>CONNECT</c>/<c>TRACE</c>/<c>TRACK</c>, only
/// <c>GET</c>/<c>HEAD</c>/<c>POST</c> in <c>no-cors</c>, no body on <c>GET</c>/<c>HEAD</c>) and the
/// headers (validated as Fetch's <c>Headers</c> validates them; a forbidden request-header is dropped
/// silently, as the <c>request</c> guard does; <c>no-cors</c> keeps only no-CORS-safelisted ones).
/// So <c>Cookie</c>, <c>Host</c>, <c>Origin</c> and the <c>Sec-</c> family never reach the wire from
/// script, whatever the transport does.
/// </para>
/// <para>
/// <b>What the page may see.</b> The transport's response is privileged — every header, Set-Cookie
/// included, and the real status. The page gets <see cref="TransportResponse.ScriptVisibleStatusCode"/>,
/// <see cref="TransportResponse.GetScriptVisibleHeaders"/>, the final URL, <c>redirected</c>, the
/// <c>type</c> the transport's tainting gives, and no body for an opaque or opaque-redirect response.
/// A network error — the transport's <see cref="TransportException"/>, a CORS failure among them — is
/// a <c>TypeError</c>, as it is in a browser.
/// </para>
/// <para>
/// Without a profile transport the loader's cookie-less fallback client answers instead. It keeps and
/// sends no cookies, so the credentials mode has nothing to gate there; it follows redirects itself
/// and performs no CORS check, so every response it returns is exposed as a basic one — still
/// through the same header filter.
/// </para>
/// </remarks>
internal sealed partial class FetchBinding
{
    /// <summary>A request as the page authored it, after the gates above.</summary>
    private sealed record AuthorRequest(
        Uri Url,
        string Method,
        IReadOnlyList<KeyValuePair<string, string>> Headers,
        string? Body,
        RequestMode Mode,
        CredentialsMode Credentials,
        RedirectMode Redirect);

    /// <summary>
    /// A request the page authored that Fetch refuses with a <c>TypeError</c>; the message is the
    /// reason. Caught where the request is made and rethrown or rejected as the realm's own
    /// <c>TypeError</c>.
    /// </summary>
    private sealed class AuthorRequestError(string message) : Exception(message);

    /// <summary>
    /// The realm's own <c>TypeError</c> constructor, read when the surface is installed — before a
    /// page's first script — so a page that replaces the global cannot change what a failed fetch
    /// rejects with. The same reasoning as the JSON intrinsics beside it.
    /// </summary>
    private JsValue _typeError;

    /// <summary>A <c>TypeError</c> to reject with.</summary>
    private JsValue TypeErrorValue(IJsRealm realm, string message) =>
        _typeError.IsFunction ? realm.Construct(_typeError, [JsValue.String(message)]) : ErrorValue(realm, message);

    /// <summary>
    /// Applies Fetch's "new Request" checks to what the page supplied, answering the request that
    /// may be sent. Throws <see cref="AuthorRequestError"/> for what Fetch refuses.
    /// </summary>
    /// <param name="url">The URL as the page wrote it.</param>
    /// <param name="baseUrl">What a relative <paramref name="url"/> resolves against: the calling document's base.</param>
    /// <param name="method">The method, or <see langword="null"/> for <c>GET</c>.</param>
    /// <param name="headers">The headers as the page supplied them, unvalidated.</param>
    /// <param name="body">The body text, or <see langword="null"/> for none.</param>
    private static AuthorRequest PrepareAuthorRequest(
        string url,
        string baseUrl,
        string? method,
        IEnumerable<KeyValuePair<string, string>> headers,
        string? body,
        RequestMode mode,
        CredentialsMode credentials,
        RedirectMode redirect)
    {
        var resolved = UrlResolver.Resolve(url, baseUrl)
                       ?? throw new AuthorRequestError($"Failed to parse URL from {url}.");

        var normalizedMethod = MethodOf(method);
        if (mode == RequestMode.NoCors && !FetchHeaders.IsCorsSafelistedMethod(normalizedMethod))
            throw new AuthorRequestError($"'{normalizedMethod}' is unsupported in no-cors mode.");
        if (body is not null && normalizedMethod is "GET" or "HEAD")
            throw new AuthorRequestError("Request with GET/HEAD method cannot have body.");

        var accepted = new List<KeyValuePair<string, string>>();
        foreach (var (name, raw) in headers)
        {
            var value = FetchHeaders.NormalizeHeaderValue(raw);
            if (!FetchHeaders.IsHeaderName(name))
                throw new AuthorRequestError($"'{name}' is not a valid HTTP header name.");
            if (!FetchHeaders.IsHeaderValue(value))
                throw new AuthorRequestError($"'{value}' is not a valid value for the '{name}' header.");

            // The request guard drops a forbidden name silently, and the request-no-cors guard drops
            // anything a no-cors request could not send without a preflight.
            if (FetchHeaders.IsForbiddenRequestHeader(name, value) ||
                (mode == RequestMode.NoCors && !FetchHeaders.IsNoCorsSafelistedRequestHeader(name, value)))
                continue;

            accepted.Add(new(name, value));
        }

        return new AuthorRequest(resolved, normalizedMethod, accepted, body, mode, credentials, redirect);
    }

    /// <summary>
    /// A request method as Fetch takes it: a token that is not a forbidden method, with the six
    /// standard methods upper-cased and every other one kept as written. <see langword="null"/> is
    /// <c>GET</c>.
    /// </summary>
    private static string MethodOf(string? method)
    {
        if (method is null)
            return "GET";
        if (!FetchHeaders.IsHeaderName(method))
            throw new AuthorRequestError($"'{method}' is not a valid HTTP method.");
        if (FetchHeaders.IsForbiddenMethod(method))
            throw new AuthorRequestError($"'{method}' HTTP method is unsupported.");
        return FetchHeaders.NormalizeMethod(method);
    }

    /// <summary>
    /// The request mode: <paramref name="init"/>'s when the page passed one (Web IDL refuses a value
    /// outside the enum, and Fetch refuses <c>navigate</c>), otherwise the input Request's — whose
    /// <c>navigate</c> is fetched as <c>same-origin</c> — otherwise <c>cors</c>.
    /// </summary>
    private static RequestMode ModeOf(string? input, string? init)
    {
        if (init is not null)
        {
            return init switch
            {
                "same-origin" => RequestMode.SameOrigin,
                "no-cors" => RequestMode.NoCors,
                "cors" => RequestMode.Cors,
                "navigate" => throw new AuthorRequestError(
                    "Cannot construct a Request with a RequestInit whose mode member is set as 'navigate'."),
                _ => throw InvalidEnumValue(init, "RequestMode"),
            };
        }

        return input switch
        {
            "same-origin" or "navigate" => RequestMode.SameOrigin,
            "no-cors" => RequestMode.NoCors,
            _ => RequestMode.Cors,
        };
    }

    /// <summary>The credentials mode, by the same precedence; <c>same-origin</c> by default.</summary>
    private static CredentialsMode CredentialsOf(string? input, string? init) =>
        (init ?? input) switch
        {
            "omit" => CredentialsMode.Omit,
            "include" => CredentialsMode.Include,
            "same-origin" => CredentialsMode.SameOrigin,
            null => CredentialsMode.SameOrigin,
            var other when init is not null => throw InvalidEnumValue(other, "RequestCredentials"),
            _ => CredentialsMode.SameOrigin,
        };

    /// <summary>The redirect mode, by the same precedence; <c>follow</c> by default.</summary>
    private static RedirectMode RedirectOf(string? input, string? init) =>
        (init ?? input) switch
        {
            "error" => RedirectMode.Error,
            "manual" => RedirectMode.Manual,
            "follow" => RedirectMode.Follow,
            null => RedirectMode.Follow,
            var other when init is not null => throw InvalidEnumValue(other, "RequestRedirect"),
            _ => RedirectMode.Follow,
        };

    private static AuthorRequestError InvalidEnumValue(string value, string type) =>
        new($"The provided value '{value}' is not a valid enum value of type {type}.");

    /// <summary>The enum values as a Request object reports them.</summary>
    private static string NameOf(RequestMode mode) => mode switch
    {
        RequestMode.SameOrigin => "same-origin",
        RequestMode.NoCors => "no-cors",
        RequestMode.Navigate => "navigate",
        _ => "cors",
    };

    private static string NameOf(CredentialsMode credentials) => credentials switch
    {
        CredentialsMode.Omit => "omit",
        CredentialsMode.Include => "include",
        _ => "same-origin",
    };

    private static string NameOf(RedirectMode redirect) => redirect switch
    {
        RedirectMode.Error => "error",
        RedirectMode.Manual => "manual",
        _ => "follow",
    };

    /// <summary>
    /// The wire message for an author request: its accepted headers on the request or, for the
    /// content headers <see cref="HttpRequestMessage"/> keeps on the body, on the body.
    /// </summary>
    private static HttpRequestMessage CreateMessage(AuthorRequest request)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
        if (request.Body != null)
            message.Content = CreateRequestContent(request.Body, request.Headers);

        foreach (var (name, value) in request.Headers)
        {
            // Content-Type already travelled with the body, above.
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;

            if (message.Headers.TryAddWithoutValidation(name, value))
                continue;

            // What HttpRequestMessage.Headers refuses is a *content* header: Content-Language,
            // Content-Disposition, Content-Encoding and the rest belong to the body in this API, not
            // the request. TryAddWithoutValidation reports the refusal by returning false, so the
            // retry against the content is what actually sends them. (Content-Length, which the
            // framework derives from the body, is a forbidden request-header and never gets here.)
            message.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        return message;
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
    /// <c>ArgumentException</c>. The symptom was not a crash but a request that was never sent at
    /// all, for every POST whose content type is spelled the ordinary way: google.com's start page
    /// posts <c>application/x-www-form-urlencoded;charset=utf-8</c> (its XHR sets that through
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
    private static StringContent CreateRequestContent(string requestBody, IReadOnlyList<KeyValuePair<string, string>> requestHeaders)
    {
        var content = new StringContent(requestBody, Encoding.UTF8);
        var contentType = requestHeaders
            .Where(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .Select(header => header.Value)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(contentType))
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

    /// <summary>
    /// Sends an author request for <paramref name="client"/> and answers the complete response, its
    /// body already buffered. Blocking: a page's <c>fetch()</c> has always settled before it returned.
    /// </summary>
    private TransportResponse SendAuthorRequest(HttpRequestMessage message, RequestContext context) =>
        _resources.SendAsync(message, context).GetAwaiter().GetResult();

    /// <summary>
    /// The <c>Response</c> a page receives for <paramref name="response"/>: Fetch's filtered response
    /// for the transport's tainting, never the privileged header list or status.
    /// </summary>
    private static JsValue ExposeResponse(
        TransportResponse response,
        ResponseFactory createResponse,
        ResourceTrace.Attempt attempt,
        string method)
    {
        var opaque = response.Tainting is ResponseTainting.Opaque or ResponseTainting.OpaqueRedirect;
        var message = response.Message;
        var body = opaque ? string.Empty : message.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        attempt.Completed(opaque ? null : body, response.StatusCode, message.Content.Headers.ContentType?.MediaType, method);

        // Headers joins repeated fields with ", ", which is what Headers.get answers for them.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in response.GetScriptVisibleHeaders())
            headers[name] = headers.TryGetValue(name, out var existing) ? $"{existing}, {value}" : value;

        // An opaque response has an empty URL list; an opaque-redirect keeps the URL that redirected.
        // Either way the URL a page reads carries no fragment.
        var url = response.Tainting == ResponseTainting.Opaque ? string.Empty : WithoutFragment(response.FinalUrl);
        var redirected = response.Tainting != ResponseTainting.Opaque && response.Redirected;
        var type = response.Tainting switch
        {
            ResponseTainting.Cors => "cors",
            ResponseTainting.Opaque => "opaque",
            ResponseTainting.OpaqueRedirect => "opaqueredirect",
            _ => "basic",
        };

        return createResponse(
            body,
            response.ScriptVisibleStatusCode,
            opaque ? string.Empty : message.ReasonPhrase ?? string.Empty,
            url,
            type,
            redirected,
            headers);

        static string WithoutFragment(Uri url) =>
            url.Fragment.Length == 0 ? url.AbsoluteUri : url.GetLeftPart(UriPartial.Query);
    }
}
