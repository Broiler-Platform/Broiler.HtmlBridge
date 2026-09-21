using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The bridge's single lifetime/composition seam. A <see cref="DomBridge"/> owns one registry and
/// drains it on <see cref="System.IDisposable.Dispose"/>, so any per-session resource — starting
/// with the injected <see cref="Broiler.Layout.ILayoutView"/> — has exactly one place that
/// releases it.
/// </summary>
/// <remarks>
/// Teardowns run in last-in-first-out order (later registrations depend on earlier ones), and a
/// failing teardown is logged and skipped so one leak cannot strand the rest. Not thread-safe:
/// registration and disposal are expected on the owning document thread.
/// </remarks>
internal sealed class DomBridgeDisposalRegistry : IDisposable
{
    private readonly List<IDisposable> _disposables = [];
    private bool _disposed;

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
}
