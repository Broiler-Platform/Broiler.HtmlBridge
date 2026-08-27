using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge service the <see cref="FormBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.9). The HTMLFormElement interface — the form-controls
/// collection (with named access) and validity — is otherwise pure tree/attribute work over the
/// assembly's static <c>DomBridge</c> helpers; the only bridge coupling is turning a resolved form
/// control into its JS wrapper. This replaces the <c>DomBridge</c> back-reference the old
/// <c>FormElementsCollection</c> carried purely for that purpose.
/// </summary>
internal interface IFormHost
{
    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);

    /// <summary>
    /// Runs the form-reset algorithm (HTML §4.10.21.4) over <paramref name="form"/>'s controls.
    /// </summary>
    /// <remarks>
    /// One call rather than a set of clear-this-flag primitives: a reset is defined over the dirty
    /// flags on the bridge's per-element form-control state, and the <c>&lt;select&gt;</c> case
    /// reaches into the option collection as well, so exposing the pieces would put the algorithm on
    /// the far side of the seam from the state it is written in terms of.
    /// </remarks>
    void ResetForm(DomElement form);

    /// <summary>
    /// The form's controls in tree order, including form-associated custom elements —
    /// <c>form.elements</c> lists them and a browser agrees, so the tag-only canonical query cannot
    /// be the collection this reports.
    /// </summary>
    IReadOnlyList<DomElement> CollectFormControls(DomElement form);

    /// <summary>Whether a form-associated custom element's own validity (set through
    /// <c>ElementInternals.setValidity</c>) is satisfied. A form is valid when all its controls are,
    /// and a custom control's validity is not readable from its markup.</summary>
    bool IsCustomElementValid(DomElement element);
}
