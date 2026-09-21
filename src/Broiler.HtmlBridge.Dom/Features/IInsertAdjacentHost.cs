using System.Collections.Generic;
using Broiler.JSeal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="InsertAdjacentBinding"/> needs from the bridge for the DOM
/// <c>insertAdjacentElement</c> / <c>insertAdjacentText</c> / <c>insertAdjacentHTML</c> methods: the
/// wrapper → element reverse lookup, the side-effecting insertion primitive, the text-node factory,
/// the fragment parser for the HTML variant, and the computed-style engine reset the HTML variant
/// performs after inserting parsed nodes. The neutral tree helpers the position resolution uses
/// (<c>ParentEl</c>, <c>ChildIndexOf</c>) stay the bridge's <c>internal static</c> helpers, called
/// directly.
/// </summary>
/// <remarks>
/// The contract names no engine type, and it carries no realm. The spec's <c>SyntaxError</c> /
/// <c>NoModificationAllowedError</c> is raised through <c>call.Realm.DomError</c>, which the module
/// reaches from the call frame it already has; a contract that carried a realm only to raise an error
/// would be carrying the same thing under a new name.
/// </remarks>
internal interface IInsertAdjacentHost : INodeInsertionHost, ITextNodeFactoryHost
{
    /// <summary>Reverse wrapper lookup: the element whose JS wrapper is <paramref name="wrapper"/>.</summary>
    DomElement? FindElement(JsValue wrapper);

    List<DomNode> BuildAdjacentHtmlNodes(DomElement contextElement, string html);
    void ResetComputedStyleEngines();
}
