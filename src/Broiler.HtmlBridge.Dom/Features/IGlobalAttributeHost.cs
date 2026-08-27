using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="GlobalAttributeBinding"/> needs from the bridge: the style-scope
/// invalidation fired when a selector-affecting global attribute (<c>id</c>, <c>class</c>, <c>dir</c>)
/// changes, so cascaded styles recompute. The plain reflected attributes (<c>title</c>, <c>lang</c>,
/// <c>accessKey</c>, <c>draggable</c>) don't affect selectors and need no host call — they go through the
/// bridge's neutral <c>internal static</c> <c>SetAttr</c>/<c>TryGetAttribute</c> helpers directly.
/// </summary>
internal interface IGlobalAttributeHost
{
    void InvalidateStyleScope(DomElement element);
}
