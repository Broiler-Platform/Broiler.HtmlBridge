using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Broiler.JavaScript.Modules;

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
/// module path — the string-rewriting linker fallback was retired once the engine (patches 0010/0011:
/// top-level-await codegen + module-orchestration completion) was pinned and every surface took this path.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type stays engine-typed, and the reason is no longer that a contract could not be
/// designed.</b> The other bridge units name engine types to build values; this one <em>is</em> an
/// engine type — the module graph's resolve/fetch hooks are <see langword="protected"/> overrides, so
/// the coupling is the base class rather than a call. JSEAL still declares only <em>that</em> an
/// engine binds modules (<c>JsCapabilities.Modules</c>, <c>DynamicImport</c>) and how host versus
/// guest source is run (<c>IJsSource</c>), neither of which offers a specifier resolver or a source
/// fetcher.
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
/// <c>Broiler.HtmlBridge.Jseal.BroilerJs</c>, and <c>JSModuleContext</c> lives in
/// <c>Broiler.JavaScript.Modules</c>, which that project deliberately does not reference: its
/// <c>engineProjectRefs</c> budget is 2 and <c>scripts/check-engine-neutrality.sh</c> enforces that
/// number for a provider as well as for a binding. Adding the reference is a real budget increase and
/// wants an argument in its own diff, not a side effect of a refactor.
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
    private readonly Dictionary<string, string>? _rootUrls;

    public BridgeModuleContext(
        ContentSecurityPolicy? csp = null,
        string? pageUrl = null,
        IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot>? roots = null)
        : base(new SynchronizationContext())
    {
        _csp = csp;
        _pageUrl = pageUrl;
        _rootUrls = RootUrls(roots);
    }

    /// <summary>
    /// What each root key presents as, for <see cref="GetModuleUrl"/>.
    /// </summary>
    /// <remarks>
    /// <c>ModuleRoot.BaseUrl</c> is already the answer for all three kinds a document produces, which
    /// is why this is a lookup rather than a rule: the extractor sets it to the page's URL for an
    /// inline root and to the root's own key for a <c>data:</c> or external one
    /// (<c>Core/Scripting/ScriptExtractionService.cs</c>, the <c>graphKey</c> switch and the line
    /// after it). Only the first of those differs from the key, and it is the only one that was
    /// wrong.
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
    protected override string? Resolve(string dirPath, string relativePath)
    {
        if (relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return relativePath;

        if (Uri.TryCreate(relativePath, UriKind.Absolute, out var abs))
            return abs.AbsoluteUri;

        if (!string.IsNullOrEmpty(dirPath) && Uri.TryCreate(dirPath, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, relativePath, out var resolved))
            return resolved.AbsoluteUri;

        return null;
    }

    // A resolved module's own relative imports resolve against its full URL (URL relative-reference
    // semantics), not a filesystem directory.
    protected override string GetModuleDirectory(string fullPath) => fullPath;

    /// <summary>
    /// The URL a module key presents as: <c>import.meta.url</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>THE BASE CLASS ASKED FOR THIS OVERRIDE AND SAID WHAT HAPPENS WITHOUT IT.</b> Its own
    /// remarks read "Only the host knows what its keys are... A host with keys of another shape
    /// overrides this", and, of the null it returns for a key it cannot express, "a module whose key
    /// cannot be expressed as a URL reads <c>undefined</c> — which a script can detect — instead of a
    /// plausible lie". This host has keys of another shape and did not override it, so the default
    /// ran: an inline root's key is <c>inline:0</c>, <c>Uri.TryCreate("inline:0", Absolute, out _)</c>
    /// is TRUE because <c>inline</c> parses as a scheme, and the default returned <c>inline:0</c>
    /// verbatim. The null branch that would have made it detectable was never reached. Measured, on a
    /// page at <c>https://example.test/dir/page.html</c>: <c>import.meta.url</c> was <c>inline:0</c>.
    /// </para>
    /// <para>
    /// <b>Only the URL was wrong; resolution was always right.</b> The base a specifier resolves
    /// against arrives separately, as <c>RunScriptAsync</c>'s second argument, and for an inline root
    /// that is the page's URL — so <c>import('./sibling.js')</c> from an inline module already
    /// rejected with <c>module not found: https://example.test/dir/sibling.js</c>, resolved correctly.
    /// The two facts had diverged, which is why nothing caught it: everything that USES the base
    /// worked, and only the value a page can READ was false.
    /// </para>
    /// </remarks>
    protected override string GetModuleUrl(string moduleKey)
    {
        if (moduleKey is not null && _rootUrls is { } urls && urls.TryGetValue(moduleKey, out var url))
            return url;

        return base.GetModuleUrl(moduleKey);
    }

    // Read a resolved module's source through the bridge's CSP-gated fetch. `module.filePath` is the key
    // produced by Resolve: a data: URL or an absolute file/http URL.
    protected override Task<string> ReadModuleSourceAsync(JSModule module)
    {
        var key = module.filePath;

        if (key.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (_csp != null && !_csp.AllowsExternalScript(key, _pageUrl, null))
                throw new InvalidOperationException($"module blocked by Content-Security-Policy: {key}");

            var decoded = ScriptExtractionService.DecodeDataUri(key);
            if (string.IsNullOrEmpty(decoded))
                throw new FileNotFoundException($"empty data module: {key}");
            return Task.FromResult(decoded);
        }

        if (_csp != null && !_csp.AllowsExternalScript(key, _pageUrl, null))
            throw new InvalidOperationException($"module blocked by Content-Security-Policy: {key}");

        var source = ScriptExtractionService.FetchExternalScript(key, _pageUrl);
        if (string.IsNullOrEmpty(source))
            throw new FileNotFoundException($"module not found: {key}");
        return Task.FromResult(source);
    }
}
