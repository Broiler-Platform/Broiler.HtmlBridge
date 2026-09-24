using Broiler.Dom;
using Broiler.JSeal;
using Broiler.Net.Cookies;
using Broiler.Net.Http;
using static Broiler.HtmlBridge.DomBridgeUtils;
using NetOrigin = Broiler.Net.Sites.Origin;

namespace Broiler.HtmlBridge;

/// <summary>
/// The bridge's network identity: the profile transport its loaders send through, and the
/// <see cref="DocumentRequestContext"/> of every document it hosts — the top document it was attached
/// to and each frame document it loaded — from which every request's <see cref="RequestContext"/> is
/// built.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from trusted state only.</b> The top document's context comes from the URL the host
/// attached the bridge with, through <see cref="DomBridgeSessionOptions.DocumentContextFactory"/>; a
/// frame's comes from its container document's context, the URL its response was actually served
/// from, and its container's <c>sandbox</c> attribute. Nothing here reads a value page script can
/// set on the document itself (<c>document.domain</c>, <c>location</c> of an about:blank document).
/// </para>
/// <para>
/// <b>Frames share the top realm but not its identity.</b> Script in every frame runs on the top
/// document's realm, so a frame's requests can only be attributed where the bridge knows which
/// document is asking: the frame's own scripts, module roots and sub-resources. The frame map below
/// is what makes that possible.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    private readonly Func<Uri, DocumentRequestContext>? _documentContextFactory;
    private readonly IDocumentCookieAccess? _injectedCookieAccess;
    private IDocumentCookieAccess? _privateCookieAccess;
    private DocumentRequestContext? _documentContext;
    private bool _documentContextAttached;

    // Frame document → its request context. Keyed by the severed content document the frame's
    // container links to (BrowsingContextManager), so ancestry is the container chain.
    private readonly Dictionary<DomDocument, DocumentRequestContext> _frameDocumentContexts = [];

    // Contexts whose document is sandboxed without allow-same-origin: their frames inherit the
    // sandboxed-origin flag (HTML: a nested browsing context's sandboxing flags include its
    // container document's), which an opaque origin alone cannot say — a data: document is opaque
    // without being sandboxed, and its frames keep their own origins.
    private readonly HashSet<DocumentRequestContext> _sandboxedDocumentContexts = new(ReferenceEqualityComparer.Instance);

    // Frame document → the Content-Security-Policies its scripts were checked against when it loaded,
    // for the module scripts it asks for later (a classic script's import()).
    private readonly Dictionary<DomDocument, Scripting.ContentSecurityPolicySet> _frameScriptPolicies = [];

    private static readonly Uri AboutBlank = new("about:blank");

    /// <summary>
    /// The profile transport every loader of this bridge sends through, or <see langword="null"/> when
    /// the bridge uses the cookie-less fallback clients.
    /// </summary>
    internal IBrowserRequestTransport? Network => _resources.Network;

    /// <summary>
    /// The request context of the top-level document this bridge is attached to. Before the first
    /// <c>Attach</c> it is the context of <c>about:blank</c>.
    /// </summary>
    internal DocumentRequestContext TopDocumentContext =>
        _documentContext ??= CreateTopDocumentContext(AboutBlank);

    /// <summary>
    /// The <c>document.cookie</c> access for this bridge's documents: the profile's
    /// (<see cref="DomBridgeSessionOptions.Cookies"/>), or a store private to this bridge.
    /// </summary>
    internal IDocumentCookieAccess CookieAccess =>
        _injectedCookieAccess ?? (_privateCookieAccess ??= new DocumentCookieAccess(new CookieStore()));

    /// <summary>
    /// Installs <c>document.cookie</c> on a document object: HTML's getter and setter over
    /// <see cref="CookieAccess"/>, for the document <paramref name="context"/> answers at the time of
    /// each access.
    /// </summary>
    /// <param name="document">The top document's object, or a frame document's.</param>
    /// <param name="context">
    /// The document's request context, or <see langword="null"/> once it no longer has one (a frame
    /// document that was replaced), which HTML treats as cookie-averse.
    /// </param>
    /// <remarks>
    /// <para>
    /// The access object is the only way through: it is the non-HTTP cookie API, so HttpOnly cookies
    /// are neither read nor overwritten, and a cookie-averse document (a URL that is not HTTP(S):
    /// <c>about:blank</c>, <c>data:</c>, <c>file:</c>) reads the empty string and writes nothing. It
    /// answers false for an opaque origin — a sandboxed frame without <c>allow-same-origin</c>, a
    /// <c>data:</c> frame — which is HTML's <c>SecurityError</c>.
    /// </para>
    /// <para>
    /// The assigned value is converted with the realm's own <c>ToString</c> before any of that, as
    /// Web IDL converts a <c>DOMString</c> argument, because <c>document.cookie = obj</c> is entitled to
    /// run the object's <c>toString</c>.
    /// </para>
    /// <para>
    /// <b>And only a script of the same origin as the document may use it.</b> HTML needs no such check:
    /// a script can only hold a Document of its own origin, since every route to another origin's goes
    /// through a WindowProxy that refuses it. Here every document shares one realm, so a node a page
    /// leaves on a global (<c>window.el = ...</c>) hands a cross-origin frame's script the page's
    /// Document through <c>el.ownerDocument</c>, and with it the page's cookies to read and to set. The
    /// running document (<see cref="CurrentScriptDocumentContext"/>) is therefore checked against this
    /// one, and anything cross-origin gets the <c>SecurityError</c> a cross-origin access gets. The
    /// shared realm still lets such a script read the page's DOM through the node; this closes the
    /// cookie jar, which is what the profile shares across every page of the site.
    /// </para>
    /// </remarks>
    private void DefineDocumentCookie(JsValue document, Func<DocumentRequestContext?> context)
    {
        Realm.DefineAccessor(
            document,
            "cookie",
            (in call) =>
            {
                if (context() is not { } documentContext)
                    return JsValue.String(string.Empty);

                if (!IsSameOriginAsCurrentScript(documentContext) ||
                    !CookieAccess.TryGetCookie(documentContext, out var cookie))
                    throw call.Realm.DomError("SecurityError", "Access to 'cookie' is denied for this document.");

                return JsValue.String(cookie);
            },
            (in call) =>
            {
                var value = call.Realm.ToJsString(call.Length > 0 ? call[0] : JsValue.Undefined);
                if (context() is { } documentContext &&
                    (!IsSameOriginAsCurrentScript(documentContext) || !CookieAccess.TrySetCookie(documentContext, value)))
                    throw call.Realm.DomError("SecurityError", "Access to 'cookie' is denied for this document.");

                return JsValue.Undefined;
            });
    }

    /// <summary>
    /// Whether the document whose script is running is <paramref name="document"/> itself or has the
    /// same origin (an opaque origin is the same only as itself).
    /// </summary>
    private bool IsSameOriginAsCurrentScript(DocumentRequestContext document)
    {
        var caller = CurrentScriptDocumentContext();
        return ReferenceEquals(caller, document) || caller.Origin.IsSameOrigin(document.Origin);
    }

    /// <summary>
    /// The request context of <paramref name="document"/>: the top document's, or a loaded frame
    /// document's. False for any other document (a <c>createHTMLDocument</c> document, a render
    /// projection), which has no browsing context of its own.
    /// </summary>
    internal bool TryGetDocumentContext(DomDocument document, out DocumentRequestContext context)
    {
        if (ReferenceEquals(document, _document))
        {
            context = TopDocumentContext;
            return true;
        }

        return _frameDocumentContexts.TryGetValue(document, out context!);
    }

    /// <summary>
    /// The request context of the document that owns <paramref name="node"/>, falling back to the
    /// top document's for a node in a document with no browsing context of its own.
    /// </summary>
    internal DocumentRequestContext DocumentContextFor(DomNode node) =>
        TryGetDocumentContext(GetOwningDocument(node), out var context) ? context : TopDocumentContext;

    /// <summary>
    /// The request context of the document whose script is running: a frame's document while that
    /// frame's scripts, timers, message handlers, module bodies or the jobs they queue (microtasks,
    /// promise reactions, <c>await</c> resumptions — see <see cref="Dom.Runtime.WindowJobPump"/>) run
    /// under its window context, the top document otherwise. The client of every <c>fetch()</c>,
    /// <c>XMLHttpRequest</c> and <c>sendBeacon</c>, and the initiator of a script navigation.
    /// </summary>
    internal DocumentRequestContext CurrentScriptDocumentContext() =>
        CurrentScriptFrame() is { } container ? FrameDocumentContext(container) : TopDocumentContext;

    /// <summary>
    /// The base URL of the same document as <see cref="CurrentScriptDocumentContext"/>: what a relative
    /// URL a script hands to <c>fetch()</c> or <c>sendBeacon</c> resolves against.
    /// </summary>
    private string CurrentScriptBaseUrl() =>
        CurrentScriptFrame() is { } container ? GetSubDocumentBaseUrl(container) : _pageUrl;

    /// <summary>
    /// The frame container whose window context is current, or <see langword="null"/> for the top
    /// document. Every document shares one realm, so the window-context switch is the only witness to
    /// which document's script is on the stack; nothing the page can assign is consulted.
    /// </summary>
    private DomElement? CurrentScriptFrame() =>
        _browsingContexts.CurrentWindowOverride is { IsObject: true } window &&
        _browsingContexts.TryGetSubWindowContainer(window, out var container)
            ? container
            : null;

    /// <summary>
    /// The frame document whose script is running, as the module scripts it asks for are requested for
    /// (see <see cref="CurrentScriptDocumentContext"/>), or <see langword="null"/> for the top document.
    /// </summary>
    private Scripting.ModuleClient? CurrentScriptModuleClient() =>
        CurrentScriptFrame() is { } container ? FrameModuleClient(container) : null;

    /// <summary>
    /// The document of the frame <paramref name="container"/> holds, as its module scripts are requested
    /// for: its request context, the policies its scripts were checked against when it loaded, and its
    /// base URL.
    /// </summary>
    /// <remarks>
    /// A frame document whose policies were never recorded (it had no script of its own to extract) is
    /// held to the page's, which is what it inherits when it has a local scheme and the stricter answer
    /// otherwise.
    /// </remarks>
    private Scripting.ModuleClient FrameModuleClient(DomElement container)
    {
        var policies = GetContentDocument(container) is { } frameDocument &&
                       _frameScriptPolicies.TryGetValue(frameDocument, out var recorded)
            ? recorded
            : new Scripting.ContentSecurityPolicySet(Csp);
        return new Scripting.ModuleClient(FrameDocumentContext(container), policies, GetSubDocumentBaseUrl(container));
    }

    /// <summary>
    /// Records the Content-Security-Policies a frame document's scripts are checked against: what its
    /// response delivered or it inherited, and what its own markup declares.
    /// </summary>
    private void SetFrameScriptPolicies(DomDocument frameDocument, Scripting.ContentSecurityPolicySet policies) =>
        _frameScriptPolicies[frameDocument] = policies;

    /// <summary>
    /// Makes a classic script's <c>import()</c> a request of the document whose script calls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every document shares one realm and one module context, and the engine gives an <c>import()</c>
    /// in a classic script no referrer: it resolves the specifier against whichever module root ran last
    /// and the module context fetched it as the top document's — with the page's cookies, as same-origin
    /// with the page, under the page's policy — when a frame's script, timer or promise reaction called
    /// it. The engine reaches the global <c>import</c> function for every such call, so it is replaced
    /// here by one that asks which document's script is running (the window context, as
    /// <see cref="CurrentScriptDocumentContext"/> does) and hands the call to the module context as that
    /// document's (<see cref="Scripting.BridgeModuleContext.ImportForClassicScript{T}"/>). A module's
    /// own <c>import()</c> uses the loader the engine injects into it, and its requester is its root's.
    /// </para>
    /// <para>
    /// Only a page with a module context has one to replace: without module roots the engine runs
    /// classic script on a plain context, whose <c>import()</c> has no loader at all.
    /// </para>
    /// </remarks>
    private void InstallClassicScriptImportAttribution(IJsRealm realm, Scripting.BridgeModuleContext? modules)
    {
        if (modules is null)
            return;

        var engineImport = realm.GetProperty(realm.Global, "import");
        if (!engineImport.IsFunction)
            return;

        // Not enumerable and not writable, as the engine's own is.
        realm.DefineValue(
            realm.Global,
            "import",
            realm.NewMethod(
                "import",
                (in JsCall call) =>
                {
                    var callRealm = call.Realm;
                    var arguments = call.Arguments.ToArray();
                    return modules.ImportForClassicScript(
                        CurrentScriptModuleClient(),
                        () => callRealm.Invoke(engineImport, JsValue.Undefined, arguments));
                },
                1),
            JsPropertyFlags.Configurable);
    }

    /// <summary>
    /// The script fetch context for <paramref name="document"/>, or <see langword="null"/> without a
    /// profile transport. It is bound to the current document lifetime, so the bridge's teardown
    /// cancels what it started.
    /// </summary>
    internal ScriptFetchContext? ScriptFetchFor(DocumentRequestContext document) =>
        _resources.Network is { } network ? new ScriptFetchContext(network, document, _resources.Lifetime) : null;

    /// <summary>
    /// Establishes the top document's context for an <c>Attach</c>, before anything is parsed or
    /// prefetched. A re-attach is a new document: what the previous one still had in flight is
    /// cancelled and its frame contexts are dropped.
    /// </summary>
    /// <param name="documentUrl">The attached URL, or <see langword="null"/> for none (about:blank).</param>
    /// <param name="modules">The context being attached, when the host built it as a module context.</param>
    private void BeginDocumentContext(Uri? documentUrl, Scripting.BridgeModuleContext? modules)
    {
        if (_documentContextAttached)
        {
            _resources.BeginDocument();
            _frameDocumentContexts.Clear();
            _sandboxedDocumentContexts.Clear();
            _frameScriptPolicies.Clear();
        }

        _documentContextAttached = true;
        _documentContext = CreateTopDocumentContext(documentUrl ?? AboutBlank);

        // A module context the host built for this document gets the document's identity and
        // lifetime now, before any module can be fetched: roots run, and dynamic imports happen,
        // only after Attach. The host's engine never needs the network itself.
        modules?.BindDocument(ScriptFetchFor(_documentContext));
    }

    private DocumentRequestContext CreateTopDocumentContext(Uri documentUrl) =>
        _documentContextFactory?.Invoke(documentUrl) ?? DocumentRequestContext.CreateTopLevel(documentUrl);

    /// <summary>
    /// A frame's request context, per HTML's rules for the document a nested navigation creates.
    /// </summary>
    /// <param name="container">The iframe, frame or object element.</param>
    /// <param name="documentUrl">
    /// The URL the frame's document came from: the response's final URL, a <c>data:</c> URL, a
    /// <c>file:</c> URL — or <see langword="null"/> for <c>srcdoc</c>, <c>about:blank</c> and a frame
    /// with nothing to load, which inherit their creator's URL and origin.
    /// </param>
    /// <param name="opaque">True when the load failed with a network error: the error document is opaque.</param>
    /// <remarks>
    /// A frame sandboxed without <c>allow-same-origin</c> — by its own attribute or its container
    /// document's — has a fresh opaque origin whatever it loads. A <c>data:</c> document always does.
    /// A <c>srcdoc</c> or <c>about:blank</c> document takes its container document's URL as its cookie
    /// URL and that document's origin; any other document takes its URL's origin.
    /// </remarks>
    private DocumentRequestContext CreateFrameDocumentContext(DomElement container, string? documentUrl, bool opaque = false)
    {
        var parent = DocumentContextFor(container);
        var sandboxed = _sandboxedDocumentContexts.Contains(parent) || IsSandboxedWithoutSameOrigin(container);

        DocumentRequestContext context;
        if (string.IsNullOrWhiteSpace(documentUrl) ||
            documentUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(documentUrl, UriKind.Absolute, out var url) && !IsDataUrl(documentUrl))
        {
            context = parent.CreateChild(parent.DocumentUrl, sandboxed ? NetOrigin.CreateOpaque() : parent.Origin);
        }
        else if (IsDataUrl(documentUrl))
        {
            // A data: URL longer than System.Uri accepts still makes an opaque, cookie-averse
            // document; the empty data: URL stands in for it.
            context = parent.CreateChild(
                Uri.TryCreate(documentUrl, UriKind.Absolute, out var dataUrl) ? dataUrl : new Uri("data:,"),
                NetOrigin.CreateOpaque());
        }
        else
        {
            context = parent.CreateChild(url!, sandboxed || opaque ? NetOrigin.CreateOpaque() : null);
        }

        if (sandboxed)
            _sandboxedDocumentContexts.Add(context);
        return context;

        static bool IsDataUrl(string value) => value.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Records the request context of a frame's newly built document.</summary>
    private void SetFrameDocumentContext(DomDocument frameDocument, DocumentRequestContext context) =>
        _frameDocumentContexts[frameDocument] = context;

    /// <summary>Forgets a frame document's context when the frame's document is replaced or dropped.</summary>
    private void RemoveFrameDocumentContext(DomDocument frameDocument)
    {
        if (_frameDocumentContexts.Remove(frameDocument, out var context))
            _sandboxedDocumentContexts.Remove(context);
        _frameScriptPolicies.Remove(frameDocument);
    }

    /// <summary>
    /// The frame document's own context for the frame <paramref name="container"/> holds, or the
    /// container document's when its document has not been loaded.
    /// </summary>
    internal DocumentRequestContext FrameDocumentContext(DomElement container) =>
        GetContentDocument(container) is { } frameDocument &&
        _frameDocumentContexts.TryGetValue(frameDocument, out var context)
            ? context
            : DocumentContextFor(container);

    /// <summary>
    /// The request that loads the document of the frame <paramref name="container"/> holds: a nested
    /// navigation contained by, and initiated by, the container's document (the src attribute).
    /// </summary>
    private RequestContext FrameNavigationRequest(DomElement container)
    {
        var parent = DocumentContextFor(container);
        var destination = container.TagName?.ToLowerInvariant() switch
        {
            "frame" => RequestDestination.Frame,
            "object" => RequestDestination.Object,
            "embed" => RequestDestination.Embed,
            _ => RequestDestination.IFrame,
        };
        return RequestContext.NestedNavigation(parent, parent, destination);
    }

    /// <summary>
    /// Whether an iframe's <c>sandbox</c> attribute is present without the <c>allow-same-origin</c>
    /// keyword, which gives the frame's document an opaque origin (HTML "sandboxed origin browsing
    /// context flag"). Tokens are ASCII-whitespace separated and compared ASCII case-insensitively.
    /// </summary>
    private static bool IsSandboxedWithoutSameOrigin(DomElement container)
    {
        if (!string.Equals(container.TagName, "iframe", StringComparison.OrdinalIgnoreCase) ||
            !TryGetAttribute(container, "sandbox", out var sandbox))
            return false;

        foreach (var token in sandbox.Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "allow-same-origin", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A linked stylesheet's request, as HTML "fetch a style resource" for a <c>&lt;link&gt;</c>
    /// makes it: destination <c>style</c> for the link's document, the mode and credentials its
    /// <c>crossorigin</c> attribute implies, and the <c>style-src</c> check it passed repeated for
    /// every redirect. The document's mode goes with it, for the check on the response's type.
    /// </summary>
    private Dom.Runtime.StyleSheetRequest LinkStyleSheetRequest(DomElement link)
    {
        var nonce = TryGetAttribute(link, "nonce", out var value) ? value : null;
        var context = RequestContext.Subresource(
                DocumentContextFor(link), RequestDestination.Style, CorsSettings.Parse(GetAttr(link, "crossorigin")))
            with { HopPolicy = (url, _) => IsStyleFetchAllowedByCsp(url.AbsoluteUri, nonce) };
        return new Dom.Runtime.StyleSheetRequest(context, IsQuirksModeFor(link));
    }

    /// <summary>
    /// An <c>@import</c>'s request: destination <c>style</c>, no-cors with credentials, for the
    /// importing document, with the same <c>style-src</c> check the import passed (none for an import
    /// the policy exempted because it preceded the policy's <c>&lt;meta&gt;</c>), and that document's mode.
    /// </summary>
    /// <param name="importer">
    /// A node of the importing document: a frame's, for an import in a frame's sheet. A node of a
    /// document with no browsing context of its own imports as the top document.
    /// </param>
    /// <param name="exemptFromCsp">Whether the policy exempted this import.</param>
    private Dom.Runtime.StyleSheetRequest ImportStyleSheetRequest(DomNode importer, bool exemptFromCsp) =>
        new(
            RequestContext.Subresource(DocumentContextFor(importer), RequestDestination.Style) with
            {
                HopPolicy = exemptFromCsp ? null : (url, _) => IsStyleFetchAllowedByCsp(url.AbsoluteUri, nonce: null),
            },
            IsQuirksModeFor(importer));

    /// <summary>
    /// Whether the document that owns <paramref name="node"/> is in quirks mode — what decides whether a
    /// stylesheet it requests may arrive as another type than <c>text/css</c>
    /// (<see cref="BridgeTransport.IsAcceptableStyleSheet"/>). A node of a document with no browsing
    /// context of its own answers for the top document, as <see cref="DocumentContextFor"/> does.
    /// </summary>
    /// <remarks>
    /// The mode is read from the document's doctype, with the predicate the parse applied to the page's
    /// markup (<c>DocumentModeContext.IsQuirksDoctype</c>, as <see cref="SelectsStandardsMode"/> uses
    /// it): a legacy doctype selects quirks mode, and so does none at all — except in an
    /// <c>iframe srcdoc</c> document, which the HTML parser never puts in quirks mode.
    /// </remarks>
    private bool IsQuirksModeFor(DomNode node)
    {
        var document = GetOwningDocument(node);
        if (!ReferenceEquals(document, _document) && !_frameDocumentContexts.ContainsKey(document))
            document = _document;

        if (document.DocumentType is { } doctype)
            return Layout.DocumentModeContext.IsQuirksDoctype(doctype.Name, doctype.PublicId, doctype.SystemId);

        return !(GetFrameForContentDocument(document) is { } frame &&
                 string.Equals(frame.TagName, "iframe", StringComparison.OrdinalIgnoreCase) &&
                 TryGetAttribute(frame, "srcdoc", out _));
    }
}
