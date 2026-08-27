using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Form-associated custom elements (HTML §4.13.5): <c>attachInternals()</c>, the
/// <c>ElementInternals</c> object it hands back, and the <c>ValidityState</c> and
/// <c>CustomStateSet</c> that hang off it.
/// </summary>
/// <remarks>
/// <para>
/// This is the last of the three capabilities the Custom Elements slice named and left out. It is
/// what lets a component be a <em>control</em> rather than a widget that happens to sit inside a
/// form: <c>attachInternals()</c> was undefined, so the constructor line every such component opens
/// with — <c>this.internals_ = this.attachInternals()</c> — was a <c>TypeError</c> that took the
/// constructor down, and with it the upgrade of every instance on the page.
/// </para>
/// <para>
/// <b>The members live on the prototype and an instance has no own properties</b>, with the
/// per-instance state in a weak table — the shape <c>Range</c>, <c>Selection</c> and <c>Blob</c>
/// established, and measured: Chromium reports zero own property names on an
/// <c>ElementInternals</c>. <c>ValidityState</c> is keyed into the same table, so
/// <c>internals.validity</c> is one object whose flags track the internals rather than a snapshot.
/// </para>
/// <para>
/// <b>Every form-related member refuses on an element that is not form-associated</b>, rather than
/// answering an empty or neutral value. That distinction is observable and specified: a component
/// that calls <c>attachInternals()</c> without declaring <c>static formAssociated = true</c> gets an
/// object whose <c>states</c> and <c>shadowRoot</c> work and whose <c>form</c>, <c>labels</c>,
/// <c>willValidate</c>, <c>validity</c>, <c>validationMessage</c>, <c>setFormValue</c>,
/// <c>setValidity</c>, <c>checkValidity</c> and <c>reportValidity</c> are each a
/// <c>NotSupportedError</c> naming that reason. Answering <c>null</c> for <c>form</c> there would
/// say "this control has no form" where the truth is "this is not a control".
/// </para>
/// <para>
/// <b><c>setFormValue</c> is not a shape-only stub.</b> The value it records is the element's
/// submission value, and it is read back where a browser reads it: constructing a form's entry list,
/// which is what <c>new FormData(form)</c> hands over. A <c>FormData</c> argument contributes its own
/// entries and the element's <c>name</c> is not used; <c>null</c> means the element submits nothing.
/// </para>
/// <para>
/// <b><c>formStateRestoreCallback</c> is deliberately never fired.</b> It reports a value restored by
/// session history or an autofill pass, and this engine performs neither — firing it with the value
/// the page just set would be an invention rather than a restoration.
/// </para>
/// <para>Every expectation is Chromium's measured answer over the same probe run against both.</para>
/// </remarks>
internal sealed class ElementInternalsBinding(IElementInternalsHost host)
{
    private readonly IElementInternalsHost _host = host;

    private JSObject? _internalsPrototype;
    private JSObject? _validityPrototype;

    /// <summary>The factory for a <c>CustomStateSet</c>, held here rather than left on the global so
    /// a page cannot mint one out of band.</summary>
    private JSObject? _customStateSetFactory;

    /// <summary>
    /// The state behind each <c>ElementInternals</c> — and behind its <c>ValidityState</c>, which is
    /// keyed into the same table so its flags read through to the internals that owns them.
    /// </summary>
    private readonly ConditionalWeakTable<JSObject, InternalsState> _states = new();

    /// <summary>The internals already attached to an element, so a second <c>attachInternals()</c>
    /// can refuse the way a browser does.</summary>
    private readonly Dictionary<DomElement, JSObject> _byElement = [];

    /// <summary>
    /// The validity flag names, in the order <c>ValidityState</c> exposes them — measured from
    /// Chromium's own <c>for…in</c> over an input's <c>validity</c>. <c>valid</c> is derived and
    /// comes last.
    /// </summary>
    private static readonly string[] ValidityFlags =
    [
        "valueMissing", "typeMismatch", "patternMismatch", "tooLong", "tooShort",
        "rangeUnderflow", "rangeOverflow", "stepMismatch", "badInput", "customError",
    ];

    private sealed class InternalsState(DomElement element)
    {
        public DomElement Element { get; } = element;

        /// <summary>The validity flags currently set. Empty means valid.</summary>
        public HashSet<string> Flags { get; } = new(StringComparer.Ordinal);

        public string ValidationMessage { get; set; } = string.Empty;

        /// <summary>The element's submission value as a single string, or <see langword="null"/> when
        /// it submits nothing or submits through <see cref="SubmissionEntries"/>.</summary>
        public string? SubmissionValue { get; set; }

        /// <summary>The entries a <c>FormData</c> submission value contributes, which replace the
        /// element's own <c>name</c>/value pair rather than adding to it.</summary>
        public List<KeyValuePair<string, string>>? SubmissionEntries { get; set; }

        public JSObject? Validity { get; set; }

        public JSObject? States { get; set; }
    }

    // -------- Registration --------

    /// <summary>
    /// Registers <c>ElementInternals</c>, <c>ValidityState</c> and <c>CustomStateSet</c>, and installs
    /// their members. Runs once per context, with the other interface constructors.
    /// </summary>
    internal void RegisterInterfaces(JSContext context)
    {
        context.Eval("""
            (function () {
                // None of the three is constructible: they come from attachInternals() and from the
                // members of the object it returns.
                function ElementInternals() { throw new TypeError("Failed to construct 'ElementInternals': Illegal constructor"); }
                function ValidityState() { throw new TypeError("Failed to construct 'ValidityState': Illegal constructor"); }
                function CustomStateSet() { throw new TypeError("Failed to construct 'CustomStateSet': Illegal constructor"); }
                globalThis.ElementInternals = ElementInternals;
                globalThis.ValidityState = ValidityState;
                globalThis.CustomStateSet = CustomStateSet;

                // CustomStateSet is a setlike interface, so it is written in JavaScript over a real
                // Set: that gets the iteration protocol — for…of, values/keys/entries, forEach —
                // right by construction rather than by re-deriving it through host functions. The
                // backing set is a WeakMap entry, so an instance carries no own properties, the same
                // shape ElementInternals itself uses.
                var backing = new WeakMap();
                function raw(self) {
                    var set = backing.get(self);
                    if (!set) throw new TypeError('Illegal invocation');
                    return set;
                }

                Object.defineProperty(CustomStateSet.prototype, 'size', {
                    get: function () { return raw(this).size; }, enumerable: true, configurable: true
                });
                CustomStateSet.prototype.add = function (value) { raw(this).add(String(value)); return this; };
                CustomStateSet.prototype.delete = function (value) { return raw(this).delete(String(value)); };
                CustomStateSet.prototype.has = function (value) { return raw(this).has(String(value)); };
                CustomStateSet.prototype.clear = function () { raw(this).clear(); };
                CustomStateSet.prototype.forEach = function (callback, thisArg) {
                    var self = this;
                    raw(this).forEach(function (value) { callback.call(thisArg, value, value, self); });
                };
                CustomStateSet.prototype.values = function () { return raw(this).values(); };
                CustomStateSet.prototype.keys = function () { return raw(this).keys(); };
                CustomStateSet.prototype.entries = function () { return raw(this).entries(); };
                CustomStateSet.prototype[Symbol.iterator] = CustomStateSet.prototype.values;

                globalThis.__broilerMakeCustomStateSet = function () {
                    var set = Object.create(CustomStateSet.prototype);
                    backing.set(set, new Set());
                    return set;
                };
            })();
            """);

        // Captured and then deleted, so the factory is reachable from here and from nowhere a page
        // can call.
        _customStateSetFactory = context["__broilerMakeCustomStateSet"] as JSObject;
        context.Eval("delete globalThis.__broilerMakeCustomStateSet;");

        if (context["ElementInternals"] is not JSObject internalsConstructor ||
            internalsConstructor[(KeyString)"prototype"] is not JSObject internalsPrototype ||
            context["ValidityState"] is not JSObject validityConstructor ||
            validityConstructor[(KeyString)"prototype"] is not JSObject validityPrototype)
            return;

        _internalsPrototype = internalsPrototype;
        _validityPrototype = validityPrototype;

        // The two members that answer for any custom element, form-associated or not.
        Getter(internalsPrototype, "shadowRoot", state => _host.ShadowRootOf(state.Element), formOnly: false);
        Getter(internalsPrototype, "states", StatesOf, formOnly: false);

        Getter(internalsPrototype, "form", state =>
            _host.FormOwnerOf(state.Element) is { } form ? _host.ToJSObject(form) : JSNull.Value);
        Getter(internalsPrototype, "labels", state => _host.LabelsFor(state.Element));
        Getter(internalsPrototype, "willValidate", state => Bool(!_host.IsDisabled(state.Element)));
        Getter(internalsPrototype, "validity", ValidityOf);
        Getter(internalsPrototype, "validationMessage", state => new JSString(state.ValidationMessage));

        Method(internalsPrototype, "setFormValue", 1, SetFormValue);
        Method(internalsPrototype, "setValidity", 1, SetValidity);
        Method(internalsPrototype, "checkValidity", 0, (InternalsState state, in Arguments _) => CheckValidity(state));
        // reportValidity would additionally surface the message to the user; there is no presentation
        // surface, so it is checkValidity's answer with the same invalid event, which is the part a
        // page observes.
        Method(internalsPrototype, "reportValidity", 0, (InternalsState state, in Arguments _) => CheckValidity(state));

        foreach (var flag in ValidityFlags)
        {
            var name = flag;
            validityPrototype.FastAddProperty(
                name,
                new DomFunction((in a) => Bool(StateForValidity(in a, name).Flags.Contains(name)), $"get {name}"),
                null,
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        validityPrototype.FastAddProperty(
            "valid",
            new DomFunction((in a) => Bool(StateForValidity(in a, "valid").Flags.Count == 0), "get valid"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    // -------- attachInternals --------

    /// <summary>
    /// <c>element.attachInternals()</c>. Installed on every HTML element wrapper, because that is
    /// where a browser puts it — on <c>HTMLElement.prototype</c>, refusing at call time rather than
    /// being absent on the elements it refuses for.
    /// </summary>
    internal JSValue AttachInternals(DomElement element, in Arguments a)
    {
        if (_byElement.ContainsKey(element))
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'attachInternals' on 'HTMLElement': ElementInternals for the specified element was already attached.",
                "NotSupportedError");
            return JSUndefined.Value;
        }

        if (!_host.IsCustomElement(element))
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'attachInternals' on 'HTMLElement': Unable to attach ElementInternals to non-custom elements.",
                "NotSupportedError");
            return JSUndefined.Value;
        }

        var internals = new JSObject();
        if (_internalsPrototype is not null)
            internals.BasePrototypeObject = _internalsPrototype;

        _states.Add(internals, new InternalsState(element));
        _byElement[element] = internals;
        return internals;
    }

    // -------- form-association reads the bridge needs --------

    /// <summary>
    /// The element's submission entries for a form's entry list, or <see langword="null"/> when it
    /// contributes nothing. <paramref name="name"/> is the element's <c>name</c> content attribute.
    /// </summary>
    internal IReadOnlyList<KeyValuePair<string, string>>? SubmissionEntriesFor(DomElement element, string? name)
    {
        if (!_byElement.TryGetValue(element, out var internals) || !_states.TryGetValue(internals, out var state))
            return null;

        if (state.SubmissionEntries is { } entries)
            return entries;

        // No name, no entry — the same rule an ordinary control follows, and the reason a page that
        // forgets the name attribute sees nothing submitted.
        return state.SubmissionValue is { } value && !string.IsNullOrEmpty(name)
            ? [new KeyValuePair<string, string>(name, value)]
            : null;
    }

    /// <summary>Whether the element's own validity (as set through <c>setValidity</c>) is satisfied.
    /// A form's validity is the conjunction of its controls', so this is what a form asks.</summary>
    internal bool IsValid(DomElement element) =>
        !_byElement.TryGetValue(element, out var internals) ||
        !_states.TryGetValue(internals, out var state) ||
        state.Flags.Count == 0;

    /// <summary>Forgets an element's internals — used when its shadow of state must not outlive it in
    /// the by-element table.</summary>
    internal void Forget(DomElement element) => _byElement.Remove(element);

    // -------- members --------

    private JSValue StatesOf(InternalsState state)
    {
        if (state.States is { } existing)
            return existing;

        if (_customStateSetFactory is not { } factory)
            return JSUndefined.Value;

        if (factory.InvokeFunction(new Arguments(JSUndefined.Value)) is not JSObject set)
            return JSUndefined.Value;

        state.States = set;
        return set;
    }

    private JSValue ValidityOf(InternalsState state)
    {
        if (state.Validity is { } existing)
            return existing;

        var validity = new JSObject();
        if (_validityPrototype is not null)
            validity.BasePrototypeObject = _validityPrototype;

        // Keyed into the same table as the internals, so the flag getters read the live state rather
        // than a copy taken when the object was built.
        _states.Add(validity, state);
        state.Validity = validity;
        return validity;
    }

    /// <summary>
    /// <c>setFormValue(value, state?)</c>. The second argument is the state a browser would hand back
    /// through <c>formStateRestoreCallback</c>; this engine restores no state, so it is accepted and
    /// not retained rather than being rejected — a component that passes it must still work.
    /// </summary>
    private JSValue SetFormValue(InternalsState state, in Arguments a)
    {
        var value = a.Length > 0 ? a[0] : JSUndefined.Value;
        state.SubmissionValue = null;
        state.SubmissionEntries = null;

        if (value.IsNull || value.IsUndefined)
            return JSUndefined.Value;

        if (value is JSObject entrySource && DomBridge.TryReadFormDataEntries(entrySource, out var entries))
        {
            state.SubmissionEntries = entries;
            return JSUndefined.Value;
        }

        state.SubmissionValue = value.ToString();
        return JSUndefined.Value;
    }

    /// <summary>
    /// <c>setValidity(flags, message?, anchor?)</c>. Any flag set makes the element invalid and a
    /// message is then required — measured, an omitted message with a flag raised is a
    /// <c>TypeError</c> rather than an empty message.
    /// </summary>
    private JSValue SetValidity(InternalsState state, in Arguments a)
    {
        var raised = new HashSet<string>(StringComparer.Ordinal);
        if (a.Length > 0 && a[0] is JSObject flags)
        {
            foreach (var flag in ValidityFlags)
            {
                if (flags[(KeyString)flag] is { } value && value.BooleanValue)
                    raised.Add(flag);
            }
        }

        var message = a.Length > 1 && !a[1].IsUndefined && !a[1].IsNull ? a[1].ToString() : null;
        if (raised.Count > 0 && string.IsNullOrEmpty(message))
        {
            return JSException.ThrowTypeError<JSValue>(
                "Failed to execute 'setValidity' on 'ElementInternals': " +
                "The second argument should not be empty if one or more flags in the first argument are true.");
        }

        state.Flags.Clear();
        foreach (var flag in raised)
            state.Flags.Add(flag);
        state.ValidationMessage = raised.Count > 0 ? message! : string.Empty;
        return JSUndefined.Value;
    }

    /// <summary>
    /// <c>checkValidity()</c> — and <c>reportValidity()</c>, which differs only in surfacing the
    /// message. An invalid element receives an <c>invalid</c> event first, which is how a page hears
    /// about the failure without polling every control.
    /// </summary>
    private JSValue CheckValidity(InternalsState state)
    {
        if (state.Flags.Count == 0 || _host.IsDisabled(state.Element))
            return JSBoolean.True;

        _host.DispatchInvalidEvent(state.Element);
        return JSBoolean.False;
    }

    private static JSValue Bool(bool value) => value ? JSBoolean.True : JSBoolean.False;

    // -------- plumbing --------

    private delegate JSValue InternalsOperation(InternalsState state, in Arguments a);

    private void Method(JSObject prototype, string name, int length, InternalsOperation body) =>
        prototype.FastAddValue(
            name,
            new DomFunction((in a) => body(StateFor(in a, name, execute: true), in a), name, length),
            JSPropertyAttributes.EnumerableConfigurableValue);

    private void Getter(JSObject prototype, string name, Func<InternalsState, JSValue> read, bool formOnly = true) =>
        prototype.FastAddProperty(
            name,
            new DomFunction((in a) => read(StateFor(in a, name, execute: false, formOnly: formOnly)), $"get {name}"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

    /// <summary>
    /// The state behind the receiver, refusing for a receiver that is not an <c>ElementInternals</c>
    /// and — for every form-related member — for one whose element is not form-associated.
    /// </summary>
    private InternalsState StateFor(in Arguments a, string member, bool execute, bool formOnly = true)
    {
        if (a.This is not JSObject receiver || !_states.TryGetValue(receiver, out var state))
        {
            return JSException.ThrowTypeError<InternalsState>(
                $"Failed to {(execute ? "execute" : "read")} '{member}' {(execute ? "on" : "from")} 'ElementInternals': Illegal invocation");
        }

        if (formOnly && !_host.IsFormAssociatedCustomElement(state.Element))
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                execute
                    ? $"Failed to execute '{member}' on 'ElementInternals': The target element is not a form-associated custom element."
                    : $"Failed to read the '{member}' property from 'ElementInternals': The target element is not a form-associated custom element.",
                "NotSupportedError");
        }

        return state;
    }

    private InternalsState StateForValidity(in Arguments a, string member)
    {
        if (a.This is JSObject receiver && _states.TryGetValue(receiver, out var state))
            return state;

        return JSException.ThrowTypeError<InternalsState>(
            $"Failed to read the '{member}' property from 'ValidityState': Illegal invocation");
    }
}
