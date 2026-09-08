using System.Security.Cryptography;
using System.Text;
using Broiler.VM.Profile.JavaScript.Compiler;
using Broiler.VM.Profile.JavaScript.Format;

namespace Broiler.HtmlBridge;

/// <summary>
/// Compiled artifacts, kept so that a document seen twice is compiled once.
/// </summary>
/// <remarks>
/// <para>
/// <b>IT CACHES BYTES AND NOT HANDLES, AND THAT IS THE CONTRACT RATHER THAN A CHOICE.</b>
/// Broiler.VM settled at VM-0 that bytes are the only input from which a verified artifact may be
/// produced — the round trip is mandatory even when the compiler that produced them is in the same
/// process and inside the same trust boundary. So every use still goes through
/// <c>runtime.Verify</c>, under that operation's own allowance, and what is saved here is
/// compilation and nothing else. A cache that skipped verification would not be a faster host, it
/// would be a different and weaker one.
/// </para>
/// <para>
/// <b>A cache that serves the wrong program is worse than no cache</b>, so the key covers every
/// input that reaches the compiler, and the two that are not obvious are named rather than
/// assumed:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>The compiler's own identity</b>, as the module version id of the assembly
/// <see cref="JsCompiler"/> lives in. The profile publishes a format version but no compiler
/// version, and the format version is the wrong question anyway: a Broiler.VM bump that changes
/// how the lowering emits, without changing the format it emits into, must miss. An MVID changes
/// whenever that assembly's bytes change, which is exactly the event that should invalidate.
/// </description></item>
/// <item><description>
/// <b>The referrer</b>, which is the document's URL. It is not decoration: it is what a relative
/// specifier in that unit resolves against, so the same text under two documents is two programs.
/// </description></item>
/// </list>
/// <para>
/// <b>Bounded, because a page chooses what goes in it.</b> A document with a thousand distinct
/// inline scripts would otherwise pin a thousand artifacts for the life of the process. The bound
/// is on entries and on total bytes, and the least recently used entry goes first.
/// </para>
/// </remarks>
internal sealed class VmCompilationCache
{
    /// <summary>The cache the engine uses, shared so two pages running one library compile it once.</summary>
    internal static VmCompilationCache Shared { get; } = new(MaximumEntries, MaximumBytes);

    private const int MaximumEntries = 64;
    private const long MaximumBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The compiler's identity, computed once. See the remarks on why it is an MVID.
    /// </summary>
    private static readonly Guid CompilerIdentity = typeof(JsCompiler).Assembly.ManifestModule.ModuleVersionId;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private long _bytes;
    private long _clock;

    internal VmCompilationCache(int maximumEntries, long maximumBytes)
    {
        _maximumEntries = maximumEntries;
        _maximumBytes = maximumBytes;
    }

    /// <summary>How many compilations this cache has had to perform.</summary>
    /// <remarks>
    /// The counters exist so a test can assert the cache WORKS without asserting how LONG anything
    /// took. "The second run of the same document compiled nothing" is a fact about behaviour that
    /// a machine either reproduces or does not; "the second run was faster" is a measurement, and a
    /// measurement in a test is a flake waiting for a loaded build agent.
    /// </remarks>
    internal int Compilations { get; private set; }

    /// <summary>How many compilations this cache has avoided.</summary>
    internal int Hits { get; private set; }

    /// <summary>Forgets everything, so one test cannot see another's entries or counters.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _bytes = 0;
            Compilations = 0;
            Hits = 0;
        }
    }

    /// <summary>
    /// The artifact for <paramref name="units"/>, compiled if this cache has not seen them before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Only a success is stored.</b> A refusal is cheap to reproduce, carries diagnostics whose
    /// shape this cache would have to preserve exactly, and is rare enough that keeping it would
    /// buy nothing; caching one would also mean a broken script staying broken across a change to
    /// the compiler that fixed it, for as long as the process lived.
    /// </para>
    /// <para>
    /// <b>The stored array is handed out, not copied.</b> That is safe because the only consumer is
    /// <c>runtime.Verify</c>, which reads the bytes into its own verified state, and nothing on
    /// this path writes to them — but it is the reason this type is internal and its callers are
    /// counted rather than being a general-purpose cache.
    /// </para>
    /// </remarks>
    internal JsCompilation GetOrCompile(
        IReadOnlyList<JsScriptUnit> units,
        IReadOnlyList<JsModuleUnit> modules,
        JsCompileRequest request,
        Func<JsCompilation> compile)
    {
        var key = Key(units, modules, request);

        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var hit))
            {
                hit.LastUsed = ++_clock;
                Hits++;
                return new JsCompilation(true, hit.Artifact, []);
            }
        }

        // OUTSIDE THE LOCK. Compiling a megabyte of script while holding it would serialise every
        // other page in the process behind this one, and the worst a concurrent duplicate costs is
        // one wasted compilation that the second writer discards.
        var compiled = compile();

        if (!compiled.Succeeded || compiled.Artifact is null)
        {
            lock (_lock)
            {
                Compilations++;
            }

            return compiled;
        }

        lock (_lock)
        {
            Compilations++;

            if (compiled.Artifact.LongLength <= _maximumBytes && !_entries.ContainsKey(key))
            {
                _entries[key] = new Entry(compiled.Artifact, ++_clock);
                _bytes += compiled.Artifact.LongLength;
                Evict();
            }
        }

        return compiled;
    }

    /// <summary>Drops least-recently-used entries until the cache is inside both bounds.</summary>
    private void Evict()
    {
        while (_entries.Count > _maximumEntries || _bytes > _maximumBytes)
        {
            var oldest = string.Empty;
            var oldestUse = long.MaxValue;

            foreach (var (candidate, entry) in _entries)
            {
                if (entry.LastUsed >= oldestUse)
                    continue;

                oldest = candidate;
                oldestUse = entry.LastUsed;
            }

            if (oldest.Length == 0)
                return;

            _bytes -= _entries[oldest].Artifact.LongLength;
            _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// The identity of one compilation: everything that reaches the compiler, hashed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Hashed rather than kept whole</b> because the inputs are the page's scripts, and holding
    /// them as dictionary keys would store every document twice. SHA-256 is used for the same
    /// reason a code cache anywhere uses a cryptographic digest: a collision here would run one
    /// page's program for another, so a checksum whose collisions are merely unlikely-in-practice
    /// is not the right tool.
    /// </para>
    /// <para>
    /// <b>Every field is length-prefixed.</b> Concatenating <c>a</c> + <c>bc</c> and <c>ab</c> +
    /// <c>c</c> produces one digest for two different documents, and a cache is exactly where that
    /// stops being a curiosity.
    /// </para>
    /// </remarks>
    private static string Key(
        IReadOnlyList<JsScriptUnit> units,
        IReadOnlyList<JsModuleUnit> modules,
        JsCompileRequest request)
    {
        var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        Span<byte> scalars = stackalloc byte[16];
        CompilerIdentity.TryWriteBytes(scalars);
        digest.AppendData(scalars);

        Add(digest, JsFormat.FormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Add(digest, request.Manifest.ToString());
        Add(digest, request.Form.ToString());
        Add(digest, request.Backend);
        Add(digest, units.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var unit in units)
        {
            Add(digest, unit.Name);
            Add(digest, unit.Text);
            Add(digest, unit.Referrer);
            Add(digest, unit.ForceStrict ? "strict" : "sloppy");
            AddOptions(digest, unit.Options);
        }

        Add(digest, modules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var module in modules)
        {
            Add(digest, module.Key);
            Add(digest, module.Text);
            AddOptions(digest, module.Options);

            var requests = module.Requests ?? [];
            Add(digest, requests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            foreach (var resolved in requests)
            {
                Add(digest, resolved.Specifier);
                Add(digest, resolved.Key);
            }
        }

        return Convert.ToHexString(digest.GetHashAndReset());
    }

    /// <summary>
    /// The parse options, spelled out rather than trusted to <c>ToString</c>.
    /// </summary>
    /// <remarks>
    /// They are constant at this engine's own call site today — every script is
    /// <c>SliceParseOptions.Script</c> — but they are part of what the compiler reads, and a key
    /// that covered only what varies today is a key that goes wrong the day something else does.
    /// </remarks>
    private static void AddOptions(IncrementalHash digest, SliceParseOptions options)
    {
        Add(digest, options.Goal.ToString());
        Add(digest, options.AllowTopLevelAwait ? "await" : "no-await");
        Add(digest, options.MaximumNestingDepth.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void Add(IncrementalHash digest, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        digest.AppendData(length);
        digest.AppendData(bytes);
    }

    private sealed class Entry(byte[] artifact, long lastUsed)
    {
        internal byte[] Artifact { get; } = artifact;

        internal long LastUsed { get; set; } = lastUsed;
    }
}
