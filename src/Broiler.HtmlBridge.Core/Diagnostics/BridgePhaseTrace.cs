using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Broiler.HtmlBridge.Core.Diagnostics;

/// <summary>
/// An opt-in, off-by-default phase timer for the DOM bridge, readable by hosts above it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is here and not in the host that reports it.</b> The WPT runner's own
/// <c>WptPhaseTrace</c> found <c>DomBridge.Attach</c> at <b>50.8% of a whole WPT run</b>
/// (444.91 ms per call) — the single largest phase, larger than everything else together — but it
/// cannot see inside <c>Attach</c>, because <c>Broiler.HtmlBridge.Dom</c> sits below the runner and
/// cannot reference it. This assembly is referenced by both, so a scope opened in the bridge can be
/// read by the runner and merged into one table.
/// </para>
/// <para>
/// <b>Shared and locked, like the runner's.</b> The phases here are whole parses and whole API
/// registrations — tens to hundreds of milliseconds — so a lock per scope is far below the noise,
/// and a shared accumulator is what lets the thread that prints see what the threads that worked
/// recorded.
/// </para>
/// <para>
/// <b>Cost when off, which is always in production.</b> <see cref="Measure"/> reads one static
/// <see langword="bool"/> and returns a struct whose <see cref="Scope.Dispose"/> is a null check.
/// </para>
/// </remarks>
public static class BridgePhaseTrace
{
    /// <summary>Phase names. These strings are the vocabulary the published breakdown uses.</summary>
    public static class Phases
    {
        /// <summary>`DomBridge.ParseHtml` — tokenize, build the DOM tree, and the bridge's node tables.</summary>
        public const string ParseHtml = "    · ParseHtml (DOM build)";

        /// <summary>
        /// `DomBridge.RegisterDocument` — publishing the document, window and the DOM API surface
        /// onto the JS context.
        /// </summary>
        public const string RegisterDocument = "    · RegisterDocument (DOM API surface)";

        // --- Sub-steps of RegisterDocument, nested one level further. ---

        /// <summary>Document object: basics, events, writing, traversal, collections, metadata.</summary>
        public const string RegDocumentObject = "        - document object";

        /// <summary>Window basics, fetch, MessageChannel, CSSStyleSheet, getComputedStyle.</summary>
        public const string RegWindowBasics = "        - window basics + fetch";

        /// <summary>`RegisterWindowGlobals` — timers, storage, location and the rest of window.</summary>
        public const string RegWindowGlobals = "        - window globals";

        /// <summary>performance, navigator and viewport objects.</summary>
        public const string RegWindowObjects = "        - performance/navigator/viewport";

        /// <summary>`RegisterContentRenderingPolyfills`.</summary>
        public const string RegContentPolyfills = "        - content-rendering polyfills";

        /// <summary>`RegisterSecurityAndConstructorPolyfills`.</summary>
        public const string RegSecurityPolyfills = "        - security/constructor polyfills";

        /// <summary>`MirrorWindowMembersOntoGlobal` — the window-to-global descriptor sweep.</summary>
        public const string RegWindowMirror = "        - window→global mirror";
    }

    private static readonly Dictionary<string, (double Ms, int Count)> _accumulator = new(StringComparer.Ordinal);
    private static readonly object _sync = new();

    /// <summary>Whether the phase timers run. Off by default.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Discards the accumulated totals.</summary>
    public static void Reset()
    {
        lock (_sync)
            _accumulator.Clear();
    }

    /// <summary>The accumulated phase totals since the last <see cref="Reset"/>. A copy.</summary>
    public static IReadOnlyDictionary<string, (double Ms, int Count)> Totals()
    {
        lock (_sync)
            return new Dictionary<string, (double, int)>(_accumulator);
    }

    /// <summary>Times the <see langword="using"/> block into <paramref name="phase"/> when enabled.</summary>
    public static Scope Measure(string phase) => Enabled ? new Scope(phase) : default;

    private static void Add(string phase, long startTimestamp)
    {
        var ms = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        lock (_sync)
        {
            _accumulator.TryGetValue(phase, out var previous);
            _accumulator[phase] = (previous.Ms + ms, previous.Count + 1);
        }
    }

    /// <summary>One timed phase. <see langword="default"/> is the disabled, do-nothing form.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _phase;
        private readonly long _start;

        internal Scope(string phase)
        {
            _phase = phase;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_phase is not null)
                Add(_phase, _start);
        }
    }
}
