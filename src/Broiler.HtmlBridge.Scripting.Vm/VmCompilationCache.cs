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
/// <b>IT CACHES BYTES AND RE-VERIFIES ON EVERY LOAD, WHICH IS WHAT THE CONTRACT SAYS TO DO.</b>
/// ADR 0010's consequences put it directly: release 1 gives a browser no code cache, the persisted
/// envelope is approved as contract and not as a release feature with no envelope member exposed,
/// and "a host that needs it caches source-to-artifact bytes itself and re-verifies on every load,
/// which is the contract's intended behaviour rather than a workaround". So this is that host doing
/// that. Every use still goes through <c>runtime.Verify</c> under its own allowance, and what is
/// saved is the lowering and nothing else.
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
/// <b>The referrer</b>, which is the document's URL. It reaches the artifact through exactly one
/// instruction — the one a dynamic <c>import()</c> emits — so for the overwhelming majority of
/// scripts it changes nothing, and keying on it is deliberately pessimistic: an extra miss is the
/// safe direction, and a script that does contain an <c>import()</c> genuinely is a different
/// program under a different base.
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
    /// <summary>The cache the engine uses, shared across the engines a process creates.</summary>
    /// <remarks>
    /// <b>WHAT HITS IS AN IDENTICAL UNIT LIST, WHICH IS NARROWER THAN "EVERY LIBRARY ONCE" AND
    /// WIDER THAN "ONE DOCUMENT".</b> The key is a digest over the whole ordered unit list, so a
    /// library shared by two pages is not separately keyed — it is one unit inside a whole-document
    /// digest. Through <c>Execute(scripts)</c> no document URL is supplied, so two pages with
    /// byte-identical script lists DO share, correctly: they compile to the same program. Through
    /// the module-capable overload the URL joins the identity and separates them.
    /// <para>
    /// Whole-document granularity is what the engine's design forces rather than a limitation of
    /// the key: a document's scripts are compiled into ONE artifact so they share one realm, and
    /// each unit's code is emitted against the accumulated code of the ones before it — so bytes
    /// keyed per script would be bytes compiled in a different neighbourhood. Shared across engines
    /// rather than held per engine because a new engine is built per navigation; a per-engine cache
    /// would never hit at all.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Where this cache also keeps its artifacts, so a document compiled in one run is not compiled
    /// again in the next. Null — no disk at all — unless a host sets one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a second tier and not a replacement.</b> Memory is asked first, disk second, the
    /// compiler last; anything found on disk is promoted into memory so the next hit in this run
    /// costs nothing. What disk buys over memory is exactly one thing: surviving a restart. Within
    /// a run the in-memory tier already has it.
    /// </para>
    /// <para>
    /// <b>Never set on the guest-load cache.</b> Persisting what a page passed to <c>eval</c> would
    /// write guest-chosen strings to disk under a name derived from their content, and would undo
    /// the scoping that keeps one navigation chain's evaluations out of another's. Only documents
    /// are persisted, and only when a host asks. See <c>VmArtifactStore</c> for why the default is
    /// no disk at all.
    /// </para>
    /// </remarks>
    internal VmArtifactStore? Store { get; set; }

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
        JsCompileRequest request)
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

        // DISK IS THE SECOND TIER, ASKED BEFORE THE COMPILER AND AFTER MEMORY. A hit here is
        // promoted into memory, so the next one in this run costs a dictionary lookup rather than
        // a file read. It counts as a hit and not a compilation, because that is what it is.
        if (Store is { } store && store.TryRead(key) is { } stored)
        {
            lock (_lock)
            {
                Hits++;
                Remember(key, stored);
            }

            return new JsCompilation(true, stored, []);
        }

        // OUTSIDE THE LOCK. Compiling a megabyte of script while holding it would serialise every
        // other page in the process behind this one, and the worst a concurrent duplicate costs is
        // one wasted compilation that the second writer discards.
        var compiled = JsCompiler.Compile(units, modules, request);

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
            Remember(key, compiled.Artifact);
        }

        // Outside the lock for the same reason the compile is: a file write must not hold every
        // other page in the process behind it. Best effort throughout — see VmArtifactStore.
        Store?.Write(key, compiled.Artifact);

        return compiled;
    }

    /// <summary>Adds one artifact to the in-memory tier, if it fits and is not already there.</summary>
    /// <remarks>The caller holds the lock.</remarks>
    private void Remember(string key, byte[] artifact)
    {
        if (artifact.LongLength > _maximumBytes || _entries.ContainsKey(key))
            return;

        _entries[key] = new Entry(artifact, ++_clock);
        _bytes += artifact.LongLength;
        Evict();
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

    /// <summary>Appends one length-prefixed field to the digest.</summary>
    /// <remarks>
    /// <b>THE UTF-16 CODE UNITS, NOT UTF-8, AND THAT IS A CORRECTNESS FIX RATHER THAN A
    /// PREFERENCE.</b> <c>Encoding.UTF8.GetBytes</c> uses replacement fallback, so every lone
    /// surrogate encodes to the same replacement bytes: <c>\uD800</c> and <c>\uD801</c> are
    /// indistinguishable once encoded. That is reachable rather than theoretical — the profile's
    /// tokenizer accepts a lone surrogate as a legal JavaScript string element, and says so — so
    /// two scripts differing only there would have hashed to one key and the cache would have
    /// served one page's program to another. Hashing the code units is a faithful injection: no
    /// fallback, nothing collapsed, and the byte order is the platform's for both writer and
    /// reader because the digest never leaves this process.
    /// </remarks>
    private static void Add(IncrementalHash digest, string? value)
    {
        var text = (value ?? string.Empty).AsSpan();
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, text.Length);
        digest.AppendData(length);
        digest.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(text));
    }

    private sealed class Entry(byte[] artifact, long lastUsed)
    {
        internal byte[] Artifact { get; } = artifact;

        internal long LastUsed { get; set; } = lastUsed;
    }
}
