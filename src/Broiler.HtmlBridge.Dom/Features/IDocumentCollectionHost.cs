using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentCollectionBinding"/> needs from the bridge: the realm
/// its collections are minted in, the document-order element list, the JS-wrapper factory, and the
/// stylesheet-object builder. Attribute reads use the bridge's neutral <c>internal static</c>
/// <c>TryGetAttribute</c> and <c>HasAttr</c> helpers directly, so they are not on this contract.
/// </summary>
/// <remarks>
/// <para>
/// The whole contract is spelled in JSEAL: a JS object is a <see cref="JsValue"/>, and the wrapper
/// factory is <see cref="WrapNode"/>.
/// </para>
/// <para>
/// A collection is minted in a realm, so <see cref="Realm"/> is what the module asks for, and both
/// implementers have one — the bridge, and the sub-document host the frame projection delegates to.
/// </para>
/// </remarks>
internal interface IDocumentCollectionHost
{
    /// <summary>
    /// The realm the collections this module builds belong to. Never null while a document is
    /// attached; a document collection is only reachable after attach.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>
    /// Every element in the document, in tree order, recomputed per read — which is what makes the
    /// collections built over it live. <c>links</c> and <c>scripts</c> are filtered out of this one
    /// list rather than collected separately, so a document has only one element ordering.
    /// </summary>
    IReadOnlyList<DomElement> Elements { get; }

    /// <summary>
    /// Index into <see cref="Elements"/> of the <c>&lt;script&gt;</c> element whose program the host
    /// is evaluating right now, or a negative value when no script is running. The same insertion
    /// point <see cref="IDocumentWriteHost"/> exposes, needed here for
    /// <c>document.currentScript</c>.
    /// </summary>
    int CurrentScriptIndex { get; }

    JsValue BuildStyleSheetObject(DomElement styleElement);

    /// <summary>
    /// Whether <paramref name="element"/> has an associated CSS style sheet, and so belongs in
    /// <c>document.styleSheets</c> (CSSOM §2.2: the collection is every sheet associated with the
    /// document, a <c>&lt;link rel=stylesheet&gt;</c> included).
    /// </summary>
    /// <remarks>
    /// The same predicate the sub-document collection uses, rather than a second reading of it.
    /// Filtering <see cref="Elements"/> to tag <c>style</c> here instead would leave an externally
    /// linked sheet out of the main document's collection however well it loaded — a page whose
    /// linked sheet demonstrably applies (<c>getComputedStyle</c> reads the linked colour) would
    /// still report <c>document.styleSheets.length === 0</c>. Sharing the predicate is what keeps
    /// the two documents agreeing about what a document's stylesheets are.
    /// </remarks>
    bool HasAssociatedStyleSheet(DomElement element);
}
