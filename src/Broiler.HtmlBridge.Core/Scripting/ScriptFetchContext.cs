using Broiler.Net.Http;
using System;
using System.Threading;

namespace Broiler.HtmlBridge;

/// <summary>
/// The profile network a document's scripts are fetched through, and the document they are
/// fetched for: what <see cref="ScriptExtractionService.ExtractAll(string, string?, Scripting.ContentSecurityPolicy?, ScriptFetchContext?)"/>
/// sends each external classic script and module root through, and what the bridge's module and
/// script-insertion loaders use for the document that owns them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Profile-level transport, document-level context.</b> <see cref="Transport"/> is the one network
/// session the host's profile owns (Broiler.Net's <c>BrowserNetworkSession</c>): it holds the cookie
/// store, follows redirects with a cookie decision per hop, and enforces CORS and credentials modes.
/// <see cref="Document"/> is the trusted identity of the document making the request: the URL whose
/// cookies it uses, its origin, and its parent chain for frames. Neither ever comes from page script.
/// </para>
/// <para>
/// <b>Per-script request shape.</b> The context carries no destination, mode or credentials: those
/// are decided per script from its type and its <c>crossorigin</c> attribute. A classic script is a
/// no-cors request with credentials included unless the attribute asks for CORS; a module script and
/// every module it imports is a CORS request whose credentials are <c>same-origin</c>, or
/// <c>include</c> for <c>crossorigin="use-credentials"</c> (HTML "fetch a single module script").
/// </para>
/// <para>
/// <b>Cancellation.</b> <see cref="CancellationToken"/> is the document's lifetime: a host cancels it
/// when the navigation that loads the document is stopped or replaced. Each fetch still keeps its own
/// 30-second budget.
/// </para>
/// </remarks>
public sealed class ScriptFetchContext
{
    /// <param name="transport">The profile's request transport.</param>
    /// <param name="document">The document the scripts belong to.</param>
    /// <param name="cancellationToken">Cancels every fetch made for the document.</param>
    public ScriptFetchContext(
        IBrowserRequestTransport transport,
        DocumentRequestContext document,
        CancellationToken cancellationToken = default)
    {
        Transport = transport ?? throw new ArgumentNullException(nameof(transport));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        CancellationToken = cancellationToken;
    }

    /// <summary>The profile's request transport.</summary>
    public IBrowserRequestTransport Transport { get; }

    /// <summary>The document the scripts are fetched for (the request's client).</summary>
    public DocumentRequestContext Document { get; }

    /// <summary>The document's lifetime; cancelling it abandons every fetch still in flight.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// The request for one script of this document: destination <c>script</c>, with the mode and
    /// credentials HTML derives from the element's <c>crossorigin</c> value and, for a module, the
    /// CORS default a missing attribute means for modules. <paramref name="hopPolicy"/> is the
    /// Content-Security-Policy check the caller applied to the URL, repeated for every redirect.
    /// </summary>
    internal ScriptRequest ForScript(bool isModule, string? crossOrigin, Func<Uri, int, bool>? hopPolicy) =>
        new(this, BridgeTransport.ScriptRequestContext(Document, isModule, crossOrigin, hopPolicy));

    /// <summary>The same context for another document of the same profile and lifetime.</summary>
    internal ScriptFetchContext WithDocument(DocumentRequestContext document) =>
        ReferenceEquals(document, Document) ? this : new(Transport, document, CancellationToken);
}

/// <summary>One script fetch: the document's fetch context and the request context for this script.</summary>
internal readonly record struct ScriptRequest(ScriptFetchContext Fetch, RequestContext Context)
{
    /// <summary>
    /// Whether a prefetch issued as <paramref name="issued"/> may serve a fetch requested as
    /// <paramref name="requested"/>: both through no transport, or both through the same transport
    /// with the same request shape.
    /// </summary>
    internal static bool Same(ScriptRequest? issued, ScriptRequest? requested) =>
        (issued, requested) switch
        {
            (null, null) => true,
            ({ } a, { } b) => ReferenceEquals(a.Fetch.Transport, b.Fetch.Transport) &&
                              BridgeTransport.SameRequestShape(a.Context, b.Context),
            _ => false,
        };
}
