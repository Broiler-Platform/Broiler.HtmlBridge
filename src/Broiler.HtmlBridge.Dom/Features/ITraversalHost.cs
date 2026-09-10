using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow set of bridge services the <see cref="TraversalBinding"/> feature module needs
/// (HtmlBridge complexity-reduction roadmap Phase 3, first vertical slice). It replaces the
/// former direct reach into <c>DomBridge</c> private state from the traversal callbacks with a
/// small, named contract: the realm, JS-wrapper identity, node lookup, the two
/// range-boundary/geometry helpers that still live in the bridge (Phase 5 relocates geometry to
/// Layout), and the range-scoped node-construction seams the <c>Range</c> content operations mint
/// bridge nodes through. No member exposes arbitrary bridge feature state, so the module never
/// holds a god-object back-reference.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's: a wrapper is a <see cref="JsValue"/> and errors are raised
/// through <see cref="IJsRealm"/>, so nothing here names an engine type. The bridge's implementation
/// (<c>DomBridge.TraversalHost.cs</c>) used to be where the handles met the engine objects the
/// unmigrated half of the bridge holds; it meets none now — both lookups forward the handle.
/// </remarks>
internal interface ITraversalHost
{
    /// <summary>
    /// The realm the traversal objects are built in, and through which this module raises a
    /// <c>DOMException</c>. Never null while a document is attached; the traversal APIs are only
    /// reachable from an attached document. It replaces the former <c>JsContext</c> seam, which
    /// existed for exactly one purpose — DOMException plumbing — that <see cref="IJsCalls.DomError"/>
    /// now owns.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>The main document root node that owns a range created without an explicit root.</summary>
    DomNode DocumentNode { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>Resolves the canonical node behind a JS wrapper, or null.</summary>
    DomNode? FindNode(JsValue wrapper);

    /// <summary>Resolves the canonical element behind a JS wrapper, or null.</summary>
    DomElement? FindElement(JsValue wrapper);

    /// <summary>Compares two boundary points within <paramref name="docRoot"/>, returning -1/0/1
    /// per the DOM Range comparison rules.</summary>
    int CompareBoundaryPosition(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB);

    /// <summary>The used-value client rectangles covering the range's content (bridge geometry;
    /// Phase 5 moves this to Layout).</summary>
    IReadOnlyList<(double Left, double Top, double Width, double Height)> GetClientRectsForRange(DomRange range);

    /// <summary>Builds a CSSOM-View <c>DOMRect</c>-shaped JS object from a used-value rectangle.</summary>
    JsValue CreateDomRect((double Left, double Top, double Width, double Height) rectData);

    /// <summary>Mints a JS-wrapped comment node registered for wrapper lookup
    /// (<c>document.createComment</c>).</summary>
    JsValue CreateCommentNode(string data);

    /// <summary>Mints a bridge <c>#document-fragment</c> to receive extracted/cloned range content,
    /// registered so <see cref="WrapNode"/> can wrap it.</summary>
    DomNode CreateRangeResultFragment();

    /// <summary>Clones a node for a range content operation, carrying host runtime state, registered
    /// for wrapper lookup.</summary>
    DomNode CloneRangeNode(DomNode node, bool deep);

    /// <summary>Mints a bridge text node for a range content operation, registered for wrapper
    /// lookup.</summary>
    DomText CreateRangeTextNode(string data);

    /// <summary>
    /// Parses <paramref name="html"/> as a fragment in <paramref name="contextElement"/>'s content
    /// model and returns its parentless top-level nodes, for
    /// <c>Range.createContextualFragment</c>. The same parse <c>insertAdjacentHTML</c> uses.
    /// </summary>
    List<DomNode> ParseHtmlFragment(DomElement contextElement, string html);

    /// <summary>
    /// Mints a bridge element, for the one case <c>createContextualFragment</c> needs one: a range
    /// whose start node has no element context at all, where HTML §3.5 parses against a
    /// <c>body</c>.
    /// </summary>
    DomElement CreateBridgeElement(string tagName);

    /// <summary>
    /// Whether <paramref name="documentRoot"/> is a nested browsing context's document — an
    /// <c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c> content document — rather than
    /// a free-standing <c>createDocument</c>/<c>createHTMLDocument</c> result. It is what separates
    /// the two for <c>getSelection()</c>, which a browser answers with <c>null</c> for a document that
    /// is not being displayed anywhere.
    /// </summary>
    bool HasBrowsingContext(DomNode documentRoot);
}
