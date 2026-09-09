using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentEventTargetBinding"/> needs from the bridge: the
/// document node (the EventTarget), its per-type listener store, the registration operations over
/// that store, and the shared event-dispatch algorithm.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, but two of its members exist only because one is still there
/// behind it. A registration is an <c>EventListenerRegistration</c> whose listener field is a
/// Broiler.JS value (<c>DomBridge/RuntimeStates.cs</c>), and the registration semantics live in the
/// engine-typed <c>EventListenerBinding</c> — neither file is this slice's — so
/// <see cref="AddListener"/> and <see cref="RemoveListener"/> are where the handle becomes an engine
/// value. Putting them on the contract rather than in the module is what lets the module speak JSEAL
/// while that record stands; they collapse into <c>EventListenerBinding</c> when it moves.
/// </para>
/// <para>
/// <see cref="DispatchEvent"/> answers the "not cancelled" boolean <c>dispatchEvent</c> is specified
/// to return, which is the whole of what the dispatch produces for a caller.
/// </para>
/// </remarks>
internal interface IDocumentEventTargetHost
{
    DomNode DocumentNode { get; }

    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);

    /// <inheritdoc cref="EventListenerBinding.AddListener" />
    void AddListener(List<EventListenerRegistration> listeners, JsValue listener, JsValue options);

    /// <inheritdoc cref="EventListenerBinding.RemoveListener" />
    void RemoveListener(List<EventListenerRegistration>? listeners, JsValue listener, JsValue options);

    JsValue DispatchEvent(DomNode target, JsValue evt);
}
