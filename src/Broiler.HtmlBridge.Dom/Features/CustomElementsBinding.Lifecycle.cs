using System.Text.RegularExpressions;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// A custom element's life after its definition: construction through the <c>HTMLElement</c> base,
/// upgrade, form-associated state, and the <c>connected</c>/<c>disconnected</c>/<c>adopted</c>/
/// <c>attributeChanged</c> reactions. The registry and the class remarks are in
/// <c>CustomElementsBinding.cs</c>.
/// </summary>
internal sealed partial class CustomElementsBinding
{
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
    /// Argument zero is the <c>new.target</c> its JavaScript caller read; see the class remarks for
    /// why it is passed rather than taken from <see cref="JsCall.NewTarget"/>, which for this call
    /// frame would describe the ordinary call the base makes and not the <c>new</c> that began it.
    /// </para>
    /// <para>
    /// Argument one is the interface whose constructor is running — HTML §4.13.3's "active function
    /// object", and the reason it has to be passed rather than inferred. The base a class extends must
    /// be the one its definition names: <c>class X extends HTMLButtonElement</c> registered without an
    /// <c>extends</c> option is a <c>TypeError</c>, and so is an <c>HTMLElement</c> subclass registered
    /// with one. Without the active interface both would silently construct the wrong element — an
    /// autonomous <c>&lt;x-thing&gt;</c> reached through <c>HTMLButtonElement</c>, which a browser
    /// refuses.
    /// </para>
    /// <para>
    /// A refusal is returned as a string, not thrown: the JavaScript base turns it into a real
    /// <c>TypeError</c> carrying that message, where a host throw would surface without the base's
    /// framing. <see cref="JsValue.Null"/> means the generic "Illegal constructor" — a
    /// <c>new.target</c> with no definition, and a bare <c>new HTMLElement()</c> with no
    /// <c>new.target</c> at all.
    /// </para>
    /// </remarks>
    internal JsValue ConstructForNewTarget(in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject ||
            !_byConstructor.TryGetValue(call[0], out var definition))
            return JsValue.Null;

        var activeInterface = call.Length > 1 ? call.Realm.ToJsString(call[1]) : "HTMLElement";
        var requiredInterface = definition.IsCustomizedBuiltIn
            ? DomBridgeUtils.HtmlInterfaceForTag(definition.LocalName)
            : "HTMLElement";

        if (!string.Equals(activeInterface, requiredInterface, StringComparison.Ordinal))
        {
            return JsValue.String($"Failed to construct '{activeInterface}': Illegal constructor: " +
                (definition.IsCustomizedBuiltIn
                    ? "localName does not match the HTML element interface"
                    : "autonomous custom elements must extend HTMLElement"));
        }

        if (_constructionStack.Count > 0)
        {
            var pending = _constructionStack[^1];
            _constructionStack.RemoveAt(_constructionStack.Count - 1);
            return _host.WrapNode(pending);
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
        return _host.WrapNode(created);
    }

    /// <summary>
    /// Creates an element for a defined custom tag by running its constructor, which is what makes
    /// <c>document.createElement('x-thing')</c> hand back an instance of the class rather than a
    /// plain element. <paramref name="isValue"/> is <c>createElement</c>'s <c>is</c> option, which
    /// selects a customized built-in. Returns <see cref="JsValue.Missing"/> when nothing matches, so
    /// the ordinary path takes over.
    /// </summary>
    internal JsValue CreateDefinedElement(string tagName, string? isValue)
    {
        var lookup = isValue ?? tagName;
        if (!_byName.TryGetValue(lookup, out var definition) ||
            !string.Equals(definition.LocalName, tagName, StringComparison.Ordinal) ||
            (isValue is null && definition.IsCustomizedBuiltIn))
            return JsValue.Missing;

        var created = _host.Realm.Construct(definition.Constructor);
        return created.IsObject ? created : JsValue.Missing;
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
            _host.Realm.Construct(definition.Constructor);
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

        foreach (var attributeName in DomBridgeUtils.AttributeNames(element).ToList())
        {
            if (definition.ObservedAttributes.Contains(attributeName) &&
                DomBridgeUtils.TryGetAttribute(element, attributeName, out var value))
            {
                InvokeReaction(element, "attributeChangedCallback",
                    JsValue.String(attributeName), JsValue.Null, JsValue.String(value));
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
                InvokeReaction(element, "formAssociatedCallback", _host.WrapNode(owner));
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
                    owner is null ? JsValue.Null : _host.WrapNode(owner));
            }

            var disabled = _host.IsFormControlDisabled(element);
            if (!_disabled.TryGetValue(element, out var previousDisabled) || previousDisabled != disabled)
            {
                _disabled[element] = disabled;
                InvokeReaction(element, "formDisabledCallback", JsValue.Boolean(disabled));
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
    private void InvokeReaction(DomElement element, string callback, params JsValue[] arguments)
    {
        if (!_upgraded.Contains(element) || !_host.TryGetWrapper(element, out var wrapper))
            return;

        var realm = _host.Realm;
        var reaction = realm.GetProperty(wrapper, callback);
        if (!reaction.IsFunction)
            return;

        try
        {
            realm.Invoke(reaction, wrapper, arguments);
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

        // JsValue.String maps a CLR null onto JavaScript null, which is exactly what an absent
        // attribute reports here — so the removal case needs no arm of its own.
        var newValue = DomBridgeUtils.TryGetAttribute(element, attributeName, out var current)
            ? JsValue.String(current)
            : JsValue.Null;

        InvokeReaction(element, "attributeChangedCallback",
            JsValue.String(attributeName),
            oldValue is null ? JsValue.Null : JsValue.String(oldValue),
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
    internal void OnAdoption(DomNode node, JsValue oldDocument, JsValue newDocument)
    {
        foreach (var element in InclusiveElements(node))
            InvokeReaction(element, "adoptedCallback", oldDocument, newDocument);
    }

    private static IEnumerable<DomElement> InclusiveElements(DomNode node) =>
        node.InclusiveDescendants().OfType<DomElement>();
}
