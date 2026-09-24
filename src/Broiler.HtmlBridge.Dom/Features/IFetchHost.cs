using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge service the <see cref="FetchBinding"/> feature module needs.
/// Networking is otherwise self-contained — host I/O
/// goes through the injected <see cref="Broiler.HtmlBridge.Dom.Runtime.ResourceLoader"/> — so the
/// bridge couplings are the current page URL (the base for a relative <c>Response.redirect</c>
/// target), the realm the surface is installed into, and the three objects a fetch body is made of
/// that other modules own. Exposed as named members implemented explicitly on <see cref="DomBridge"/>
/// so the public surface is unchanged.
/// </summary>
/// <remarks>
/// The contract names no engine type: every JavaScript value it carries is a <see cref="JsValue"/>
/// and the realm is an <see cref="IJsRealm"/>. No member of the bridge side, <c>DomBridge/Hosts.Window.cs</c>,
/// converts: the streams module is typed in <see cref="JsValue"/> wherever it carries a JavaScript value,
/// and the wrapper-to-node lookup takes a handle.
/// <para>
/// The realm is read through the host rather than captured at construction because the bridge builds
/// this module in its constructor and adopts its realm only when a document is attached.
/// </para>
/// </remarks>
internal interface IFetchHost : IPageUrlHost, IRealmHost
{
    /// <summary>
    /// The document a script's <c>fetch()</c>, <c>XMLHttpRequest</c> or <c>sendBeacon</c> is sent
    /// for: the request's client, whose origin, site and cookies the transport uses. It is the frame
    /// document whose script is running when the bridge can tell — a frame's scripts, timers and
    /// message handlers run under that frame's window context — and the top document otherwise.
    /// </summary>
    /// <remarks>
    /// Read from trusted bridge state at the moment of the call, never from anything the page can
    /// set: every document shares one realm, so the window-context switch is the only witness to
    /// which document is asking. A promise reaction a frame queued runs outside that switch and is
    /// attributed to the top document.
    /// </remarks>
    Broiler.Net.Http.DocumentRequestContext FetchClient { get; }

    /// <summary>
    /// The base URL a relative request URL resolves against, for the same document as
    /// <see cref="FetchClient"/>: the frame's base URL for a frame's script, the page URL otherwise.
    /// </summary>
    string FetchBaseUrl { get; }

    /// <summary>
    /// The bytes and type of a <c>Blob</c>, or <see langword="null"/> when <paramref name="candidate"/>
    /// is not one — what a <c>sendBeacon</c> body and its <c>Content-Type</c> are extracted from.
    /// </summary>
    (byte[] Bytes, string Type)? BlobContentOf(JsValue candidate);

    /// <summary>
    /// A real <c>Blob</c> over <paramref name="bytes"/>, for <c>response.blob()</c>. The interface
    /// belongs to <c>BlobBinding</c>, not here — this seam exists so the fetch path hands back the
    /// same object a page's own <c>new Blob(...)</c> produces rather than a look-alike.
    /// </summary>
    JsValue CreateBlob(byte[] bytes, string contentType);

    /// <summary>
    /// The entry list of a <c>&lt;form&gt;</c> wrapper (HTML §4.10.21.4), or <see langword="null"/>
    /// when the object is not one. <c>new FormData(form)</c> is the shape a page collects a form's
    /// values with, and enumerating the wrapper's own properties — which is what it did — produced
    /// the element's members rather than the form's fields.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string>>? FormEntriesFor(JsValue candidate);

    /// <summary>
    /// A real <c>ReadableStream</c> over a body's text, for <c>response.body</c> and
    /// <c>request.body</c>. The interface belongs to the streams asset, not here — this seam exists
    /// so a fetch body is the same object <c>blob.stream()</c> and a page's own
    /// <c>new ReadableStream</c> produce, rather than a look-alike.
    /// </summary>
    JsValue StreamOverText(string text);

    /// <summary>
    /// The same stream, reporting the first read or cancel through <paramref name="onDisturbed"/> —
    /// the Body mixin's <c>bodyUsed</c>, which is what makes <c>text()</c>, <c>json()</c> and
    /// <c>clone()</c> refuse a body something has already consumed.
    /// </summary>
    JsValue StreamOverTextObserved(string text, Action onDisturbed);

    /// <summary>Whether a reader holds the given body stream — the Body mixin's "locked" half.</summary>
    bool IsStreamLocked(JsValue stream);
}
