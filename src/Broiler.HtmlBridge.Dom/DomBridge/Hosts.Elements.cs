using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFormHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FormBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.9). Explicit interface member, so it does not
/// widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The form slice is not a seam any more: the module speaks JSEAL, <c>WrapNode</c> is forwarded as it
/// stands and no member converts, so wrapper identity (<c>form.q === form.elements.q</c>) is the same
/// question it was before. (This said the rest of the bridge still held engine objects and
/// <c>JsInterop</c> cast between them.)
/// </remarks>
public sealed partial class DomBridge : IFormHost
{
    IJsRealm IFormHost.Realm => Realm;

    JsValue IFormHost.WrapNode(DomNode node) => WrapNode(node);

    void IFormHost.ResetForm(DomElement form) => ResetFormControls(form);

    IReadOnlyList<DomElement> IFormHost.CollectFormControls(DomElement form) =>
        CollectFormControlsIncludingCustom(form);

    bool IFormHost.IsCustomElementValid(DomElement element) =>
        !CustomElements.IsFormAssociated(element) || ElementInternals.IsValid(element);
}

// Explicit IFormAssociationHost implementation for the FormAssociationBinding feature module: the
// realm, the wrapper factory, the document-order element list, the by-id lookup and the live NodeList
// a control's `labels` is. Explicit interface members, so these seams do not widen the public
// DomBridge surface.
//
// The whole seam is spelled in JSEAL now. The collection was minted through DomCollectionBinding's
// engine-typed entry point and unwrapped back across JsInterop while that module was unmigrated; it
// has a realm-shaped NodeList of its own, so the list is built and handed on without either
// conversion. Realm must be an explicit implementation — DomBridge.Realm is internal, so an implicit
// one does not compile (CS0737).
public sealed partial class DomBridge : Dom.Features.IFormAssociationHost
{
    IJsRealm Dom.Features.IFormAssociationHost.Realm => Realm;

    JsValue Dom.Features.IFormAssociationHost.WrapNode(DomNode node) => WrapNode(node);

    IReadOnlyList<DomElement> Dom.Features.IFormAssociationHost.Elements => Elements;

    DomElement? Dom.Features.IFormAssociationHost.GetElementById(string id) =>
        FindInSubTree(DocumentElement, element => element.Id == id);

    JsValue Dom.Features.IFormAssociationHost.LiveNodeList(Func<List<DomElement>> contents) =>
        Dom.Features.DomCollectionBinding.NodeList(
            Realm,
            // Re-run on every read, and the wrapping with it: a label created since the list was
            // handed out has no wrapper yet, so wrapping here rather than at build time is what
            // keeps the list live rather than merely re-counted.
            () => [.. contents().Select(WrapNode)]);

    bool Dom.Features.IFormAssociationHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);
}

// Explicit IFormControlHost implementation for the FormControlBinding feature module (Phase 3): the
// input's dirty IDL value/checked state stays on the per-element FormControl runtime slot and is exposed
// here as named primitives; the <select> value resolution delegates to the SelectBinding the bridge
// owns, and the radio-sibling walk / style-scope invalidation forward to the existing bridge helpers.
// Explicit interface members, so these seams do not widen the public DomBridge surface.
//
// Neither half of this seam is engine-typed any more: the module speaks JSEAL, and the one member
// that produces a JavaScript object — the FileList — is minted by DomCollectionBinding.FileList in
// the bridge's realm and cached as the JsValue it answers, with no JsInterop cast. (This said the
// builder was unmigrated and that the engine object crossed through a JsInterop cast.)
public sealed partial class DomBridge : Dom.Features.IFormControlHost
{
    /// <summary>One <c>FileList</c> per file input, cached so <c>input.files === input.files</c>. The
    /// contents function stays live over an always-empty list rather than being a fixed one, so a file
    /// selection would need no second shape.</summary>
    private readonly Dictionary<DomElement, JsValue> _fileLists = [];

    IJsRealm Dom.Features.IFormControlHost.Realm => Realm;

    JsValue Dom.Features.IFormControlHost.GetFileList(DomElement element)
    {
        if (_fileLists.TryGetValue(element, out var existing))
            return existing;

        // DomCollectionBinding.FileList's only overload, which takes the realm and answers a JsValue.
        // (This said "the realm overload", as if an engine-object one still stood beside it.)
        // DomCollectionBinding's header named this call as the one keeping FileList(JSContext, ...)
        // alive; it is migrated, so a file input no longer asks the bridge for a script context and
        // no longer throws "asked for before the bridge was attached" when there is a realm but no
        // context to hand it.
        var files = Dom.Features.DomCollectionBinding.FileList(Realm, static () => []);
        _fileLists[element] = files;
        return files;
    }

    bool Dom.Features.IFormControlHost.TryGetFormControlValue(DomElement element, out string value)
    {
        if (_formState.TryGetDirtyValue(element, out var stored) && stored is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    void Dom.Features.IFormControlHost.SetFormControlValue(DomElement element, string value) =>
        _formState.SetDirtyValue(element, value);

    string Dom.Features.IFormControlHost.GetSelectValue(DomElement element) => _select.GetValue(element);

    void Dom.Features.IFormControlHost.SetSelectValue(DomElement element, string value) =>
        _select.SetValue(element, value);

    bool Dom.Features.IFormControlHost.TryGetFormControlChecked(DomElement element, out bool value) =>
        _formState.TryGetDirtyChecked(element, out value);

    void Dom.Features.IFormControlHost.SetFormControlChecked(DomElement element, bool value) =>
        _formState.SetDirtyChecked(element, value);

    void Dom.Features.IFormControlHost.InvalidateStyleScope(DomElement anchor) => InvalidateStyleScope(anchor);
}

// Explicit IFormSubmitHost implementation for the FormSubmitBinding feature module (Phase 3): the bridge
// exposes read access to the live per-node listener store via an explicit interface member, so the
// submit action never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IFormSubmitHost
{
    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IFormSubmitHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);

    void Dom.Features.IFormSubmitHost.RequestFormSubmission(DomElement form)
    {
        var index = IndexOfForm(form);
        if (index < 0)
        {
            // A form the script built but never inserted. There is nothing for the host to find
            // when it walks the serialized document, so saying "submit form 3" would name a form
            // that is not there.
            RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
                "form.submit() on a form that is not in the document; nothing to submit");
            return;
        }

        var action = ResolveFormAction(form);
        RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
            $"form.submit() requested for form {index} to {action}; the host builds the data set and decides whether to follow it");

        RequestNavigation(new NavigationRequest(action, NavigationKind.FormSubmit) { FormIndex = index });
    }

    /// <summary>
    /// The form's position among the document's forms, in document order, or <c>-1</c> when it is
    /// not in the document. The same walk <see cref="Elements"/> makes, so the host counting forms
    /// in the serialized document counts them in this order.
    /// </summary>
    private int IndexOfForm(DomElement form)
    {
        var seen = 0;
        foreach (var element in _document.InclusiveDescendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ReferenceEquals(element, form))
                return seen;

            seen++;
        }

        return -1;
    }

    /// <summary>
    /// The form's <c>action</c> resolved against the document, falling back to the document's own
    /// URL — which is what an absent or empty <c>action</c> means (HTML §4.10.21.3).
    /// </summary>
    private string ResolveFormAction(DomElement form)
    {
        var action = form.GetAttribute("action");
        if (string.IsNullOrWhiteSpace(action))
            return _pageUrl;

        return Uri.TryCreate(_pageUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, action, out var resolved)
                ? resolved.ToString()
                : action;
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISelectHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.SelectBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.8). Explicit interface members, so these
/// seams do not widen the public <c>DomBridge</c> surface. The select/option form-control state
/// lives in the bridge's per-element <see cref="FormControlRuntimeState"/> table, reached through
/// <see cref="FormControlStateFor"/>; these named accessors are the only way the module touches it.
/// </summary>
/// <remarks>
/// The select slice is not a half-migrated seam any more: the module speaks JSEAL, the wrapper factory
/// answers a handle and the reverse lookup takes one, so no cast is left in this file. Wrapper
/// identity (<c>option === option</c>, and the weak tables keyed on it) is the same question it was
/// before, because <c>Runtime/JsObjectRegistry</c> keys on <see cref="JsValue.ObjectIdentity"/> — the
/// reference the handle carries, which is canonical per object by definition of handle equality.
/// </remarks>
public sealed partial class DomBridge : ISelectHost
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element form-control runtime state
    // (checkbox/radio checkedness, option value/defaultSelected, select selectedIndex, dialog
    // returnValue) was the FormControl slot of the process-static ElementRuntimeState table; it is now
    // a per-bridge instance table, owned by the session's bridge. Still element-keyed, so it GCs with
    // the element and the cloneNode copy (see CloneDomElement) is preserved. Reached from the bridge's
    // own instance methods directly, and from the feature bindings that need it (EventTargetBinding's
    // click checkbox/radio toggle, and the `:checked` selector state provider) through their host
    // interfaces — the concern's static callers were threaded, not left on a process-static table.
    private readonly Broiler.Dom.Html.HtmlFormState _formState = new()
    {
        OnStateChanged = BridgeRuntimeStateEpoch.Bump
    };

    IJsRealm ISelectHost.Realm => Realm;

    JsValue ISelectHost.WrapNode(DomNode node) => WrapNode(node);

    DomElement? ISelectHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(wrapper) : null;

    bool ISelectHost.TryGetSelectedIndex(DomElement select, out int index) =>
        _formState.TryGetDirtySelectedIndex(select, out index);

    void ISelectHost.SetSelectedIndex(DomElement select, int index) =>
        _formState.SetDirtySelectedIndex(select, index);

    bool ISelectHost.TryGetOptionValue(DomElement option, out string value)
    {
        if (_formState.TryGetDirtyValue(option, out var stored) && stored is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // defaultSelected reflects the `selected` CONTENT ATTRIBUTE (HTML §4.10.10), so the runtime slot
    // is an override of it rather than the whole story.
    bool ISelectHost.GetOptionDefaultSelected(DomElement option) =>
        _formState.GetEffectiveOptionSelected(option);

    // Writing the property writes the attribute it reflects, so a later reset — which clears the
    // slot — restores what was written rather than what the markup happened to say.
    void ISelectHost.SetOptionDefaultSelected(DomElement option, bool value)
    {
        _formState.SetDirtyOptionSelected(option, value);
        if (value)
            SetAttr(option, "selected", string.Empty);
        else
            RemoveAttr(option, "selected");
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ITableHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.TableBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.5). Explicit interface members, so these
/// seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The table slice is spelled in JSEAL end to end: every member of this contract is realm- or
/// handle-typed, and the module's last engine-typed member went with the installer overload whose
/// caller had stopped needing it. What stood here called this "the half-migrated seam for the table
/// slice" and named <see cref="Dom.Runtime.JsInterop"/> as "the cast between them": this file has
/// never performed that cast, and since the installer's deletion nothing in the slice does. The
/// property the remark was defending holds and still matters -- a JSEAL object handle carries the
/// engine's own object, so wrapper identity (<c>row === row</c>, and the weak tables keyed on it) is
/// the same question it was before.
/// </remarks>
public sealed partial class DomBridge : ITableHost
{
    IJsRealm ITableHost.Realm => Realm;

    JsValue ITableHost.WrapNode(DomNode node) => WrapNode(node);

    DomElement ITableHost.CreateElement(string tag)
    {
        var element = CreateBridgeElement(tag);
        return element;
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IDialogHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.DialogBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.7). Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. The dialog/
/// popover state lives in the per-element <see cref="DialogRuntimeState"/> table (a dialog's
/// <c>returnValue</c> in <see cref="FormControlRuntimeState"/>) and <c>_topLayerCounter</c>. The
/// module reaches it only through these accessors, but DomBridge/AnchorResolver reads it directly.
/// </summary>
/// <remarks>
/// Nothing in this file is engine-typed. The module speaks JSEAL, and the one member that hands an
/// object to the rest of the bridge, <see cref="IDialogHost.DispatchFullscreenChange"/>, builds its
/// event through the realm and gives that handle to the element dispatcher as it is. (This called
/// the file the half-migrated seam, said that dispatcher still took an engine object, and had the
/// event cross to it through a <c>JsInterop</c> cast.)
/// </remarks>
public sealed partial class DomBridge : IDialogHost
{
    IJsRealm IDialogHost.Realm => Realm;

    void IDialogHost.SetOpenAttribute(DomElement element, bool open)
    {
        if (open)
            SetAttr(element, "open", "");
        else
            RemoveAttr(element, "open");
    }

    bool IDialogHost.HasOpenAttribute(DomElement element) => HasAttr(element, "open");

    void IDialogHost.InvalidateStyleScope(DomElement element) => InvalidateStyleScope(element);

    void IDialogHost.AssignNextTopLayerOrder(DomElement element) =>
        DialogStateFor(element).TopLayerOrder.Set(++_topLayerCounter);

    void IDialogHost.SetDialogModal(DomElement element, bool modal)
    {
        if (modal)
            DialogStateFor(element).Modal.Set(true);
        else
            DialogStateFor(element).Modal.Remove();
    }

    void IDialogHost.SetPopoverOpen(DomElement element, bool open)
    {
        if (open)
        {
            DialogStateFor(element).PopoverOpen.Set(true);
            // A fresh show clears any leftover "transitioning out" mark: if the element is now
            // transitioning `overlay` at all, it is transitioning *in*.
            DialogStateFor(element).PopoverTransitioningOut.Remove();
        }
        else
        {
            DialogStateFor(element).PopoverOpen.Remove();
        }
    }

    void IDialogHost.SetFullscreen(DomElement element, bool fullscreen)
    {
        if (fullscreen)
            DialogStateFor(element).Fullscreen.Set(true);
        else
            DialogStateFor(element).Fullscreen.Remove();
    }

    DomElement? IDialogHost.GetFullscreenElement() => FindFullscreenElement();

    void IDialogHost.DispatchFullscreenChange(DomElement target)
    {
        try
        {
            // The event is built through the realm and the dispatcher takes the handle as it is, so
            // the object the listener sees is the one this built. (This said the event was unwrapped
            // for a dispatcher that still took an engine object.)
            var evt = Realm.NewObject();
            Realm.DefineValue(evt, "type", JsValue.String("fullscreenchange"));
            Realm.DefineValue(evt, "bubbles", JsValue.True);
            _eventDispatch.DispatchEventOnElement(target, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.DispatchFullscreenChange",
                $"fullscreenchange handler error: {ex.Message}", ex);
        }
    }

    void IDialogHost.MarkPopoverOverlayTransitioningOut(DomElement element) =>
        DialogStateFor(element).PopoverTransitioningOut.Set(true);

    string IDialogHost.GetReturnValue(DomElement element) =>
        _formState.TryGetReturnValue(element, out var rv) && rv is string s
            ? s
            : string.Empty;

    void IDialogHost.SetReturnValue(DomElement element, string value) =>
        _formState.SetReturnValue(element, value);

    bool IDialogHost.PopoverKeepsOverlayOnHide(DomElement element) => PopoverKeepsOverlayOnHide(element);

    bool IDialogHost.DialogKeepsOverlayOnClose(DomElement element) => DialogKeepsOverlayOnClose(element);

    bool IDialogHost.DialogKeepsDisplayOnClose(DomElement element) => DialogKeepsDisplayOnClose(element);
}

// Explicit ICanvasHost implementation for the CanvasBinding feature module: the canvas context reaches
// the realm's Uint8ClampedArray constructor through the window object and nothing else, so the module
// touches no bridge private state and the public surface is unchanged.
//
// The window is the bridge's root, a handle, so this forwards it. A bridge that has not registered a
// window yet still answers `undefined` and not the Missing the root holds: the module's `IsObject`
// guard reads the two alike, but `undefined` is what this member has answered since the contract
// stopped expressing the absence as a CLR null, and dropping the coalesce would have compiled without
// a word.
public sealed partial class DomBridge : Dom.Features.ICanvasHost
{
    JsValue Dom.Features.ICanvasHost.Window =>
        WindowHandle.IsMissing ? JsValue.Undefined : WindowHandle;
}

// Explicit IComputedStyleHost implementation for the ComputedStyleBinding feature module (Phase 3):
// the bridge exposes its realm, the JS-wrapper reverse lookup and the computed-style object builder via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// Nothing in this file crosses to the engine. The wrapper reverse lookup in DomBridge/Utilities.cs
// takes the handle this member is handed, so the member forwards it, and the registry behind it is
// keyed on JsValue.ObjectIdentity rather than on an engine object. The lookup keeps its engine-shaped
// name; that is a rename waiting to happen, not a seam.
public sealed partial class DomBridge : Dom.Features.IComputedStyleHost
{
    IJsRealm Dom.Features.IComputedStyleHost.Realm => Realm;

    DomElement? Dom.Features.IComputedStyleHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(wrapper) : null;

    JsValue Dom.Features.IComputedStyleHost.BuildComputedStyle(DomElement? element, string? pseudoElement)
        => BuildComputedStyleObject(element, pseudoElement);
}

// Explicit IInlineStyleHost implementation for StyleDeclarationBinding's inline (element.style)
// declaration callbacks (Phase 2 item 4 de-globalization, 2026-07-17): the per-element inline-style
// dictionary and "set via JS" bookkeeping moved off the process-static ElementRuntimeState table onto
// the bridge instance, so the module reaches them through this narrow contract (each member forwards to
// the corresponding bridge-instance helper) rather than a static DomBridge call.
public sealed partial class DomBridge : Dom.Features.IInlineStyleHost
{
    Dictionary<string, string> Dom.Features.IInlineStyleHost.InlineStyle(DomElement element)
        => InlineStyle(element);

    void Dom.Features.IInlineStyleHost.MarkInlineStylePropSetByJs(DomElement element, string property)
        => MarkInlineStylePropSetByJs(element, property);

    void Dom.Features.IInlineStyleHost.UnmarkInlineStylePropSetByJs(DomElement element, string property)
        => UnmarkInlineStylePropSetByJs(element, property);

    void Dom.Features.IInlineStyleHost.ClearInlineStylePropsSetByJs(DomElement element)
        => ClearInlineStylePropsSetByJs(element);

    IReadOnlyCollection<string> Dom.Features.IInlineStyleHost.InlineStylePropsSetByJs(DomElement element)
        => InlineStylePropsSetByJs(element);
}

// Explicit IElementGeometryHost implementation for the ElementGeometryBinding feature module (Phase 3):
// the box-model metrics and scrolling operations are the one Phase 3 family that genuinely reads the live
// layout, so the contract is wide by design. Each member forwards to the existing private LayoutMetrics.*
// method — the module now names the exact geometry surface it depends on instead of reaching into the
// bridge directly.
//
// This file names no engine type, and the two option-reading members below are why it used to. Each
// unwrapped the JSEAL handle the page had passed into the engine's own object and handed it to an
// adapter in LayoutMetrics.Scrolling.cs, whose entire body wrapped it back into a handle to do the
// read. The comment that stood here justified the detour by saying those readers "read an engine call
// frame and are shared word for word with the window and sub-window scroll hosts": they read a JSEAL
// handle, and they were shared with nothing -- these two members were their only callers in the tree.
// The window and sub-window hosts have their own copy, in DomBridge/Hosts.Documents.cs.
public sealed partial class DomBridge : Dom.Features.IElementGeometryHost
{
    bool Dom.Features.IElementGeometryHost.IsViewportElementForMetrics(DomElement element) => IsViewportElementForMetrics(element);

    double Dom.Features.IElementGeometryHost.GetClientTopForDomElement(DomElement element) => GetClientTopForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetClientLeftForDomElement(DomElement element) => GetClientLeftForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetClientWidthForDomElement(DomElement element, bool isRoot) => GetClientWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetClientHeightForDomElement(DomElement element, bool isRoot) => GetClientHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetWidthForDomElement(DomElement element, bool isRoot) => GetOffsetWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetHeightForDomElement(DomElement element, bool isRoot) => GetOffsetHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetScrollWidthForDomElement(DomElement element, bool isRoot) => GetScrollWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetScrollHeightForDomElement(DomElement element, bool isRoot) => GetScrollHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetTopForDomElement(DomElement element) => GetOffsetTopForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetOffsetLeftForDomElement(DomElement element) => GetOffsetLeftForDomElement(element);

    double? Dom.Features.IElementGeometryHost.GetElementScrollOffset(DomElement element, bool vertical) => GetElementScrollOffset(element, vertical);

    void Dom.Features.IElementGeometryHost.SetElementScrollOffsetsWithBehavior(DomElement element,
        double? left, double? top, bool relative, bool clamp, string? behavior)
        => SetElementScrollOffsetsWithBehavior(element, left, top, relative, clamp, behavior);

    DomElement? Dom.Features.IElementGeometryHost.GetOffsetParentForDomElement(DomElement element) => GetOffsetParentForDomElement(element);
    DomElement? Dom.Features.IElementGeometryHost.GetScrollParentForDomElement(DomElement element) => GetScrollParentForDomElement(element);

    (double Left, double Top, double Width, double Height) Dom.Features.IElementGeometryHost.GetBoundingClientRectForDomElement(DomElement element, bool isRoot)
        => GetBoundingClientRectForDomElement(element, isRoot);

    /// <summary>
    /// <c>scrollIntoView</c>'s argument, which is a dictionary, a boolean, or nothing at all.
    /// </summary>
    /// <remarks>
    /// The no-argument case is <see cref="JsValue.IsMissing"/> rather than a length test for the reason
    /// the contract records: <c>scrollIntoView()</c> and <c>scrollIntoView(undefined)</c> are different
    /// calls here — the first aligns "start-if-needed", the second aligns "nearest" — and Missing is what
    /// tells them apart.
    /// </remarks>
    (string Block, string Inline, string? Behavior) Dom.Features.IElementGeometryHost.GetScrollIntoViewOptions(in JsCall call)
    {
        const string defaultBlock = "start";
        const string defaultInline = "nearest";

        var first = call[0];
        if (first.IsMissing)
            return (defaultBlock, "start-if-needed", null);

        if (first.IsObject)
        {
            return (
                NormalizeScrollIntoViewAlignment(ReadScrollStringOption(first, "block"), defaultBlock),
                NormalizeScrollIntoViewAlignment(ReadScrollStringOption(first, "inline"), defaultInline),
                ReadScrollBehaviorOption(first));
        }

        if (first.IsBoolean)
        {
            return first.AsBoolean
                ? (defaultBlock, defaultInline, null)
                : ("end", defaultInline, null);
        }

        return (defaultBlock, defaultInline, null);
    }

    void Dom.Features.IElementGeometryHost.ScrollElementIntoView(DomElement element, string? block, string? inline, string? behavior)
        => ScrollElementIntoView(element, block, inline, behavior);

    /// <summary>
    /// <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c>'s arguments: a scroll-options dictionary, or the
    /// <c>(x, y)</c> pair.
    /// </summary>
    /// <remarks>
    /// The coordinates go through the realm's <c>ToNumber</c>, which is the coercion the engine was
    /// performing before — <c>el.scrollTo("100", "0")</c> scrolls, it does not scroll to NaN.
    /// </remarks>
    (double? Left, double? Top, string? Behavior) Dom.Features.IElementGeometryHost.GetScrollOptions(in JsCall call)
    {
        var first = call[0];
        if (first.IsMissing)
            return (null, null, null);

        if (first.IsObject)
        {
            return (
                ReadScrollCoordinateOption(first, "left"),
                ReadScrollCoordinateOption(first, "top"),
                ReadScrollBehaviorOption(first));
        }

        var second = call[1];
        return (call.Realm.ToNumber(first), second.IsMissing ? null : call.Realm.ToNumber(second), null);
    }

    JsValue Dom.Features.IElementGeometryHost.WrapNode(DomNode node) => WrapNode(node);
}

// Explicit IHitTestHost implementation for the HitTestBinding feature module (Phase 3): the bridge
// exposes the realm, the document root, the JS-wrapper factory and the point hit-test via explicit
// interface members, so the module never reaches an arbitrary bridge private field and the public surface
// is unchanged.
//
// Realm is implemented explicitly because DomBridge.Realm is internal: an implicit implementation of a
// public interface member cannot be satisfied by a non-public property (CS0737).
public sealed partial class DomBridge : Dom.Features.IHitTestHost
{
    IJsRealm Dom.Features.IHitTestHost.Realm => Realm;

    DomElement Dom.Features.IHitTestHost.DocumentElement => DocumentElement;

    JsValue Dom.Features.IHitTestHost.WrapNode(DomNode node) => WrapNode(node);

    IReadOnlyList<DomElement> Dom.Features.IHitTestHost.HitTestDocumentPoint(DomNode docRoot, double x, double y)
        => HitTestDocumentPoint(docRoot, x, y);
}
