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

    /// <summary>
    /// Records that the page asked to submit <paramref name="form"/>, into the same pending-navigation
    /// slot <c>location.*</c> writes to — so the last thing a document asked for is the thing that
    /// happens, whichever way it asked.
    /// </summary>
    /// <remarks>
    /// The bridge, not this module, works out the form's position and resolves its <c>action</c>:
    /// both are document-level facts, and the binding has neither the element list nor the page URL.
    /// </remarks>
    void RequestFormSubmission(DomElement form);
}
