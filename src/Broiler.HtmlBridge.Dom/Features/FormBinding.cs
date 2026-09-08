using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTMLFormElement feature binding (HtmlBridge complexity-reduction roadmap Phase 3, P3.9) —
/// <c>form.elements</c> (an <c>HTMLFormControlsCollection</c> with numeric and named access),
/// <c>form.length</c>, <c>form.action</c>, and the constraint-validation checks
/// (<c>checkValidity</c>/<c>reportValidity</c>) that the bridge exposes on form-associated elements.
/// Control collection and validity are pure tree/attribute work over the assembly's static
/// <c>DomBridge</c> helpers (<c>CollectFormControls</c>, <c>HasAttr</c>/<c>TryGetAttribute</c>,
/// <c>ChildElements</c>/<c>IsText</c>); the only bridge coupling — the realm, and wrapping a control
/// as a JS object — goes through the narrow <see cref="IFormHost"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Named access is a host-completed lookup, and the order is the whole of it.</b> Both objects
/// with a named getter — the controls collection and the form wrapper itself — resolve a name only
/// after the object's own properties and its prototype chain have failed to, which is what WebIDL's
/// named-property semantics require: a form containing a control named <c>submit</c> must not shadow
/// <c>form.submit()</c>, and one containing a control named <c>action</c> must not shadow the action
/// attribute. That rule is now stated once, in <see cref="FormNamedControls"/>, an
/// <see cref="IJsExotic"/> the realm consults <em>after</em> ordinary lookup rather than a
/// property-lookup override each object repeated for itself.
/// </para>
/// <para>
/// <b>The form wrapper's own object is still engine-typed, and that is pinned from outside.</b>
/// <c>DomBridge/JsObjects.cs</c> decides a wrapper's type when it mints it — <c>new
/// FormElementJSObject(…)</c> — and installs every other member on the same object, so the wrapper
/// cannot become <c>realm.NewExotic(…)</c> until that site migrates. Until then
/// <see cref="FormElementJSObject"/> is the adapter: a subclass whose one override defers to the same
/// handler the collection uses, in the same order the realm would consult it. The collection has no
/// such caller — this module builds it — so it is a real exotic already.
/// </para>
/// </remarks>
internal sealed class FormBinding(IFormHost host)
{
    private readonly IFormHost _host = host;

    /// <summary>
    /// Engine-typed adapter for <c>DomBridge/ElementInterfaces.cs</c>, which still holds the element
    /// wrapper as an engine object. See the remarks on this class.
    /// </summary>
    internal void Install(JSObject obj, DomElement element, string tag) =>
        Install(Runtime.JsInterop.FromEngineObject(obj), element, tag);

    /// <summary>Installs the <c>HTMLFormElement</c> members on <paramref name="obj"/> when
    /// <paramref name="element"/> is a <c>&lt;form&gt;</c>.</summary>
    internal void Install(JsValue obj, DomElement element, string tag)
    {
        if (tag != "form")
            return;

        var realm = _host.Realm;

        // elements — the form controls collection (numeric + named access)
        realm.DefineAccessor(obj, "elements",
            (in call) => BuildElementsCollection(call.Realm, element), null);
        // length — alias for elements.length
        realm.DefineAccessor(obj, "length",
            (in _) => JsValue.Number(_host.CollectFormControls(element).Count), null);
        // action (read/write)
        realm.DefineAccessor(obj, "action",
            (in _) => JsValue.String(DomBridge.TryGetAttribute(element, "action", out var act) ? act : string.Empty),
            (in call) => SetAction(element, in call));
        // reset() — HTML §4.10.21.4. It did not exist, so `form.reset()` was a TypeError on
        // undefined: the call that a "clear this form" control is written as aborted the handler
        // rather than clearing anything, and every edited control kept its edited state.
        realm.DefineValue(obj, "reset",
            realm.NewMethod("reset", (in _) => { _host.ResetForm(element); return JsValue.Undefined; }, 0));
    }

    /// <summary>
    /// <c>form.elements</c> — an <c>HTMLFormControlsCollection</c>: the controls at integer indices,
    /// a <c>length</c>, and a named getter over the controls' <c>name</c> attributes.
    /// </summary>
    /// <remarks>
    /// <b>The indices are installed rather than served by the handler, because that is what this
    /// collection has always done.</b> They are a snapshot taken when the object is built, while
    /// <c>length</c> re-collects on every read — the two can therefore disagree for a collection a
    /// script holds across a mutation, which is a pre-existing defect and not one to fix under a
    /// refactor. Serving them from <see cref="IJsExotic.TryGetIndex"/> would silently make the
    /// collection live and change what an <c>i &lt; length</c> loop walks, so the handler reports no
    /// indices at all and the snapshot stays a snapshot.
    /// </remarks>
    private JsValue BuildElementsCollection(IJsRealm realm, DomElement form)
    {
        var controls = _host.CollectFormControls(form);

        var collection = realm.NewExotic(new FormNamedControls(form, _host, missingIsNull: true));
        for (int i = 0; i < controls.Count; i++)
            realm.DefineIndex(collection, (uint)i, _host.WrapNode(controls[i]));

        realm.DefineAccessor(collection, "length",
            (in _) => JsValue.Number(_host.CollectFormControls(form).Count), null);

        return collection;
    }

    private static JsValue SetAction(DomElement element, in JsCall call)
    {
        // ToJsString, not the handle's rendering: an object assigned to `form.action` runs its own
        // toString, which is the coercion a page observes in the attribute afterwards.
        DomBridge.SetAttr(element, "action", call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    // -------- Constraint validation --------

    /// <summary>Whether <paramref name="element"/> satisfies constraint validation — a form is valid
    /// when all its controls are; a required input/textarea/select needs a non-empty value.</summary>
    internal bool IsElementValid(DomElement element)
    {
        if (string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
            return AreFormChildrenValid(element);

        // A form-associated custom element's validity is whatever it set through its internals; no
        // amount of reading its markup can answer for it.
        if (!_host.IsCustomElementValid(element))
            return false;

        // Individual element validation
        if (!DomBridge.HasAttr(element, "required"))
            return true;

        var tag = element.TagName;
        if (string.Equals(tag, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "textarea", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "select", StringComparison.OrdinalIgnoreCase))
        {
            DomBridge.TryGetAttribute(element, "value", out var val);
            return !string.IsNullOrEmpty(val);
        }

        return true;
    }

    private bool AreFormChildrenValid(DomElement form)
    {
        foreach (var child in DomBridge.ChildElements(form))
        {
            if (!DomBridge.IsText(child) && !IsElementValid(child))
                return false;
            if (!AreFormChildrenValid(child))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The control of <paramref name="form"/> whose <c>name</c> is <paramref name="name"/>, or
    /// <see langword="false"/> when none carries it.
    /// </summary>
    /// <remarks>
    /// Shared by <c>form.elements</c>' named access and the form element's own named getter so the
    /// two cannot answer differently. They differ only in what a miss means, which is the callers'
    /// business: the collection reports <c>null</c>, the form leaves the property undefined.
    /// </remarks>
    internal static bool TryFindNamedControl(DomElement form, string name, IFormHost host, out JsValue control)
    {
        control = JsValue.Undefined;
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var ctrl in host.CollectFormControls(form))
        {
            if (ctrl.GetAttribute("name") is { } controlName &&
                string.Equals(controlName, name, StringComparison.Ordinal))
            {
                control = host.WrapNode(ctrl);
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// The named getter shared by <c>form.elements</c> and the <c>&lt;form&gt;</c> wrapper: a name the
/// object's own properties did not answer resolves to the control carrying it (HTML §4.10.3,
/// §4.10.11).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordinary properties win, and the realm — not this class — enforces it.</b> That inversion is
/// the point of <see cref="IJsExotic"/>: the two classes this replaces each called the base lookup
/// first and then fell through here, so the rule was written twice and could have been written
/// differently. It is now written nowhere in the bridge, because the contract states it.
/// </para>
/// <para>
/// <b><paramref name="missingIsNull"/> is the one thing the two callers disagree about.</b> The
/// collection answers <c>null</c> for a name nothing carries and the form leaves the property
/// undefined, which is what both did before. The collection's <c>null</c> is why it answers every
/// name: a handler that declined would let the miss fall through to <c>undefined</c>. That has one
/// visible consequence — <c>'nothing' in form.elements</c> is now <see langword="true"/> where it was
/// <see langword="false"/>, since the realm asks this same question for <c>in</c>. Answering the read
/// the way it has always been answered is the behaviour worth keeping; the <c>null</c> itself is a
/// pre-existing deviation (WebIDL's named-property getter yields <c>undefined</c> for an unsupported
/// name — it is <c>namedItem()</c> that returns <c>null</c>) and correcting it belongs in its own
/// change.
/// </para>
/// <para>
/// <b>No indices and no supported names, deliberately.</b> The collection installs its indices as
/// ordinary properties, a snapshot, exactly as it always has (see
/// <c>FormBinding.BuildElementsCollection</c>); and neither object enumerated its controls' names
/// before, so supplying them here would add them to <c>Object.keys</c>, <c>for…in</c> and a spread —
/// browser-correct, and a behaviour change rather than a refactor.
/// </para>
/// </remarks>
internal sealed class FormNamedControls(DomElement form, IFormHost host, bool missingIsNull) : IJsExotic
{
    /// <inheritdoc />
    public bool TryGetNamed(string name, out JsValue value)
    {
        if (FormBinding.TryFindNamedControl(form, name, host, out value))
            return true;

        value = JsValue.Null;
        return missingIsNull;
    }

    /// <inheritdoc />
    public bool TryGetIndex(uint index, out JsValue value)
    {
        value = JsValue.Undefined;
        return false;
    }

    /// <inheritdoc />
    public bool TrySetNamed(string name, JsValue value) => false;

    /// <inheritdoc />
    public IReadOnlyList<string> SupportedNames => [];

    /// <inheritdoc />
    public uint IndexedLength => 0;
}

/// <summary>
/// The JS wrapper for a <c>&lt;form&gt;</c>: an ordinary element object that additionally resolves an
/// unknown property name to the control carrying that name — <c>HTMLFormElement</c>'s named getter
/// (HTML §4.10.3).
/// </summary>
/// <remarks>
/// <para>
/// <c>form.elements</c> already offered named access, but the form itself did not, so <c>form.q</c>
/// was <see langword="undefined"/>. That is the spelling pages actually use, and undefined does not
/// announce itself: google.com's homepage hands the result straight to its search-box component —
/// <c>var r=hp_SKb(),u=r.q</c> — which stores it and later reads <c>F.value</c>, throwing
/// <c>Cannot get property value of undefined</c> from a component far from the lookup that failed.
/// </para>
/// <para>
/// Named access is a fallback and not an override: WebIDL consults named properties only when the
/// object and its prototype chain do not already answer, so <c>form.action</c> stays the action
/// attribute even when a control is named <c>action</c>, and <c>form.submit</c> stays the method even
/// when a control is named <c>submit</c>. A name nothing carries is left undefined rather than null,
/// since an absent named property is an absent property.
/// </para>
/// <para>
/// <b>This class is an adapter, and it is meant to be deleted.</b> The lookup it performs is
/// <see cref="FormNamedControls"/>, an <see cref="IJsExotic"/>; what is engine-typed here is only
/// <em>where</em> the handler is consulted from. <c>DomBridge/JsObjects.cs</c> mints the form's
/// wrapper with <c>new</c> and installs every other <c>HTMLFormElement</c> member on the object it
/// gets back, so the wrapper's type is fixed by an unmigrated site; when that site migrates the
/// wrapper becomes <c>realm.NewExotic(new FormNamedControls(form, host, missingIsNull: false))</c>
/// and this subclass goes away. The order below is the order the realm applies, written out: base
/// lookup, then the handler, then the base again with the caller's <c>throwError</c> so a genuine
/// miss fails the way an ordinary miss on this object would.
/// </para>
/// </remarks>
internal sealed class FormElementJSObject : JSObject
{
    private readonly FormNamedControls _named;

    internal FormElementJSObject(DomElement form, IFormHost host) =>
        _named = new FormNamedControls(form, host, missingIsNull: false);

    /// <inheritdoc />
    protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
    {
        var result = base.GetValue(key, receiver, false);
        if (result != null && !result.IsUndefined)
            return result;

        return _named.TryGetNamed(key.Value.ToString(), out var control)
            ? Runtime.JsInterop.ToEngineObject(control)
            : base.GetValue(key, receiver, throwError);
    }
}
