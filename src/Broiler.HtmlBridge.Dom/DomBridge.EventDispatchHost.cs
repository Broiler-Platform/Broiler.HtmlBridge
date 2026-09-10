using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IEventDispatchHost"/>, the narrow contract
/// the extracted <see cref="Broiler.HtmlBridge.Dom.Features.EventDispatchBinding"/> feature module
/// consumes (HtmlBridge complexity-reduction roadmap Phase 3, P3.3). Explicit interface members, so
/// these seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// This is the half-migrated seam for the dispatch slice: the module speaks JSEAL, the wrapper cache
/// and the two global wrappers the bridge holds are still engine objects, and
/// <see cref="JsInterop"/> is the cast between them — a cast and not a conversion, so
/// <c>event.target === el</c> is the same question it always was.
/// </remarks>
public sealed partial class DomBridge : IEventDispatchHost
{
    IJsRealm IEventDispatchHost.Realm => Realm;

    JsValue IEventDispatchHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode IEventDispatchHost.DocumentNode => _document;

    JsValue IEventDispatchHost.DocumentWrapper =>
        _documentJSObject is null ? JsValue.Missing : JsInterop.FromEngineObject(_documentJSObject);

    JsValue IEventDispatchHost.WindowWrapper =>
        _windowJSObject is null ? JsValue.Missing : JsInterop.FromEngineObject(_windowJSObject);

    Dictionary<string, List<EventListenerRegistration>> IEventDispatchHost.GetEventListeners(DomNode node) =>
        GetEventListeners(node);

    // The inline on* store is a dictionary of engine values on InlineStyleRuntimeState, a file this
    // round does not own, so the callability test the dispatch loop used to make stays here on the
    // engine side and the module is handed a handle or nothing. Anything that is not callable answers
    // Missing, exactly as the failed type-pattern did.
    JsValue IEventDispatchHost.InlineEventHandler(DomNode node, string eventType) =>
        GetInlineEventHandlers(node).TryGetValue(eventType, out var handler) && handler is JSFunction inlineFunction
            ? JsInterop.FromEngineObject(inlineFunction)
            : JsValue.Missing;
}
