using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge service the <see cref="FormBinding"/> feature module needs. The HTMLFormElement
/// interface — the form-controls
/// collection (with named access) and validity — is otherwise pure tree/attribute work over the
/// assembly's static <c>DomBridge</c> helpers; the only bridge coupling is the realm the collection
/// is built in and turning a resolved form control into its JS wrapper.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The realm inherited from <see cref="IRealmHost"/> is what mints <c>form.elements</c> as a
/// host-completed object (see <see cref="IJsExotic"/>), which is what its named access is.
/// </remarks>
internal interface IFormHost : INodeWrapperHost, IRealmHost
{
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
