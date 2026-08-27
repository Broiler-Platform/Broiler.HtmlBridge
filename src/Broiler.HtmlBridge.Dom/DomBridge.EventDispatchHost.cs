using Broiler.JavaScript.Runtime;
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
public sealed partial class DomBridge : IEventDispatchHost
{
    JSObject IEventDispatchHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomNode IEventDispatchHost.DocumentNode => _document;

    JSObject? IEventDispatchHost.DocumentJSObject => _documentJSObject;

    JSObject? IEventDispatchHost.WindowJSObject => _windowJSObject;

    Dictionary<string, List<EventListenerRegistration>> IEventDispatchHost.GetEventListeners(DomNode node) =>
        GetEventListeners(node);

    Dictionary<string, JSValue> IEventDispatchHost.GetInlineEventHandlers(DomNode node) =>
        GetInlineEventHandlers(node);
}
