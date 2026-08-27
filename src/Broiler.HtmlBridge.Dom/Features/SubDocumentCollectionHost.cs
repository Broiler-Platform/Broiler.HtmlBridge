using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Projects a nested browsing context onto <see cref="IDocumentCollectionHost"/>, so a sub-document's
/// <c>forms</c>/<c>images</c>/<c>links</c>/<c>anchors</c>/<c>scripts</c>/<c>embeds</c>/<c>plugins</c>/
/// <c>styleSheets</c> are built by the same <see cref="DocumentCollectionBinding"/> the main document
/// uses rather than by a second, older implementation of the same idea.
/// </summary>
/// <remarks>
/// <para>
/// The sub-document surface used to build those collections itself, as
/// <see cref="JavaScript.BuiltIns.Array.JSArray"/> snapshots — the shape the main document was moved
/// off. So an <c>&lt;iframe&gt;</c>'s <c>contentDocument</c> answered a different object model from
/// the document containing it: <c>d.forms.constructor.name</c> was <c>"Array"</c> where the parent's
/// was <c>"HTMLCollection"</c>, <c>d.forms === d.forms</c> was <see langword="false"/>, there was no
/// <c>namedItem</c> and no named access, appending a form left a held collection's <c>length</c>
/// unchanged, and <c>anchors</c>, <c>embeds</c> and <c>plugins</c> were absent outright. Nothing about
/// a frame's document makes it a different kind of document, and a script inside one is a script like
/// any other.
/// </para>
/// <para>
/// Only two of the contract's members are genuinely per-document: the element list, which must be this
/// root's sub-tree rather than the main document's, and <c>currentScript</c>, which a sub-document does
/// not track. The rest — wrapper identity and the two stylesheet services — are per-<em>node</em>
/// questions the bridge answers the same way whichever document asks, so they delegate straight
/// through.
/// </para>
/// </remarks>
internal sealed class SubDocumentCollectionHost(ISubDocumentHost host, DomNode docRoot)
    : IDocumentCollectionHost
{
    public JSObject ToJSObject(DomNode node) => host.ToJSObject(node);

    /// <summary>
    /// Every element in this sub-document, in tree order, recomputed per read — which is what makes
    /// the collections built over it live, exactly as the main document's are.
    /// </summary>
    public IReadOnlyList<DomElement> Elements =>
        [.. docRoot.InclusiveDescendants().OfType<DomElement>()];

    /// <summary>
    /// Always negative: a sub-document has no script insertion point of its own, so
    /// <c>document.currentScript</c> would answer <c>null</c> here. The property is not registered on
    /// the sub-document surface at all, so nothing reads this; it is the honest answer rather than an
    /// index into another document's element list, which is what deferring to the bridge would give.
    /// </summary>
    public int CurrentScriptIndex => -1;

    public JSObject BuildStyleSheetObject(DomElement styleElement) => host.BuildStyleSheetObject(styleElement);

    public bool HasAssociatedStyleSheet(DomElement element) => host.HasAssociatedStyleSheet(element);
}
