using System;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Internal.Scripting;

/// <summary>
/// URL/origin context for Content Security Policy source-expression matching (Phase 7 item 1, third
/// concern): resolving a candidate URL against the page, same-origin comparison, and matching a CSP
/// source token — a scheme source (<c>https:</c>) or an absolute host-source (<c>https://host:port/path</c>)
/// — against a resolved candidate URL. Deliberately separate from <see cref="ContentSecurityPolicy"/>'s
/// directive parse/evaluation and from <see cref="CspMetaDiscovery"/>'s document discovery: discovery
/// answers "where is the policy", the policy answers "what does it allow", and this answers "does this URL
/// satisfy a source token".
/// </summary>
internal static class CspSourceMatching
{
    /// <summary>Resolves <paramref name="url"/> to an absolute URI, using <paramref name="pageUrl"/> as
    /// the base for a relative URL. Returns <c>null</c> when it cannot be resolved. Delegates to the
    /// shared <see cref="UrlResolver"/>.</summary>
    public static Uri? ResolveUri(string url, string? pageUrl) => UrlResolver.Resolve(url, pageUrl);

    /// <summary>Whether <paramref name="candidate"/> is same-origin with <paramref name="pageUrl"/>
    /// (scheme/host/port; two <c>file:</c> URLs are treated as same-origin).</summary>
    public static bool IsSameOrigin(Uri candidate, string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
            return false;

        if (string.Equals(candidate.Scheme, "file", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(pageUri.Scheme, "file", StringComparison.OrdinalIgnoreCase))
            return true;

        return Origin.SchemeHostPortEquals(candidate, pageUri);
    }

    /// <summary>Whether a CSP source token is a bare scheme source such as <c>https:</c>
    /// (as opposed to a keyword like <c>'self'</c> or a host source with <c>://</c>).</summary>
    public static bool IsSchemeSource(string source)
        => source.EndsWith(':') &&
           !source.StartsWith('\'') &&
           !source.Contains("://", StringComparison.Ordinal);

    /// <summary>Whether an absolute host-source token (<c>scheme://host:port/path</c>) matches
    /// <paramref name="candidate"/> by scheme, host, port and path prefix.</summary>
    public static bool MatchesAbsoluteSource(string source, Uri candidate)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri))
            return false;

        if (!string.Equals(sourceUri.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceUri.Host, candidate.Host, StringComparison.OrdinalIgnoreCase) ||
            sourceUri.Port != candidate.Port)
            return false;

        var path = sourceUri.AbsolutePath;
        return string.IsNullOrEmpty(path) ||
               string.Equals(path, "/", StringComparison.Ordinal) ||
               candidate.AbsolutePath.StartsWith(path, StringComparison.Ordinal);
    }
}
