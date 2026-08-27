using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="FormSubmitBinding"/> needs from the bridge for the
/// <c>form.submit()</c> action: read access to the form's registered event listeners (to fire the
/// synthetic <c>submit</c> event). The no-op function factory (<c>UndefinedFunction</c>), the listener
/// invoker (<c>InvokeEventListener</c>) and the render logger are the bridge's <c>internal static</c>
/// helpers, called directly.
/// </summary>
internal interface IFormSubmitHost
{
    /// <summary>The per-event-type registered listeners for <paramref name="node"/> (live store).</summary>
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);
}
