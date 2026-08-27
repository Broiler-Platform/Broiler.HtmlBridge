using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IComputedStyleHost implementation for the ComputedStyleBinding feature module (Phase 3):
// the bridge exposes the JS-wrapper reverse lookup and the computed-style object builder via explicit
// interface members, so the module never reaches an arbitrary bridge private field and the public
// surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IComputedStyleHost
{
    DomElement? Dom.Features.IComputedStyleHost.FindDomElementByJSObject(JSObject jsObj)
        => FindDomElementByJSObject(jsObj);

    JSObject Dom.Features.IComputedStyleHost.BuildComputedStyleObject(DomElement? element, string? pseudoElement)
        => BuildComputedStyleObject(element, pseudoElement);
}
