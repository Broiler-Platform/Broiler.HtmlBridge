using System;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge.Internal.Scripting;

/// <summary>
/// Document-side <b>discovery</b> of a Content Security Policy: locating the policy directive string
/// declared via a <c>&lt;meta http-equiv="Content-Security-Policy" content="…"&gt;</c> tag. This is
/// deliberately separate from <see cref="Broiler.HtmlBridge.Scripting.ContentSecurityPolicy"/>,
/// which <b>parses and evaluates</b> the directive string — discovery answers "where is the policy in
/// this document", the policy answers
/// "what does it allow" (Phase 7 item 1: split parse / discovery / policy).
/// </summary>
/// <remarks>
/// Discovery is <c>Broiler.Dom.Html</c> parser output (Phase 7 item 2): the shared
/// <see cref="HtmlTokenizer"/> enumerates start tags, so — unlike the former regex scan — a
/// <c>&lt;meta&gt;</c> inside a comment or a <c>&lt;script&gt;</c>/<c>&lt;style&gt;</c> raw-text body is
/// correctly ignored, and a <c>&gt;</c> inside a quoted attribute value no longer truncates the tag.
/// </remarks>
internal static class CspMetaDiscovery
{
    /// <summary>
    /// Returns the directive string (the <c>content</c> value) of the first supported CSP
    /// <c>&lt;meta&gt;</c> tag in <paramref name="html"/>, or <c>null</c> when none is present.
    /// The returned string is unparsed — hand it to
    /// <see cref="Broiler.HtmlBridge.Scripting.ContentSecurityPolicy.Parse"/>.
    /// </summary>
    public static string? FindPolicyContent(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        foreach (var token in new HtmlTokenizer().Tokenize(html))
        {
            if (token.Type != TokenType.StartTag ||
                !string.Equals(token.Name, "meta", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!token.Attributes.TryGetValue("http-equiv", out var httpEquiv) ||
                !string.Equals(httpEquiv, "Content-Security-Policy", StringComparison.OrdinalIgnoreCase))
                continue;

            if (token.Attributes.TryGetValue("content", out var content) &&
                !string.IsNullOrWhiteSpace(content))
                return content;
        }

        return null;
    }
}
