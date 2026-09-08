using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ShadowDomBinding"/> needs from the bridge for the shadow-DOM
/// JS-binding members (<c>element.shadowRoot</c> / <c>element.attachShadow()</c>). The per-element
/// shadow linkage (host, root, mode) lives on the bridge's <c>ElementRuntimeState.Shadow</c> slot and is
/// exposed here as named primitives (the P3.7 pattern) so the module never touches the runtime-state
/// object: the existing-root lookup, the open/closed mode read, and a single <c>AttachShadowRoot</c>
/// primitive that creates the <c>#shadow-root</c> element, links it to its host and records the mode in
/// one step. JS-wrapper identity and the realm round it out.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The script context this contract used to carry was there for exactly one thing — raising the
/// <c>NotSupportedError</c> a second <c>attachShadow</c> gets — and <see cref="IJsCalls.DomError"/>
/// owns that now. The realm stays because the module still has to mint the mode read and the wrapper
/// answers in it, and because <c>attachShadow</c> is also reached from
/// <see cref="ElementInternalsBinding"/> through a path that carries no call frame.
/// </remarks>
internal interface IShadowDomHost
{
    /// <summary>The realm the shadow-root answers are minted in, and the one the
    /// <c>NotSupportedError</c> is raised in.</summary>
    IJsRealm Realm { get; }

    /// <summary>The element's attached shadow root, or null.</summary>
    DomElement? GetShadowRoot(DomElement element);

    /// <summary>The element's shadow mode ("open"/"closed"), if a string mode is recorded.</summary>
    bool TryGetShadowMode(DomElement element, out string mode);

    /// <summary>Creates the <c>#shadow-root</c> element, links it to <paramref name="host"/> as its
    /// shadow root with <paramref name="mode"/>, and returns it.</summary>
    DomElement AttachShadowRoot(DomElement host, string mode);

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);
}
