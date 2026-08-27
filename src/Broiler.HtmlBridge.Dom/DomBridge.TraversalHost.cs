using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
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
public sealed partial class DomBridge : ITraversalHost
{
    JSContext ITraversalHost.JsContext => _jsContext!;

    DomNode ITraversalHost.DocumentNode => _document;

    JSObject ITraversalHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomNode? ITraversalHost.FindDomNodeByJSObject(JSObject? jsObj) =>
        jsObj is null ? null : FindDomNodeByJSObject(jsObj);

    DomElement? ITraversalHost.FindDomElementByJSObject(JSObject? jsObj) =>
        jsObj is null ? null : FindDomElementByJSObject(jsObj);

    int ITraversalHost.CompareBoundaryPosition(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB) =>
        CompareBoundaryPosition(docRoot, containerA, offsetA, containerB, offsetB);

    IReadOnlyList<(double Left, double Top, double Width, double Height)> ITraversalHost.GetClientRectsForRange(DomRange range) =>
        GetClientRectsForRange(range);

    JSObject ITraversalHost.CreateDomRectObject((double Left, double Top, double Width, double Height) rectData) =>
        CreateDomRectObject(rectData);

    JSObject ITraversalHost.CreateCommentNode(string data)
    {
        var comment = CreateBridgeCommentNode(data);
        return ToJSObject(comment);
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
