using Broiler.Layout;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge;

/// <summary>
/// Dependencies owned by one <see cref="DomBridge"/> session.
/// </summary>
/// <remarks>
/// <para>
/// <b>One options object serves every document of a navigation.</b> A host builds one
/// <see cref="DomBridgeFactory"/> per navigation (or per profile) and every hop's bridge is created
/// from it, so the members here are profile-level services, never one document's state. A bridge
/// derives its own document identity from the URL it is attached with, through
/// <see cref="DocumentContextFactory"/>.
/// </para>
/// <para>
/// <b>Without a network</b> the bridge's loaders fall back to process-wide clients that send and keep
/// no cookies, so no document's cookies leak into another's requests.
/// </para>
/// </remarks>
public sealed class DomBridgeSessionOptions
{
    /// <summary>
    /// Creates the document-scoped layout view lazily. The bridge disposes the
    /// resulting view with the session.
    /// </summary>
    public Func<ILayoutView>? LayoutViewFactory { get; init; }

    /// <summary>
    /// The profile's request transport (Broiler.Net's <c>BrowserNetworkSession</c>): every sub-resource
    /// the bridge loads — external and inserted scripts, module imports, linked and imported
    /// stylesheets, frame documents and <c>fetch()</c> — is sent through it, with the requesting
    /// document's <see cref="DocumentRequestContext"/>. The transport sends and stores the profile's
    /// cookies per hop and enforces CORS and credentials modes. The bridge never disposes it.
    /// </summary>
    public IBrowserRequestTransport? Network { get; init; }

    /// <summary>
    /// The profile's <c>document.cookie</c> access, for the bridge's documents and frames. A
    /// <c>BrowserNetworkSession</c> implements it over the same store as <see cref="Network"/>. When
    /// null, each bridge keeps a private in-memory store of its own.
    /// </summary>
    public IDocumentCookieAccess? Cookies { get; init; }

    /// <summary>
    /// Builds the request context of the top-level document a bridge is attached to, from the URL
    /// passed to <c>Attach</c> (<c>about:blank</c> when the bridge is attached without a usable URL).
    /// A host whose document is not an ordinary top-level one — a sandboxed page, a document with an
    /// explicit origin — supplies it; the default is <see cref="DocumentRequestContext.CreateTopLevel"/>.
    /// Frames the document embeds are derived from the result.
    /// </summary>
    public Func<Uri, DocumentRequestContext>? DocumentContextFactory { get; init; }
}
