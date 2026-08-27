using System;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// The one origin serialization/comparison implementation shared by CSP source matching, cross-origin
/// checks, <c>postMessage</c> target-origin delivery, and the <c>Location</c> <c>origin</c>/<c>host</c>
/// projections (Phase 7 item 4 — the "one URL resolution/origin implementation shared by script, CSS,
/// fetch, XHR and frames" exit criterion, origin half). Before this, the
/// <c>scheme://host[:port]</c> construction was copy-pasted in five places and the scheme+host+port
/// comparison in two; this collapses both to a single primitive.
/// </summary>
/// <remarks>
/// This helper is the origin <em>primitive</em> only. Each caller keeps its own surrounding policy —
/// which schemes/values inherit the embedding origin (<c>about:blank</c>, <c>data:</c>, <c>file:</c>),
/// how a null page URL is treated — because those nuances differ per site (a cross-origin check and a
/// CSP <c>'self'</c> match do not agree on <c>file:</c>). Only the shared serialization and the
/// scheme/host/port equality live here.
/// </remarks>
internal static class Origin
{
    /// <summary>
    /// The origin serialization <c>scheme://host[:port]</c> (the default port is omitted). This matches
    /// the URL Standard's ASCII origin serialization for the schemes Broiler models.
    /// </summary>
    public static string Of(Uri uri) =>
        $"{uri.Scheme}://{HostOf(uri)}";

    /// <summary>
    /// The <c>host[:port]</c> form used by <c>Location.host</c> (the default port is omitted).
    /// </summary>
    public static string HostOf(Uri uri) =>
        uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    /// <summary>
    /// Whether two URIs share scheme, host and port — the origin-equality primitive underlying both the
    /// cross-origin check and the CSP <c>'self'</c> match. Scheme/host compare case-insensitively.
    /// </summary>
    public static bool SchemeHostPortEquals(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port;
}
