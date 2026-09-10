using System.Runtime.ExceptionServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>EventTarget.prototype</c>'s three methods, routed to whichever listener store the receiver
/// actually uses — so a node, a document and the window are the <c>EventTarget</c>s they already
/// claim to be.
/// </summary>
/// <remarks>
/// <para>
/// The realm carries its own <c>EventTarget</c>, a JS-engine class whose <c>addEventListener</c>
/// keeps its listeners in fields on the C# instance. A DOM wrapper is an ordinary object and never
/// one of those, so borrowing the prototype method threw: <c>node instanceof EventTarget</c>
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
/// <para>
/// <b>The routing table names no engine type, and the fallback is the part that had to be shown to be
/// expressible.</b> It used to read "JSEAL cannot say: the function that was there before I replaced
/// it, called with exactly these arguments and this receiver", and that is no longer true — a JSEAL
/// call frame exposes the arguments it was supplied as a span, and <c>Invoke(function, thisValue,
/// arguments)</c> takes one, so handing the call back to the engine's own function is a single line.
/// The other half was the callees: a routed method can only be realm-minted if every body it reaches
/// takes a JSEAL frame, so the three window operations and the three node ones had to migrate in the
/// same change as this file. They did.
/// </para>
/// <para>
/// <b>What did not move is the receiver resolution, and it is a table rather than a call frame.</b>
/// The node-wrapper registry and the window wrapper field are keyed on the engine's own object, and
/// both live in files this group does not own — so the receiver is unwrapped to ask them, which is a
/// cast over the object the handle already carries and not a conversion. The same pin is why
/// <c>DomBridge/JsObjects.cs</c> and <c>JsObjects.NonElementNodes.cs</c> still install per-wrapper
/// copies for a wrapper minted before the realm carried <c>EventTarget</c> (guarded by
/// <see cref="_eventTargetRoutingReady"/>), and why <c>Dom.Features.EventTargetBinding</c> keeps an
/// engine-framed twin of each of the three below for them.
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
    /// <remarks>
    /// The three functions the engine installed are read out first and captured, because the very
    /// next thing this does is overwrite them; each routed method keeps its own so that a receiver
    /// this bridge does not own still reaches the one that was there.
    /// </remarks>
    internal void RegisterEventTargetRouting()
    {
        var proto = PrototypeHandleOfInterface("EventTarget");
        if (!proto.IsObject)
            return;

        var realm = Realm;
        var engineAdd = realm.GetProperty(proto, "addEventListener");
        var engineRemove = realm.GetProperty(proto, "removeEventListener");
        var engineDispatch = realm.GetProperty(proto, "dispatchEvent");

        RouteEventTargetMethod(proto, "addEventListener", 2, engineAdd,
            (in JsCall call, DomNode node) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in call),
            (in JsCall call) => Dom.Features.WindowEventTargetBinding.AddEventListener(this, in call));

        RouteEventTargetMethod(proto, "removeEventListener", 2, engineRemove,
            (in JsCall call, DomNode node) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in call),
            (in JsCall call) => Dom.Features.WindowEventTargetBinding.RemoveEventListener(this, in call));

        RouteEventTargetMethod(proto, "dispatchEvent", 1, engineDispatch,
            (in JsCall call, DomNode node) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in call),
            (in JsCall call) => Dom.Features.WindowEventTargetBinding.DispatchEvent(this, in call));

        _eventTargetRoutingReady = true;
    }

    /// <summary>A prototype method's body once the receiver has been resolved to a DOM node.</summary>
    private delegate JsValue NodeEventTargetOperation(in JsCall call, DomNode node);

    /// <summary>A prototype method's body for the window receiver, which is not a node.</summary>
    private delegate JsValue WindowEventTargetOperation(in JsCall call);

    /// <summary>
    /// Installs one routed method on <c>EventTarget.prototype</c>, keeping the engine's own as the
    /// fallback for a receiver this bridge does not own.
    /// </summary>
    /// <remarks>
    /// The property is enumerable, configurable and writable — the attributes the engine's own three
    /// carried and the ones Web IDL asks for on a prototype — so only the <em>body</em> of each
    /// changes, not its descriptor. The declared <c>length</c> is Web IDL's, measured against
    /// Chromium: 2, 2, 1.
    /// </remarks>
    private void RouteEventTargetMethod(JsValue proto, string name, int length, JsValue engineMethod,
        NodeEventTargetOperation onNode, WindowEventTargetOperation onWindow)
    {
        Realm.DefineValue(proto, name, Realm.NewMethod(name, (in call) =>
        {
            // The two tables below are keyed on the engine's own object, which an object handle
            // carries; a non-object receiver is in neither without asking.
            if (call.This.IsObject)
            {
                var receiver = Dom.Runtime.JsInterop.ToEngineObject(call.This);

                if (_windowJSObject is { } window && ReferenceEquals(receiver, window))
                    return onWindow(in call);

                if (_jsObjects.TryGetNode(call.This, out var node))
                    return onNode(in call, node);
            }

            return InvokeEngineEventTargetMethod(engineMethod, name, in call);
        }, length));
    }

    /// <summary>
    /// Hands the call back to the function the engine installed, for a receiver this bridge does not
    /// own — <c>new EventTarget()</c>, an <c>AbortSignal</c>, anything else engine-side. Its own
    /// receiver check is what still rejects a receiver that is neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver and every supplied argument go through unchanged: an argument the page did not
    /// pass is not invented, so the engine's own arity checks see the call the page actually made.
    /// </para>
    /// <para>
    /// <b>The rethrow is not tidiness.</b> A realm's <c>Invoke</c> reports what the callee threw as a
    /// JSEAL exception carrying the thrown value, and JSEAL has no "throw this value again" operation
    /// — only "throw a new error of this kind". Letting that wrapper escape into the engine would
    /// hand the page a freshly synthesised <c>Error</c> built from a CLR exception in place of the
    /// engine's own, so <c>EventTarget.prototype.addEventListener.call({}, …)</c> would stop being
    /// catchable as a <c>TypeError</c>. Rethrowing the inner exception with its stack intact keeps
    /// the object the page catches the object the engine threw.
    /// </para>
    /// </remarks>
    private static JsValue InvokeEngineEventTargetMethod(JsValue engineMethod, string name, in JsCall call)
    {
        if (!engineMethod.IsFunction)
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                $"Failed to execute '{name}' on 'EventTarget': Illegal invocation");

        try
        {
            return call.Realm.Invoke(engineMethod, call.This, call.Arguments);
        }
        catch (JsEngineException wrapped) when (wrapped.InnerException is { } thrownByEngine)
        {
            ExceptionDispatchInfo.Throw(thrownByEngine);
            throw; // Unreachable: ExceptionDispatchInfo.Throw never returns.
        }
    }
}
