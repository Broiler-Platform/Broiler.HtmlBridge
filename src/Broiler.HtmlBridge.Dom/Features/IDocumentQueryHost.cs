using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentQueryBinding"/> needs from the bridge: the document
/// root, the document-order element list, the JS-wrapper factory, selector validation, and the two
/// DOM collection factories a query answers with. Sub-tree search is the bridge's neutral
/// <c>internal static</c> helper, called directly, so it is not on this contract.
/// </summary>
/// <remarks>
/// The three members below the DOM ones were reached as <c>DomBridge.ValidateSelector(selector,
/// host.JsContext)</c> and <c>DomCollectionBinding.NodeList(host.JsContext, …)</c> — that is, by
/// handing the module the bridge's script context so it could pass it straight back. The context was
/// never the module's to hold: it wanted a <c>SyntaxError</c> raised and a collection built, and both
/// are now asked for by name. That is what lets this contract, and the module over it, name no engine
/// type at all.
/// </remarks>
internal interface IDocumentQueryHost
{
    /// <summary>
    /// The single JS wrapper identity for <paramref name="node"/>, as a JSEAL handle over the same
    /// engine object the bridge's wrapper tables are keyed on.
    /// </summary>
    JsValue ToJsObject(DomNode node);

    DomElement DocumentElement { get; }
    IReadOnlyList<DomElement> Elements { get; }

    // Selector matching moved onto the host (Phase 2 item 4 de-globalization): it reads the per-bridge
    // `:checked` state, so it is now a bridge-instance method rather than a static helper.
    bool MatchesSelector(DomElement element, string selector, DomElement? scope = null);

    /// <summary>
    /// Throws a <c>SyntaxError</c> <c>DOMException</c> when <paramref name="selector"/> is not a valid
    /// selector list (DOM §4.2.6), and returns quietly when it is.
    /// </summary>
    void ValidateSelector(string selector);

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
