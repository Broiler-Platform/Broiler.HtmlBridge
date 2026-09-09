using System.Collections.Generic;
using Broiler.HtmlBridge.Jseal;
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
/// <para>
/// The contract names no engine type. The script context it used to carry was here for one thing —
/// so <c>DomBridge.ThrowDOMException</c> could raise the spec's <c>SyntaxError</c> /
/// <c>NoModificationAllowedError</c> — and that is now <c>call.Realm.DomError</c>, which the module
/// reaches through the call frame it already has. Nothing replaces it here, because a contract that
/// carried a realm only to raise an error would be carrying the same thing under a new name.
/// </para>
/// <para>
/// <see cref="FindElement"/> is the rename that goes with the migration: the name it replaces spelled
/// the engine's object type out, so every call site that mentioned the member mentioned the engine.
/// It is the shape <c>ITraversalHost</c> and <c>ISubDocumentHost</c> already took.
/// </para>
/// </remarks>
internal interface IInsertAdjacentHost
{
    /// <summary>Reverse wrapper lookup: the element whose JS wrapper is <paramref name="wrapper"/>.</summary>
    DomElement? FindElement(JsValue wrapper);

    void InsertNodeAt(DomNode parent, DomNode node, int index);
    DomText CreateBridgeTextNode(string data);
    List<DomNode> BuildAdjacentHtmlNodes(DomElement contextElement, string html);
    void ResetComputedStyleEngines();
}
