using Broiler.Layout;

namespace Broiler.HtmlBridge;

/// <summary>
/// Dependencies owned by one <see cref="DomBridge"/> session.
/// </summary>
public sealed class DomBridgeSessionOptions
{
    /// <summary>
    /// Creates the document-scoped layout view lazily. The bridge disposes the
    /// resulting view with the session.
    /// </summary>
    public Func<ILayoutView>? LayoutViewFactory { get; init; }
}
