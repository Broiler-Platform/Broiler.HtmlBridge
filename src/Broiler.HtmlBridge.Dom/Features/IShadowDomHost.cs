using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ShadowDomBinding"/> needs from the bridge for the shadow-DOM
/// JS-binding members (<c>element.shadowRoot</c> / <c>element.attachShadow()</c>). The per-element
/// shadow linkage (host, root, mode) lives on the bridge's <c>ElementRuntimeState.Shadow</c> slot and is
/// exposed here as named primitives (the P3.7 pattern) so the module never touches the runtime-state
/// object: the existing-root lookup, the open/closed mode read, and a single <c>AttachShadowRoot</c>
/// primitive that creates the <c>#shadow-root</c> element, links it to its host and records the mode in
/// one step. JS-wrapper identity and the <see cref="JSContext"/> the DOM-exception thrower needs round it
/// out.
/// </summary>
internal interface IShadowDomHost
{
    JSContext? JsContext { get; }

    /// <summary>The element's attached shadow root, or null.</summary>
    DomElement? GetShadowRoot(DomElement element);

    /// <summary>The element's shadow mode ("open"/"closed"), if a string mode is recorded.</summary>
    bool TryGetShadowMode(DomElement element, out string mode);

    /// <summary>Creates the <c>#shadow-root</c> element, links it to <paramref name="host"/> as its
    /// shadow root with <paramref name="mode"/>, and returns it.</summary>
    DomElement AttachShadowRoot(DomElement host, string mode);

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);
}
