using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

// Explicit ILocationHost implementation for the LocationBinding feature module, following the same
// shape as DomBridge.WindowEventTargetHost: the module reaches a named contract rather than a bridge
// private, and the public surface gains only the PendingNavigation the runtime interface declares.
//
// The dispatch below is the engine-typed half of the seam: window dispatch still takes the engine's own
// event object, and Dom.Runtime.JsInterop is a cast rather than a conversion — the listeners see the
// object the module built. Realm is implemented explicitly because DomBridge.Realm is internal, and an
// implicit implementation of a public interface member cannot be satisfied by a non-public property
// (CS0737).
public sealed partial class DomBridge : Dom.Features.ILocationHost
{
    private NavigationRequest? _pendingNavigation;

    /// <inheritdoc />
    public NavigationRequest? TakePendingNavigation()
    {
        var pending = _pendingNavigation;
        _pendingNavigation = null;
        return pending;
    }

    IJsRealm Dom.Features.ILocationHost.Realm => Realm;

    void Dom.Features.ILocationHost.DispatchWindowEvent(JsValue evt)
        => DispatchWindowEvent(Dom.Runtime.JsInterop.ToEngineObject(evt));

    void Dom.Features.ILocationHost.RequestNavigation(NavigationRequest request)
        => RequestNavigation(request);

    /// <summary>
    /// Records where the page asked to go. Nothing is loaded here — see
    /// <see cref="NavigationRequest"/> for why the decision belongs to the host.
    /// </summary>
    /// <remarks>
    /// Last request wins. A browser starts navigating on the first assignment and supersedes it on
    /// the second, landing on the last one the script asked for before it stopped running; keeping
    /// the first instead would follow a target the page had already changed its mind about.
    /// </remarks>
    private void RequestNavigation(NavigationRequest request)
    {
        if (_pendingNavigation is { } superseded)
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.location",
                $"{superseded.Url} superseded by {request.Url} before the document settled; the later request is the one that stands");
        }

        _pendingNavigation = request;
    }
}
