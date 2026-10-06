using Broiler.JSeal;
using Broiler.Net.Http;
using Broiler.Net.Sites;
using NetOrigin = Broiler.Net.Sites.Origin;

namespace Broiler.HtmlBridge;

/// <summary>
/// Which Web Storage areas a document of the page uses (HTML §12.2.3): the page's own pair, the pair
/// of the storage key a frame's document has, or none at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every document here shares one global object, so the areas cannot simply sit on it.</b> A frame's
/// script that reads a bare <c>localStorage</c> reads the global's, as it reads its <c>location</c> and
/// <c>name</c> there; and the page's two areas were mirrored onto every frame's window besides. So every
/// frame used the page's areas, whatever its origin: reCAPTCHA's anchor frame, on www.google.com, kept
/// its <c>rc::</c> keys in the area of whichever page embedded it, and could read everything that page
/// had stored. The global's two members are therefore accessors that answer the areas of the document
/// whose script is running (<see cref="CurrentScriptDocumentContext"/>), a frame's window answers the
/// areas of the document it shows, and the view of the top window a frame holds answers the page's.
/// </para>
/// <para>
/// <b>Which areas, as Chromium partitions them.</b> A document's storage key is its origin, the top-level
/// site, and whether the document or any frame between it and the page is cross-site to that site. A
/// document whose key is the page's -- the page, a frame of the page's origin with no cross-site frame
/// above it, an about:blank or srcdoc frame that inherited the page's origin, a sandboxed frame allowed
/// its origin -- uses the page's areas. Any other key has a pair of its own, which every document of the
/// page with that key shares: two frames of one foreign origin see each other's items and none of the
/// page's, and a frame of the page's own origin inside a cross-site frame sees neither. Measured in
/// Chromium, with frames of each kind on a page at <c>http://127.0.0.1</c> and frames from
/// <c>http://localhost</c>.
/// </para>
/// <para>
/// <b>An opaque origin has no storage.</b> A frame sandboxed without <c>allow-same-origin</c>, a
/// <c>data:</c> frame and the documents such frames contain read either area as a <c>SecurityError</c>,
/// with Chromium's message for the reason. Two kinds of opaque document keep their storage: the page
/// itself, whatever its URL -- a page attached with none is about:blank to the network, and its areas
/// have always worked -- and any frame that inherited the page's origin. Every <c>file:</c> document
/// shares one key, as Chromium keys them all as <c>file://</c>.
/// </para>
/// <para>
/// <b>The areas live as long as the page.</b> They are built with the window, as the page's always were,
/// so nothing carries over to the next page, where Chromium keeps both kinds for the origin and the tab.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The page's own two areas, built with the window.
    private StorageAreas _pageStorage;

    // Every other storage key a script of this page has reached, with its two areas.
    private readonly Dictionary<StorageKey, StorageAreas> _keyedStorage = [];

    // What every file: document's storage is keyed by in place of its own opaque origin.
    private static readonly object FileStorageOrigin = new();

    private readonly record struct StorageAreas(JsValue Local, JsValue Session);

    /// <summary>
    /// A storage key other than the page's: the document's storage origin, and whether the document is
    /// partitioned as a third party because it, or a frame above it, is cross-site to the page.
    /// </summary>
    private readonly record struct StorageKey(object Origin, bool CrossSite);

    /// <summary>
    /// Builds the page's two areas and installs the global's <c>localStorage</c> and
    /// <c>sessionStorage</c>: accessors without setters, as Chromium's are, that answer the areas of the
    /// document whose script is running.
    /// </summary>
    private void InstallWebStorage(JsValue window)
    {
        var realm = Realm;
        _pageStorage = NewStorageAreas(realm);
        _keyedStorage.Clear();

        realm.DefineAccessor(window, "localStorage",
            (in call) => StorageOf(CurrentScriptDocumentContext(), session: false, call.Realm), null);
        realm.DefineAccessor(window, "sessionStorage",
            (in call) => StorageOf(CurrentScriptDocumentContext(), session: true, call.Realm), null);
    }

    /// <summary>The page's own <c>localStorage</c> or <c>sessionStorage</c>.</summary>
    internal JsValue PageStorage(bool session) => session ? _pageStorage.Session : _pageStorage.Local;

    /// <summary>
    /// The <c>localStorage</c> or <c>sessionStorage</c> of <paramref name="document"/>; throws the
    /// <c>SecurityError</c> a document with an opaque origin gets for reading either.
    /// </summary>
    internal JsValue StorageOf(DocumentRequestContext document, bool session, IJsRealm realm)
    {
        var areas = StorageAreasOf(document, session, realm);
        return session ? areas.Session : areas.Local;
    }

    private StorageAreas StorageAreasOf(DocumentRequestContext document, bool session, IJsRealm realm)
    {
        var page = TopDocumentContext;
        if (ReferenceEquals(document, page))
            return _pageStorage;

        var origin = StorageOriginOf(document);
        var pageOrigin = StorageOriginOf(page);
        var crossSite = IsCrossSiteToPage(document, page, pageOrigin);
        if (!crossSite && Equals(origin, pageOrigin))
            return _pageStorage;

        if (origin is NetOrigin { IsOpaque: true })
            throw realm.DomError("SecurityError", StorageDenial(document, session));

        var key = new StorageKey(origin, crossSite);
        if (!_keyedStorage.TryGetValue(key, out var areas))
        {
            areas = NewStorageAreas(Realm);
            _keyedStorage[key] = areas;
        }

        return areas;
    }

    private static StorageAreas NewStorageAreas(IJsRealm realm) =>
        new(Dom.Features.WebStorageBinding.BuildStorage(realm), Dom.Features.WebStorageBinding.BuildStorage(realm));

    /// <summary>
    /// The origin a document's storage is keyed by: its own, except that every <c>file:</c> document
    /// that is not sandboxed shares one, where the network gives each a fresh opaque origin.
    /// </summary>
    private object StorageOriginOf(DocumentRequestContext document) =>
        document.Origin.IsOpaque &&
        string.Equals(document.DocumentUrl.Scheme, "file", StringComparison.OrdinalIgnoreCase) &&
        !_sandboxedDocumentContexts.Contains(document)
            ? FileStorageOrigin
            : document.Origin;

    /// <summary>
    /// Whether <paramref name="document"/>, or any frame document between it and the page, is cross-site
    /// to the page -- what partitions a document's storage as a third party's (Chromium's "ancestor chain
    /// bit"). A frame of the page's own origin inside a frame of another site is such a document.
    /// </summary>
    private bool IsCrossSiteToPage(DocumentRequestContext document, DocumentRequestContext page, object pageOrigin)
    {
        for (var context = document; context is not null && !ReferenceEquals(context, page); context = context.Parent)
        {
            if (IsCrossSite(StorageOriginOf(context), pageOrigin))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether two storage origins are of different sites: tuple origins by their schemeful sites, and
    /// anything else -- an opaque origin, the shared <c>file:</c> origin -- by identity.
    /// </summary>
    private static bool IsCrossSite(object origin, object pageOrigin) =>
        origin is NetOrigin { IsOpaque: false } tuple && pageOrigin is NetOrigin { IsOpaque: false } pageTuple
            ? SiteMatching.GetSite(SiteResolver.Default, tuple) != SiteMatching.GetSite(SiteResolver.Default, pageTuple)
            : !Equals(origin, pageOrigin);

    /// <summary>Chromium's message for reading an area from a document with an opaque origin, by why it is opaque.</summary>
    private string StorageDenial(DocumentRequestContext document, bool session)
    {
        var reason = _sandboxedDocumentContexts.Contains(document)
            ? "The document is sandboxed and lacks the 'allow-same-origin' flag."
            : IsDataUrlDocument(document)
                ? "Storage is disabled inside 'data:' URLs."
                : "Access is denied for this document.";
        return $"Failed to read the '{(session ? "sessionStorage" : "localStorage")}' property from 'Window': {reason}";
    }

    /// <summary>
    /// Whether the document's own URL is a <c>data:</c> URL. A srcdoc or about:blank frame inside a
    /// <c>data:</c> frame carries that frame's URL too, as its cookie URL, but it inherited the frame's
    /// origin where a <c>data:</c> document has one of its own -- and Chromium tells the two apart.
    /// </summary>
    private static bool IsDataUrlDocument(DocumentRequestContext document) =>
        string.Equals(document.DocumentUrl.Scheme, "data", StringComparison.OrdinalIgnoreCase) &&
        !(document.Parent is { } parent && ReferenceEquals(parent.Origin, document.Origin));
}
