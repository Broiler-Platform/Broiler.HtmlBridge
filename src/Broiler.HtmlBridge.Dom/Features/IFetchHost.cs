namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge service the <see cref="FetchBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.11). Networking is otherwise self-contained — host I/O
/// goes through the injected <see cref="Broiler.HtmlBridge.Dom.Runtime.ResourceLoader"/> — so the only
/// bridge coupling is the current page URL, used as the base when resolving a relative
/// <c>Response.redirect</c> target. Exposed as a named member implemented explicitly on
/// <see cref="DomBridge"/> so the public surface is unchanged.
/// </summary>
internal interface IFetchHost
{
    /// <summary>The document's current URL, used as the base for resolving relative redirect URLs.</summary>
    string PageUrl { get; }

    /// <summary>
    /// A real <c>Blob</c> over <paramref name="bytes"/>, for <c>response.blob()</c>. The interface
    /// belongs to <c>BlobBinding</c>, not here — this seam exists so the fetch path hands back the
    /// same object a page's own <c>new Blob(...)</c> produces rather than a look-alike.
    /// </summary>
    Broiler.JavaScript.Runtime.JSValue CreateBlob(byte[] bytes, string contentType);

    /// <summary>
    /// The entry list of a <c>&lt;form&gt;</c> wrapper (HTML §4.10.21.4), or <see langword="null"/>
    /// when the object is not one. <c>new FormData(form)</c> is the shape a page collects a form's
    /// values with, and enumerating the wrapper's own properties — which is what it did — produced
    /// the element's members rather than the form's fields.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string>>? FormEntriesFor(Broiler.JavaScript.Runtime.JSObject candidate);

    /// <summary>
    /// A real <c>ReadableStream</c> over a body's text, for <c>response.body</c> and
    /// <c>request.body</c>. The interface belongs to the streams asset, not here — this seam exists
    /// so a fetch body is the same object <c>blob.stream()</c> and a page's own
    /// <c>new ReadableStream</c> produce, rather than a look-alike.
    /// </summary>
    Broiler.JavaScript.Runtime.JSValue StreamOverText(string text);

    /// <summary>
    /// The same stream, reporting the first read or cancel through <paramref name="onDisturbed"/> —
    /// the Body mixin's <c>bodyUsed</c>, which is what makes <c>text()</c>, <c>json()</c> and
    /// <c>clone()</c> refuse a body something has already consumed.
    /// </summary>
    Broiler.JavaScript.Runtime.JSValue StreamOverTextObserved(string text, System.Action onDisturbed);

    /// <summary>Whether a reader holds the given body stream — the Body mixin's "locked" half.</summary>
    bool IsStreamLocked(Broiler.JavaScript.Runtime.JSValue stream);
}
