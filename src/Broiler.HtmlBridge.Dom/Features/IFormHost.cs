using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge service the <see cref="FormBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.9). The HTMLFormElement interface — the form-controls
/// collection (with named access) and validity — is otherwise pure tree/attribute work over the
/// assembly's static <c>DomBridge</c> helpers; the only bridge coupling is the realm the collection
/// is built in and turning a resolved form control into its JS wrapper. This replaces the
/// <c>DomBridge</c> back-reference the old <c>FormElementsCollection</c> carried purely for that
/// purpose.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. <see cref="WrapNode"/> was <c>ToJSObject</c>: a name that says <em>JSObject</em> is an
/// engine reference too, so it moves with the type it named.
/// </remarks>
internal interface IFormHost
{
    /// <summary>
    /// The realm the <c>form.elements</c> collection and the <c>HTMLFormElement</c> members are built
    /// in. It is what mints the collection as a host-completed object (see
    /// <see cref="IJsExotic"/>), which is what its named access is.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

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
