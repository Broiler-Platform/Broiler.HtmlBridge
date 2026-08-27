using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="EventHandlerReflectorBinding"/> needs from the bridge for the
/// inline <c>on*</c> event-handler IDL reflectors (<c>onclick</c>, <c>onload</c>, …): read/write access
/// to the per-node inline-handler map. Setting <c>element.onclick = fn</c> stores the function; setting
/// it to a non-function clears it; reading returns the stored function or <c>null</c>. The map is the
/// same live store the <see cref="EventDispatchBinding"/> reads and the bridge's
/// <c>CompileInlineEventAttributes</c> populates from <c>on*</c> content attributes.
/// </summary>
internal interface IEventHandlerReflectorHost
{
    /// <summary>The inline <c>on*</c> handlers for <paramref name="node"/> (live map).</summary>
    Dictionary<string, JSValue> GetInlineEventHandlers(DomNode node);
}
