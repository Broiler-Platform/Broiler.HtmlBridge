using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISubWindowHost"/>, the contract the extracted
/// <see cref="Broiler.HtmlBridge.Dom.Features.SubWindowBinding"/> feature module consumes (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.17). Explicit interface members, so these seams do not widen
/// the public <c>DomBridge</c> surface. The module owns the sub-window object and its scroll/
/// getComputedStyle surface; the bridge keeps the sub-document builder, resource loading and scroll
/// geometry it reaches through here.
/// </summary>
public sealed partial class DomBridge : ISubWindowHost
{
    JSObject? ISubWindowHost.WindowJSObject => _windowJSObject;

    JSObject ISubWindowHost.GetOrCreateSubDocument(DomElement container) => GetOrCreateSubDocument(container);

    DomDocument? ISubWindowHost.GetContentDocument(DomElement container) => GetContentDocument(container);

    DomElement? ISubWindowHost.GetFrameForContentDocument(DomNode? owningDocument) =>
        GetFrameForContentDocument(owningDocument);

    string ISubWindowHost.ResolveSubResourceUrl(string resourceUrl, string? baseUrl) =>
        ResolveSubResourceUrl(resourceUrl, baseUrl);

    string ISubWindowHost.GetInheritedSubDocumentBaseUrl(DomElement container) =>
        GetInheritedSubDocumentBaseUrl(container);

    double ISubWindowHost.GetElementScrollOffset(DomElement element, bool vertical) =>
        GetElementScrollOffset(element, vertical);

    void ISubWindowHost.SetElementScroll(DomElement element, double? left, double? top, bool relative, string? behavior) =>
        SetElementScrollOffsetsWithBehavior(element, left, top, relative: relative, clamp: false, behavior: behavior);

    (double? Left, double? Top, string? Behavior) ISubWindowHost.GetScrollArguments(in Arguments args) =>
        GetScrollArguments(args);

    DomElement? ISubWindowHost.FindDomElementByJSObject(JSObject jsObj) => FindDomElementByJSObject(jsObj);

    JSObject ISubWindowHost.BuildComputedStyleObject(DomElement? element, string? pseudoElement) =>
        BuildComputedStyleObject(element, pseudoElement);

    JSValue? ISubWindowHost.GetGlobal(string name) => _jsContext?[name];

    void ISubWindowHost.PublishPendingSubDocumentGlobals(DomElement containerElement, JSObject subWindow) =>
        PublishPendingSubDocumentGlobals(containerElement, subWindow);
}
