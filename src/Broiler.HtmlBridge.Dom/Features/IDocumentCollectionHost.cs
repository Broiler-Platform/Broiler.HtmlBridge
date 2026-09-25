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
/// <para>
/// Two inherited members carry a condition of their own here: a document collection is only reachable
/// after attach, so <see cref="Realm"/> is never null for this module, and <see cref="Elements"/> is
/// recomputed per read — which is what makes these collections live — with <c>links</c> and
/// <c>scripts</c> filtered out of that one list rather than collected separately.
/// </para>
/// </remarks>
internal interface IDocumentCollectionHost : IElementsHost, INodeWrapperHost, IRealmHost
{
    /// <summary>
    /// Index into <see cref="Elements"/> of the <c>&lt;script&gt;</c> element whose program the host
    /// is evaluating right now, or a negative value when no script is running. The same insertion
    /// point <see cref="IDocumentWriteHost"/> exposes, needed here for
    /// <c>document.currentScript</c>.
    /// </summary>
    int CurrentScriptIndex { get; }

    /// <summary>
    /// The script-inserted <c>&lt;script&gt;</c> running now, which <c>document.currentScript</c>
    /// names ahead of <see cref="CurrentScriptIndex"/>, or null.
    /// </summary>
    DomElement? RunningInsertedScript { get; }

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
