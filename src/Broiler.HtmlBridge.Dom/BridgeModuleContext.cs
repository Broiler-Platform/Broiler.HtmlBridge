using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Broiler.JavaScript.Modules;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// A <see cref="JSModuleContext"/> whose module resolution/fetch seams (patch 0008) are wired to the
/// browser host: import specifiers resolve as URLs against the importing module's URL (not filesystem
/// paths), and module sources are read through the bridge's <see cref="ScriptExtractionService"/> fetch
/// (<c>file://</c>/<c>http(s)</c>/<c>data:</c>) under the page's content-security policy. Attaching a
/// <c>DomBridge</c> to this context installs the DOM globals on the same realm the modules execute in, so
/// a module can touch <c>document</c>/<c>window</c> exactly like a classic script.
///
/// This is the engine-driven module path. It is used only when the underlying engine actually binds static
/// imports (see <see cref="EngineModuleSupport"/>); otherwise a page's modules are left unrun. It is the sole
/// module path.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type stays engine-typed.</b> The other bridge units name engine types to build values; this one <em>is</em> an
/// engine type — the module graph's resolve/fetch hooks are <see langword="protected"/> overrides, so
/// the coupling is the base class rather than a call. JSEAL still declares only <em>that</em> an
/// engine binds modules (<c>JsCapabilities.Modules</c>, <c>DynamicImport</c>) and how host script,
/// classic script and dynamic source are run (<c>IJsSource</c>), neither of which offers a specifier
/// resolver or a source fetcher.
/// </para>
/// <para>
/// <b>The contract that would close it, written down so the next attempt does not have to rediscover
/// it.</b> Resolution and fetching are the <em>host's</em> — that is not a preference, it is what
/// Broiler.VM's embedding contract states outright and what <c>VmModuleMap</c> already implements:
/// the host owns identity resolution, transport, content policy and the module map, and the core
/// never fetches anything. So the contract is a pair. A host-implemented
/// <c>IJsModuleLoader</c> — <c>string? Resolve(string specifier, string? referrer)</c> and
/// <c>string Load(string key)</c> — is what this class's two overrides would become, unchanged in
/// behaviour, and it names no engine type. A provider-implemented <c>IJsModules</c> —
/// <c>JsValue EvaluateModule(string source, string key, string? baseUrl)</c> — is what
/// <c>RunScriptAsync</c> would become, and it is the member <c>JsealConformanceTests</c> records as
/// missing when it lists <c>Modules</c> and <c>DynamicImport</c> as inexpressible. Both implementers
/// exist: Broiler.JS answers by handing the loader to a <c>JSModuleContext</c>'s two overrides, and
/// the VM profile answers by handing it to <c>VmModuleMap</c>, which is already that shape.
/// </para>
/// <para>
/// <b>Two things block landing it, and neither is in this file.</b> First,
/// <c>Broiler.HtmlBridge.Scripting/ScriptEngine.cs</c> constructs this type directly and then uses it
/// as a <c>JSContext</c> (<c>using JSContext context = moduleContext ?? new JSContext();</c>) and as
/// the thing it calls <c>RunScriptAsync</c> on — so the type cannot stop being a <c>JSModuleContext</c>
/// until that file changes with it. Second, the implementation belongs in
/// <c>Broiler.JSeal.BroilerJs</c>, and <c>JSModuleContext</c> lives in
/// <c>Broiler.JavaScript.Modules</c>, which that project deliberately does not reference. Adding the
/// reference wants an argument in its own diff, not a side effect of a refactor.
/// </para>
/// <para>
/// Nothing needs to change for the rest of the migration to proceed around it: this derives from the
/// engine's context type, so the provider's <c>IJsRealmAdoption.TryAdopt</c> already accepts an
/// instance of it, and a page whose modules run in one gets the same JSEAL realm as a page whose
/// scripts do not.
/// </para>
/// </remarks>
internal sealed class BridgeModuleContext : JSModuleContext
{
    private readonly ContentSecurityPolicy? _csp;
    private readonly string? _pageUrl;

    // What each root key presents as (import.meta.url), where that is not the key itself: the page's
    // roots from the constructor, and the roots of every frame document registered since.
    private readonly ConcurrentDictionary<string, string> _rootUrls = new(StringComparer.Ordinal);

    // Numbers the frame documents whose roots are registered, for keys and bases of their own.
    private int _documentSerial;

    // A frame root's text, by its document-unique key, until the loader reads it (ReadModuleSourceAsync):
    // it was fetched, and authorised against the frame's policy, when the frame's scripts were extracted.
    private readonly ConcurrentDictionary<string, string> _rootSources = new(StringComparer.Ordinal);

    // The base a frame root's own imports resolve against, by its key (GetModuleDirectory): the root's
    // document URL -- or its own URL, for an external root -- with the document's fragment.
    private readonly ConcurrentDictionary<string, string> _rootBases = new(StringComparer.Ordinal);

    // The document's network: the profile transport, the top document's request context and its
    // lifetime. Null until a bridge attaches to this context (and for good without a transport), in
    // which case modules are fetched through the cookie-less fallback client.
    private volatile ScriptFetchContext? _fetch;

    // Who asked for each module: keyed by module key, and by a root's base URL (the directory its
    // imports resolve from), valued with the requesting document and the root's crossorigin setting.
    // A module inherits its importer's entry the first time it is resolved, which is HTML's
    // "descendant module scripts use the root's fetch options". The resolver hooks run on the module
    // loader's pool thread, hence concurrent.
    private readonly ConcurrentDictionary<string, ModuleRequester> _requesters = new(StringComparer.Ordinal);

    // The classic script import() running on this thread, while its specifier is being resolved; see
    // ImportForClassicScript. Per thread because the resolver hooks of module graphs already loading
    // run on the loader's pool threads at the same time, and must not see it.
    [ThreadStatic]
    private static ClassicImport? _classicImport;

    /// <summary>
    /// The document a module is fetched for (null: the attached top document, with the page's policy)
    /// and its root's <c>crossorigin</c>.
    /// </summary>
    private readonly record struct ModuleRequester(ModuleClient? Client, string? CrossOrigin);

    /// <summary>A classic script's <c>import()</c> in progress on this thread, for this context.</summary>
    private sealed class ClassicImport(BridgeModuleContext owner, ModuleClient client)
    {
        public BridgeModuleContext Owner { get; } = owner;

        public ModuleClient Client { get; } = client;

        /// <summary>Set once the call's own specifier has been resolved; nested resolutions are the module's.</summary>
        public bool Resolved { get; set; }
    }

    public BridgeModuleContext(
        ContentSecurityPolicy? csp = null,
        string? pageUrl = null,
        IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot>? roots = null)
        : base(new SynchronizationContext())
    {
        _csp = csp;
        _pageUrl = pageUrl;
        if (RootUrls(roots) is { } urls)
        {
            foreach (var (key, url) in urls)
                _rootUrls[key] = url;
        }
        RegisterRoots(roots, client: null);
    }

    /// <summary>
    /// Gives this context the network of the document a bridge attached to it: modules are then
    /// fetched through <paramref name="fetch"/>'s transport, for its document, cancelled with its
    /// lifetime. <see langword="null"/> keeps the cookie-less fallback client.
    /// </summary>
    internal void BindDocument(ScriptFetchContext? fetch) => _fetch = fetch;

    /// <summary>
    /// Records module roots about to run for <paramref name="client"/>, so the modules they import
    /// are requested as that document's, with each root's <c>crossorigin</c> setting and under that
    /// document's policy. <see langword="null"/> means the attached top document. The first
    /// registration of a key wins: the module map is shared, so a module two documents import is
    /// fetched once, for whichever asked first.
    /// </summary>
    internal void RegisterRoots(IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot>? roots, ModuleClient? client)
    {
        if (roots is null)
            return;

        foreach (var root in roots)
            Register(root, client);
    }

    /// <summary>
    /// Records a frame document's module roots (they run on the top document's module context) and
    /// answers them as they must be run (<see cref="StartDocumentRoot"/>): each with a key and a base
    /// URL of this document's own, so that neither the page's roots nor another frame's can already
    /// hold them, and with its text kept for the loader to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the roots are renamed.</b> Who asked for a module is found by the importer's key or
    /// base, and a root's are shared. An inline root's key is its position in its own document
    /// (<c>inline:1</c>) and its base is its document's URL -- which for a <c>srcdoc</c> frame is its
    /// embedder's. The page's own inline root registered that URL first, so a sandboxed
    /// <c>srcdoc</c> frame's imports were requested as the page: with the page's cookies and as
    /// same-origin, where the frame's opaque origin allows neither. An external root's key and base
    /// are its URL, which the page, or another frame, may already have loaded as a module of its own --
    /// and a root started by key (<see cref="StartDocumentRoot"/>) would then be answered with that
    /// already-evaluated module instead of running.
    /// </para>
    /// <para>
    /// The base gets a fragment naming the document. A fragment takes no part in resolving a relative
    /// URL, so the root's imports resolve exactly as before; <c>import.meta.url</c> stays the
    /// document's URL, or the root's own (<see cref="GetModuleUrl"/>).
    /// </para>
    /// </remarks>
    /// <param name="roots">The frame document's module roots, in document order.</param>
    /// <param name="document">The frame document they are requested for.</param>
    /// <param name="fallbackBaseUrl">The frame document's base URL, for a root that carries none.</param>
    internal IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot> RegisterDocumentRoots(
        IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot> roots,
        ModuleClient document,
        string? fallbackBaseUrl = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(document);

        var tag = $"broiler-document-{Interlocked.Increment(ref _documentSerial)}";
        var registered = new List<Broiler.HtmlBridge.Scripting.ModuleRoot>(roots.Count);
        foreach (var root in roots)
        {
            var baseUrl = string.IsNullOrEmpty(root.BaseUrl) ? fallbackBaseUrl : root.BaseUrl;
            var own = root with
            {
                Key = $"{root.Key}#{tag}",
                BaseUrl = string.IsNullOrEmpty(baseUrl) ? baseUrl : WithFragment(baseUrl!, tag),
            };

            // What the root presents as: its document's URL for an inline root, its own URL otherwise.
            _rootUrls[own.Key] =
                !string.IsNullOrEmpty(root.BaseUrl) && !string.Equals(root.Key, root.BaseUrl, StringComparison.Ordinal)
                    ? root.BaseUrl!
                    : root.Key;
            if (!string.IsNullOrEmpty(own.BaseUrl))
                _rootBases[own.Key] = own.BaseUrl!;
            _rootSources[own.Key] = root.Source ?? string.Empty;

            Register(own, document);
            registered.Add(own);
        }

        return registered;
    }

    /// <summary>
    /// Starts a frame's module root, registered by <see cref="RegisterDocumentRoots"/>, on the calling
    /// thread: the engine loads its graph, links it and evaluates it as it would an <c>import()</c>, and
    /// every job that takes -- the evaluation itself, each resumption of a top-level <c>await</c>, each
    /// reaction -- is posted to the job queue current on this thread. The task completes when the root
    /// has evaluated, top-level <c>await</c>s included, and faults with what the root threw.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not <see cref="JSModuleContext.RunScriptAsync"/>.</b> That evaluates the module on a
    /// worker thread under a job pump of its own, so the frame's module ran in whichever window context
    /// was current while the worker held the context, and its continuations came back on that pump,
    /// not through the frame's window. Waiting for it inside the frame's window context bounded only
    /// the first part: a module still running when the wait gave up -- a slow static import, a long
    /// body, a top-level <c>await</c> on a timer, which could not fire while the wait blocked the
    /// thread that fires it -- carried on as the embedding page once the switch was undone. Started
    /// here, from a job of the frame's window (<c>WindowContextManager.TryEnqueueJob</c>), nothing of
    /// the root runs outside that window's jobs, and nothing waits for it.
    /// </para>
    /// <para>
    /// The root's static imports are read synchronously, as a frame's classic scripts are, through
    /// <see cref="ReadModuleSourceAsync"/>. The call holds the context, so the engine records this
    /// context as the current one for every continuation the load schedules.
    /// </para>
    /// </remarks>
    internal Task StartDocumentRoot(Broiler.HtmlBridge.Scripting.ModuleRoot root)
    {
        ArgumentNullException.ThrowIfNull(root);

        using (EnterExecution())
            return LoadModuleAsync(root.BaseUrl ?? string.Empty, root.Key);
    }

    private void Register(Broiler.HtmlBridge.Scripting.ModuleRoot root, ModuleClient? client)
    {
        var requester = new ModuleRequester(client, root.CrossOrigin);
        _requesters.TryAdd(root.Key, requester);
        if (!string.IsNullOrEmpty(root.BaseUrl))
            _requesters.TryAdd(root.BaseUrl!, requester);
    }

    private static string WithFragment(string url, string fragment) =>
        Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            ? new UriBuilder(absolute) { Fragment = fragment }.Uri.AbsoluteUri
            : $"{url.Split('#', 2)[0]}#{fragment}";

    /// <summary>
    /// What each root key presents as, for <see cref="GetModuleUrl"/>.
    /// </summary>
    /// <remarks>
    /// <c>ModuleRoot.BaseUrl</c> is already the answer for all three kinds a document produces, which
    /// is why this is a lookup rather than a rule: the extractor sets it to the page's URL for an
    /// inline root and to the root's own key for a <c>data:</c> or external one
    /// (<c>Core/Scripting/ScriptExtractionService.cs</c>, the <c>graphKey</c> switch and the line
    /// after it). Only the first of those differs from the key.
    /// </remarks>
    private static Dictionary<string, string>? RootUrls(
        IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot>? roots)
    {
        if (roots is null || roots.Count == 0)
            return null;

        Dictionary<string, string>? map = null;

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root.BaseUrl) || string.Equals(root.Key, root.BaseUrl, StringComparison.Ordinal))
                continue;

            (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[root.Key] = root.BaseUrl!;
        }

        return map;
    }

    // Resolve an import specifier to its module key (an absolute URL). Mirrors the shared UrlResolver
    // contract used everywhere else in the bridge (absolute stays; relative resolves against the base;
    // else null), plus a data: passthrough (a data: URL is its own key). `dirPath` is the importing
    // module's base — for URL modules that is the module's own absolute URL (see GetModuleDirectory).
    //
    // Resolution is also where a module learns who imports it: dirPath is the importer's key (or a
    // root's base URL), so the importer's requester passes to the module here. A classic script's
    // import() is the exception: it has no importer module, and the engine hands it the base of
    // whichever root ran last, so a frame's import() is resolved against, and requested for, the
    // frame's document instead (ImportForClassicScript).
    protected override string? Resolve(string dirPath, string relativePath)
    {
        // A frame root started by its key (StartDocumentRoot) names itself; its key is not a URL.
        if (_rootSources.ContainsKey(relativePath))
            return relativePath;

        if (_classicImport is { Resolved: false } classic && ReferenceEquals(classic.Owner, this))
        {
            // Only the call's own specifier: a graph that loads synchronously resolves the imported
            // module's own imports on this thread before the call returns, against that module.
            classic.Resolved = true;
            var own = ResolveKey(classic.Client.BaseUrl, relativePath);
            if (own is not null)
                _requesters.TryAdd(own, new ModuleRequester(classic.Client, CrossOrigin: null));
            return own;
        }

        var key = ResolveKey(dirPath, relativePath);
        if (key is not null && !string.IsNullOrEmpty(dirPath) && _requesters.TryGetValue(dirPath, out var importer))
            _requesters.TryAdd(key, importer);
        return key;
    }

    /// <summary>
    /// Runs <paramref name="import"/>, a classic script's <c>import()</c> call, as a request of
    /// <paramref name="client"/>'s document: its specifier resolves against that document's base URL,
    /// and the module it names — and what that module imports — is fetched for that document, under its
    /// policy, with the credentials a classic script's fetch options give it (<c>same-origin</c>).
    /// <see langword="null"/> runs the call as the engine would, as the top document's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why here, and why only the resolution.</b> A classic script reaches the engine's loader with no
    /// referrer module, so nothing the module map records says whose script it was; the bridge knows,
    /// from the window context the script runs in, and says so around the call. The engine resolves the
    /// specifier synchronously inside the call, before anything is fetched, so marking this thread for
    /// the length of the call is enough to attribute the request, and the requester map carries it to
    /// the fetch and to the module's own imports from there.
    /// </para>
    /// <para>
    /// The script's own <c>crossorigin</c> attribute is not known here (a frame's classic scripts are
    /// evaluated as source text), so the default a script without one has applies.
    /// </para>
    /// </remarks>
    internal T ImportForClassicScript<T>(ModuleClient? client, Func<T> import)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (client is null)
            return import();

        var previous = _classicImport;
        _classicImport = new ClassicImport(this, client);
        try
        {
            return import();
        }
        finally
        {
            _classicImport = previous;
        }
    }

    private static string? ResolveKey(string dirPath, string relativePath)
    {
        if (relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return relativePath;

        // "/lib.js" resolves against the importer even where Uri.TryCreate calls it an absolute file
        // path (Unix): see UrlResolver.IsPathAbsoluteReference.
        if (!Internal.Scripting.UrlResolver.IsPathAbsoluteReference(relativePath) &&
            Uri.TryCreate(relativePath, UriKind.Absolute, out var abs))
            return abs.AbsoluteUri;

        if (!string.IsNullOrEmpty(dirPath) && Uri.TryCreate(dirPath, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, relativePath, out var resolved))
            return resolved.AbsoluteUri;

        return null;
    }

    // A resolved module's own relative imports resolve against its full URL (URL relative-reference
    // semantics), not a filesystem directory; a frame root's against the base it was registered with.
    protected override string GetModuleDirectory(string fullPath) =>
        _rootBases.TryGetValue(fullPath, out var rootBase) ? rootBase : fullPath;

    /// <summary>
    /// The URL a module key presents as: <c>import.meta.url</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The override is required, and its absence is silent.</b> The base class answers a key
    /// verbatim whenever it parses as an absolute URI, and an inline root's key is <c>inline:0</c>,
    /// which does parse — <c>inline</c> reads as a scheme — so the base's null branch, the one a
    /// script could detect, is never reached and <c>import.meta.url</c> reads <c>inline:0</c>.
    /// </para>
    /// <para>
    /// <b>Only the URL depends on this; resolution does not.</b> The base a specifier resolves
    /// against arrives separately, as <c>RunScriptAsync</c>'s second argument, and for an inline root
    /// that is the page's URL — which is why a wrong <c>import.meta.url</c> leaves
    /// <c>import('./sibling.js')</c> resolving correctly, and why nothing else catches a regression
    /// here.
    /// </para>
    /// </remarks>
    protected override string GetModuleUrl(string moduleKey)
    {
        if (moduleKey is not null && _rootUrls.TryGetValue(moduleKey, out var url))
            return url;

        return base.GetModuleUrl(moduleKey);
    }

    // Read a resolved module's source through the bridge's CSP-gated fetch. `module.filePath` is the key
    // produced by Resolve: a data: URL or an absolute file/http URL. The policy is the requesting
    // document's: the page's, or a frame's own for a module a frame asked for.
    protected override Task<string> ReadModuleSourceAsync(JSModule module)
    {
        var key = module.filePath;

        // A frame root's own text, already fetched and authorised with its document's scripts.
        if (_rootSources.TryRemove(key, out var rootSource))
            return Task.FromResult(rootSource);

        var requester = RequesterOf(key);

        if (key.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!AllowsModule(requester, key))
                throw new InvalidOperationException($"module blocked by Content-Security-Policy: {key}");

            var decoded = ScriptExtractionService.DecodeDataUri(key);
            if (string.IsNullOrEmpty(decoded))
                throw new FileNotFoundException($"empty data module: {key}");
            return Task.FromResult(decoded);
        }

        if (!AllowsModule(requester, key))
            throw new InvalidOperationException($"module blocked by Content-Security-Policy: {key}");

        var source = ScriptExtractionService.FetchExternalScript(key, _pageUrl, ModuleRequest(requester));
        if (string.IsNullOrEmpty(source))
            throw new FileNotFoundException($"module not found: {key}");
        return Task.FromResult(source);
    }

    private ModuleRequester RequesterOf(string key) => _requesters.TryGetValue(key, out var found) ? found : default;

    /// <summary>
    /// Whether the requesting document's policy admits a module script from <paramref name="url"/>: a
    /// frame's own policies, with its base URL as <c>'self'</c>, or else the page's.
    /// </summary>
    private bool AllowsModule(ModuleRequester requester, string url) =>
        requester.Client is { } client
            ? client.Policies.AllowsExternalScript(url, client.BaseUrl, null)
            : _csp is null || _csp.AllowsExternalScript(url, _pageUrl, null);

    /// <summary>
    /// The request for an imported module, or <see langword="null"/> for the fallback client: a CORS
    /// script request for the document that imports it, with <c>same-origin</c> credentials, or
    /// <c>include</c> when its root said <c>crossorigin="use-credentials"</c>. A module a classic script's
    /// <c>import()</c> asked for is that script's document's (a frame's, when a frame's script called it),
    /// with the default; one nothing attributes is the top document's. The policy check above is repeated
    /// for every redirect.
    /// </summary>
    private ScriptRequest? ModuleRequest(ModuleRequester requester)
    {
        if (_fetch is not { } fetch)
            return null;

        return fetch.WithDocument(requester.Client?.Document ?? fetch.Document).ForScript(
            isModule: true,
            requester.CrossOrigin,
            requester.Client is null && _csp is null ? null : (url, _) => AllowsModule(requester, url.AbsoluteUri));
    }
}

/// <summary>
/// A document other than the top one, as the module scripts it runs and imports are requested for: a
/// frame's request context, the Content-Security-Policies its scripts are checked against, and its base
/// URL — what a classic script's <c>import()</c> specifier resolves against, and what those policies
/// read as <c>'self'</c>.
/// </summary>
/// <param name="Document">The frame document's request context.</param>
/// <param name="Policies">
/// Every policy the frame document enforces: what its response delivered or it inherited, and what its
/// own markup declares — the set its classic scripts and module roots were already checked against.
/// </param>
/// <param name="BaseUrl">The frame document's base URL.</param>
internal sealed record ModuleClient(DocumentRequestContext Document, ContentSecurityPolicySet Policies, string BaseUrl);
