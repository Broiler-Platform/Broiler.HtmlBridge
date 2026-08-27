using System.Collections.Generic;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>Where a <c>&lt;script&gt;</c>'s program text comes from.</summary>
public enum ScriptSourceKind
{
    /// <summary>Inline script — the program is the element's text content.</summary>
    Inline,

    /// <summary>External <c>src</c> pointing at a <c>data:</c> URI.</summary>
    DataUri,

    /// <summary>External <c>src</c> pointing at a file/http(s)/relative URL.</summary>
    External,
}

/// <summary>
/// Metadata-rich descriptor of one discovered <c>&lt;script&gt;</c> element (Phase 7 item 3): its
/// document order, source kind, source URL (for external/data-URI), nonce, and the
/// <c>async</c>/<c>defer</c>/<c>type=module</c> flags — plus the resolved program text when the script
/// was authorised and its body was available. This is the neutral, host-agnostic shape the loader
/// (item 4) and the event loop (item 6) consume; it does not itself perform I/O or CSP decisions.
/// </summary>
public sealed record ScriptDescriptor(
    int DocumentOrder,
    ScriptSourceKind Kind,
    string? Url,
    string? Nonce,
    bool IsAsync,
    bool IsDefer,
    bool IsModule,
    string Content);

/// <summary>
/// One entry in a document's <see cref="ModuleMap"/> (Phase 7 item 6): a recognised
/// <c>&lt;script type="module"&gt;</c> root keyed for the browser module system. Inline modules are keyed by
/// a synthetic <c>inline:{order}</c> id; module scripts with a <c>src</c> are keyed by URL. An authorised
/// module carries its resolved body in <see cref="Source"/> with <see cref="IsExecutable"/> <c>true</c>; a
/// module blocked by CSP or unresolvable has a <c>null</c> <see cref="Source"/> and is not executable. The
/// map records the document's top-level module roots; the executable roots are in
/// <see cref="ScriptExtractionResult.ModuleRoots"/>.
/// </summary>
public sealed record ModuleMapEntry(
    int DocumentOrder,
    ScriptSourceKind Kind,
    string Key,
    string? Url,
    string? Source,
    bool IsExecutable);

/// <summary>
/// The document's module map (Phase 7 item 6): the registry of recognised
/// <c>&lt;script type="module"&gt;</c> roots in document order, so a module is never silently dropped.
/// Inline, <c>data:</c> and external module roots are all resolved and, when authorised, linked into the
/// executable graph. Keyed by <see cref="ModuleMapEntry.Key"/> (an inline module's synthetic id or a module
/// URL).
/// </summary>
public sealed class ModuleMap
{
    private readonly List<ModuleMapEntry> _entries = [];
    private readonly Dictionary<string, ModuleMapEntry> _byKey = new(System.StringComparer.Ordinal);

    /// <summary>The module entries in document order.</summary>
    public IReadOnlyList<ModuleMapEntry> Entries => _entries;

    /// <summary>Number of recognised module scripts.</summary>
    public int Count => _entries.Count;

    /// <summary>Looks up a module entry by its <see cref="ModuleMapEntry.Key"/>.</summary>
    public bool TryGet(string key, out ModuleMapEntry? entry) => _byKey.TryGetValue(key, out entry);

    internal void Add(ModuleMapEntry entry)
    {
        _entries.Add(entry);
        _byKey[entry.Key] = entry;
    }
}

/// <summary>
/// An authorised top-level ES-module root (a <c>&lt;script type="module"&gt;</c> whose source passed CSP):
/// its resolved module key, its already-decoded/fetched source, and the base URL its relative imports
/// resolve against. A consumer drives the JS engine's own module machinery to run each root (which pulls in
/// its transitive imports itself); this is the sole module-execution input since the string-rewriting
/// <c>EsModuleLinker</c> fallback was retired (Phase 7 tail).
/// </summary>
public sealed record ModuleRoot(string Key, string Source, string? BaseUrl);

/// <summary>
/// Holds the result of extracting all scripts from an HTML page, separated into regular
/// (inline / data-URI / external), deferred, and async scripts so the engine can execute them in the
/// correct order — plus the metadata descriptors, the module map, the linked module graph and the
/// authorised module roots (Phase 7 items 3 and 6).
/// </summary>
public sealed class ScriptExtractionResult(
    IReadOnlyList<string> scripts,
    IReadOnlyList<string> deferredScripts,
    IReadOnlyList<string> asyncScripts,
    IReadOnlyList<ScriptDescriptor>? descriptors = null,
    ModuleMap? moduleMap = null,
    IReadOnlyList<ModuleRoot>? moduleRoots = null)
{
    /// <summary>Regular scripts to execute in document order.</summary>
    public IReadOnlyList<string> Scripts { get; } = scripts;

    /// <summary>Deferred scripts to execute after all regular scripts.</summary>
    public IReadOnlyList<string> DeferredScripts { get; } = deferredScripts;

    /// <summary>Async scripts that may execute as soon as they are available.</summary>
    public IReadOnlyList<string> AsyncScripts { get; } = asyncScripts;

    /// <summary>
    /// Every discovered <c>&lt;script&gt;</c> in document order with its metadata (Phase 7 item 3),
    /// including <c>type=module</c> scripts that the classic <see cref="Scripts"/>/<see cref="DeferredScripts"/>/
    /// <see cref="AsyncScripts"/> lists omit. The classic lists remain the authoritative execution buckets;
    /// this exposes the metadata that used to be computed and discarded.
    /// </summary>
    public IReadOnlyList<ScriptDescriptor> Descriptors { get; } = descriptors ?? [];

    /// <summary>The document's module map (Phase 7 item 6): every recognised module in document order.</summary>
    public ModuleMap ModuleMap { get; } = moduleMap ?? new ModuleMap();

    /// <summary>
    /// The authorised top-level module roots in document order (Phase 7 item 6). A consumer drives the JS
    /// engine's own module machinery (see <c>BridgeModuleContext</c>) to run these — each root loads its own
    /// transitive imports — when the engine binds imports (<c>EngineModuleSupport.Available</c>). This is the
    /// sole module-execution input since the string-rewriting <c>EsModuleLinker</c> fallback was retired
    /// (Phase 7 tail).
    /// </summary>
    public IReadOnlyList<ModuleRoot> ModuleRoots { get; } = moduleRoots ?? [];
}
