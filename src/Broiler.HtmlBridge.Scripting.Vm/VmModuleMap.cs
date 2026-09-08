using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Scripting;
using Broiler.VM.Profile.JavaScript.Compiler;
using Broiler.VM.Profile.JavaScript.Format;

namespace Broiler.HtmlBridge;

/// <summary>
/// The document's module map: what a specifier names, and the graph reachable from a key.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution is the HOST's job and this is where the browser does it.</b> Broiler.VM's
/// embedding contract divides the labour explicitly — the host owns identity resolution, transport,
/// content policy, the module map and the event loop, and the core never fetches anything. So the
/// profile is never asked what <c>./a.mjs</c> means; it is handed a key that already means
/// something here, and it asks this map to confirm the answer it was given.
/// </para>
/// <para>
/// <b>It resolves through the same <c>UrlResolver</c> that formed the keys.</b>
/// <c>ScriptExtractionService</c> builds each <see cref="ModuleRoot.Key"/> by resolving the
/// element's URL against the page, so resolving an <c>import()</c> specifier any other way would
/// produce a key this map does not contain and turn a module the document declared into one that
/// cannot be found. Two resolvers is the defect a module map exists to prevent; the
/// <c>InternalsVisibleTo</c> that makes this possible says the same thing.
/// </para>
/// </remarks>
internal sealed class VmModuleMap
{
    /// <summary>The map of a document that declared no modules.</summary>
    internal static VmModuleMap Empty { get; } = new(null, null);

    private readonly Dictionary<string, ModuleRoot> _byKey;
    private readonly string? _documentUrl;

    internal VmModuleMap(IReadOnlyList<ModuleRoot>? roots, string? documentUrl)
    {
        _documentUrl = string.IsNullOrEmpty(documentUrl) ? null : documentUrl;
        _byKey = new Dictionary<string, ModuleRoot>(StringComparer.Ordinal);

        foreach (var root in roots ?? [])
        {
            // First declaration wins, matching the extraction service, which already de-duplicates
            // on the same key. A second entry for one key would be a second module with its own
            // bindings, which is the thing a key exists to prevent.
            _byKey.TryAdd(root.Key, root);
        }
    }

    internal bool IsEmpty => _byKey.Count == 0;

    /// <summary>
    /// Resolves what <paramref name="specifier"/> names when imported from <paramref name="referrer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two kinds of referrer reach this, and both are the host's own.</b> A module importing
    /// another is named by its key, and its <see cref="ModuleRoot.BaseUrl"/> is the base — which is
    /// what makes an inline module resolve against the page and a fetched one against itself. A
    /// classic script calling <c>import()</c> is named by the document's URL, which this engine set
    /// as that code unit's referrer, and the document is then the base. A referrer that is neither
    /// resolves nothing rather than falling back to something: an arbitrary string used as a base
    /// would let an artifact choose what its own specifiers mean.
    /// </para>
    /// <para>
    /// A specifier that resolves to a key this document did not declare is not resolvable here
    /// either. The host fetches nothing on the guest's behalf at this point, so a module the page
    /// never named has no source to compile.
    /// </para>
    /// </remarks>
    internal bool TryResolve(string referrer, string specifier, out string key)
    {
        key = string.Empty;

        string? relativeTo =
            _byKey.TryGetValue(referrer, out var from) ? from.BaseUrl ?? from.Key
            : string.Equals(referrer, _documentUrl, StringComparison.Ordinal) ? _documentUrl
            : null;

        if (relativeTo is null)
            return false;

        var resolved = UrlResolver.Resolve(specifier, relativeTo);

        if (resolved is null)
            return false;

        key = resolved.AbsoluteUri;
        return _byKey.ContainsKey(key);
    }

    /// <summary>
    /// Answers one resolution request the profile put to this composition.
    /// </summary>
    /// <remarks>
    /// The request is the referring module's key, the specifier as the source wrote it, and the key
    /// the artifact claims they resolve to, separated by NULs. Yes only when this host's own
    /// resolution of the first two is the third — so an artifact bundled under some other host's
    /// rules is refused here rather than run under ours.
    /// </remarks>
    internal bool Confirms(ReadOnlySpan<byte> request)
    {
        var parts = JsFormat.DecodeText(request).Split('\0');

        return parts.Length == 3
            && TryResolve(parts[0], parts[1], out var resolved)
            && string.Equals(resolved, parts[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// The module named by <paramref name="key"/> and everything reachable from it, root first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Root first because the compiler treats the first module of a compilation as the graph's
    /// root and reaches the rest from it. A module in the set that the root cannot reach is still
    /// verified and never evaluated, which is what the specification says of a module nothing
    /// requests — but this walks the graph rather than handing over every module the document
    /// declared, so that an <c>import()</c> of one module does not drag in a sibling's imports.
    /// </para>
    /// <para>
    /// A module whose source does not parse is included with no requests rather than dropped. The
    /// compilation that follows refuses it and says why; refusing it here would move the diagnostic
    /// somewhere the caller cannot report per-script and would give two answers to one question.
    /// </para>
    /// </remarks>
    internal IReadOnlyList<JsModuleUnit>? GraphFrom(string key)
    {
        if (!_byKey.ContainsKey(key))
            return null;

        var units = new List<JsModuleUnit>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { key };
        var pending = new Queue<string>();
        pending.Enqueue(key);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            var module = _byKey[current];
            var requested = JsCompiler.Requests(module.Source, SliceParseOptions.Module);
            var resolutions = new List<JsResolvedRequest>(requested.Specifiers.Count);

            foreach (var specifier in requested.Specifiers)
            {
                if (!TryResolve(current, specifier, out var target))
                    continue;

                resolutions.Add(new JsResolvedRequest(specifier, target));

                if (seen.Add(target))
                    pending.Enqueue(target);
            }

            units.Add(new JsModuleUnit(current, module.Source, SliceParseOptions.Module, resolutions));
        }

        return units;
    }
}
