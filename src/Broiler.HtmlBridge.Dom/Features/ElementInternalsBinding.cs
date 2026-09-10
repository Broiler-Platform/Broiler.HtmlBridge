using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <para>
/// <b>The JavaScript vocabulary is JSEAL's</b> (<see cref="IJsRealm"/>). The two JavaScript assets
/// this module installs are source this repository authored and ships, so they run through
/// <see cref="IJsSource.EvaluateHostScript"/> rather than the guest-source entry point — a page's
/// Content-Security-Policy has no say over them. No engine type is named anywhere in this file: the
/// script context <see cref="RegisterInterfaces"/> used to be handed is gone (the module always
/// reached its realm through its host and never read it), and <c>DomBridge.TryReadFormDataEntries</c>
/// now takes a realm and a handle.
/// </para>
/// </remarks>
internal sealed class ElementInternalsBinding(IElementInternalsHost host)
{
    private readonly IElementInternalsHost _host = host;

    private JsValue _internalsPrototype;
    private JsValue _validityPrototype;

    /// <summary>The factory for a <c>CustomStateSet</c>, held here rather than left on the global so
    /// a page cannot mint one out of band.</summary>
    private JsValue _customStateSetFactory;

    /// <summary>
    /// The state behind each <c>ElementInternals</c> — and behind its <c>ValidityState</c>, which is
    /// keyed into the same table so its flags read through to the internals that owns them.
    /// </summary>
    /// <remarks>
    /// Keyed on the object identity behind the handle (see <see cref="IdentityOf"/>) rather than on
    /// the handle itself: a <see cref="JsValue"/> is a struct and cannot be a
    /// <see cref="ConditionalWeakTable{TKey,TValue}"/> key, while the engine object it carries is the
    /// same instance the rest of the bridge's wrapper tables are keyed on. The keys stay weak, so an
    /// internals the page has dropped is not kept alive by this table.
    /// </remarks>
    private readonly ConditionalWeakTable<object, InternalsState> _states = new();

    /// <summary>The internals already attached to an element, so a second <c>attachInternals()</c>
    /// can refuse the way a browser does.</summary>
    private readonly Dictionary<DomElement, JsValue> _byElement = [];

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

        public JsValue Validity { get; set; }

        public JsValue States { get; set; }
    }

    /// <summary>
    /// The identity a weak per-object registry keys on: the reference the handle carries.
    /// </summary>
    /// <remarks>
    /// <b>This used to unwrap to the engine's own object, on the reasoning that a
    /// <see cref="JsValue"/> is a struct and so cannot be a
    /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/> key.</b> The
    /// struct is not the key; the reference it carries is, and
    /// <see cref="JsValue.ObjectIdentity"/> is that reference. It is the same instance this table
    /// was keyed on before, under the one provider that could reach it - so nothing about the
    /// answers changes - and it is now an instance every provider supplies.
    /// </remarks>
    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "a per-object registry was keyed on a handle that is not an object");

    // -------- Registration --------

    /// <summary>
    /// Registers <c>ElementInternals</c>, <c>ValidityState</c> and <c>CustomStateSet</c>, and installs
    /// their members. Runs once per realm, with the other interface constructors.
    /// </summary>
    /// <remarks>
    /// It takes nothing: the realm this installs into is the host's, which is the same realm the
    /// registration hub is building when it calls this.
    /// </remarks>
    internal void RegisterInterfaces()
    {
        var realm = _host.Realm;

        realm.EvaluateHostScript(
            """
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
            """,
            "broiler:element-internals");

        // Captured and then deleted, so the factory is reachable from here and from nowhere a page
        // can call. The deletion was a second evaluation of `delete globalThis.…`; the realm removes
        // an own property directly, which is the same operation with no source to compile.
        _customStateSetFactory = realm.GetProperty(realm.Global, "__broilerMakeCustomStateSet");
        if (!_customStateSetFactory.IsObject)
            _customStateSetFactory = JsValue.Missing;
        realm.DeleteProperty(realm.Global, "__broilerMakeCustomStateSet");

        var internalsConstructor = realm.GetProperty(realm.Global, "ElementInternals");
        var validityConstructor = realm.GetProperty(realm.Global, "ValidityState");
        if (!internalsConstructor.IsObject || !validityConstructor.IsObject)
            return;

        var internalsPrototype = realm.GetProperty(internalsConstructor, "prototype");
        var validityPrototype = realm.GetProperty(validityConstructor, "prototype");
        if (!internalsPrototype.IsObject || !validityPrototype.IsObject)
            return;

        _internalsPrototype = internalsPrototype;
        _validityPrototype = validityPrototype;

        // The two members that answer for any custom element, form-associated or not.
        Getter(realm, internalsPrototype, "shadowRoot", (_, state) => _host.ShadowRootOf(state.Element), formOnly: false);
        Getter(realm, internalsPrototype, "states", StatesOf, formOnly: false);

        Getter(realm, internalsPrototype, "form", (_, state) =>
            _host.FormOwnerOf(state.Element) is { } form ? _host.WrapNode(form) : JsValue.Null);
        Getter(realm, internalsPrototype, "labels", (_, state) => _host.LabelsFor(state.Element));
        Getter(realm, internalsPrototype, "willValidate", (_, state) => JsValue.Boolean(!_host.IsDisabled(state.Element)));
        Getter(realm, internalsPrototype, "validity", ValidityOf);
        Getter(realm, internalsPrototype, "validationMessage", (_, state) => JsValue.String(state.ValidationMessage));

        Method(realm, internalsPrototype, "setFormValue", 1, SetFormValue);
        Method(realm, internalsPrototype, "setValidity", 1, SetValidity);
        Method(realm, internalsPrototype, "checkValidity", 0, (InternalsState state, in JsCall _) => CheckValidity(state));
        // reportValidity would additionally surface the message to the user; there is no presentation
        // surface, so it is checkValidity's answer with the same invalid event, which is the part a
        // page observes.
        Method(realm, internalsPrototype, "reportValidity", 0, (InternalsState state, in JsCall _) => CheckValidity(state));

        foreach (var flag in ValidityFlags)
        {
            var name = flag;
            realm.DefineAccessor(
                validityPrototype,
                name,
                (in call) => JsValue.Boolean(StateForValidity(in call, name).Flags.Contains(name)),
                null);
        }

        realm.DefineAccessor(
            validityPrototype,
            "valid",
            (in call) => JsValue.Boolean(StateForValidity(in call, "valid").Flags.Count == 0),
            null);
    }

    // -------- attachInternals --------

    /// <summary>
    /// <c>element.attachInternals()</c>. Installed on every HTML element wrapper, because that is
    /// where a browser puts it — on <c>HTMLElement.prototype</c>, refusing at call time rather than
    /// being absent on the elements it refuses for.
    /// </summary>
    internal JsValue AttachInternals(DomElement element)
    {
        var realm = _host.Realm;

        if (_byElement.ContainsKey(element))
        {
            throw realm.DomError(
                "NotSupportedError",
                "Failed to execute 'attachInternals' on 'HTMLElement': ElementInternals for the specified element was already attached.");
        }

        if (!_host.IsCustomElement(element))
        {
            throw realm.DomError(
                "NotSupportedError",
                "Failed to execute 'attachInternals' on 'HTMLElement': Unable to attach ElementInternals to non-custom elements.");
        }

        var internals = realm.NewObject();
        if (_internalsPrototype.IsObject)
            realm.SetPrototype(internals, _internalsPrototype);

        _states.Add(IdentityOf(internals), new InternalsState(element));
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
        if (!TryGetState(element, out var state))
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
        !TryGetState(element, out var state) || state.Flags.Count == 0;

    /// <summary>Forgets an element's internals — used when its shadow of state must not outlive it in
    /// the by-element table.</summary>
    internal void Forget(DomElement element) => _byElement.Remove(element);

    private bool TryGetState(DomElement element, out InternalsState state)
    {
        if (_byElement.TryGetValue(element, out var internals) &&
            _states.TryGetValue(IdentityOf(internals), out var found))
        {
            state = found;
            return true;
        }

        state = null!;
        return false;
    }

    // -------- members --------

    private JsValue StatesOf(IJsRealm realm, InternalsState state)
    {
        if (state.States.IsObject)
            return state.States;

        if (!_customStateSetFactory.IsObject)
            return JsValue.Undefined;

        var set = realm.Invoke(_customStateSetFactory, JsValue.Undefined);
        if (!set.IsObject)
            return JsValue.Undefined;

        state.States = set;
        return set;
    }

    private JsValue ValidityOf(IJsRealm realm, InternalsState state)
    {
        if (state.Validity.IsObject)
            return state.Validity;

        var validity = realm.NewObject();
        if (_validityPrototype.IsObject)
            realm.SetPrototype(validity, _validityPrototype);

        // Keyed into the same table as the internals, so the flag getters read the live state rather
        // than a copy taken when the object was built.
        _states.Add(IdentityOf(validity), state);
        state.Validity = validity;
        return validity;
    }

    /// <summary>
    /// <c>setFormValue(value, state?)</c>. The second argument is the state a browser would hand back
    /// through <c>formStateRestoreCallback</c>; this engine restores no state, so it is accepted and
    /// not retained rather than being rejected — a component that passes it must still work.
    /// </summary>
    private JsValue SetFormValue(InternalsState state, in JsCall call)
    {
        var value = call.Length > 0 ? call[0] : JsValue.Undefined;
        state.SubmissionValue = null;
        state.SubmissionEntries = null;

        if (value.IsNullish)
            return JsValue.Undefined;

        // FormData is recognised by shape, through the members the reader reaches on the object.
        // The object test stays ahead of the call: it is what the engine-typed pattern match
        // performed, and a primitive carries none of those members anyway.
        if (value.IsObject &&
            DomBridge.TryReadFormDataEntries(call.Realm, value, out var entries))
        {
            state.SubmissionEntries = entries;
            return JsValue.Undefined;
        }

        // The observable ECMAScript coercion: a submission value with its own toString participates,
        // which is what a page passing a wrapper object around expects.
        state.SubmissionValue = call.Realm.ToJsString(value);
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>setValidity(flags, message?, anchor?)</c>. Any flag set makes the element invalid and a
    /// message is then required — measured, an omitted message with a flag raised is a
    /// <c>TypeError</c> rather than an empty message.
    /// </summary>
    private JsValue SetValidity(InternalsState state, in JsCall call)
    {
        var realm = call.Realm;
        var raised = new HashSet<string>(StringComparer.Ordinal);
        if (call.Length > 0 && call[0].IsObject)
        {
            var flags = call[0];
            foreach (var flag in ValidityFlags)
            {
                if (realm.GetProperty(flags, flag).AsBoolean)
                    raised.Add(flag);
            }
        }

        var message = call.Length > 1 && !call[1].IsNullish ? realm.ToJsString(call[1]) : null;
        if (raised.Count > 0 && string.IsNullOrEmpty(message))
        {
            throw realm.Error(
                JsErrorKind.TypeError,
                "Failed to execute 'setValidity' on 'ElementInternals': " +
                "The second argument should not be empty if one or more flags in the first argument are true.");
        }

        state.Flags.Clear();
        foreach (var flag in raised)
            state.Flags.Add(flag);
        state.ValidationMessage = raised.Count > 0 ? message! : string.Empty;
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>checkValidity()</c> — and <c>reportValidity()</c>, which differs only in surfacing the
    /// message. An invalid element receives an <c>invalid</c> event first, which is how a page hears
    /// about the failure without polling every control.
    /// </summary>
    private JsValue CheckValidity(InternalsState state)
    {
        if (state.Flags.Count == 0 || _host.IsDisabled(state.Element))
            return JsValue.True;

        _host.DispatchInvalidEvent(state.Element);
        return JsValue.False;
    }

    // -------- plumbing --------

    private delegate JsValue InternalsOperation(InternalsState state, in JsCall call);

    private void Method(IJsRealm realm, JsValue prototype, string name, int length, InternalsOperation body) =>
        realm.DefineValue(
            prototype,
            name,
            realm.NewMethod(name, (in call) => body(StateFor(in call, name, execute: true), in call), length));

    private void Getter(IJsRealm realm, JsValue prototype, string name, Func<IJsRealm, InternalsState, JsValue> read, bool formOnly = true) =>
        realm.DefineAccessor(
            prototype,
            name,
            (in call) => read(call.Realm, StateFor(in call, name, execute: false, formOnly: formOnly)),
            null);

    /// <summary>
    /// The state behind the receiver, refusing for a receiver that is not an <c>ElementInternals</c>
    /// and — for every form-related member — for one whose element is not form-associated.
    /// </summary>
    private InternalsState StateFor(in JsCall call, string member, bool execute, bool formOnly = true)
    {
        if (!call.This.IsObject || !_states.TryGetValue(IdentityOf(call.This), out var state))
        {
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                $"Failed to {(execute ? "execute" : "read")} '{member}' {(execute ? "on" : "from")} 'ElementInternals': Illegal invocation");
        }

        if (formOnly && !_host.IsFormAssociatedCustomElement(state.Element))
        {
            throw call.Realm.DomError(
                "NotSupportedError",
                execute
                    ? $"Failed to execute '{member}' on 'ElementInternals': The target element is not a form-associated custom element."
                    : $"Failed to read the '{member}' property from 'ElementInternals': The target element is not a form-associated custom element.");
        }

        return state;
    }

    private InternalsState StateForValidity(in JsCall call, string member)
    {
        if (call.This.IsObject && _states.TryGetValue(IdentityOf(call.This), out var state))
            return state;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to read the '{member}' property from 'ValidityState': Illegal invocation");
    }
}
