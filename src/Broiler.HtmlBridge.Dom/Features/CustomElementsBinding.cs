using System.Text.RegularExpressions;
using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Custom Elements (HTML §4.13) — the <c>customElements</c> registry, a constructible
/// <c>HTMLElement</c> base, and the reaction callbacks a definition receives.
/// </summary>
/// <remarks>
/// <para>
/// There was no production implementation at all: <c>customElements</c> was undefined and
/// <c>HTMLElement</c> threw <c>Illegal constructor</c>, so <c>class X extends HTMLElement</c>
/// followed by <c>customElements.define(…)</c> failed on the bare name and took the whole script
/// with it. The WPT runner carried a shim to get past that, which had to fake the parts it could not
/// reach — its <c>HTMLElement</c> handed back a plain element that did not carry the class's
/// prototype, so the shim copied the reaction callbacks across by hand and a component's own methods
/// were simply unreachable.
/// </para>
/// <para>
/// <b>Why the base constructor is JavaScript and everything else is not.</b> Constructing a custom
/// element is the one step that needs <c>new.target</c>: <c>new X()</c> runs <c>X</c>'s constructor,
/// which calls <c>super()</c>, and only <c>new.target</c> says which subclass is being built and so
/// which prototype and tag name the element must get. A host function cannot see it — the engine's
/// <c>Arguments</c> does not carry it — so the base lives in JavaScript and calls back here for the
/// element itself. Everything else (the registry, name validation, upgrades, reaction dispatch) is
/// C#, where the DOM is.
/// </para>
/// <para>
/// <b>Upgrading reuses the same constructor path rather than a second one</b>, which is what the
/// specification's "custom element construction stack" is for. Upgrading pushes the existing element
/// onto <see cref="_constructionStack"/> and calls the definition's constructor; the base's callback
/// finds a pending element and hands that one back instead of minting a new one, so the author's
/// constructor body runs against the element already in the tree. Without it an upgrade would have
/// to copy attributes and children onto a fresh element and swap it in — which is what the shim did,
/// and it loses node identity: a page holding a reference to the element before the definition
/// landed would keep pointing at the discarded one.
/// </para>
/// <para>
/// Reactions are dispatched off the canonical <c>DomDocument.Mutated</c> stream, which is raised
/// synchronously at mutation time. That matters: a browser runs <c>connectedCallback</c> before the
/// statement after <c>appendChild</c>, so building this on <c>MutationObserver</c> — whose delivery
/// is a microtask — would have put every reaction one checkpoint late.
/// </para>
/// <para>
/// <b>Customized built-ins and <c>adoptedCallback</c> are in.</b> A definition may name the built-in
/// it extends, and an element then has an <em>is value</em> as well as a local name — which is what
/// <see cref="_isValues"/> holds and what <see cref="DefinitionFor"/> matches on. The is value is not
/// the <c>is</c> content attribute: an element parsed from <c>&lt;button is="fancy-b"&gt;</c> has
/// both, but <c>new FancyButton()</c> and <c>createElement('button', {is: 'fancy-b'})</c> produce an
/// element whose <c>getAttribute('is')</c> is <see langword="null"/> while it still serializes as
/// <c>&lt;button is="fancy-b"&gt;</c>. Measured against Chromium, both halves.
/// </para>
/// <para>
/// <b>Not in this slice:</b> form-associated custom elements (<c>formAssociated</c>,
/// <c>attachInternals</c>, <c>ElementInternals</c>). It is a separate capability rather than a piece
/// of this one.
/// </para>
/// </remarks>
internal sealed partial class CustomElementsBinding(ICustomElementsHost host)
{
    private readonly ICustomElementsHost _host = host;

    /// <summary>The definitions, by tag name.</summary>
    private readonly Dictionary<string, CustomElementDefinition> _byName = new(StringComparer.Ordinal);

    /// <summary>The same definitions by constructor, so <c>getName</c> and the base's callback can
    /// go the other way. One constructor may define only one element (HTML §4.13.4).</summary>
    private readonly Dictionary<JSObject, CustomElementDefinition> _byConstructor = [];

    /// <summary>
    /// The elements currently being upgraded, innermost last — the specification's custom element
    /// construction stack. Non-empty exactly while a definition's constructor is running for an
    /// element that already exists.
    /// </summary>
    private readonly List<DomElement> _constructionStack = [];

    /// <summary><c>whenDefined</c> resolvers waiting on a name that is not defined yet.</summary>
    private readonly Dictionary<string, List<JSObject>> _whenDefined = new(StringComparer.Ordinal);

    /// <summary>
    /// The <em>is value</em> of an element that has one without carrying an <c>is</c> content
    /// attribute — what <c>new FancyButton()</c> and <c>createElement('button', {is: …})</c> produce.
    /// </summary>
    /// <remarks>
    /// Kept beside the registry rather than on the element, because it is only meaningful to the
    /// registry: it is what decides which definition an element belongs to, and it is the one piece
    /// of custom-element state a content attribute cannot always carry. An element parsed with
    /// <c>is="fancy-b"</c> is not in this table — its attribute is the is value — which is why
    /// <see cref="IsValueOf"/> reads both.
    /// </remarks>
    private readonly Dictionary<DomElement, string> _isValues = [];

    /// <summary>
    /// One definition. <paramref name="Name"/> is the custom element name the registry is keyed by;
    /// <paramref name="LocalName"/> is the tag an element of it actually has — the same string for an
    /// autonomous element, and the <c>extends</c> option's value for a customized built-in.
    /// </summary>
    private sealed record CustomElementDefinition(
        string Name,
        string LocalName,
        JSObject Constructor,
        HashSet<string> ObservedAttributes,
        bool HasAttributeChangedCallback,
        bool FormAssociated)
    {
        /// <summary>Whether this definition extends a built-in element rather than defining a new tag.</summary>
        public bool IsCustomizedBuiltIn => !string.Equals(Name, LocalName, StringComparison.Ordinal);
    }

    /// <summary>
    /// A valid custom element name (HTML §4.13.1): starts with an ASCII lower alpha, contains a
    /// hyphen, and holds no upper-case letters.
    /// </summary>
    /// <remarks>
    /// The reserved names below are the SVG and MathML element names that already contain a hyphen,
    /// so they would otherwise pass the shape test while naming something that exists. Measured
    /// against a browser rather than transcribed: each is a <c>SyntaxError</c>, as is a name with no
    /// hyphen, an empty name, one with an upper-case letter, and one starting with a digit.
    /// </remarks>
    [GeneratedRegex(@"^[a-z][-._0-9a-z]*-[-._0-9a-z]*$", RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex ValidNamePattern();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
    {
        "annotation-xml", "color-profile", "font-face", "font-face-src", "font-face-uri",
        "font-face-format", "font-face-name", "missing-glyph",
    };

    internal static bool IsValidCustomElementName(string name) =>
        name.Length > 0 && !ReservedNames.Contains(name) && ValidNamePattern().IsMatch(name);

    /// <summary>Whether <paramref name="tagName"/> has a definition — used by the element-wrapper
    /// interface lookup so a defined element reports its own class.</summary>
    internal bool IsDefined(string tagName) => _byName.ContainsKey(tagName);

    /// <summary>Whether the element is a custom element — the gate <c>attachInternals</c> applies,
    /// which a browser refuses for an ordinary one.</summary>
    internal bool IsCustom(DomElement element) => _upgraded.Contains(element);

    /// <summary>Whether the element is an upgraded custom element whose definition declared
    /// <c>static formAssociated = true</c>.</summary>
    internal bool IsFormAssociated(DomElement element) =>
        _upgraded.Contains(element) && DefinitionFor(element) is { FormAssociated: true };

    /// <summary>
    /// The element's <em>is value</em>: the name of the definition it claims to belong to, or
    /// <see langword="null"/> for an ordinary element.
    /// </summary>
    /// <remarks>
    /// The internal value wins over the content attribute because it is the one an element created
    /// through <c>createElement(tag, {is})</c> or a constructor has, and such an element carries no
    /// <c>is</c> attribute at all. An element parsed from markup has only the attribute. Neither
    /// alone covers both shapes.
    /// </remarks>
    private string? IsValueOf(DomElement element) =>
        _isValues.TryGetValue(element, out var recorded) ? recorded
        : DomBridge.TryGetAttribute(element, "is", out var attribute) && attribute.Length > 0 ? attribute
        : null;

    /// <summary>
    /// Notes an element's is value. Called for an element created with an <c>is</c> option whose name
    /// is not defined yet — the value has to survive so a later <c>define</c> can upgrade it, and so
    /// serialization can report it.
    /// </summary>
    internal void RecordIsValue(DomElement element, string isValue) => _isValues[element] = isValue;

    /// <summary>The is value serialization should report for <paramref name="element"/>, or
    /// <see langword="null"/> when the element has none of its own.</summary>
    internal string? SerializedIsValue(DomElement element) =>
        _isValues.TryGetValue(element, out var recorded) ? recorded : null;

    /// <summary>
    /// The definition <paramref name="element"/> belongs to, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// An is value is authoritative when there is one: it selects the definition, and the definition
    /// only applies if its local name is the element's tag. That last test is what keeps
    /// <c>&lt;div is="fancy-b"&gt;</c> an ordinary <c>&lt;div&gt;</c> — measured, a browser leaves it
    /// alone rather than upgrading it against the wrong interface. With no is value the element can
    /// only be an autonomous custom element, which is why a customized-built-in definition is
    /// excluded here: <c>&lt;button&gt;</c> with a <c>button</c>-extending definition in the registry
    /// is still a plain button.
    /// </remarks>
    private CustomElementDefinition? DefinitionFor(DomElement element)
    {
        var tag = DomBridge.AsciiToLower(element.TagName);
        if (IsValueOf(element) is { } isValue)
        {
            return _byName.TryGetValue(isValue, out var customized) &&
                   string.Equals(customized.LocalName, tag, StringComparison.Ordinal)
                ? customized
                : null;
        }

        return _byName.TryGetValue(tag, out var autonomous) && !autonomous.IsCustomizedBuiltIn
            ? autonomous
            : null;
    }

    // ---------------- registry ----------------

    /// <summary><c>customElements.define(name, constructor)</c>.</summary>
    internal JSValue Define(in Arguments a)
    {
        var context = _host.JsContext;
        var name = a.Length > 0 ? a[0].ToString() : string.Empty;

        if (a.Length < 2 || a[1] is not JSObject constructor || constructor is not JSFunction)
            throw new JSException(new JSString(
                "TypeError: Failed to execute 'define' on 'CustomElementRegistry': the constructor is not a constructor."));

        if (!IsValidCustomElementName(name))
        {
            DomBridge.ThrowDOMException(
                context,
                $"Failed to execute 'define' on 'CustomElementRegistry': '{name}' is not a valid custom element name.",
                "SyntaxError");
            return JSUndefined.Value;
        }

        // The `extends` option names the built-in this definition customizes; absent, null or
        // undefined all mean an autonomous element whose local name is its own name.
        var localName = name;
        if (a.Length > 2 && a[2] is JSObject options && options[(KeyString)"extends"] is { } extends &&
            !extends.IsUndefined && !extends.IsNull)
        {
            if (!TryResolveExtends(context, extends.ToString(), out localName))
                return JSUndefined.Value;
        }

        if (_byName.ContainsKey(name))
        {
            DomBridge.ThrowDOMException(
                context,
                $"Failed to execute 'define' on 'CustomElementRegistry': the name '{name}' has already been used.",
                "NotSupportedError");
            return JSUndefined.Value;
        }

        if (_byConstructor.ContainsKey(constructor))
        {
            DomBridge.ThrowDOMException(
                context,
                "Failed to execute 'define' on 'CustomElementRegistry': this constructor has already been used.",
                "NotSupportedError");
            return JSUndefined.Value;
        }

        var definition = new CustomElementDefinition(
            name,
            localName,
            constructor,
            ReadObservedAttributes(constructor),
            constructor[(KeyString)"prototype"] is JSObject prototype &&
                prototype[(KeyString)"attributeChangedCallback"] is JSFunction,
            // formAssociated is read off the constructor, not the prototype: it is a static, and an
            // instance getter of the same name deliberately does not count — measured, a class
            // declaring `get formAssociated()` gets a NotSupportedError from attachInternals.
            constructor[(KeyString)"formAssociated"] is { } formAssociated && formAssociated.BooleanValue);

        _byName[name] = definition;
        _byConstructor[constructor] = definition;

        UpgradeDefined(definition);
        ResolveWhenDefined(name, constructor);
        return JSUndefined.Value;
    }

    /// <summary>
    /// Validates an <c>extends</c> option and yields the local name it names, or reports the
    /// specified failure and answers <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Two rejections, and they are distinct rather than one "bad tag" case: a name that is itself a
    /// valid custom element name cannot be extended (a customized built-in customizes a
    /// <em>built-in</em>), and a name no HTML element has is an <c>HTMLUnknownElement</c>, which has
    /// no interface to extend either. Both messages are Chromium's, measured rather than invented —
    /// a page that logs the failure sees the same text it would in a browser.
    /// </remarks>
    private static bool TryResolveExtends(JSContext context, string extendsName, out string localName)
    {
        localName = DomBridge.AsciiToLower(extendsName);

        if (IsValidCustomElementName(localName))
        {
            DomBridge.ThrowDOMException(
                context,
                $"Failed to execute 'define' on 'CustomElementRegistry': \"{extendsName}\" is a valid custom element name",
                "NotSupportedError");
            return false;
        }

        if (DomBridge.HtmlInterfaceForTag(localName) == "HTMLUnknownElement")
        {
            DomBridge.ThrowDOMException(
                context,
                $"Failed to execute 'define' on 'CustomElementRegistry': \"{extendsName}\" is an HTMLUnknownElement",
                "NotSupportedError");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The definition's <c>observedAttributes</c>, read once at definition time as the specification
    /// requires — a later change to the static getter does not retroactively widen what is observed.
    /// </summary>
    private static HashSet<string> ReadObservedAttributes(JSObject constructor)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);
        if (constructor[(KeyString)"observedAttributes"] is not JSObject list)
            return observed;

        var length = (int)list[(KeyString)"length"].DoubleValue;
        for (var index = 0; index < length; index++)
        {
            var entry = list[(uint)index];
            if (entry is not null && !entry.IsUndefined && !entry.IsNull)
                observed.Add(entry.ToString());
        }

        return observed;
    }

    internal JSValue Get(in Arguments a)
    {
        var name = a.Length > 0 ? a[0].ToString() : string.Empty;
        return _byName.TryGetValue(name, out var definition) ? definition.Constructor : JSUndefined.Value;
    }

    internal JSValue GetName(in Arguments a) =>
        a.Length > 0 && a[0] is JSObject constructor && _byConstructor.TryGetValue(constructor, out var definition)
            ? new JSString(definition.Name)
            : JSNull.Value;

    /// <summary>
    /// <c>customElements.whenDefined(name)</c> — a real promise that resolves with the constructor,
    /// rejecting with a <c>SyntaxError</c> for an invalid name.
    /// </summary>
    internal JSValue WhenDefined(in Arguments a)
    {
        var name = a.Length > 0 ? a[0].ToString() : string.Empty;
        if (!IsValidCustomElementName(name))
            return _host.RejectedPromise(
                $"SyntaxError: '{name}' is not a valid custom element name.");

        if (_byName.TryGetValue(name, out var defined))
            return _host.ResolvedPromise(defined.Constructor);

        var (promise, resolver) = _host.PendingPromise();
        if (!_whenDefined.TryGetValue(name, out var waiting))
            _whenDefined[name] = waiting = [];
        waiting.Add(resolver);
        return promise;
    }

    private void ResolveWhenDefined(string name, JSObject constructor)
    {
        if (!_whenDefined.Remove(name, out var waiting))
            return;

        foreach (var resolver in waiting)
            _host.Resolve(resolver, constructor);
    }

    /// <summary><c>customElements.upgrade(root)</c> — upgrades the shadow-including inclusive
    /// descendants of <paramref name="a"/>[0] that have a definition and are not upgraded yet.</summary>
    internal JSValue Upgrade(in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject wrapper || _host.NodeFor(wrapper) is not { } root)
            return JSUndefined.Value;

        foreach (var element in InclusiveElements(root))
        {
            if (DefinitionFor(element) is { } definition)
                TryUpgrade(element, definition);
        }

        return JSUndefined.Value;
    }

    // ---------------- construction and upgrade ----------------

    /// <summary>
    /// The host half of the JavaScript <c>HTMLElement</c> base: given the <c>new.target</c> the base
    /// read, hands back the element the constructor should become.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An element already being upgraded is returned as it is — that is the construction stack, and
    /// it is what lets an upgrade run the author's constructor against the node already in the tree
    /// rather than a replacement. Otherwise a fresh element is minted for the definition's
    /// <em>local</em> name, which is the extended tag for a customized built-in.
    /// </para>
    /// <para>
    /// <paramref name="a"/>[1] is the interface whose constructor is running — HTML §4.13.3's "active
    /// function object", and the reason it has to be passed rather than inferred. The base a class
    /// extends must be the one its definition names: <c>class X extends HTMLButtonElement</c>
    /// registered without an <c>extends</c> option is a <c>TypeError</c>, and so is an
    /// <c>HTMLElement</c> subclass registered with one. Without the active interface both would
    /// silently construct the wrong element — an autonomous <c>&lt;x-thing&gt;</c> reached through
    /// <c>HTMLButtonElement</c>, which a browser refuses.
    /// </para>
    /// <para>
    /// A refusal is returned as a string, not thrown: the JavaScript base turns it into a real
    /// <c>TypeError</c> carrying that message, where a host throw would surface as a bare string with
    /// no name. <see cref="JSNull"/> means the generic "Illegal constructor" — a
    /// <c>new.target</c> with no definition, and a bare <c>new HTMLElement()</c> with no
    /// <c>new.target</c> at all.
    /// </para>
    /// </remarks>
    internal JSValue ConstructForNewTarget(in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject newTarget ||
            !_byConstructor.TryGetValue(newTarget, out var definition))
            return JSNull.Value;

        var activeInterface = a.Length > 1 ? a[1].ToString() : "HTMLElement";
        var requiredInterface = definition.IsCustomizedBuiltIn
            ? DomBridge.HtmlInterfaceForTag(definition.LocalName)
            : "HTMLElement";

        if (!string.Equals(activeInterface, requiredInterface, StringComparison.Ordinal))
        {
            return new JSString($"Failed to construct '{activeInterface}': Illegal constructor: " +
                (definition.IsCustomizedBuiltIn
                    ? "localName does not match the HTML element interface"
                    : "autonomous custom elements must extend HTMLElement"));
        }

        if (_constructionStack.Count > 0)
        {
            var pending = _constructionStack[^1];
            _constructionStack.RemoveAt(_constructionStack.Count - 1);
            return _host.ToJSObject(pending);
        }

        var created = _host.CreateBridgeElement(definition.LocalName);
        if (definition.IsCustomizedBuiltIn)
        {
            // The is value, not an `is` attribute: a constructed customized built-in reports
            // getAttribute('is') as null while still serializing as <button is="…">. Measured.
            _isValues[created] = definition.Name;
        }

        // A constructed element is upgraded by definition — it was built from its own class. Marking
        // it here is what makes its reactions fire: without it a `document.createElement('x-thing')`
        // instance took no attributeChangedCallback, because the reaction dispatch only knows about
        // elements that went through an upgrade.
        _upgraded.Add(created);
        return _host.ToJSObject(created);
    }

    /// <summary>
    /// Creates an element for a defined custom tag by running its constructor, which is what makes
    /// <c>document.createElement('x-thing')</c> hand back an instance of the class rather than a
    /// plain element. <paramref name="isValue"/> is <c>createElement</c>'s <c>is</c> option, which
    /// selects a customized built-in. Returns <see langword="null"/> when nothing matches, so the
    /// ordinary path takes over.
    /// </summary>
    internal JSObject? CreateDefined(string tagName, string? isValue)
    {
        var lookup = isValue ?? tagName;
        if (!_byName.TryGetValue(lookup, out var definition) ||
            !string.Equals(definition.LocalName, tagName, StringComparison.Ordinal) ||
            (isValue is null && definition.IsCustomizedBuiltIn))
            return null;

        return _host.Construct(definition.Constructor) is { } created ? created : null;
    }

    /// <summary>Upgrades every element in the document that this definition now names, in tree
    /// order — the elements a page parsed before the definition landed.</summary>
    private void UpgradeDefined(CustomElementDefinition definition)
    {
        foreach (var element in _host.Elements)
        {
            if (ReferenceEquals(DefinitionFor(element), definition))
                TryUpgrade(element, definition);
        }
    }

    /// <summary>The elements already marked as upgraded, so an element is never upgraded twice.</summary>
    private readonly HashSet<DomElement> _upgraded = [];

    /// <summary>
    /// Runs <paramref name="definition"/>'s constructor against <paramref name="element"/>, then the
    /// reactions the specification enqueues for an upgrade: <c>attributeChangedCallback</c> for each
    /// observed attribute it already carries, and <c>connectedCallback</c> when it is in the tree.
    /// </summary>
    /// <remarks>
    /// The attribute callbacks come before the connected one and report an <c>oldValue</c> of
    /// <see langword="null"/> — the element is only now becoming a custom element, so from the
    /// definition's point of view every attribute it already has is being set for the first time.
    /// Measured.
    /// </remarks>
    private void TryUpgrade(DomElement element, CustomElementDefinition definition)
    {
        if (!_upgraded.Add(element))
            return;

        _constructionStack.Add(element);
        try
        {
            _host.Construct(definition.Constructor);
        }
        catch (Exception)
        {
            // A constructor that throws leaves the element un-upgraded rather than taking the
            // define() call down with it — one bad definition must not stop the others.
            _upgraded.Remove(element);
            return;
        }
        finally
        {
            if (_constructionStack.Count > 0 && ReferenceEquals(_constructionStack[^1], element))
                _constructionStack.RemoveAt(_constructionStack.Count - 1);
        }

        foreach (var attributeName in DomBridge.AttributeNames(element).ToList())
        {
            if (definition.ObservedAttributes.Contains(attributeName) &&
                DomBridge.TryGetAttribute(element, attributeName, out var value))
            {
                InvokeReaction(element, "attributeChangedCallback",
                    new JSString(attributeName), JSNull.Value, new JSString(value));
            }
        }

        if (definition.FormAssociated)
        {
            // The form-association reaction comes before the connected one and is not conditional on
            // being connected: an upgrade inside a form reports that form, and one outside any form
            // reports null only once something moves it. Tracked from here so the first observation
            // is the upgrade itself rather than a later mutation.
            _formAssociated.Add(element);
            _formOwners[element] = _host.FormOwnerOf(element);
            _disabled[element] = _host.IsFormControlDisabled(element);
            if (_formOwners[element] is { } owner)
                InvokeReaction(element, "formAssociatedCallback", _host.ToJSObject(owner));
        }

        if (_host.IsConnected(element))
            InvokeReaction(element, "connectedCallback");
    }

    /// <summary>The upgraded form-associated custom elements, and the two pieces of state whose
    /// changes they are told about.</summary>
    private readonly HashSet<DomElement> _formAssociated = [];
    private readonly Dictionary<DomElement, DomElement?> _formOwners = [];
    private readonly Dictionary<DomElement, bool> _disabled = [];

    /// <summary>
    /// Re-reads every form-associated custom element's owner and disabled state and reports what
    /// changed. Called after each mutation, because both are computed from the tree rather than
    /// stored: a form owner changes when the element moves, when its <c>form</c> attribute changes,
    /// and when the form it names appears; a disabled state changes with its own attribute and with
    /// any ancestor <c>&lt;fieldset&gt;</c>'s.
    /// </summary>
    /// <remarks>
    /// Sweeping rather than deriving which element a given mutation could have affected: the set is
    /// only the form-associated custom elements a page has actually upgraded, and the alternatives —
    /// mapping a fieldset mutation to its descendants, an id change to the elements naming it — are
    /// the kind of partial dependency tracking that silently misses a case.
    /// </remarks>
    internal void SyncFormState()
    {
        if (_formAssociated.Count == 0)
            return;

        foreach (var element in _formAssociated.ToList())
        {
            var owner = _host.FormOwnerOf(element);
            if (!_formOwners.TryGetValue(element, out var previousOwner) || !ReferenceEquals(previousOwner, owner))
            {
                _formOwners[element] = owner;
                InvokeReaction(element, "formAssociatedCallback",
                    owner is null ? JSNull.Value : _host.ToJSObject(owner));
            }

            var disabled = _host.IsFormControlDisabled(element);
            if (!_disabled.TryGetValue(element, out var previousDisabled) || previousDisabled != disabled)
            {
                _disabled[element] = disabled;
                InvokeReaction(element, "formDisabledCallback",
                    disabled ? JSBoolean.True : JSBoolean.False);
            }
        }
    }

    /// <summary>
    /// Reports a form reset to the form-associated custom elements among
    /// <paramref name="controls"/> — the reaction a component resets its own value in.
    /// </summary>
    /// <remarks>
    /// <c>formStateRestoreCallback</c> has no equivalent hook and is deliberately never fired: it
    /// reports a value restored by session history or an autofill pass, and this engine performs
    /// neither, so firing it would be an invention rather than a restoration.
    /// </remarks>
    internal void OnFormReset(IReadOnlyList<DomElement> controls)
    {
        foreach (var control in controls)
        {
            if (_formAssociated.Contains(control))
                InvokeReaction(control, "formResetCallback");
        }
    }

    // ---------------- reactions ----------------

    /// <summary>Runs a reaction callback on an upgraded element, if its class declares one.</summary>
    /// <remarks>
    /// Looked up on the element itself rather than on the definition's prototype: the element's
    /// prototype <em>is</em> the class's after an upgrade, so this finds an override on the instance
    /// and an inherited callback alike, and calls it with <c>this</c> bound to the element the way
    /// the author wrote it expecting.
    /// </remarks>
    private void InvokeReaction(DomElement element, string callback, params JSValue[] arguments)
    {
        if (!_upgraded.Contains(element) || !_host.TryGetWrapper(element, out var wrapper))
            return;

        if (wrapper[(KeyString)callback] is not JSFunction reaction)
            return;

        try
        {
            _host.Call(reaction, wrapper, arguments);
        }
        catch (Exception)
        {
            // A throwing reaction is reported to the page's error handling, not propagated into the
            // DOM operation that triggered it — an appendChild must not fail because a component's
            // connectedCallback did.
        }
    }

    /// <summary>Dispatches the connected/disconnected reactions for a tree mutation.</summary>
    internal void OnChildListMutation(IReadOnlyList<DomNode> added, IReadOnlyList<DomNode> removed)
    {
        foreach (var node in removed)
        {
            foreach (var element in InclusiveElements(node))
                InvokeReaction(element, "disconnectedCallback");
        }

        foreach (var node in added)
        {
            foreach (var element in InclusiveElements(node))
            {
                // An element inserted with a definition already in place becomes a custom element
                // now — the shape a page produces with `innerHTML` or by appending parsed markup
                // after its component script ran. Upgrading dispatches the connected reaction itself,
                // so it must not be dispatched twice.
                if (!_upgraded.Contains(element) && DefinitionFor(element) is { } definition)
                {
                    TryUpgrade(element, definition);
                    continue;
                }

                if (_host.IsConnected(element))
                    InvokeReaction(element, "connectedCallback");
            }
        }
    }

    /// <summary>Dispatches <c>attributeChangedCallback</c> for an observed attribute.</summary>
    internal void OnAttributeMutation(DomElement element, string attributeName, string? oldValue)
    {
        if (!_upgraded.Contains(element) ||
            DefinitionFor(element) is not { } definition ||
            !definition.ObservedAttributes.Contains(attributeName))
            return;

        var newValue = DomBridge.TryGetAttribute(element, attributeName, out var current)
            ? new JSString(current)
            : (JSValue)JSNull.Value;

        InvokeReaction(element, "attributeChangedCallback",
            new JSString(attributeName),
            oldValue is null ? JSNull.Value : new JSString(oldValue),
            newValue);
    }

    /// <summary>
    /// Dispatches <c>adoptedCallback(oldDocument, newDocument)</c> for a node that changed document.
    /// </summary>
    /// <remarks>
    /// The whole adopted subtree receives it, not only the node named on the record: adoption moves
    /// every descendant's node document, so every upgraded custom element in it has changed document
    /// too. The removal from the old tree that adoption performs first is an ordinary child-list
    /// mutation, so <c>disconnectedCallback</c> has already run by the time this does — which is the
    /// order a browser produces and is measured rather than assumed.
    /// </remarks>
    internal void OnAdoption(DomNode node, JSValue oldDocument, JSValue newDocument)
    {
        foreach (var element in InclusiveElements(node))
            InvokeReaction(element, "adoptedCallback", oldDocument, newDocument);
    }

    private static IEnumerable<DomElement> InclusiveElements(DomNode node) =>
        node.InclusiveDescendants().OfType<DomElement>();
}
