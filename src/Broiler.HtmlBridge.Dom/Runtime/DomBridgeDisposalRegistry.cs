using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The bridge's single lifetime/composition seam (HtmlBridge complexity-reduction roadmap
/// Phase 2, P2.1). A <see cref="DomBridge"/> owns one registry and drains it on
/// <see cref="System.IDisposable.Dispose"/>, so any per-session resource — starting with the
/// injected <see cref="Broiler.Layout.ILayoutView"/> — has exactly one place that releases it.
/// Phase 2's later PRs grow this into <c>BrowserDocumentSession</c>; keeping it minimal now
/// avoids moving document/URL/viewport fields before the disposal contract is characterized.
/// </summary>
/// <remarks>
/// Teardowns run in last-in-first-out order (later registrations depend on earlier ones), and a
/// failing teardown is logged and skipped so one leak cannot strand the rest. Not thread-safe:
/// registration and disposal are expected on the owning document thread (Phase 2's P2.4 defines
/// the single-owner threading model).
/// </remarks>
internal sealed class DomBridgeDisposalRegistry : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private bool _disposed;

    /// <summary>Whether <see cref="Dispose"/> has already run.</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// Registers a resource to dispose when the owning bridge is disposed. If the registry is
    /// already disposed, <paramref name="resource"/> is disposed immediately so nothing leaks.
    /// </summary>
    public void Add(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_disposed)
        {
            SafeDispose(resource);
            return;
        }

        _disposables.Add(resource);
    }

    /// <summary>Registers a teardown <see cref="Action"/> (wrapped as an <see cref="IDisposable"/>).</summary>
    public void Add(Action teardown)
    {
        ArgumentNullException.ThrowIfNull(teardown);
        Add(new ActionDisposable(teardown));
    }

    /// <summary>
    /// Disposes every registered resource in reverse registration order. Idempotent: a second
    /// call is a no-op. Per-item failures are logged and do not abort the drain.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        for (var i = _disposables.Count - 1; i >= 0; i--)
            SafeDispose(_disposables[i]);
        _disposables.Clear();
    }

    private static void SafeDispose(IDisposable resource)
    {
        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.HtmlRenderer, "DomBridgeDisposalRegistry.Dispose",
                $"A registered teardown threw during disposal: {ex.Message}", ex);
        }
    }

    private sealed class ActionDisposable(Action teardown) : IDisposable
    {
        public void Dispose() => teardown();
    }
}
