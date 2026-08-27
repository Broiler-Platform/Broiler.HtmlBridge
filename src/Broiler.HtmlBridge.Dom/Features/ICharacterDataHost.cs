using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="CharacterDataBinding"/> needs from the bridge: the
/// notifying character-data setter (mutation-observer aware), the text-node factory (for
/// <c>splitText</c>), the and the JS-wrapper factory. Read-side text access,
/// node-type tests and the neutral tree helpers are the bridge's <c>internal static</c> helpers,
/// called directly.
/// </summary>
internal interface ICharacterDataHost
{
    void SetCharacterData(DomNode node, string? value);
    DomText CreateBridgeTextNode(string data);
    JSObject ToJSObject(DomNode node);

    /// <summary>
    /// The attached JS context, used only to construct the <c>DOMException</c> an out-of-bounds
    /// offset must throw — the same way <see cref="INodeMutationHost.JsContext"/> is used. Null
    /// before the bridge is attached, in which case the binding falls back to a plain error.
    /// </summary>
    JSContext? JsContext { get; }
}
