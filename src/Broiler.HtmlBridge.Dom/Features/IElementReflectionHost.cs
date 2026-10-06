namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ElementReflectionBinding"/> needs from the bridge: the current
/// page URL, read at call time so a URL-typed IDL getter (<c>&lt;object&gt;.data</c>,
/// <c>&lt;a&gt;/&lt;area&gt;/&lt;base&gt;/&lt;link&gt;.href</c>) resolves its relative content attribute
/// against the live document base. The content-attribute reads/writes themselves go through the bridge's
/// neutral <c>internal static</c> <c>TryGetAttribute</c>/<c>SetAttr</c> helpers, called directly.
/// </summary>
internal interface IElementReflectionHost : IPageUrlHost
{
    /// <summary>
    /// The document's base URL now: its <c>&lt;base href&gt;</c>, or its URL -- which pushState and a
    /// fragment navigation move -- when it has none.
    /// </summary>
    string DocumentBaseUrl { get; }

    /// <summary>The document's URL now, which a <c>&lt;base&gt;</c>'s own <c>href</c> resolves against.</summary>
    string DocumentUrl { get; }
}
