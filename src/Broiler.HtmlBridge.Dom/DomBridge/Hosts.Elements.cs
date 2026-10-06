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
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FormBinding"/> feature module consumes.
/// Explicit interface member, so it does not
/// widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The module speaks JSEAL, <c>WrapNode</c> is forwarded as it stands and no member converts, so
/// wrapper identity (<c>form.q === form.elements.q</c>) is preserved.
/// </remarks>
public sealed partial class DomBridge : IFormHost
{
    void IFormHost.ResetForm(DomElement form) => RunAsScriptCall(() => ResetForm(form));

    IReadOnlyList<DomElement> IFormHost.CollectFormControls(DomElement form) =>
        CollectFormControlsIncludingCustom(form);
}

// Explicit IFormAssociationHost implementation for the FormAssociationBinding feature module: the
// by-id lookup and the live NodeList a control's `labels` is, with the realm, the wrapper factory
// and the document-order element list inherited from the shared primitives. Explicit interface
// members, so these seams do not widen the public DomBridge surface.
//
// The whole seam is spelled in JSEAL now. The collection was minted through DomCollectionBinding's
// engine-typed entry point and unwrapped back across JsInterop while that module was unmigrated; it
// has a realm-shaped NodeList of its own, so the list is built and handed on without either
// conversion.
public sealed partial class DomBridge : Dom.Features.IFormAssociationHost
{
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

// Explicit IFormControlHost implementation for the FormControlBinding feature module: the
// input's dirty IDL value/checked state stays on the per-element FormControl runtime slot and is exposed
// here as named primitives; the <select> value resolution delegates to the SelectBinding the bridge
// owns, and the radio-sibling walk / style-scope invalidation forward to the existing bridge helpers.
// Explicit interface members, so these seams do not widen the public DomBridge surface.
//
// Neither half of this seam is engine-typed: the module speaks JSEAL, and the one member that
// produces a JavaScript object — the FileList — is minted by DomCollectionBinding.FileList in the
// bridge's realm and cached as the JsValue it answers.
public sealed partial class DomBridge : Dom.Features.IFormControlHost
{
    /// <summary>One <c>FileList</c> per file input, cached so <c>input.files === input.files</c>, live over
    /// the files the user chose for it (DomBridge/FileInputs.cs).</summary>
    private readonly Dictionary<DomElement, JsValue> _fileLists = [];

    JsValue Dom.Features.IFormControlHost.GetFileList(DomElement element)
    {
        if (_fileLists.TryGetValue(element, out var existing))
            return existing;

        // DomCollectionBinding.FileList's only overload, which takes the realm and answers a JsValue.
        // A file input never asks the bridge for a script context, so it cannot throw "asked for
        // before the bridge was attached" when there is a realm but no context to hand it.
        var files = Dom.Features.DomCollectionBinding.FileList(Realm, () => ChosenFilesOf(element).Select(static file => file.Object).ToList());
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

    void Dom.Features.IFormControlHost.SetFormControlValue(DomElement element, string value)
    {
        var previous = _formState.GetEffectiveValue(element);
        _formState.SetDirtyValue(element, value);
        NoteScriptSetFieldValue(element, value);
        if (!string.Equals(previous, value, StringComparison.Ordinal))
        {
            FieldVersion++;
            MoveCaretToEndAfterScriptValue(element, previous, value);
        }
    }

    string Dom.Features.IFormControlHost.GetSelectValue(DomElement element) => _select.GetValue(element);

    string Dom.Features.IFormControlHost.GetFileInputValue(DomElement element) => FileInputValue(element);

    void Dom.Features.IFormControlHost.SetFileInputValue(DomElement element, string value, IJsRealm realm) =>
        SetFileInputValue(element, value, realm);

    void Dom.Features.IFormControlHost.SetSelectValue(DomElement element, string value) =>
        _select.SetValue(element, value);

    bool Dom.Features.IFormControlHost.TryGetFormControlChecked(DomElement element, out bool value) =>
        _formState.TryGetDirtyChecked(element, out value);

    void Dom.Features.IFormControlHost.SetFormControlChecked(DomElement element, bool value) =>
        _formState.SetDirtyChecked(element, value);
}

// Explicit IFormSubmitHost implementation for the FormSubmitBinding feature module: form.submit() is an
// explicit interface member, so the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IFormSubmitHost
{
    void Dom.Features.IFormSubmitHost.SubmitFromSubmitMethod(DomElement form) => SubmitFromSubmitMethod(form);

    /// <summary>
    /// Asks the host to submit <paramref name="form"/> as <paramref name="submitter"/>, with what its
    /// <c>formdata</c> listeners did to its entry list (<paramref name="edits"/>).
    /// </summary>
    private void RequestFormSubmission(DomElement form, DomElement? submitter, (int X, int Y) imagePoint, IReadOnlyList<FormDataEdit> edits)
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

        var action = ResolveFormAction(form, submitter);
        RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
            $"A submission of form {index} to {action}; the host decides whether to follow it");

        // What the submission sends, from the page's own form data set (see NavigationRequest.Submission): the
        // same encoding a frame's submission into the page gets (DomBridge/FrameSubmission.cs).
        var entries = ApplyFormDataEdits(BuildFormEntryList(form, submitter, imagePoint), edits);
        FormSubmissionRequest submission;
        if (SubmissionMethodOf(form, submitter) == "post")
        {
            var body = EncodeFormBody(entries, FormEncodingOf(form, submitter));
            submission = new FormSubmissionRequest(action, body.Content, body.ContentType);
        }
        else
        {
            submission = new FormSubmissionRequest(WithQuery(action, UrlEncodeEntries(entries)));
        }

        // The form's own document starts a form submission (HTML "submit": the form's node document
        // is the source document), whoever called submit().
        RequestNavigation(new NavigationRequest(action, NavigationKind.FormSubmit)
        {
            FormIndex = index,
            SubmitterIndex = submitter is null ? -1 : IndexOfButtonOrInput(submitter),
            SubmitterX = imagePoint.X,
            SubmitterY = imagePoint.Y,
            FormDataEdits = edits,
            Initiator = DocumentContextFor(form),
            Submission = submission,
        });
    }

    /// <summary>
    /// <paramref name="control"/>'s position among the document's <c>button</c> and <c>input</c> elements in
    /// document order, or <c>-1</c> when it is not in the document -- how the host finds a submitter in its
    /// parse of the serialized document, as it finds the form by <see cref="IndexOfForm"/>.
    /// </summary>
    private int IndexOfButtonOrInput(DomElement control)
    {
        var seen = 0;
        foreach (var element in _document.InclusiveDescendants().OfType<DomElement>())
        {
            if (!element.TagName.Equals("button", StringComparison.OrdinalIgnoreCase) &&
                !element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ReferenceEquals(element, control))
                return seen;

            seen++;
        }

        return -1;
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
    /// The submission's action -- the submitter's <c>formaction</c>, else the form's <c>action</c> --
    /// resolved against the document, falling back to the document's own URL — which is what an absent
    /// or empty action means (HTML §4.10.21.3).
    /// </summary>
    private string ResolveFormAction(DomElement form, DomElement? submitter = null)
    {
        var action = submitter?.GetAttribute("formaction") ?? form.GetAttribute("action");
        if (string.IsNullOrWhiteSpace(action))
            return CurrentPageUrl;

        return Uri.TryCreate(DocumentBaseUrl(), UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, action, out var resolved)
                ? resolved.ToString()
                : action;
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISelectHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.SelectBinding"/> feature module consumes.
/// Explicit interface members, so these
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
    // The per-element form-control runtime state
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

    DomElement? ISelectHost.FindElement(JsValue wrapper) => FindDomElementByJSObject(wrapper);

    // Each option's selectedness and dirtiness, once a script or the user has changed its select, and the
    // selects that hold them (Features/SelectBinding.cs). Element-keyed, so they go with their elements.
    private sealed class OptionState(bool selected, bool dirty)
    {
        public bool Selected { get; set; } = selected;

        public bool Dirty { get; set; } = dirty;
    }

    private readonly ConditionalWeakTable<DomElement, OptionState> _optionStates = new();
    private readonly ConditionalWeakTable<DomElement, object> _heldSelects = new();
    private static readonly object Held = new();

    // One live collection per element and kind -- a select's options and selected options -- so a page's
    // `select.options === select.options` holds.
    private readonly ConditionalWeakTable<DomElement, Dictionary<string, JsValue>> _elementCollections = new();

    bool ISelectHost.TryGetOptionState(DomElement option, out bool selected, out bool dirty)
    {
        if (_optionStates.TryGetValue(option, out var state))
        {
            selected = state.Selected;
            dirty = state.Dirty;
            return true;
        }

        selected = dirty = false;
        return false;
    }

    void ISelectHost.SetOptionState(DomElement option, bool selected, bool dirty)
    {
        if (_optionStates.TryGetValue(option, out var state))
        {
            state.Selected = selected;
            state.Dirty = dirty;
            return;
        }

        _optionStates.AddOrUpdate(option, new OptionState(selected, dirty));
    }

    bool ISelectHost.IsSelectHeld(DomElement select) => IsSelectHeld(select);

    /// <summary>Whether a script or the user has changed the select, so its options' selectedness is held rather than read from their markup.</summary>
    private bool IsSelectHeld(DomElement select) => _heldSelects.TryGetValue(select, out _);

    void ISelectHost.HoldSelect(DomElement select) => _heldSelects.AddOrUpdate(select, Held);

    void ISelectHost.NoteSelectionChanged(DomElement select)
    {
        BridgeRuntimeStateEpoch.Bump();
        InvalidateStyleScope(select);
        NoteElementStateChange();
    }

    JsValue ISelectHost.LiveCollection(DomElement owner, string kind, Func<List<JsValue>> contents, Action<JsValue>? initialize,
        Dom.Features.DomCollectionBinding.OptionsCollectionOperations? options)
    {
        var collections = _elementCollections.GetValue(owner, static _ => new Dictionary<string, JsValue>(StringComparer.Ordinal));
        if (collections.TryGetValue(kind, out var existing))
            return existing;

        var collection = options is null
            ? LiveCollection(contents)
            : Dom.Features.DomCollectionBinding.HtmlOptionsCollection(Realm, contents, name => NamedItem(Realm, contents, name), options);
        collections[kind] = collection;
        initialize?.Invoke(collection);
        return collection;
    }

    /// <summary>
    /// Copies an option's selectedness and dirtiness to its clone (HTML's cloning steps for option), and that
    /// a select holds its options' -- which the clones of its options carry -- to the select's.
    /// </summary>
    private void CopySelectState(DomElement source, DomElement clone)
    {
        if (_optionStates.TryGetValue(source, out var state))
            _optionStates.AddOrUpdate(clone, new OptionState(state.Selected, state.Dirty));
        if (_heldSelects.TryGetValue(source, out _))
            _heldSelects.AddOrUpdate(clone, Held);
    }

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
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ITableHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.TableBinding"/> feature module consumes.
/// Explicit interface members, so these
/// seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The table slice is spelled in JSEAL end to end: every member of this contract is realm- or
/// handle-typed. A JSEAL object handle carries the engine's own object, so wrapper identity
/// (<c>row === row</c>, and the weak tables keyed on it) is preserved.
/// </remarks>
public sealed partial class DomBridge : ITableHost
{
    DomElement ITableHost.CreateElement(string tag)
    {
        var element = CreateBridgeElement(tag);
        return element;
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IDialogHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.DialogBinding"/> feature module consumes.
/// Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. The dialog/
/// popover state lives in the per-element <see cref="DialogRuntimeState"/> table (a dialog's
/// <c>returnValue</c> in <see cref="FormControlRuntimeState"/>) and <c>_topLayerCounter</c>. The
/// module reaches it only through these accessors, but DomBridge/AnchorResolver reads it directly.
/// </summary>
/// <remarks>
/// Nothing in this file is engine-typed. The module speaks JSEAL, and the one member that hands an
/// object to the rest of the bridge, <see cref="IDialogHost.DispatchFullscreenChange"/>, builds its
/// event through the realm and gives that handle to the element dispatcher as it is.
/// </remarks>
public sealed partial class DomBridge : IDialogHost
{
    void IDialogHost.SetOpenAttribute(DomElement element, bool open)
    {
        if (open)
            SetAttr(element, "open", "");
        else
            RemoveAttr(element, "open");
    }

    bool IDialogHost.HasOpenAttribute(DomElement element) => HasAttr(element, "open");

    bool IDialogHost.IsDialogModal(DomElement element) => IsModalDialog(element);

    bool IDialogHost.FireDialogEvent(DomElement element, string type, bool cancelable, string? oldState, string? newState) =>
        FireToggleEvent(element, type, cancelable, oldState, newState);

    void IDialogHost.QueueToggleEvent(DomElement element, string oldState, string newState) =>
        QueueToggleEvent(element, oldState, newState);

    bool IDialogHost.IsPopoverShowing(DomElement element) => IsPopoverShowing(element);

    void IDialogHost.HidePopoversForDialog(DomElement dialog) => HidePopoversForDialog(dialog);

    void IDialogHost.RunDialogFocusingSteps(DomElement dialog) => RunDialogFocusingSteps(dialog);

    void IDialogHost.RestoreFocusAfterDialog(DomElement dialog, bool wasModal) => RestoreFocusAfterDialog(dialog, wasModal);

    void IDialogHost.QueueDialogFrameAction(Action action) => QueueFrameAction(() =>
    {
        if (_realm is not null)
            action();
    });

    JsValue IDialogHost.RunAsScriptCall(Func<JsValue> call) => RunAsScriptCall(call);

    /// <summary>Whether <paramref name="element"/> is a dialog open as a modal one.</summary>
    private bool IsModalDialog(DomElement element) =>
        HasAttr(element, "open") && DialogStateFor(element).Modal is { IsSet: true, Value: true };

    void IDialogHost.AssignNextTopLayerOrder(DomElement element) =>
        DialogStateFor(element).TopLayerOrder.Set(++_topLayerCounter);

    void IDialogHost.SetDialogModal(DomElement element, bool modal)
    {
        if (modal)
        {
            DialogStateFor(element).Modal.Set(true);
            _modalDialogs.Add(element);
        }
        else
        {
            DialogStateFor(element).Modal.Remove();
            _modalDialogs.Remove(element);
        }

        NoteElementStateChange();
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
            // the object the listener sees is the one this built.
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

    string IDialogHost.GetReturnValue(DomElement element) =>
        _formState.TryGetReturnValue(element, out var rv) && rv is string s
            ? s
            : string.Empty;

    void IDialogHost.SetReturnValue(DomElement element, string value) =>
        _formState.SetReturnValue(element, value);

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

// Explicit IComputedStyleHost implementation for the ComputedStyleBinding feature module:
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
    DomElement? Dom.Features.IComputedStyleHost.FindElement(JsValue wrapper) => FindDomElementByJSObject(wrapper);

    JsValue Dom.Features.IComputedStyleHost.BuildComputedStyle(DomElement? element, string? pseudoElement)
        => BuildComputedStyleObject(element, pseudoElement);
}

// Explicit IInlineStyleHost implementation for StyleDeclarationBinding's inline (element.style)
// declaration callbacks: the per-element inline-style
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

// Explicit IElementGeometryHost implementation for the ElementGeometryBinding feature module:
// the box-model metrics and scrolling operations are the one family that genuinely reads the live
// layout, so the contract is wide by design. Each member forwards to the existing private LayoutMetrics.*
// method — the module now names the exact geometry surface it depends on instead of reaching into the
// bridge directly.
//
// This file names no engine type: the two option-reading members below read a JSEAL handle directly.
// The window and sub-window hosts keep their own copy of that reading, in
// DomBridge/Hosts.Documents.cs.
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
}

// Explicit IHitTestHost implementation for the HitTestBinding feature module: the bridge
// exposes the point hit-test via an explicit interface member, and inherits the realm, the document
// root and the JS-wrapper factory from the shared primitives, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IHitTestHost
{
    IReadOnlyList<DomElement> Dom.Features.IHitTestHost.HitTestDocumentPoint(DomNode docRoot, double x, double y)
        => HitTestDocumentPoint(docRoot, x, y);
}
