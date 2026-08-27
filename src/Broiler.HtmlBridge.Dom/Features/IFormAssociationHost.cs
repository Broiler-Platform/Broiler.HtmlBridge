using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="FormAssociationBinding"/> needs: the JS-wrapper factory, the
/// document-order element list (to find a control's labels), the by-id lookup that both the
/// <c>form</c> content attribute and a label's <c>for</c> resolve through, and the realm holding
/// <c>NodeList</c> (a control's <c>labels</c> is a live one).
/// </summary>
internal interface IFormAssociationHost
{
    JSObject ToJSObject(DomNode node);
    IReadOnlyList<DomElement> Elements { get; }
    DomElement? GetElementById(string id);
    Broiler.JavaScript.Engine.JSContext? JsContext { get; }

    /// <summary>Whether the element belongs to a custom element definition that declared
    /// <c>formAssociated</c>. Such an element is form-associated and labelable in its own right, so
    /// the tag lists here cannot answer for it (HTML §4.13.5).</summary>
    bool IsFormAssociatedCustomElement(DomElement element);
}
