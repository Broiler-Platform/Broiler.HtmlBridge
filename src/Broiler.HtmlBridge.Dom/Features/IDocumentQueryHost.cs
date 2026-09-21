using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentQueryBinding"/> needs from the bridge: the document
/// root, the document-order element list, the JS-wrapper factory, selector validation, and the two
/// DOM collection factories a query answers with. Sub-tree search is the bridge's neutral
/// <c>internal static</c> helper, called directly, so it is not on this contract.
/// </summary>
/// <remarks>
/// The three members below the DOM ones ask for what the module actually wants — a <c>SyntaxError</c>
/// raised, a collection built — by name, rather than taking the bridge's script context so they can
/// pass it straight back. That is what lets this contract, and the module over it, name no engine
/// type at all. The inherited <see cref="ISelectorMatchHost.MatchesSelector"/> is a host member
/// rather than a static helper for a reason of its own: it reads the per-bridge <c>:checked</c> state.
/// </remarks>
internal interface IDocumentQueryHost : IDocumentElementHost, IElementsHost, IJsObjectHost, ISelectorMatchHost
{
    /// <summary>
    /// A <c>NodeList</c> over what <paramref name="contents"/> answers. The function is re-asked on
    /// every read, so passing one that recomputes makes the list live and passing one that closes over
    /// a fixed list makes it the snapshot <c>querySelectorAll</c> is specified to be.
    /// </summary>
    JsValue NodeList(Func<List<JsValue>> contents);

    /// <summary>
    /// An <c>HTMLCollection</c> over what <paramref name="contents"/> answers — always live, as every
    /// collection typed <c>HTMLCollection</c> is. <paramref name="namedLookup"/> answers the named
    /// getter, returning the matching wrapper or <see langword="null"/> for a name it does not serve.
    /// </summary>
    JsValue HtmlCollection(Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup);
}
