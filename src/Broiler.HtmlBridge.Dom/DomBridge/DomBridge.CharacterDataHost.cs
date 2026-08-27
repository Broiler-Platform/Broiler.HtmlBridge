using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

// Explicit ICharacterDataHost implementation for the CharacterDataBinding feature module (Phase 3):
// the bridge exposes the notifying character-data setter, the text-node factory, the JS-wrapper
// factory and wrapper-cache invalidation via explicit interface members, so the module never reaches
// an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.ICharacterDataHost
{
    void Dom.Features.ICharacterDataHost.SetCharacterData(DomNode node, string? value)
        => SetCharacterData(node, value);

    DomText Dom.Features.ICharacterDataHost.CreateBridgeTextNode(string data)
        => CreateBridgeTextNode(data);

    JSObject Dom.Features.ICharacterDataHost.ToJSObject(DomNode node) => ToJSObject(node);


    JSContext? Dom.Features.ICharacterDataHost.JsContext => _jsContext;
}
