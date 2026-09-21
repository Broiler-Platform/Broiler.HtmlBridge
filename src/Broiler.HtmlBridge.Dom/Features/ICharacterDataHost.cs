using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="CharacterDataBinding"/> needs from the bridge: the
/// notifying character-data setter (mutation-observer aware), the text-node factory (for
/// <c>splitText</c>), and the JS-wrapper factory. Read-side text access,
/// node-type tests and the neutral tree helpers are the bridge's <c>internal static</c> helpers,
/// called directly.
/// </summary>
/// <remarks>
/// The contract names no engine type, and it carries no script context: that seam existed for one
/// purpose — constructing the <c>DOMException</c> an out-of-bounds offset must throw — and
/// <see cref="IJsCalls.DomError"/> owns that now, reached from the call frame each operation already
/// has.
/// </remarks>
internal interface ICharacterDataHost : INodeWrapperHost, ITextNodeFactoryHost
{
    void SetCharacterData(DomNode node, string? value);
}
