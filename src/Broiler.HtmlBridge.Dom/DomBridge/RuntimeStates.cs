using Broiler.JavaScript.Runtime;
using Broiler.CSS;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Runtime;

internal readonly record struct EventListenerRegistration(JSValue Listener, bool Capture, bool Once = false, bool Passive = false);

/// <summary>
/// Per-element inline-style runtime state — the authoritative in-memory inline style, whether it has
/// been seeded from the <c>style=</c> attribute, the set of properties written through the JS
/// <c>element.style</c> path, and the inline <c>on*</c> event handlers. Reached through the bridge's
/// per-instance <c>InlineStyleStateFor</c> accessor.
///
/// Formerly <c>ElementRuntimeState</c>, the catch-all node-runtime-state composite; every other concern
/// (form control, scroll, dialog, shadow, stylesheet, document, animation — the classes below) has since
/// been split into its own per-bridge instance table (Phase 2 items 3/4), leaving only the inline-style
/// concern here. The node model deliberately does not own this state.
/// </summary>
internal sealed class InlineStyleRuntimeState
{
    // P2.5: addEventListener listeners moved off this (process-global) table into the instance-scoped
    // EventTargetRegistry; only inline on* handlers remain node-runtime state here.
    public Dictionary<string, JSValue> InlineEventHandlers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Inline-style property names last written through the JS <c>element.style</c> /
    /// <c>setAttribute("style", …)</c> path, tracked so serialization and computed-style
    /// invalidation preserve author-set intent. Relocated off the <c>Broiler.Dom.DomElement</c> facade
    /// (RF-BRIDGE-1c Phase A — the node model does not own this bridge state).
    /// </summary>
    public HashSet<string> JsSetStyleProps { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Phase 4 item 1 (P4.4c): the OwnerDocRoot parallel-state field is deleted. A node's owning
    // (sub-)document is now derived from the canonical tree (a connected node's absolute root is a
    // Broiler.Dom.DomDocument after the P4.4b sever) or the node's canonical OwnerDocument when
    // detached — see DomBridge.GetOwningDocument. Sub-document createElement nodes are adopted into
    // their content document (DomDocument.AdoptNode) so their detached OwnerDocument is correct.

    /// <summary>
    /// The node's inline style in CSS kebab-case — the authoritative in-memory inline
    /// style (mutated by JS <c>element.style</c>, the anchor resolver, and synthetic
    /// form-control styling; synced back to the <c>style=</c> attribute at serialization).
    /// Relocated off the <c>Broiler.Dom.DomElement</c> facade (RF-BRIDGE-1c Phase B); reached through
    /// <c>DomBridge.InlineStyle(element)</c>, which lazily seeds it from the <c>style=</c>
    /// attribute on first access (see <see cref="StyleSeeded"/>).
    /// </summary>
    public Dictionary<string, string> Style { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <see cref="Style"/> has been seeded from the element's <c>style=</c>
    /// attribute yet. The lazy seed runs once on first <c>InlineStyle</c> access; the
    /// <c>style=</c> attribute setter and <c>cloneNode</c> set this explicitly.
    /// </summary>
    public bool StyleSeeded { get; set; }

    // Phase 2 items 3/4 (de-globalization, 2026-07-17): the former ElementRuntimeState composite's other
    // concerns — FormControl, Scroll, Dialog, Shadow, StyleSheet, Document and Animation — were each split
    // into their own per-bridge instance table (DomBridge._formControlRuntimeStates via FormControlStateFor,
    // _scrollRuntimeStates via ScrollStateFor, _dialogRuntimeStates via DialogStateFor, _shadowRuntimeStates
    // via ShadowStateFor, _styleSheetRuntimeStates via StyleSheetStateFor, _documentRuntimeStates via
    // DocumentStateFor, _animationRuntimeStates via AnimationStateFor). The inline-style concern that
    // remained was itself de-globalized to a per-bridge table (DomBridge._inlineStyleStates via
    // InlineStyleStateFor) and this composite renamed to InlineStyleRuntimeState — no process-static
    // per-element runtime table remains. See the *RuntimeState classes below (still used by those tables).
    // Phase 4 item 5 (2026-07-19): each table's cloneNode copy is now a per-class CopyTo, aggregated by
    // DomBridge.CopyBridgeRuntimeStateTo (a single authority, replacing the scattered per-field CopyTo list
    // the earlier de-globalization had inlined into CloneDomElement).
}

// Phase 4 item 5 (CloneDomElement de-risk, 2026-07-19): each runtime-state composite owns a
// CopyTo(target) that clones its own fields, co-located with the field declarations. cloneNode's
// bridge-state copy (DomBridge.CopyBridgeRuntimeStateTo) is the single caller: keeping the copy
// semantics next to the fields means adding a field can no longer silently drop from the clone
// (the old scattered CopyTo list in CloneDomElement had to be hand-updated in a different file).
internal sealed class FormControlRuntimeState
{
    public RuntimeValue<string> Value { get; } = new();
    public RuntimeValue<bool> Checked { get; } = new();
    public RuntimeValue<bool> DefaultSelected { get; } = new();
    public RuntimeValue<int> SelectedIndex { get; } = new();
    public RuntimeValue<string> ReturnValue { get; } = new();

    public void CopyTo(FormControlRuntimeState target)
    {
        Value.CopyTo(target.Value);
        Checked.CopyTo(target.Checked);
        DefaultSelected.CopyTo(target.DefaultSelected);
        SelectedIndex.CopyTo(target.SelectedIndex);
        ReturnValue.CopyTo(target.ReturnValue);
    }
}

internal sealed class ScrollRuntimeState
{
    // affectsLayout: false — a scroll offset never reaches the renderer, so writing one leaves a
    // retained geometry snapshot valid. See the RuntimeValue<T> parameter docs below.
    public RuntimeValue<double> Left { get; } = new(affectsLayout: false);
    public RuntimeValue<double> Top { get; } = new(affectsLayout: false);

    public void CopyTo(ScrollRuntimeState target)
    {
        Left.CopyTo(target.Left);
        Top.CopyTo(target.Top);
    }
}

internal sealed class DialogRuntimeState
{
    public RuntimeValue<bool> Modal { get; } = new();
    public RuntimeValue<int> TopLayerOrder { get; } = new();

    // Popover API (HTML §popover): set by showPopover(), cleared by
    // hidePopover() — except when an `overlay` allow-discrete transition keeps
    // the element in the top layer as it animates out, in which case it stays
    // set so its ::backdrop still renders for the snapshot.
    public RuntimeValue<bool> PopoverOpen { get; } = new();

    // CSS Position §overlay: set when hidePopover() left the element in the top layer because its
    // `overlay` is transitioning out (see PopoverOpen note); cleared on the next showPopover(). It
    // distinguishes an element whose `overlay` is transitioning *out* (still in the top layer) from
    // one whose `overlay` is transitioning *in* on show (held *out* of the top layer until it
    // finishes) — both have PopoverOpen set and the same transition declarations.
    public RuntimeValue<bool> PopoverTransitioningOut { get; } = new();

    // Fullscreen §fullscreen-element: set by requestFullscreen(), cleared by exitFullscreen(). A
    // fullscreen element is in the top layer and generates a ::backdrop exactly as a modal dialog
    // does, which is why it shares this state rather than carrying its own table.
    public RuntimeValue<bool> Fullscreen { get; } = new();

    public void CopyTo(DialogRuntimeState target)
    {
        Modal.CopyTo(target.Modal);
        TopLayerOrder.CopyTo(target.TopLayerOrder);
        PopoverOpen.CopyTo(target.PopoverOpen);
        PopoverTransitioningOut.CopyTo(target.PopoverTransitioningOut);
        Fullscreen.CopyTo(target.Fullscreen);
    }
}

internal sealed class ShadowRuntimeState
{
    public RuntimeValue<DomElement> Root { get; } = new();
    public RuntimeValue<DomElement> Host { get; } = new();
    public RuntimeValue<string> Mode { get; } = new();

    // Copies the shadow root/host references verbatim — the clone points at the SAME shadow
    // root/host as the source (the pre-existing cloneNode behaviour, preserved).
    public void CopyTo(ShadowRuntimeState target)
    {
        Root.CopyTo(target.Root);
        Host.CopyTo(target.Host);
        Mode.CopyTo(target.Mode);
    }
}

internal sealed class StyleSheetRuntimeState
{
    public RuntimeValue<string> FetchedCss { get; } = new();

    /// <summary>
    /// The live, mutable CSSOM rule list backing this style element's stylesheet —
    /// the single source of truth shared by the CSSOM (<c>cssRules</c>/<c>insertRule</c>/
    /// <c>deleteRule</c>), the renderer/legacy-cascade text, and the
    /// <c>getComputedStyle</c> engine sheet (Phase 6 store unification). <c>null</c>
    /// until first materialized from <see cref="RulesSourceText"/>.
    /// </summary>
    public List<CssRule>? Rules
    {
        get;
        set { field = value; BridgeRuntimeStateEpoch.Bump(); }
    }

    /// <summary>
    /// The source text <see cref="Rules"/> was last parsed from. When the element's
    /// current source text differs (e.g. <c>textContent</c> was replaced), the rules
    /// are reparsed — discarding any <c>insertRule</c>/<c>deleteRule</c> mutations,
    /// per CSSOM semantics.
    /// </summary>
    public string? RulesSourceText { get; set; }

    /// <summary>
    /// <c>true</c> once <c>insertRule</c>/<c>deleteRule</c> has mutated <see cref="Rules"/>
    /// away from the parsed source. While <c>false</c>, the renderer text is the raw
    /// author source (byte-identical to pre-Phase-6); once <c>true</c>, the renderer
    /// text is serialized from the model so the mutation is observed downstream.
    /// </summary>
    /// <remarks>
    /// The setter is also the CSSOM's mutation signal: <c>DomBridge.MarkRulesMutated</c> sets it from
    /// <c>insertRule</c>/<c>deleteRule</c>, which change the rule <em>list's contents</em> and so move
    /// the cascade without touching the DOM at all. <see cref="BridgeRuntimeStateEpoch"/> has to see
    /// that, or a retained geometry snapshot would answer from the pre-edit stylesheet.
    /// </remarks>
    public bool RulesMutated
    {
        get;
        set { field = value; BridgeRuntimeStateEpoch.Bump(); }
    }

    /// <summary>
    /// The script-set CSSOM <c>disabled</c> flag (<c>CSSStyleSheet.disabled</c>), or
    /// <c>null</c> when script has not set it. When <c>null</c> the effective disabled
    /// state falls back to the element's <c>disabled</c> content attribute (only a
    /// <c>&lt;link&gt;</c> has one). A disabled sheet neither applies to the cascade nor,
    /// for a <c>&lt;link&gt;</c>, appears in <c>document.styleSheets</c> — CSSOM §2.3 /
    /// HTML §4.2.4 (<c>&lt;link disabled&gt;</c>).
    /// </summary>
    public bool? DisabledOverride
    {
        get;
        // Disabling a sheet removes its rules from the cascade — a layout input, and one no DOM
        // mutation records, so the epoch must move with it.
        set { field = value; BridgeRuntimeStateEpoch.Bump(); }
    }

    /// <summary>
    /// The <c>href</c> this <c>&lt;link rel="stylesheet"&gt;</c> last dispatched its
    /// <c>load</c>/<c>error</c> event for, or <c>null</c> if it never has. HTML §4.2.4 fires the
    /// event once per fetch, so pointing the link at a *different* sheet must fire again — keying on
    /// the href rather than a bare bool is what makes a re-point observable while a repeated
    /// insertion or a second write of the same value stays silent.
    /// </summary>
    public string? LoadEventFiredForHref { get; set; }

    // The rule list is deep-copied (a fresh List) so the clone's insertRule/deleteRule do not
    // mutate the source sheet; the source text / mutated flag are copied verbatim. The
    // script-set DisabledOverride is deliberately NOT copied: a clone re-derives its disabled
    // state from its own `disabled` content attribute (HTMLLinkElement-disabled: the
    // "explicitly enabled" state does not persist on clones). LoadEventFiredForHref is likewise
    // not copied — a clone has not fetched anything and so has not fired.
    public void CopyTo(StyleSheetRuntimeState target)
    {
        FetchedCss.CopyTo(target.FetchedCss);
        target.Rules = Rules is null ? null : [.. Rules];
        target.RulesSourceText = RulesSourceText;
        target.RulesMutated = RulesMutated;
    }
}

internal sealed class DocumentRuntimeState
{
    public RuntimeValue<bool> HasViewport { get; } = new();

    public void CopyTo(DocumentRuntimeState target) => HasViewport.CopyTo(target.HasViewport);
}

internal sealed class AnimationRuntimeState
{
    public RuntimeValue<double> CurrentTimeMilliseconds { get; } = new();

    public void CopyTo(AnimationRuntimeState target) => CurrentTimeMilliseconds.CopyTo(target.CurrentTimeMilliseconds);
}

/// <summary>
/// Monotonic counter over every write to layout-affecting bridge runtime state — the state that
/// <c>DomBridge.CopyBridgeRuntimeStateTo</c> carries into a render projection and that therefore
/// decides what the shared geometry snapshot lays out. It is the non-DOM half of the snapshot's
/// cache key (<c>DomDocument.Version</c> is the DOM half); see
/// <c>DomBridge.CurrentLayoutSnapshotKey</c>.
/// </summary>
/// <remarks>
/// Process-wide rather than per-bridge because <see cref="RuntimeValue{T}"/> holds no back-reference
/// to the bridge that owns it. A second document in the same process therefore invalidates this one's
/// snapshot too — conservative, never wrong: a spurious bump only costs a rebuild.
/// </remarks>
internal static class BridgeRuntimeStateEpoch
{
    private static long _value;

    public static long Current => Interlocked.Read(ref _value);

    public static void Bump() => Interlocked.Increment(ref _value);
}

/// <param name="affectsLayout">
/// Whether writes to this slot can change what the renderer lays out, and so must invalidate a
/// retained geometry snapshot. True for every slot except scroll offsets: the bridge keeps those in
/// its own <c>_scrollRuntimeStates</c> table, which is not part of the projected
/// <see cref="DomDocument"/> handed to <c>ILayoutView.GetGeometry</c> — no attribute, inline-style
/// declaration or serialized text carries them — so the renderer never sees a scroll offset and its
/// box geometry cannot depend on one. (The reader that does consult scroll state, the bridge-side
/// anchor resolver, is off the snapshot path since the Phase-5 endgame moved anchor placement into
/// the engine's native pass; see <c>ComputeUnzoomedLayoutRect</c>.) Excluding them is what lets a
/// loop of <c>scrollTop</c>/<c>scrollLeft</c> writes reuse one layout instead of forcing one per
/// write — the shape behind WPT issue #1682's twenty <c>css/css-overflow</c> timeouts.
/// </param>
internal sealed class RuntimeValue<T>(bool affectsLayout = true)
{
    public bool IsSet { get; private set; }

    public T? Value { get; private set; }

    public void Set(object? value)
    {
        Value = value is null ? default : (T)value;
        IsSet = true;
        if (affectsLayout)
            BridgeRuntimeStateEpoch.Bump();
    }

    public bool TryGet(out object? value)
    {
        value = Value;
        return IsSet;
    }

    public bool Remove()
    {
        var wasSet = IsSet;
        Value = default;
        IsSet = false;
        if (affectsLayout && wasSet)
            BridgeRuntimeStateEpoch.Bump();
        return wasSet;
    }

    public void CopyTo(RuntimeValue<T> target)
    {
        if (IsSet)
            target.Set(Value);
        else
            target.Remove();
    }
}
