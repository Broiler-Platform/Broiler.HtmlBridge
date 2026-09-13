using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IComputedStyleHost implementation for the ComputedStyleBinding feature module (Phase 3):
// the bridge exposes its realm, the JS-wrapper reverse lookup and the computed-style object builder via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// Nothing in this file crosses to the engine. The wrapper reverse lookup in DomBridge/Utilities.cs
// takes the handle this member is handed, so the member forwards it, and the registry behind it is
// keyed on JsValue.ObjectIdentity rather than on an engine object. The lookup keeps its engine-shaped
// name; that is a rename waiting to happen, not a seam.
public sealed partial class DomBridge : Dom.Features.IComputedStyleHost
{
    IJsRealm Dom.Features.IComputedStyleHost.Realm => Realm;

    DomElement? Dom.Features.IComputedStyleHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(wrapper) : null;

    JsValue Dom.Features.IComputedStyleHost.BuildComputedStyle(DomElement? element, string? pseudoElement)
        => BuildComputedStyleObject(element, pseudoElement);
}
