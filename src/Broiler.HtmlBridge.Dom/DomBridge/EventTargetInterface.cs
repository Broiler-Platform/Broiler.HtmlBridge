using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>EventTarget.prototype</c>'s three methods, routed to whichever listener store the receiver
/// actually uses — so a node, a document and the window are the <c>EventTarget</c>s they already
/// claim to be.
/// </summary>
/// <remarks>
/// <para>
/// The realm carries its own <c>EventTarget</c>, a JS-engine class whose <c>addEventListener</c>
/// keeps its listeners in fields on the C# instance. A DOM wrapper is a plain <c>JSObject</c> and
/// never one of those, so borrowing the prototype method threw: <c>node instanceof EventTarget</c>
/// answered <see langword="true"/> — the interface graph says so — while
/// <c>EventTarget.prototype.addEventListener.call(node, 'x', fn)</c> was a
/// <c>TypeError: Failed to convert this to EventTarget</c>. The bridge's own
/// <c>addEventListener</c> was a separate function installed as an own property of every wrapper, so
/// <c>node.addEventListener === EventTarget.prototype.addEventListener</c> was
/// <see langword="false"/> where a browser says <see langword="true"/>.
/// </para>
/// <para>
/// That is not only a shape difference. Borrowing the prototype method is what a library does when
/// it cannot trust the instance's own — <c>EventTarget.prototype.addEventListener.call(el, …)</c> is
/// ordinary defensive code — and here it threw rather than registering, so the listener was silently
/// never added.
/// </para>
/// <para>
/// <b>Routing by receiver, with the engine's own behaviour preserved.</b> The replacement resolves
/// the receiver in three steps: the window object, then any registered node wrapper — which covers
/// elements, text, comments, fragments <em>and</em> the document, since the document's wrapper is
/// registered as its node's and its listener store is the same per-node one — and otherwise
/// delegates to the function the engine installed. So <c>new EventTarget()</c>, an
/// <c>AbortSignal</c> and every other engine-side target keep working exactly as before, and only a
/// receiver this bridge owns is taken over.
/// </para>
/// <para>
/// With one function serving every receiver, the per-wrapper copies are redundant and are gone: a
/// text or comment node now carries no own properties at all, and
/// <c>node.addEventListener === EventTarget.prototype.addEventListener</c> holds. The
/// <c>length</c> of each is Web IDL's — <c>2</c>, <c>2</c>, <c>1</c>, measured against Chromium —
/// where the copies advertised <c>3</c>, <c>3</c>, <c>1</c>.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether <c>EventTarget.prototype</c> carries the routed methods, which is what lets a wrapper
    /// stop installing its own. A wrapper minted before the realm is up still installs them.
    /// </summary>
    private bool _eventTargetRoutingReady;

    /// <summary>
    /// Replaces <c>EventTarget.prototype</c>'s <c>addEventListener</c>, <c>removeEventListener</c>
    /// and <c>dispatchEvent</c> with versions that route by receiver. A no-op when the realm has no
    /// <c>EventTarget</c>.
    /// </summary>
    internal void RegisterEventTargetRouting()
    {
        if (PrototypeOfInterface("EventTarget") is not { } proto)
            return;

        var engineAdd = proto[(KeyString)"addEventListener"] as JSFunction;
        var engineRemove = proto[(KeyString)"removeEventListener"] as JSFunction;
        var engineDispatch = proto[(KeyString)"dispatchEvent"] as JSFunction;

        RouteEventTargetMethod(proto, "addEventListener", 2, engineAdd,
            (in Arguments a, DomNode node) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in a),
            (in Arguments a) => Dom.Features.WindowEventTargetBinding.AddEventListener(this, in a));

        RouteEventTargetMethod(proto, "removeEventListener", 2, engineRemove,
            (in Arguments a, DomNode node) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in a),
            (in Arguments a) => Dom.Features.WindowEventTargetBinding.RemoveEventListener(this, in a));

        RouteEventTargetMethod(proto, "dispatchEvent", 1, engineDispatch,
            (in Arguments a, DomNode node) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in a),
            (in Arguments a) => Dom.Features.WindowEventTargetBinding.DispatchEvent(this, in a));

        _eventTargetRoutingReady = true;
    }

    /// <summary>A prototype method's body once the receiver has been resolved to a DOM node.</summary>
    private delegate JSValue NodeEventTargetOperation(in Arguments a, DomNode node);

    /// <summary>A prototype method's body for the window receiver, which is not a node.</summary>
    private delegate JSValue WindowEventTargetOperation(in Arguments a);

    /// <summary>
    /// Installs one routed method on <c>EventTarget.prototype</c>, keeping the engine's own as the
    /// fallback for a receiver this bridge does not own.
    /// </summary>
    private void RouteEventTargetMethod(JSObject proto, string name, int length, JSFunction? engineMethod,
        NodeEventTargetOperation onNode, WindowEventTargetOperation onWindow)
    {
        proto.FastAddValue(name, new DomFunction((in Arguments a) =>
        {
            if (a.This is JSObject receiver)
            {
                if (_windowJSObject is { } window && ReferenceEquals(receiver, window))
                    return onWindow(in a);

                if (_jsObjects.TryGetNode(receiver, out var node))
                    return onNode(in a, node);
            }

            return InvokeEngineEventTargetMethod(engineMethod, name, in a);
        }, name, length), JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// Hands the call back to the function the engine installed, for a receiver this bridge does not
    /// own — <c>new EventTarget()</c>, an <c>AbortSignal</c>, anything else engine-side. Its own
    /// receiver check is what still rejects a receiver that is neither.
    /// </summary>
    private static JSValue InvokeEngineEventTargetMethod(JSFunction? engineMethod, string name, in Arguments a)
    {
        if (engineMethod is null)
            return JSException.ThrowTypeError<JSValue>(
                $"Failed to execute '{name}' on 'EventTarget': Illegal invocation");

        var forwarded = new JSValue[a.Length];
        for (var i = 0; i < a.Length; i++)
            forwarded[i] = a[i];

        return engineMethod.InvokeFunction(new Arguments(a.This ?? JSUndefined.Value, forwarded));
    }
}
