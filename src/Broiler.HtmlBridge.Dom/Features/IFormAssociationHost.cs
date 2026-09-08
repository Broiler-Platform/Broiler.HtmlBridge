using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="FormAssociationBinding"/> needs: the realm, the JS-wrapper
/// factory, the document-order element list (to find a control's labels), the by-id lookup that both
/// the <c>form</c> content attribute and a label's <c>for</c> resolve through, and the live
/// <c>NodeList</c> a control's <c>labels</c> is.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. Where the contract used to hand the binding the bridge's script context — so the binding
/// could pass it straight back to the collection factory, which is the only thing it did with it —
/// it now names the one operation that needed it. Building a <c>NodeList</c> is still engine-typed
/// work in <see cref="DomCollectionBinding"/>, so the whole of it sits on the bridge's side of the
/// seam rather than being reassembled from an engine-typed list the binding would have to hold; the
/// same shape <c>ISelectorsHost.ElementsByTagName</c> took for the same reason.
/// </remarks>
internal interface IFormAssociationHost
{
    /// <summary>The realm the <c>form</c>/<c>labels</c>/<c>control</c> accessors are installed in.</summary>
    IJsRealm Realm { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>Every element in the document, in document order.</summary>
    IReadOnlyList<DomElement> Elements { get; }

    /// <summary>The element carrying <paramref name="id"/>, or <see langword="null"/>.</summary>
    DomElement? GetElementById(string id);

    /// <summary>
    /// A <b>live</b> <c>NodeList</c> over the elements <paramref name="contents"/> answers with,
    /// each wrapped through the bridge's wrapper cache. Live means the function is re-run on every
    /// read, so a label added after the list was handed out is in it.
    /// </summary>
    JsValue LiveNodeList(Func<List<DomElement>> contents);

    /// <summary>Whether the element belongs to a custom element definition that declared
    /// <c>formAssociated</c>. Such an element is form-associated and labelable in its own right, so
    /// the tag lists here cannot answer for it (HTML §4.13.5).</summary>
    bool IsFormAssociatedCustomElement(DomElement element);
}
