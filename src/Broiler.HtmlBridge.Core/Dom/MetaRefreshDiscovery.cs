using System;
using System.Globalization;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge.Dom;

/// <summary>
/// The navigation a document asks for in markup rather than in script:
/// <c>&lt;meta http-equiv="refresh" content="0;url=…"&gt;</c>.
/// <para>
/// It produces the same <see cref="NavigationRequest"/> a script navigation does, so a host follows
/// both through one path and gets one set of loop guards. What it does not share is where it comes
/// from — and that is the whole reason it lives here rather than on the bridge. A refresh
/// interstitial usually carries no script at all, and a page with no scripts never gets a
/// <c>DomBridge</c>, so anything discovered through the bridge would miss exactly the documents that
/// use this.
/// </para>
/// <para>
/// Discovery is <c>Broiler.Dom.Html</c> parser output, matching <c>CspMetaDiscovery</c>: the shared
/// <see cref="HtmlTokenizer"/> enumerates start tags, so a <c>&lt;meta&gt;</c> inside a comment or a
/// raw-text body is ignored and a <c>&gt;</c> in a quoted attribute does not truncate the tag.
/// </para>
/// </summary>
public static class MetaRefreshDiscovery
{
    /// <summary>
    /// Returns the navigation declared by the first valid refresh <c>&lt;meta&gt;</c> in
    /// <paramref name="html"/>, or <c>null</c> when there is none.
    /// </summary>
    /// <remarks>
    /// First, not last: HTML acts on the first one it reaches, and a document carrying two is
    /// telling the reader about the first.
    /// </remarks>
    public static NavigationRequest? Find(string html, string documentUrl)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        foreach (var token in new HtmlTokenizer().Tokenize(html))
        {
            if (token.Type != TokenType.StartTag ||
                !string.Equals(token.Name, "meta", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!token.Attributes.TryGetValue("http-equiv", out var httpEquiv) ||
                !string.Equals(httpEquiv?.Trim(), "refresh", StringComparison.OrdinalIgnoreCase))
                continue;

            if (token.Attributes.TryGetValue("content", out var content) &&
                TryParseContent(content, documentUrl, out var request))
                return request;
        }

        return null;
    }

    /// <summary>
    /// Parses a refresh <c>content</c> value — <c>"5"</c>, <c>"0;url=/next"</c>,
    /// <c>"0, URL='next.html'"</c> — into the navigation it declares.
    /// </summary>
    /// <remarks>
    /// A subset of HTML's shared declarative refresh steps, covering the forms documents actually
    /// use: a leading time, then an optional <c>;</c> or <c>,</c>, an optional <c>url=</c>, and an
    /// optionally quoted target. A value with no time at all is rejected rather than guessed at —
    /// the time is the one part the syntax requires, and a <c>content</c> without one is more likely
    /// a different <c>http-equiv</c>'s value than a refresh someone meant.
    /// </remarks>
    public static bool TryParseContent(string content, string documentUrl, out NavigationRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var span = content.AsSpan().Trim();

        var digits = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
            digits++;

        if (digits == 0)
            return false;

        if (!int.TryParse(span[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            // A time too long to be an int is a time nobody is waiting for.
            return false;
        }

        var rest = span[digits..];

        // A fractional part is legal and does not change which second the page is asking for.
        if (rest.Length > 0 && rest[0] == '.')
        {
            var fraction = 1;
            while (fraction < rest.Length && char.IsAsciiDigit(rest[fraction]))
                fraction++;
            rest = rest[fraction..];
        }

        rest = rest.TrimStart();
        if (rest.Length > 0 && (rest[0] == ';' || rest[0] == ','))
            rest = rest[1..].TrimStart();

        if (rest.StartsWith("url", StringComparison.OrdinalIgnoreCase))
        {
            var afterKeyword = rest[3..].TrimStart();
            if (afterKeyword.Length > 0 && afterKeyword[0] == '=')
                rest = afterKeyword[1..].TrimStart();
        }

        rest = rest.Trim();
        if (rest.Length >= 2 && (rest[0] == '"' || rest[0] == '\'') && rest[^1] == rest[0])
            rest = rest[1..^1].Trim();

        // No target is a document asking for itself — a page that polls by reloading.
        var target = rest.Length == 0 ? documentUrl : rest.ToString();

        if (Uri.TryCreate(documentUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, target, out var resolved))
        {
            target = resolved.ToString();
        }

        if (string.IsNullOrWhiteSpace(target))
            return false;

        request = new NavigationRequest(target, NavigationKind.MetaRefresh, TimeSpan.FromSeconds(seconds));
        return true;
    }
}
