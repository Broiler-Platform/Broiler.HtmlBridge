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
internal sealed class BridgeModuleContext : JSModuleContext
{
    private readonly ContentSecurityPolicy? _csp;
    private readonly string? _pageUrl;

    public BridgeModuleContext(ContentSecurityPolicy? csp = null, string? pageUrl = null)
        : base(new SynchronizationContext())
    {
        _csp = csp;
        _pageUrl = pageUrl;
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
