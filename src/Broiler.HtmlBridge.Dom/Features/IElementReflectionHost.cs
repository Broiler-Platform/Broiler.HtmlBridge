namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ElementReflectionBinding"/> needs from the bridge: the current
/// page URL, read at call time so a URL-typed IDL getter (<c>&lt;object&gt;.data</c>,
/// <c>&lt;a&gt;/&lt;area&gt;/&lt;base&gt;/&lt;link&gt;.href</c>) resolves its relative content attribute
/// against the live document base. The content-attribute reads/writes themselves go through the bridge's
/// neutral <c>internal static</c> <c>TryGetAttribute</c>/<c>SetAttr</c> helpers, called directly.
/// </summary>
internal interface IElementReflectionHost
{
    string PageUrl { get; }
}
