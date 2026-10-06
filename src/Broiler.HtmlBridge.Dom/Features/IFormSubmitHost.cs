using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="FormSubmitBinding"/> needs from the bridge for the
/// <c>form.submit()</c> action: the submission handover, and nothing else -- <c>submit()</c> fires no
/// event.
/// </summary>
internal interface IFormSubmitHost
{
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
