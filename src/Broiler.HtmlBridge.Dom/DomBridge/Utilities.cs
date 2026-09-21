using Broiler.CSS;
using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// No engine namespace at all. The two wrapper-cache reverse lookups below take a JSEAL handle, which
// is what all fifteen of their callers already hold.

/// <summary>
/// Internal helper methods — string conversions, DOM tree utilities,
/// form-control collection, table-row helpers, and JS-object builders
/// for <c>style</c>, <c>classList</c>, <c>localStorage</c>, and
/// <c>canvas.getContext("2d")</c>.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Finds the <see cref="DomElement"/> a wrapper handle stands for, or <see langword="null"/>
    /// when it stands for none — a handle that is not an object included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parameter is the handle every caller already holds.</b> Fifteen files across the
    /// assembly call this pair, and it asks <see cref="Dom.Runtime.JsObjectRegistry"/>, which is keyed
    /// on <see cref="JsValue.ObjectIdentity"/>. The <em>names</em> still spell an engine type they do
    /// not take; renaming them is a separate change, and nothing the neutrality ratchet measures
    /// depends on it, because that check counts the engine's namespace as text and a method name
    /// spells none.
    /// </para>
    /// <para>
    /// <b>A handle that is not an object answers <see langword="null"/> rather than throwing.</b>
    /// <see cref="JsValue.ObjectIdentity"/> is null for every non-object kind, so the registry
    /// reports such a handle absent and this answers <see langword="null"/> — which is exactly what
    /// the <c>IsObject</c> test that used to sit in front of ten call sites answered. Those tests
    /// were redundant rather than load-bearing and are gone; the cases in
    /// <c>NonObjectWrapperLookupTests</c> pin the behaviour they used to describe.
    /// </para>
    /// </remarks>
    private DomElement? FindDomElementByJSObject(JsValue wrapper) =>
        FindDomNodeByJSObject(wrapper) as DomElement;

    /// <summary>
    /// Finds the canonical <see cref="DomNode"/> a wrapper handle stands for, in constant time (the
    /// reverse map is a hash lookup). Unlike <see cref="FindDomElementByJSObject"/> this also resolves
    /// text/comment nodes, which ranges need because they carry canonical char-data nodes.
    /// </summary>
    /// <inheritdoc cref="FindDomElementByJSObject" path="/remarks" />
    private DomNode? FindDomNodeByJSObject(JsValue wrapper) =>
        _jsObjects.TryGetNode(wrapper, out var node) ? node : null;

    // Call sites use the canonical Broiler.Dom.DomNode.IsDescendantOf(ancestor) instance method.
    // Every bridge call site passes a non-null ancestor, so canonical's null-ancestor throw is
    // unreachable.

    /// <summary>
    /// Clones a <see cref="DomElement"/>. When <paramref name="deep"/> is true,
    /// all descendants are recursively cloned.
    /// </summary>
    private DomNode CloneDomElement(DomNode source, bool deep)
    {
        // Delegate the tree + attribute clone to canonical DomNode.CloneNode (spec §4.4)
        // instead of the bridge's hand-rolled per-node-kind rebuild. Canonical CloneShallow handles every
        // node kind — element (namespace + attribute set verbatim), text/comment (data), doctype
        // (name/publicId/systemId) and fragment — and, when deep, recurses the child list in order. This
        // replaces the former CreateBridgeElementNS-from-main-`_document` construction + SetAttribute
        // attribute copy; canonical preserves the source's owner document and attribute keys verbatim,
        // which is spec-correct (the old path minted every clone from the main document and lowercased
        // no-namespace attribute names). Cloning does not mutate the live document, so there is no
        // MutationObserver/NodeIterator/live-range side-effect coupling here (unlike Normalize).
        var clone = source.CloneNode(deep);

        // Canonical CloneNode knows nothing about the bridge's parallel per-element runtime state (inline
        // style + baked overlay, form control, scroll, dialog/popover, shadow, stylesheet, document
        // viewport, animation, position-area memo), so copy it onto every element in the cloned subtree
        // through the single CopyBridgeRuntimeStateTo authority.
        CopyRuntimeStateForClonedSubtree(source, clone, deep);
        return clone;
    }

    /// <summary>
    /// Walks a source subtree and its canonical <c>CloneNode</c> copy in lockstep, copying the bridge's
    /// per-element runtime state (via <see cref="CopyBridgeRuntimeStateTo"/>) from each source element to
    /// its clone. Canonical <c>CloneNode(deep)</c> reproduces the child list in order, so index <c>i</c>
    /// of the source's children pairs with index <c>i</c> of the clone's.
    /// </summary>
    private void CopyRuntimeStateForClonedSubtree(DomNode source, DomNode clone, bool deep)
    {
        if (source is DomElement sourceElement && clone is DomElement cloneElement)
            CopyBridgeRuntimeStateTo(sourceElement, cloneElement);

        if (!deep)
            return;

        var sourceChildren = source.ChildNodes;
        var cloneChildren = clone.ChildNodes;
        for (var i = 0; i < sourceChildren.Count && i < cloneChildren.Count; i++)
            CopyRuntimeStateForClonedSubtree(sourceChildren[i], cloneChildren[i], true);
    }

    /// <summary>
    /// The single authority that copies every bridge
    /// per-element runtime-state table from a source element onto its <c>cloneNode</c> copy.
    /// Canonical <c>DomNode.CloneNode</c> clones the tree and attributes but knows nothing about
    /// the bridge's parallel per-element state (inline style, form control, scroll, dialog/popover
    /// top layer, shadow linkage, stylesheet CSSOM, document viewport flag, animation timeline and
    /// the position-area memo), so the bridge carries it here. Consolidating it out of the scattered
    /// inline block in <see cref="CloneDomElement"/> means each state table owns its own
    /// <c>CopyTo</c> (in <c>RuntimeStates.cs</c>, next to its fields) — a new field/table can no
    /// longer be silently dropped from clones — and isolates the exact bridge-state copy the
    /// eventual canonical-<c>CloneNode</c> swap (item 5) will call alongside the canonical clone.
    /// </summary>
    private void CopyBridgeRuntimeStateTo(DomElement source, DomElement clone)
    {
        // Inline style: copy the source's live style dict — which may hold
        // JS `element.style` mutations not yet synced to the `style=` attribute — over the clone's
        // lazily-seeded attribute values. `InlineStyle(clone)` seeds from the (already-copied)
        // `style=` attribute before the Clear, so the copy is authoritative.
        var cloneStyle = InlineStyle(clone);
        cloneStyle.Clear();
        foreach (var kv in InlineStyle(source))
            cloneStyle[kv.Key] = kv.Value;

        // Per-bridge instance tables — each owns its CopyTo.
        _formState.CopyControlState(source, clone);
        ScrollStateFor(source).CopyTo(ScrollStateFor(clone));
        DialogStateFor(source).CopyTo(DialogStateFor(clone));
        StyleSheetStateFor(source).CopyTo(StyleSheetStateFor(clone));
        DocumentStateFor(source).CopyTo(DocumentStateFor(clone));
        AnimationStateFor(source).CopyTo(AnimationStateFor(clone));

        // Memoized position-area resolution (was ElementRuntimeState.Layout, now the bridge-level
        // PositionAreaResolutions cache — see AnchorResolver/ScrollPositioning.cs).
        CopyPositionAreaResolution(source, clone);

        // Baked-style overlay: serialize-time bakes now live off the
        // inline-style dict, so copy the overlay too. A no-op unless the source was cloned after baking.
        CopyBakedStyleOverlay(source, clone);

        // Web Animations targeting a pseudo-element bake outside the inline style (a pseudo has no
        // node); the serialization pass reads them off the projected element, so they travel here.
        CopyAnimatedPseudoStyles(source, clone);
    }
}

// NO ENGINE NAMESPACE. ThrowDOMException and the validators that forward to it are the realm's
// throughout. Nothing here reads a script context except to reach the page's DOMException
// constructor, and IJsCalls.DomError reaches the same global on the same realm with the same
// fallback. See the remarks on ThrowDOMException for why that is a rename and not a behaviour
// change.

/// <summary>
/// The DOM element/qualified-name validation cluster, together
/// with the JS-side constructor globals it validates against — the <c>DOMException</c> constructor
/// (and the C# helper that throws it), plus the <c>Node</c> and <c>SVGLength</c> constant carriers.
/// The spec name-validation algorithm itself now lives in the canonical
/// <see cref="DomNameValidation"/> (Broiler.Dom); the bridge only marshals the thrown
/// <see cref="DomException"/> into a JavaScript <c>DOMException</c>.
/// </summary>
public sealed partial class DomBridge
{
    // ------------------------------------------------------------------
    //  Element name validation
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates a selector argument (DOM §4.2.6), throwing <c>SyntaxError</c> when it does not parse
    /// as a selector list.
    /// </summary>
    /// <remarks>
    /// Shared by all five scripted entry points that take one — <c>querySelector</c>,
    /// <c>querySelectorAll</c>, <c>matches</c> and <c>closest</c> on an element, the two document
    /// forms, the sub-document forms, and the <c>DocumentFragment</c> forms — because a browser throws
    /// from all of them identically, which was measured rather than assumed. The CSS cascade does not
    /// come through here and stays lenient, as CSS error handling requires.
    /// <para>
    /// <b>This one takes no parameter at all, where its three neighbours above take a realm.</b> It
    /// went first, when every one of its callers was already in the migrating group and the three
    /// above still had callers holding a context — and the argument it made then is the one that
    /// moved them since: the nullable parameter was only ever forwarded to
    /// <see cref="DomBridgeUtils.ThrowDOMException"/>, and <c>IJsCalls.DomError</c> constructs through the same
    /// <c>DOMException</c> global against the same realm, so what a page catches is unchanged. The
    /// null-tolerance survives as the realm's: before <c>Attach</c> there is no realm and the check
    /// is skipped, which is what a <see langword="null"/> context meant and is the state the
    /// bridge's own pre-attach selector work runs in.
    /// </para>
    /// </remarks>
    internal void ValidateSelector(string selector)
    {
        if (_realm is { } realm && !Dom.Features.DomApiSyntax.IsValidSelectorList(selector))
        {
            throw realm.DomError(
                "SyntaxError",
                $"Failed to execute 'querySelector' on 'Document': '{selector}' is not a valid selector.");
        }
    }
}

/// <summary>
/// A form's controls and its entry list (HTML §4.10.21.4) — the two questions that have to be
/// answered together once a form-associated custom element can be one of the controls.
/// </summary>
/// <remarks>
/// <para>
/// The canonical <c>HtmlElementQueries.CollectFormControls</c> matches on the four control tags,
/// which is right for the DOM it was written against and cannot answer for a custom element: its tag
/// is whatever the page named it, and only the custom-element registry knows whether its definition
/// declared <c>formAssociated</c>. So the collection is re-walked here, in the bridge, where that
/// registry is.
/// </para>
/// <para>
/// <b>The entry list existed nowhere.</b> <c>new FormData(form)</c> enumerated the <em>wrapper's</em>
/// own string properties, so it produced the element object's members — <c>tagName</c>,
/// <c>innerHTML</c> and the rest — instead of the form's fields. That made it useless for its only
/// idiom, and it is also the place a browser reads a form-associated custom element's submission
/// value, so <c>ElementInternals.setFormValue</c> would have had nowhere to be observed. Building the
/// list properly is what keeps that from being a shape-only stub.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// <paramref name="form"/>'s controls in tree order, including its form-associated custom
    /// elements — what <c>form.elements</c> lists.
    /// </summary>
    internal List<DomElement> CollectFormControlsIncludingCustom(DomElement form) =>
        Broiler.Dom.Html.HtmlFormQueries.GetFormElements(form, el => _customElements?.IsFormAssociated(el) ?? false);

    /// <summary>
    /// <paramref name="form"/>'s entry list: each submittable control's name and current value, in
    /// tree order.
    /// </summary>
    /// <remarks>
    /// The exclusions are the specified ones and each is observable: a disabled control submits
    /// nothing, a control with no <c>name</c> submits nothing, an unchecked checkbox or radio submits
    /// nothing (and a checked one with no <c>value</c> submits <c>"on"</c>), and a button — including
    /// an <c>&lt;input type=submit&gt;</c> — submits only as the submitter, which a
    /// <c>new FormData(form)</c> has none of. A file input submits nothing because this engine has no
    /// file selection.
    /// </remarks>
    internal List<KeyValuePair<string, string>> BuildFormEntryList(DomElement form)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (var control in CollectFormControlsIncludingCustom(form))
        {
            if (Broiler.Dom.Html.HtmlFormQueries.IsFormControlDisabled(control))
                continue;

            var name = TryGetAttribute(control, "name", out var declaredName) ? declaredName : string.Empty;

            // A form-associated custom element's value is the one it set through its internals, and a
            // FormData submission value carries its own names — so it is asked before the name test.
            if (_customElements?.IsFormAssociated(control) == true)
            {
                if (_elementInternals?.SubmissionEntriesFor(control, name) is { } custom)
                    entries.AddRange(custom);
                continue;
            }

            if (string.IsNullOrEmpty(name))
                continue;

            var tag = AsciiToLower(control.TagName);
            switch (tag)
            {
                case "button":
                    continue;

                case "select":
                    entries.Add(new(name, _select.GetValue(control)));
                    continue;

                case "textarea":
                    entries.Add(new(name, _formState.GetEffectiveValue(control)));
                    continue;

                case "input":
                    AppendInputEntry(entries, control, name);
                    continue;
            }
        }

        return entries;
    }

    private void AppendInputEntry(List<KeyValuePair<string, string>> entries, DomElement input, string name)
    {
        var type = TryGetAttribute(input, "type", out var declaredType)
            ? AsciiToLower(declaredType)
            : "text";

        switch (type)
        {
            case "submit" or "reset" or "button" or "image" or "file":
                return;

            case "checkbox" or "radio":
                if (!_formState.GetEffectiveChecked(input))
                    return;
                entries.Add(new(name, TryGetAttribute(input, "value", out var boxValue) ? boxValue : "on"));
                return;

            default:
                entries.Add(new(name, _formState.GetEffectiveValue(input)));
                return;
        }
    }
}

/// <summary>
/// The form-reset algorithm (HTML §4.10.21.4) and the radio-button group invariant it depends on.
/// </summary>
/// <remarks>
/// <para>
/// A reset is defined entirely in terms of the <em>dirty flags</em> a control carries: resetting an
/// <c>&lt;input&gt;</c> means clearing its dirty value and dirty checkedness flags, after which the
/// value and checkedness track the <c>value</c> and <c>checked</c> content attributes again. The
/// bridge already keeps exactly those flags — the per-element <c>FormControl</c> runtime slots, whose
/// unset state is what the IDL getters fall back through — so the algorithm is a matter of removing
/// them rather than of computing replacement values. That is why this can be a small amount of code
/// for a specified operation: the state model was already right, and only the operation on it was
/// missing.
/// </para>
/// <para>
/// The radio invariant is not part of resetting as such, but a reset is one of the moments that can
/// break it: a group whose markup carries <c>checked</c> on more than one member has every one of
/// them restored, and "at most one member of a radio button group is checked" has to be re-imposed
/// afterwards. The same invariant is broken by <em>insertion</em> — appending an already-checked
/// radio into a group that has one — which is why <see cref="EnforceRadioGroupExclusivity"/> is
/// shared with the insertion path rather than kept private here.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Resets <paramref name="form"/>'s controls (HTML §4.10.21.4). Each resettable control's dirty
    /// flags are cleared so its state tracks its markup again, then the radio invariant is
    /// re-imposed and the style scope invalidated — <c>:checked</c> and the value-dependent
    /// selectors are cascade inputs.
    /// </summary>
    internal void ResetFormControls(DomElement form)
    {
        _formState.ResetForm(form);
        InvalidateStyleScope(form);

        // A form-associated custom element has no dirty flags to clear — its value is whatever it
        // chose to submit — so a reset reaches it as a reaction instead, which is where a component
        // restores its own default.
        _customElements?.OnFormReset(CollectFormControlsIncludingCustom(form));
    }

    /// <summary>
    /// Restores the radio invariant after <paramref name="inserted"/> (or a descendant of it) joins
    /// the tree. An already-checked radio that is appended into a group with a checked member left
    /// two checked, which is a state a browser never shows and a state no user interaction can
    /// produce — a form serialized in it submits two values for one field.
    /// </summary>
    /// <remarks>
    /// This runs on every element insertion, so its cost matters. It is a walk of the inserted
    /// subtree with a tag comparison per element, and it is the <em>third</em> such walk on this
    /// line — <c>FireDescendantOnloads</c> and <c>FireDescendantStylesheetLinkLoads</c> already
    /// traverse the same subtree with per-element predicates of the same order. So it is a constant
    /// factor on a path that is already O(subtree), not a new order of growth. A subtree carrying no
    /// checked radio does the walk and nothing else; there is no cheaper signal, because whether a
    /// radio is checked is exactly what has to be looked at.
    /// </remarks>
    private void EnforceRadioGroupExclusivityForInsertion(DomElement inserted)
    {
        foreach (var element in inserted.InclusiveDescendants().OfType<DomElement>())
        {
            if (Broiler.Dom.Html.HtmlFormQueries.IsRadioInput(element) && _formState.GetEffectiveChecked(element))
            {
                var group = Broiler.Dom.Html.HtmlFormQueries.GetRadioGroupElements(element);
                foreach (var sibling in group)
                {
                    if (!ReferenceEquals(sibling, element))
                        _formState.SetDirtyChecked(sibling, false);
                }
            }
        }
    }
}
