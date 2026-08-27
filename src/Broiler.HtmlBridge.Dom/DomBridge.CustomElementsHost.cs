using Broiler.HtmlBridge.Dom.Features;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ICustomElementsHost"/> — the DOM and
/// engine services the custom element registry needs. Explicit interface members, so calling into
/// page code does not become part of the public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : ICustomElementsHost
{
    JSContext ICustomElementsHost.JsContext => _jsContext!;

    IReadOnlyList<DomElement> ICustomElementsHost.Elements => Elements;

    JSObject ICustomElementsHost.ToJSObject(DomNode node) => ToJSObject(node);

    bool ICustomElementsHost.TryGetWrapper(DomElement element, out JSObject wrapper) =>
        _jsObjects.TryGet(element, out wrapper);

    DomNode? ICustomElementsHost.NodeFor(JSObject wrapper) => FindDomNodeByJSObject(wrapper);

    DomElement ICustomElementsHost.CreateBridgeElement(string tagName) => CreateBridgeElement(tagName);

    /// <summary>
    /// Whether the element is in a document tree. The connected test walks to the root and asks
    /// whether it is a document, rather than testing for a parent: a subtree assembled off-tree
    /// has parents all the way up and is still not connected, and <c>connectedCallback</c> must not
    /// run for it.
    /// </summary>
    /// <remarks>
    /// Any document, not only the page's. A node adopted into a frame's or a
    /// <c>createHTMLDocument</c>'s tree is connected there, and a browser runs its
    /// <c>connectedCallback</c> — measured, the cross-document <c>appendChild</c> shape reports
    /// connected, disconnected, adopted, connected.
    /// </remarks>
    bool ICustomElementsHost.IsConnected(DomElement element)
    {
        for (DomNode? node = element; node is not null; node = node.ParentNode)
        {
            if (node is DomDocument)
                return true;
        }

        return false;
    }

    DomElement? ICustomElementsHost.FormOwnerOf(DomElement element) =>
        Dom.Features.FormAssociationBinding.FormOwnerOf(this, element);

    bool ICustomElementsHost.IsFormControlDisabled(DomElement element) => IsFormControlDisabled(element);

    JSObject? ICustomElementsHost.Construct(JSObject constructor) =>
        constructor is JavaScript.BuiltIns.Function.JSFunction function
            ? function.CreateInstance(new Arguments(function)) as JSObject
            : null;

    void ICustomElementsHost.Call(JSObject function, JSValue thisValue, JSValue[] arguments)
    {
        var call = arguments.Length switch
        {
            0 => new Arguments(thisValue),
            1 => new Arguments(thisValue, arguments[0]),
            2 => new Arguments(thisValue, arguments[0], arguments[1]),
            _ => new Arguments(thisValue, arguments[0], arguments[1], arguments[2]),
        };
        function.InvokeFunction(call);
    }

    JSValue ICustomElementsHost.ResolvedPromise(JSValue value) =>
        new JSPromise((resolve, _) => resolve(value));

    JSValue ICustomElementsHost.RejectedPromise(string message) =>
        new JSPromise((_, reject) => reject(new JSString(message)));

    /// <summary>
    /// A promise plus its resolver. The executor runs synchronously, so the resolve delegate is
    /// captured out of it and wrapped as a function object the registry can hold onto until the
    /// definition it is waiting for arrives.
    /// </summary>
    (JSValue Promise, JSObject Resolver) ICustomElementsHost.PendingPromise()
    {
        Action<JSValue>? captured = null;
        var promise = new JSPromise((resolve, _) => captured = resolve);
        var resolver = new DomFunction(
            (in Arguments a) =>
            {
                captured?.Invoke(a.Length > 0 ? a[0] : JSUndefined.Value);
                return JSUndefined.Value;
            },
            "resolve",
            1);
        return (promise, resolver);
    }

    void ICustomElementsHost.Resolve(JSObject resolver, JSValue value) =>
        resolver.InvokeFunction(new Arguments(JSUndefined.Value, value));
}
