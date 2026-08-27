using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ComputedStyleBinding"/> needs from the bridge: the JS-wrapper
/// reverse lookup (a JS element object → its canonical <c>DomElement</c>) and the computed-style
/// object builder (resolving the element's used values, optionally for a pseudo-element).
/// </summary>
internal interface IComputedStyleHost
{
    DomElement? FindDomElementByJSObject(JSObject jsObj);
    JSObject BuildComputedStyleObject(DomElement? element, string? pseudoElement);
}
