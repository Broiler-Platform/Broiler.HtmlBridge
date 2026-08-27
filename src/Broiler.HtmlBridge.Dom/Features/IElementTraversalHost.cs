using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The single bridge seam <see cref="ElementTraversalBinding"/> needs: the JS-wrapper factory that maps a
/// canonical node to its cached wrapper. The element-child enumeration (<c>ChildElements</c>), the
/// element-parent walk (<c>ParentEl</c>) and the text-node test (<c>IsText</c>) are the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
internal interface IElementTraversalHost
{
    JSObject ToJSObject(DomNode node);
}
