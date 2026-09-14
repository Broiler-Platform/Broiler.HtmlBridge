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
/// The module speaks JSEAL, and so does every member here. The wrapper cache answers handles,
/// and the document and window wrappers are the bridge's roots, which are the handles the realm
/// minted, so all three forward without converting and <c>event.target === el</c> is the same
/// question it always was. (This used to call all three engine objects, with a cast between them.)
/// <c>InlineEventHandler</c> below reads a map of handles too; this called it the one crossing left.
/// </remarks>
public sealed partial class DomBridge : IEventDispatchHost
{
    IJsRealm IEventDispatchHost.Realm => Realm;

    JsValue IEventDispatchHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode IEventDispatchHost.DocumentNode => _document;

    JsValue IEventDispatchHost.DocumentWrapper => DocumentHandle;

    JsValue IEventDispatchHost.WindowWrapper => WindowHandle;

    Dictionary<string, List<EventListenerRegistration>> IEventDispatchHost.GetEventListeners(DomNode node) =>
        GetEventListeners(node);

    // The inline on* store holds handles, so the callability test is a read of the stored handle's kind
    // and the module is handed that handle, not a second one minted over the same object. This comment
    // used to say the test had to stay on the engine side because the store was a dictionary of engine
    // values in a file this round did not own. The store's only other readers and writers were
    // DomBridge/Events.cs and DomBridge/DomBridge.EventHandlerReflectorHost.cs, and all three moved
    // together. Anything that is not callable still answers Missing, as the failed type pattern did —
    // and nothing stored can fail it, because both writers (CompileInlineEventAttribute, and the
    // reflector's setter through its one caller) store only a handle that has answered IsFunction.
    JsValue IEventDispatchHost.InlineEventHandler(DomNode node, string eventType) =>
        GetInlineEventHandlers(node).TryGetValue(eventType, out var handler) && handler.IsFunction
            ? handler
            : JsValue.Missing;
}
