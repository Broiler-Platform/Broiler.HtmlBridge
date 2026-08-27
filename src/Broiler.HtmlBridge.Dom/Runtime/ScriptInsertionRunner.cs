using Broiler.Dom;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// Runs the <c>&lt;script&gt;</c> elements a page's own JavaScript inserts into the document — the
/// HTML "prepare a script" steps for a <em>script-created</em> script, which nothing performed
/// before: the host discovers scripts by scanning the document source once, so an element built with
/// <c>document.createElement('script')</c> and appended was added to the tree and then never fetched
/// or executed.
/// </summary>
/// <remarks>
/// <para>
/// That gap is not a missing convenience; it silently breaks the loader idiom a lot of the web is
/// built on — inject a <c>&lt;script src&gt;</c>, then poll or listen until the global it defines
/// appears. With the injected script never running the global never appears, the poll reschedules
/// itself forever, and the capture burns its whole
/// <see cref="DomBridgeRuntimeLimits.AsyncDrainIterationLimit"/> budget before giving up with
/// "async work did not settle" and a half-built page. html5test.com is exactly this shape: its
/// <c>waitForWhichBrowser</c> loop polls every 100 ms for a global defined by a script it injects,
/// so the whole page below the header was never generated.
/// </para>
/// <para>
/// <b>Only script-created elements are eligible.</b> The set is populated from the
/// <c>document.createElement</c>/<c>createElementNS</c> funnel alone, which is what keeps the two
/// kinds of script that must <em>not</em> run here out of it: the parser-inserted ones (the host
/// already ran those from the document source — running them again would double every side effect),
/// and the ones an <c>innerHTML</c> parse produces, which per spec are "already started" at birth and
/// never execute. Neither reaches <c>createElement</c>, so neither is ever a candidate.
/// </para>
/// <para>
/// <b>Insertion is observed, not intercepted.</b> The runner subscribes to the canonical
/// <see cref="DomDocument.Mutated"/> stream rather than hooking <c>appendChild</c>, so every path
/// that can connect a node — <c>appendChild</c>, <c>insertBefore</c>, <c>replaceChild</c>,
/// <c>append</c>/<c>prepend</c>, <c>insertAdjacentElement</c>, or attaching a fragment the script was
/// built into — is covered by one subscription. Each record sweeps the small pending list rather than
/// walking the inserted subtree, so the cost is the number of un-started script-created scripts (all
/// but always zero) and not the size of what was inserted.
/// </para>
/// <para>
/// <b>An external script runs on the event loop, an inline one runs at once.</b> A <c>src</c> script
/// defers its fetch, because the page assigns <c>onload</c> <em>after</em> <c>appendChild</c> at least
/// as often as before it, and a fetch-and-run inside the insertion would fire <c>load</c> at a handler
/// that does not exist yet. It is queued as a due-now task on the timer queue rather than as a frame
/// action, so it takes its turn in registration order among the page's own timers — a frame action
/// runs after every timer already due, which would hand a page's <c>setTimeout(check, 100)</c> a
/// script that had not loaded. An inline script has nothing to wait for and runs synchronously during
/// the insertion, as the spec requires — the line after the <c>appendChild</c> can see what it defined.
/// </para>
/// </remarks>
internal sealed class ScriptInsertionRunner
{
    private readonly IScriptInsertionHost _host;

    /// <summary>
    /// Script elements built by <c>document.createElement</c> that have not started yet.
    /// </summary>
    /// <remarks>
    /// Membership <em>is</em> the spec's "already started" flag, inverted: an element enters once, at
    /// creation, and leaves once, when it starts — so a script that is moved or re-inserted after
    /// running is simply no longer here to be found, and no second set has to be kept in step. The
    /// ones resolved as never-runnable (a non-JavaScript <c>type</c>, a source the policy blocks)
    /// leave the same way, which is what keeps them out of every later sweep.
    /// </remarks>
    private readonly List<DomElement> _pending = [];

    private DomDocument? _subscribed;
    private int _executedCount;

    public ScriptInsertionRunner(IScriptInsertionHost host) => _host = host;

    /// <summary>
    /// Records that <paramref name="element"/> came from <c>document.createElement</c> — the one
    /// origin that makes a script eligible to run on insertion. Ignores everything that is not a
    /// <c>&lt;script&gt;</c>, so the common case costs one tag-name comparison.
    /// </summary>
    public void NoteCreatedByScript(DomElement element)
    {
        if (!IsScriptElement(element))
            return;

        EnsureSubscribed();
        _pending.Add(element);
    }

    /// <summary>
    /// Drops every subscription and all per-document state. Runs on re-parse and disposal, alongside
    /// the timer and listener stores: a re-attached document's scripts are its own.
    /// </summary>
    public void Clear()
    {
        if (_subscribed is { } document)
            document.Mutated -= OnDocumentMutation;
        _subscribed = null;
        _pending.Clear();
        _executedCount = 0;
    }

    /// <summary>
    /// Subscribed lazily on the first script-created <c>&lt;script&gt;</c>, so a document that never
    /// injects one carries no subscription and pays nothing per mutation.
    /// </summary>
    private void EnsureSubscribed()
    {
        if (_subscribed is not null)
            return;

        _subscribed = _host.Document;
        _subscribed.Mutated += OnDocumentMutation;
    }

    private void OnDocumentMutation(DomMutationRecord record)
    {
        if (_pending.Count == 0 || _host.MutationDeliverySuppressed || !_host.HasJsContext)
            return;

        // A ChildList record may have connected a pending script (directly, or by attaching a
        // subtree it was built into); an attribute write may have given an already-connected one the
        // src it was waiting for. Anything else cannot change a script's readiness.
        if (record.Type == DomMutationType.ChildList
            ? record.AddedNodes is not { Count: > 0 }
            : record.Type != DomMutationType.Attributes)
            return;

        SweepPending();
    }

    /// <summary>
    /// Prepares every pending script that is now connected to the watched document.
    /// </summary>
    /// <remarks>
    /// Walks the list in place rather than a snapshot, because this runs on every mutation record for
    /// as long as anything is pending — and something usually is: <c>createElement("script")</c>
    /// followed by a feature test such as <c>"async" in s</c>, with the element never inserted, is
    /// ordinary code, and that entry stays pending for the life of the document. A per-record array
    /// copy for it would be pure waste.
    /// <para>
    /// The list can change underneath the walk: <see cref="Prepare"/> removes a script it starts, and
    /// an inline one runs page JavaScript that may create or insert more. So the walk restarts
    /// whenever the length changed — and only then, which is what keeps it terminating: a script that
    /// is connected but not yet runnable (no <c>src</c>, no text) leaves the list untouched and runs
    /// no script, so the index advances past it instead of restarting on it forever.
    /// </para>
    /// </remarks>
    private void SweepPending()
    {
        for (var i = 0; i < _pending.Count; i++)
        {
            var script = _pending[i];
            if (!IsConnectedToWatchedDocument(script))
                continue;

            var countBefore = _pending.Count;
            Prepare(script);
            if (_pending.Count != countBefore)
                i = -1;
        }
    }

    /// <summary>
    /// The HTML "prepare a script" steps this bridge can honour: settle the script's type, then run
    /// its source — an external one on the event loop, an inline one now. A script that is connected
    /// but has neither a <c>src</c> nor any text yet is left pending rather than started, so the
    /// idiom that appends the element first and assigns <c>src</c> afterwards still runs.
    /// </summary>
    private void Prepare(DomElement script)
    {
        if (!IsClassicScriptType(DomBridge.GetAttr(script, "type")))
        {
            // A module, or a non-JavaScript type such as text/template or application/json. Per spec
            // neither executes here; marking it started keeps it out of every later sweep.
            MarkStarted(script);
            return;
        }

        var nonce = DomBridge.GetAttr(script, "nonce");
        var src = DomBridge.GetAttr(script, "src");
        if (!string.IsNullOrWhiteSpace(src))
        {
            MarkStarted(script);
            QueueExternalScript(script, src, nonce);
            return;
        }

        var text = _host.TextContentOf(script);
        if (string.IsNullOrWhiteSpace(text))
            return; // Nothing to run yet — a later src or text still can.

        MarkStarted(script);
        if (_host.Csp is { } csp && !csp.AllowsInlineScript(nonce, text))
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "ScriptInsertionRunner.Prepare",
                "Content Security Policy blocked a script-inserted inline <script>.");
            return;
        }

        var label = $"inserted-{_executedCount++}";
        Record(label, text);
        Run(text, label);
    }

    /// <summary>
    /// Defers an external script's fetch and execution to the event loop. The URL is resolved and
    /// fetched inside the queued task rather than here, so an unreachable host costs its timeout
    /// during the drain — where the budget is — and not in the middle of the <c>appendChild</c> that
    /// queued it.
    /// </summary>
    private void QueueExternalScript(DomElement script, string src, string? nonce)
    {
        if (_host.Csp is { } csp && !csp.AllowsExternalScript(src, _host.PageUrl, nonce))
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "ScriptInsertionRunner.QueueExternalScript",
                $"Content Security Policy blocked a script-inserted <script src='{src}'>.");
            _host.QueueTask(() => _host.FireSimpleEvent(script, "error"));
            return;
        }

        // A data: URI carries its own program text: decoding it is the whole load, the fetch path does
        // not speak the scheme, and it is no use as an error label — it *is* the script, and would put
        // the whole program in every stack frame. Feature detection reaches for this a lot (html5test
        // injects a data: script to see whether one runs at all).
        var isDataUri = src.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
        var label = isDataUri ? $"inserted-data-{_executedCount++}" : src;

        _host.QueueTask(() =>
        {
            string? source;
            try
            {
                source = isDataUri
                    ? ScriptExtractionService.DecodeDataUri(src)
                    : ScriptExtractionService.FetchExternalScript(src, _host.PageUrl);
            }
            catch (Exception ex)
            {
                RenderLogger.LogError(LogCategory.JavaScript, "ScriptInsertionRunner.QueueExternalScript",
                    $"Failed to fetch script-inserted <script src='{src}'>: {ex.Message}", ex);
                source = null;
            }

            if (string.IsNullOrEmpty(source))
            {
                // The load failed, or produced nothing to run. Either way the page's listener has to
                // hear about it: a loader that waits for onload/onerror hangs on silence.
                _host.FireSimpleEvent(script, "error");
                return;
            }

            Record(label, source);
            Run(source, label);
            _host.FireSimpleEvent(script, "load");
        });
    }

    /// <summary>
    /// Evaluates a script's source, containing any error the way the host's own script loop does — a
    /// throwing injected script is a page bug, not a reason to abandon the render.
    /// </summary>
    /// <remarks>
    /// A throw is deliberately not an <c>error</c> event on the element: <c>error</c> there means the
    /// resource failed to load, and this one loaded and then threw. Reporting it as a load failure
    /// would tell a loader that retries on <c>error</c> to fetch the script again.
    /// </remarks>
    private void Run(string source, string label)
    {
        try
        {
            _host.EvaluateScript(source, label);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "ScriptInsertionRunner.Run",
                $"Script-inserted script {label} failed: {ex.Message}", ex);
        }
    }

    /// <summary>Archives the program text under the label its errors are reported against, so a
    /// diagnostics bundle shows the code an injected script actually ran. Off by default.</summary>
    private void Record(string label, string source)
    {
        if (ResourceTrace.IsActive)
            ResourceTrace.RecordBody(ResourceTraceKind.ExecutedScript, $"{_host.PageUrl}#{label}", source, label);
    }

    /// <summary>Sets the "already started" flag: a script leaves the pending list exactly once, and
    /// is never reconsidered after that whatever happens to it in the tree.</summary>
    private void MarkStarted(DomElement script) => _pending.Remove(script);

    private bool IsConnectedToWatchedDocument(DomElement script) =>
        ReferenceEquals(script.GetRootNode(), _host.Document);

    private static bool IsScriptElement(DomElement element) =>
        string.Equals(element.LocalName, "script", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="type"/> selects a classic script. Absent or empty means yes; anything
    /// else must be a JavaScript MIME essence, which rules out both <c>module</c> (not a classic
    /// script) and the types pages use to park data in a <c>&lt;script&gt;</c> the browser must never
    /// execute — <c>text/template</c>, <c>application/json</c>, <c>application/ld+json</c>.
    /// </summary>
    /// <remarks>
    /// The rule itself lives in <see cref="ScriptMimeType"/>, shared with the page-load extraction
    /// path. It was defined here first and only here, which is how that path came to execute every
    /// data block in a document while this one — dynamically inserted scripts — correctly skipped
    /// them.
    /// </remarks>
    private static bool IsClassicScriptType(string? type) => ScriptMimeType.IsClassicScript(type);
}
