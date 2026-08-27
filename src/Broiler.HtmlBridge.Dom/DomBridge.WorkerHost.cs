using System;
using System.IO;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Logging;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IWorkerHost"/> — the narrow contract the
/// <see cref="Broiler.HtmlBridge.Dom.Features.WorkerBinding"/> feature module consumes, in the same
/// shape as <c>DomBridge.MessagingHost.cs</c>. Explicit interface implementations, so the seams do
/// not widen the public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IWorkerHost
{
    JSContext? IWorkerHost.JsContext => _jsContext;

    /// <summary>
    /// Queued on the page's <c>BrowserEventLoop</c>, whose frame-action store is a
    /// <c>ConcurrentDictionary</c> — so this is safe to call from a worker thread, which is the
    /// whole point of the seam. Deliberately does <em>not</em> go through the disposed guard: a
    /// worker thread can be mid-post while the bridge tears down, and throwing
    /// <see cref="ObjectDisposedException"/> onto that thread would surface as a worker crash rather
    /// than the no-op it should be.
    /// </summary>
    void IWorkerHost.QueueFrameAction(Action callback)
    {
        if (_disposed)
            return;

        try
        {
            QueueFrameAction(callback);
        }
        catch (ObjectDisposedException)
        {
            // Raced with disposal; the message simply does not arrive, which is correct.
        }
    }

    /// <summary>
    /// Resolves a worker script against <paramref name="baseDirectory"/> when one is given (the
    /// <c>importScripts</c> case), otherwise against the page's local base path, then as given.
    /// </summary>
    /// <remarks>
    /// Only <c>file</c>-shaped specifiers are resolved. A worker script that would have to be
    /// fetched over the network returns <see langword="null"/> here — surfacing as an <c>error</c>
    /// event for <c>new Worker()</c>, and as a <c>NetworkError</c> for <c>importScripts</c> — rather
    /// than blocking a render on a request this host has no policy for.
    /// </remarks>
    WorkerScript? IWorkerHost.ResolveWorkerScript(string specifier, string? baseDirectory)
    {
        try
        {
            if (Uri.TryCreate(specifier, UriKind.Absolute, out var absolute))
            {
                if (!absolute.IsFile)
                    return null;

                return Read(absolute.LocalPath);
            }

            // The worker's own directory first when there is one: importScripts resolves against the
            // worker's script URL, not the document's.
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                var relative = Path.Combine(baseDirectory, specifier);
                if (File.Exists(relative))
                    return Read(relative);
            }

            var pageBase = _resources.LocalBasePath;
            if (!string.IsNullOrEmpty(pageBase))
            {
                var candidate = Path.Combine(pageBase, specifier);
                if (File.Exists(candidate))
                    return Read(candidate);
            }

            return File.Exists(specifier) ? Read(specifier) : null;
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ResolveWorkerScript",
                $"Could not read worker script '{specifier}': {ex.Message}", ex);
            return null;
        }

        static WorkerScript? Read(string path) =>
            File.Exists(path)
                ? new WorkerScript(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)))
                : null;
    }
}
