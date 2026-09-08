using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge service the <see cref="FetchBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.11). Networking is otherwise self-contained — host I/O
/// goes through the injected <see cref="Broiler.HtmlBridge.Dom.Runtime.ResourceLoader"/> — so the
/// bridge couplings are the current page URL (the base for a relative <c>Response.redirect</c>
/// target), the realm the surface is installed into, and the three objects a fetch body is made of
/// that other modules own. Exposed as named members implemented explicitly on <see cref="DomBridge"/>
/// so the public surface is unchanged.
/// </summary>
/// <remarks>
/// The contract names no engine type: every JavaScript value it carries is a <see cref="JsValue"/>
/// and the realm is an <see cref="IJsRealm"/>. Two of the members it forwards to — the streams module
/// and the bridge's own wrapper lookup — are not migrated, so the conversions live on the bridge side
/// in <c>DomBridge.FetchHost.cs</c>, which is where a seam belongs while one half of it is still
/// engine-typed.
/// </remarks>
internal interface IFetchHost
{
    /// <summary>
    /// The realm the networking surface is installed into and mints its objects in.
    /// </summary>
    /// <remarks>
    /// Read through the host rather than captured at construction because the bridge builds this
    /// module in its constructor and adopts its realm only when a document is attached — the same
    /// reason <see cref="Broiler.HtmlBridge.Dom.Features.IAttributesHost.Realm"/> and
    /// <see cref="Broiler.HtmlBridge.Dom.Features.ITraversalHost.Realm"/> are host members.
    /// </remarks>
    IJsRealm Realm { get; }

    /// <summary>The document's current URL, used as the base for resolving relative redirect URLs.</summary>
    string PageUrl { get; }

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
