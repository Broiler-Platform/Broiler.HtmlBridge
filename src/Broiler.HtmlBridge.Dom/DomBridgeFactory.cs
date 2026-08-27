using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge;

public sealed class DomBridgeFactory : IDomBridgeRuntimeFactory
{
    private readonly DomBridgeSessionOptions? _sessionOptions;

    public DomBridgeFactory()
    {
    }

    public DomBridgeFactory(DomBridgeSessionOptions sessionOptions) =>
        _sessionOptions = sessionOptions ?? throw new ArgumentNullException(nameof(sessionOptions));

    public IDomBridgeRuntime Create() => new DomBridge(_sessionOptions);
}
