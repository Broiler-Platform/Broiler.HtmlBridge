using System;

namespace Broiler.HtmlBridge.Internal.Scripting;

/// <summary>
/// The one URL-resolution implementation shared by the CSP source matcher and external-script fetching
/// (Phase 7 item 4 / the "one URL resolution/origin implementation shared by script, CSS, fetch, XHR and
/// frames" exit criterion). Resolves a possibly-relative URL to an absolute <see cref="Uri"/> against an
/// optional base URL.
/// </summary>
internal static class UrlResolver
{
    /// <summary>
    /// Returns <paramref name="url"/> as an absolute <see cref="Uri"/>, resolving a relative URL against
    /// <paramref name="baseUrl"/>. Returns <c>null</c> when the URL is relative and no usable base is
    /// given, or when neither can be parsed.
    /// </summary>
    public static Uri? Resolve(string url, string? baseUrl)
    {
        if (!IsPathAbsoluteReference(url) && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute;

        if (!string.IsNullOrWhiteSpace(baseUrl) &&
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) &&
            Uri.TryCreate(baseUri, url, out var resolved))
            return resolved;

        return null;
    }

    /// <summary>
    /// Whether <paramref name="url"/> is a path-absolute (<c>/style.css</c>) or scheme-relative
    /// (<c>//cdn.example/style.css</c>) reference, both of which must resolve against the base
    /// rather than stand on their own.
    /// </summary>
    /// <remarks>
    /// RFC 3986 requires a scheme for an absolute URI, so neither form is one — but on Unix
    /// <c>Uri.TryCreate("/style.css", UriKind.Absolute, …)</c> succeeds anyway and yields
    /// <c>file:///style.css</c>, because a leading slash is a valid absolute <em>file path</em>
    /// there. Every caller of this resolver was therefore taking a document's root-relative
    /// references off the page's own origin and pointing them at the filesystem root: a
    /// <c>&lt;script src="/app.js"&gt;</c> on an <c>https:</c> page resolved to
    /// <c>file:///app.js</c>, and the CSP source matcher compared that against the policy. The
    /// check is cheap and the two forms are the whole of what the platform gets wrong here.
    /// </remarks>
    private static bool IsPathAbsoluteReference(string? url) =>
        url is { Length: > 0 } && (url[0] == '/' || url[0] == '\\');
}
