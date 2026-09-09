using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IComputedStyleHost implementation for the ComputedStyleBinding feature module (Phase 3):
// the bridge exposes its realm, the JS-wrapper reverse lookup and the computed-style object builder via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// The contract speaks JSEAL and the rest of the bridge still holds engine objects, so
// Dom.Runtime.JsInterop is the cast between them — a cast and not a conversion, since a JSEAL object
// handle carries the engine's own object, which is why the wrapper tables keyed on that object still
// answer the same question. The wrapper reverse lookup below keeps its engine-shaped name because it
// lives in DomBridge/Utilities.cs, which has not migrated and is not owned this round.
public sealed partial class DomBridge : Dom.Features.IComputedStyleHost
{
    IJsRealm Dom.Features.IComputedStyleHost.Realm => Realm;

    DomElement? Dom.Features.IComputedStyleHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper)) : null;

    JsValue Dom.Features.IComputedStyleHost.BuildComputedStyle(DomElement? element, string? pseudoElement)
        => BuildComputedStyleObject(element, pseudoElement);
}
