using System;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// A resolved worker script: its source, and the directory its own relative <c>importScripts</c>
/// specifiers resolve against.
/// </summary>
internal readonly record struct WorkerScript(string Source, string? BaseDirectory);

/// <summary>
/// The narrow bridge services <see cref="WorkerBinding"/> needs — the same pattern as
/// <c>IMessagingHost</c>: the module reaches the few bridge operations it requires through named
/// seams rather than holding the bridge.
/// </summary>
internal interface IWorkerHost
{
    /// <summary>The page's JS execution context (<c>null</c> before attach).</summary>
    JSContext? JsContext { get; }

    /// <summary>
    /// Queues <paramref name="callback"/> on the page's event loop. Called from worker threads, so
    /// the implementation must be safe to call from a thread other than the page's.
    /// </summary>
    void QueueFrameAction(Action callback);

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
}
