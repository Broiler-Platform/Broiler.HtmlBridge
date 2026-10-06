using Broiler.Net.Http;
using Broiler.Net.Sites;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// HTML's secure contexts, which decide what a document's script may reach: <c>crypto.subtle</c>
/// among others, which Chromium offers only to a secure context.
/// </summary>
internal static class SecureContexts
{
    /// <summary>
    /// Whether a document is a secure context: it and every document containing it are at
    /// potentially trustworthy URLs, and none of them is a <c>data:</c> document.
    /// </summary>
    /// <remarks>
    /// A <c>data:</c> document has an opaque origin of its own, which is not potentially trustworthy,
    /// so Chromium makes it no secure context -- <c>isSecureContext</c> false, no <c>crypto.subtle</c>
    /// -- and nothing inside it either: a <c>srcdoc</c> frame within one, which carries its URL. A
    /// sandboxed document keeps its URL's answer although its origin is opaque too, and a worker a
    /// secure document starts from a <c>data:</c> URL is secure (<see cref="IsPotentiallyTrustworthy"/>).
    /// </remarks>
    internal static bool IsSecure(DocumentRequestContext document)
    {
        for (var current = document; current is not null; current = current.Parent)
        {
            if (!IsPotentiallyTrustworthy(current.DocumentUrl) ||
                string.Equals(current.DocumentUrl?.Scheme, "data", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Secure Contexts' "potentially trustworthy URL": <c>about:blank</c> and <c>about:srcdoc</c>,
    /// <c>data:</c>, <c>file:</c>, HTTPS, and HTTP to a loopback address or a localhost name
    /// (<see cref="HostNames.IsSecure"/>). A <c>blob:</c> URL is its creator's, which made it in script.
    /// </summary>
    internal static bool IsPotentiallyTrustworthy(Uri? url)
    {
        if (url is null)
            return true;

        return url.Scheme.ToLowerInvariant() switch
        {
            "about" or "data" or "file" or "blob" => true,
            "https" or "wss" => true,
            "http" or "ws" => HostNames.IsSecure(new UriBuilder(url) { Scheme = Uri.UriSchemeHttp }.Uri),
            _ => false,
        };
    }
}
