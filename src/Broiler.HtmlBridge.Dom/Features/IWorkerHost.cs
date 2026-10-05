using System;
using System.Threading;
using Broiler.JSeal;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// A resolved worker script: its source, the directory its own relative <c>importScripts</c>
/// specifiers resolve against when it was read from a file, and the URL it came from when it was not.
/// </summary>
internal readonly record struct WorkerScript(string Source, string? BaseDirectory, Uri? Url = null);

/// <summary>
/// What <c>new Worker(url)</c> names, decided on the page's thread: a local file, read as before; a
/// script to fetch over the network on the worker's own thread; a script carried in its URL; or a URL
/// the page may not start a worker from.
/// </summary>
internal abstract record WorkerScriptSource
{
    private WorkerScriptSource()
    {
    }

    /// <summary>A file the host reads (<see cref="IWorkerHost.ResolveWorkerScript"/>), as before.</summary>
    internal sealed record LocalFile : WorkerScriptSource;

    /// <summary>
    /// An <c>http(s)</c> script of the creating document's origin, fetched as a request of
    /// <paramref name="Client"/>, the document that created the worker.
    /// </summary>
    internal sealed record Network(Uri Url, DocumentRequestContext Client) : WorkerScriptSource;

    /// <summary>
    /// A script carried in its URL -- a <c>data:</c> URL, or a <c>blob:</c> URL the page made -- whose
    /// <c>importScripts</c> are requests of <paramref name="Client"/>.
    /// </summary>
    internal sealed record Decoded(Uri Url, string Source, DocumentRequestContext Client) : WorkerScriptSource;

    /// <summary>A URL the page may not start a worker from; the constructor throws <c>SecurityError</c> with this message.</summary>
    internal sealed record Refused(string Message) : WorkerScriptSource;
}

/// <summary>
/// The narrow bridge services <see cref="WorkerBinding"/> needs — the same pattern as
/// <c>IMessagingHost</c>: the module reaches the few bridge operations it requires through named
/// seams rather than holding the bridge.
/// </summary>
internal interface IWorkerHost
{
    /// <summary>
    /// The page's realm (<see langword="null"/> before attach, and again after teardown).
    /// </summary>
    /// <remarks>
    /// Nullable rather than throwing, because telling "attached" from "not attached" is
    /// load-bearing here in a way it is not for the other feature modules: a worker thread can call
    /// back while the bridge is tearing down, and a message that arrives then is dropped rather than
    /// turned into an exception on a thread that has nowhere to report it.
    /// </remarks>
    IJsRealm? Realm { get; }

    /// <summary>
    /// Queues <paramref name="callback"/> on the page's event loop. Called from worker threads, so
    /// the implementation must be safe to call from a thread other than the page's.
    /// </summary>
    void QueueFrameAction(Action callback);

    /// <summary>
    /// Decides what <c>new Worker(<paramref name="specifier"/>)</c> names, for the document whose script
    /// is running. Called on the page's thread, which is where the document and its policy are known.
    /// </summary>
    WorkerScriptSource ResolveWorkerSource(string specifier);

    /// <summary>
    /// Resolves a worker script specifier, or <see langword="null"/> when it cannot be found.
    /// </summary>
    /// <param name="specifier">The URL or path as written by the script.</param>
    /// <param name="baseDirectory">
    /// The directory relative specifiers resolve against. <see langword="null"/> for the
    /// <c>new Worker(url)</c> case, which resolves against the page's own local base path;
    /// set for <c>importScripts</c>, which the HTML spec resolves against the <em>worker's</em>
    /// script URL rather than the page's.
    /// </param>
    /// <remarks>
    /// A seam rather than a <c>File.ReadAllText</c> inside the binding: where a sub-resource may be
    /// read from is the host's policy, not this feature's.
    /// </remarks>
    WorkerScript? ResolveWorkerScript(string specifier, string? baseDirectory);

    /// <summary>
    /// Fetches a worker's script over the network, or decodes a <c>data:</c> one, as a request of
    /// <paramref name="client"/>. Called on the worker's thread. Answers <see langword="null"/>, with
    /// the reason in <paramref name="failure"/>, for a script that cannot be had.
    /// </summary>
    /// <param name="url">The script's URL.</param>
    /// <param name="client">The document whose worker asks.</param>
    /// <param name="imported">
    /// <see langword="false"/> for the worker's own script, which must come from its creator's origin,
    /// redirects included; <see langword="true"/> for one it imports with <c>importScripts</c>, which may
    /// come from anywhere, as a classic <c>&lt;script&gt;</c> may.
    /// </param>
    /// <param name="cancellationToken">The worker's: terminating it abandons the fetch.</param>
    /// <param name="failure">Why the script cannot be had, when it cannot.</param>
    WorkerScript? FetchWorkerScript(Uri url, DocumentRequestContext client, bool imported, CancellationToken cancellationToken, out string? failure);
}
