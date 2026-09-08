using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ITraversalHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.TraversalBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3). Every member is an explicit interface
/// implementation, so none of these seams widen the public <c>DomBridge</c> surface — the module
/// reaches them only through the interface, never through bridge private fields.
/// </summary>
/// <remarks>
/// This is the half-migrated seam for the traversal slice: the module speaks JSEAL, the rest of the
/// bridge still holds engine objects, and <see cref="Dom.Runtime.JsInterop"/> is the cast between
/// them. It is a cast and not a conversion — a JSEAL object handle carries the engine's own object —
/// so wrapper identity (<c>el === el</c>, and the weak tables keyed on it) is the same question it
/// was before.
/// </remarks>
public sealed partial class DomBridge : ITraversalHost
{
    IJsRealm ITraversalHost.Realm => Realm;

    DomNode ITraversalHost.DocumentNode => _document;

    JsValue ITraversalHost.WrapNode(DomNode node) => Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    DomNode? ITraversalHost.FindNode(JsValue wrapper) =>
        wrapper.IsObject ? FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper)) : null;

    DomElement? ITraversalHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper)) : null;

    int ITraversalHost.CompareBoundaryPosition(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB) =>
        CompareBoundaryPosition(docRoot, containerA, offsetA, containerB, offsetB);

    IReadOnlyList<(double Left, double Top, double Width, double Height)> ITraversalHost.GetClientRectsForRange(DomRange range) =>
        GetClientRectsForRange(range);

    JsValue ITraversalHost.CreateDomRect((double Left, double Top, double Width, double Height) rectData) =>
        CreateDomRectObject(rectData);

    JsValue ITraversalHost.CreateCommentNode(string data)
    {
        var comment = CreateBridgeCommentNode(data);
        return Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(comment));
    }

    DomNode ITraversalHost.CreateRangeResultFragment() => CreateBridgeDocumentFragment();

    DomNode ITraversalHost.CloneRangeNode(DomNode node, bool deep)
    {
        var clone = CloneDomElement(node, deep);
        return clone;
    }

    DomText ITraversalHost.CreateRangeTextNode(string data)
    {
        var text = CreateBridgeTextNode(data);
        return text;
    }

    List<DomNode> ITraversalHost.ParseHtmlFragment(DomElement contextElement, string html) =>
        BuildAdjacentHtmlNodes(contextElement, html);

    DomElement ITraversalHost.CreateBridgeElement(string tagName) => CreateBridgeElement(tagName);

    bool ITraversalHost.HasBrowsingContext(DomNode documentRoot) =>
        documentRoot is DomDocument document &&
        _browsingContexts.GetContainerForDocument(document) is not null;
}
