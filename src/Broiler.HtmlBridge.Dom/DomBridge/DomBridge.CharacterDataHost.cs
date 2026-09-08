using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit ICharacterDataHost implementation for the CharacterDataBinding feature module (Phase 3):
// the bridge exposes the notifying character-data setter, the text-node factory and the JS-wrapper
// factory via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// Nothing here names an engine type any more. WrapNode is the JSEAL-vocabulary name for the wrapper
// the engine-typed factory answers — the same object, since a JSEAL object handle carries the engine's
// own — and the script-context member is gone with the DOMException plumbing it existed for, which
// IJsCalls.DomError now owns.
public sealed partial class DomBridge : Dom.Features.ICharacterDataHost
{
    void Dom.Features.ICharacterDataHost.SetCharacterData(DomNode node, string? value)
        => SetCharacterData(node, value);

    DomText Dom.Features.ICharacterDataHost.CreateBridgeTextNode(string data)
        => CreateBridgeTextNode(data);

    JsValue Dom.Features.ICharacterDataHost.WrapNode(DomNode node) => WrapNode(node);
}
