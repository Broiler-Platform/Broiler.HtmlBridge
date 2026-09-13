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
/// The traversal slice is not a half-migrated seam any more: the module speaks JSEAL and both wrapper
/// lookups below take the handle they are handed, so no cast is left in this file. Wrapper identity
/// (<c>el === el</c>, and the weak tables keyed on it) is the same question it was before, because
/// <c>Runtime/JsObjectRegistry</c> keys on <see cref="JsValue.ObjectIdentity"/> — the reference the
/// handle carries.
/// </remarks>
public sealed partial class DomBridge : ITraversalHost
{
    IJsRealm ITraversalHost.Realm => Realm;

    DomNode ITraversalHost.DocumentNode => _document;

    JsValue ITraversalHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode? ITraversalHost.FindNode(JsValue wrapper) =>
        wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

    DomElement? ITraversalHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(wrapper) : null;

    int ITraversalHost.CompareBoundaryPosition(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB) =>
        CompareBoundaryPosition(docRoot, containerA, offsetA, containerB, offsetB);

    IReadOnlyList<(double Left, double Top, double Width, double Height)> ITraversalHost.GetClientRectsForRange(DomRange range) =>
        GetClientRectsForRange(range);

    JsValue ITraversalHost.CreateDomRect((double Left, double Top, double Width, double Height) rectData) =>
        CreateDomRectObject(rectData);

    JsValue ITraversalHost.CreateCommentNode(string data)
    {
        var comment = CreateBridgeCommentNode(data);
        return WrapNode(comment);
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
