using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ShadowDomBinding"/> needs from the bridge for the shadow-DOM
/// JS-binding members (<c>element.shadowRoot</c> / <c>element.attachShadow()</c>). The per-element
/// shadow linkage (host, root, mode) lives in the bridge's <c>ShadowRuntimeState</c> table and is
/// exposed here as named primitives so the module never touches the runtime-state
/// object: the existing-root lookup, the open/closed mode read, and a single <c>AttachShadowRoot</c>
/// primitive that creates the <c>#shadow-root</c> element, links it to its host and records the mode in
/// one step. JS-wrapper identity and the realm round it out.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The <c>NotSupportedError</c> a second <c>attachShadow</c> gets is raised through
/// <see cref="IJsCalls.DomError"/>, so this contract carries no script context.
/// The realm is here because the module still has to mint the mode read and the wrapper
/// answers in it, and because <c>attachShadow</c> is also reached from
/// <see cref="ElementInternalsBinding"/> through a path that carries no call frame.
/// </remarks>
internal interface IShadowDomHost
{
    /// <summary>The realm the shadow-root answers are minted in, and the one the
    /// <c>NotSupportedError</c> is raised in.</summary>
    IJsRealm Realm { get; }

    /// <summary>The element's attached shadow root, or null.</summary>
    DomShadowRoot? GetShadowRoot(DomElement element);

    /// <summary>Attaches a shadow root to <paramref name="host"/> with <paramref name="mode"/>, and returns it.</summary>
    DomShadowRoot AttachShadowRoot(
        DomElement host,
        DomShadowRootMode mode,
        bool delegatesFocus = false,
        DomSlotAssignmentMode slotAssignment = DomSlotAssignmentMode.Named);

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);
}
