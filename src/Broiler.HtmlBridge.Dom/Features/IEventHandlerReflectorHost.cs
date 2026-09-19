using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventHandlerReflectorBinding"/> needs from the bridge for the
/// inline <c>on*</c> event-handler IDL reflectors (<c>onclick</c>, <c>onload</c>, …): read/write access
/// to the per-node inline-handler store. Setting <c>element.onclick = fn</c> stores the function;
/// setting it to a non-function clears it; reading returns the stored function or <c>null</c>. The
/// store is the same live map the <see cref="EventDispatchBinding"/> reads and the bridge's
/// <c>CompileInlineEventAttributes</c> populates from <c>on*</c> content attributes.
/// </summary>
/// <remarks>
/// <b>Three operations rather than the map itself.</b> This used to say the map held the engine's own
/// values and was shared with "the unmigrated dispatch and attribute-compilation paths", so it could
/// not change shape until they moved. Neither path was unmigrated: the dispatch module spoke JSEAL and
/// the attribute compiler held a <see cref="JsValue"/>, and each converted only at the map, as this
/// contract's implementation did. The map is a dictionary of handles now. Three operations remain the
/// right surface for a different reason — they are the whole of what the reflector does, and a live
/// per-node store handed out through a contract is one any caller could write to behind the bridge.
/// </remarks>
internal interface IEventHandlerReflectorHost
{
    /// <summary>
    /// The inline handler <paramref name="node"/> carries for <paramref name="eventName"/>, or
    /// <see cref="JsValue.Null"/> when it carries none — which is the value the IDL attribute has.
    /// </summary>
    JsValue GetInlineEventHandler(DomNode node, string eventName);

    /// <summary>
    /// Stores <paramref name="handler"/> as <paramref name="node"/>'s inline handler for
    /// <paramref name="eventName"/>, replacing whatever was there.
    /// </summary>
    void SetInlineEventHandler(DomNode node, string eventName, JsValue handler);

    /// <summary>Clears <paramref name="node"/>'s inline handler for <paramref name="eventName"/>.</summary>
    void RemoveInlineEventHandler(DomNode node, string eventName);
}
