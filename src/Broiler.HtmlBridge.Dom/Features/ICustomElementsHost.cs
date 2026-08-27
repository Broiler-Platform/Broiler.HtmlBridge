using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The bridge services <see cref="CustomElementsBinding"/> consumes. Two kinds: the DOM half
/// (mint an element, ask whether one is in the tree, find a node behind a wrapper) and the engine
/// half — calling a JavaScript constructor or function, and making promises for
/// <c>whenDefined</c>. The second kind is unusual for a feature contract and is the point: a custom
/// element definition is page code, so the registry's job is largely to call back into it at the
/// right moments.
/// </summary>
internal interface ICustomElementsHost
{
    JSContext JsContext { get; }

    /// <summary>Every element in the document, in tree order — the set an upgrade sweeps.</summary>
    IReadOnlyList<DomElement> Elements { get; }

    JSObject ToJSObject(DomNode node);

    /// <summary>The wrapper already minted for <paramref name="element"/>, if any. A reaction is
    /// only ever dispatched to an element a page has seen, so this never mints one.</summary>
    bool TryGetWrapper(DomElement element, out JSObject wrapper);

    /// <summary>The node behind a wrapper, for <c>customElements.upgrade(root)</c>.</summary>
    DomNode? NodeFor(JSObject wrapper);

    DomElement CreateBridgeElement(string tagName);

    /// <summary>Whether the element is in the document tree, which is what decides
    /// <c>connectedCallback</c>.</summary>
    bool IsConnected(DomElement element);

    /// <summary>The element's form owner, or <see langword="null"/> — what
    /// <c>formAssociatedCallback</c> reports.</summary>
    DomElement? FormOwnerOf(DomElement element);

    /// <summary>Whether the element is disabled, by its own attribute or an ancestor
    /// <c>&lt;fieldset&gt;</c>'s — what <c>formDisabledCallback</c> reports.</summary>
    bool IsFormControlDisabled(DomElement element);

    /// <summary><c>new constructor()</c>, returning the object it produced.</summary>
    JSObject? Construct(JSObject constructor);

    /// <summary><c>function.call(thisValue, …arguments)</c>.</summary>
    void Call(JSObject function, JSValue thisValue, JSValue[] arguments);

    /// <summary>A promise already resolved with <paramref name="value"/>.</summary>
    JSValue ResolvedPromise(JSValue value);

    /// <summary>A promise already rejected with <paramref name="message"/>.</summary>
    JSValue RejectedPromise(string message);

    /// <summary>A pending promise and the function that resolves it.</summary>
    (JSValue Promise, JSObject Resolver) PendingPromise();

    /// <summary>Calls a resolver produced by <see cref="PendingPromise"/>.</summary>
    void Resolve(JSObject resolver, JSValue value);
}
