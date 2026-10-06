using Broiler.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A user's keyboard input, delivered to the page's scripts as a browser delivers it: <c>keydown</c>,
/// <c>keypress</c> and <c>keyup</c> at the focused element, the edits of a focused text field as
/// <c>beforeinput</c> and <c>input</c>, and what a key does by default -- Tab moves focus, Enter and
/// Space activate the focused link, button or checkbox, Enter in a text field submits its form.
/// </summary>
/// <remarks>
/// <para>
/// <b>No key reached a page's scripts.</b> The window scrolled on the arrow keys and edited a field in
/// an editor of its own, and nothing it typed reached the page: a search box that suggests as the user
/// types, a form that validates on <c>input</c>, a game listening for <c>keydown</c> -- none of them
/// heard anything, and the page's own copy of a field kept the value it was loaded with.
/// </para>
/// <para>
/// <b>The order is Chromium's, measured.</b> A character key: <c>keydown</c> (<c>keyCode</c> the
/// key's, <c>charCode</c> 0), <c>keypress</c> (<c>keyCode</c>, <c>charCode</c> and <c>which</c> the
/// character's), <c>beforeinput</c> (cancelable, the value still the old one), the edit,
/// <c>input</c>, <c>keyup</c>. Cancelling <c>keydown</c> stops the <c>keypress</c> and the edit,
/// cancelling <c>keypress</c> the edit, cancelling <c>beforeinput</c> the edit alone. Enter in a text
/// field of a form: <c>keydown</c>, <c>keypress</c>, <c>beforeinput</c> (<c>insertLineBreak</c>, with
/// no <c>input</c> after it), <c>change</c> when it was edited, a <c>click</c> at the form's default
/// button and its <c>submit</c>, then <c>keyup</c>. Enter on a button clicks it after
/// <c>keypress</c>; on a link it clicks it with no <c>keypress</c> at all; Space clicks a button or a
/// checkbox at <c>keyup</c>. Tab: <c>keydown</c> at the element it leaves, <c>change</c> there when it
/// was edited, the focus events, and <c>keyup</c> at the element it reached.
/// </para>
/// <para>
/// <b>Who edits a field.</b> A host with an editor of its own over a field -- the window's -- makes
/// the edit and says what the field holds after it (<see cref="TextInput.EditedValue"/>,
/// <see cref="FieldEdit.Value"/>); the page hears it as the user's edit and holds that value, and a
/// cancelled one is the host's to undo. A host without one has the page put typed text at the end of
/// the field's value and take a backward deletion from it.
/// </para>
/// <para>
/// <b>What is not modelled:</b> composition (an input method's text arrives as typed text), a
/// selection within a field, which a host's editor keeps, and the keys' own default actions in the
/// host -- scrolling, and the host's shortcuts -- which a host performs when the key was neither
/// cancelled nor <see cref="KeyboardInputResult.Handled"/>.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The last key that went down, whose key and code the keypress of what it typed carries, and
    // whether its keydown was cancelled, which keeps what it typed from being typed at all.
    private KeyboardInput? _lastKeyDown;
    private bool _keyDownCancelled;

    // A focused button, checkbox or radio button Space went down on, which its release clicks.
    private DomElement? _spaceArmed;

    // Whether the user last used the keyboard rather than a pointer, which decides whether a focus a
    // script moves shows itself (:focus-visible); true until the user has done anything, as in Chromium.
    private bool _keyboardModality = true;

    // Whether the focused element shows that it is focused, as :focus-visible matches it.
    private bool _focusVisible;

    // The value a focused text field had when it was focused, or last fired change, or a script last set
    // it; and whether the user has edited it since, which is what makes leaving it fire change.
    private DomElement? _changeField;
    private string? _changeBaseline;
    private bool _fieldEditedByUser;

    /// <summary>Delivers a key's press or release to the page's scripts and does what it does by default.</summary>
    internal KeyboardInputResult DispatchKeyboardInput(KeyboardInput input)
    {
        ThrowIfDisposed();
        if (_realm is null || DocumentElement is null || KeyTarget() is not { } target)
            return default;

        return input.Kind == KeyboardInputKind.Down ? KeyDown(input, target) : KeyUp(input, target);
    }

    private KeyboardInputResult KeyDown(KeyboardInput input, DomElement target)
    {
        _lastKeyDown = input;
        if (!IsModifierKey(input.Key))
        {
            _keyboardModality = true;

            // A key on a focused element shows its focus, however it got it (measured: a button the
            // mouse focused matches :focus-visible once a key is pressed on it).
            if (FocusedElementIn(FocusedDocument) is not null && !_focusVisible)
            {
                _focusVisible = true;
                NoteUserActionStateChange();
            }

            if (input.Key != "Escape")
                NotifyUserActivation(GetOwningDocument(target));
        }

        var allowed = FireKeyEvent(target, "keydown", input, input.KeyCode, charCode: 0, input.Key);
        _keyDownCancelled = !allowed;
        if (!allowed)
            return new KeyboardInputResult(true, true);

        var focused = FocusedElementIn(FocusedDocument);
        switch (input.Key)
        {
            case "Tab" when !input.CtrlKey && !input.AltKey && !input.MetaKey:
                NavigateSequentially(backward: input.ShiftKey);
                return new KeyboardInputResult(true, false) { Handled = true };

            case "Enter" when focused is not null:
                return Enter(input, focused);

            case "Escape" when TopmostModalDialog(GetOwningDocument(target)) is { } dialog:
                // A modal dialog's close request: cancel, then it closes (measured); a non-modal one stays.
                _dialogs.RequestClose(dialog, returnValue: null);
                return new KeyboardInputResult(true, false) { Handled = true };

            case " " when focused is not null && ActivatedBySpace(focused):
                // Space clicks the control when it comes up; until then the control is :active.
                _spaceArmed = focused;
                NoteUserActionStateChange(hoverOrActive: true);
                return new KeyboardInputResult(true, false) { Handled = true };
        }

        return new KeyboardInputResult(true, false);
    }

    /// <summary>The modal dialog of <paramref name="document"/> last put in the top layer, or null.</summary>
    private DomElement? TopmostModalDialog(DomDocument document) =>
        document.Descendants().OfType<DomElement>()
            .Where(element => element.TagName.Equals("dialog", StringComparison.OrdinalIgnoreCase) && IsModalDialog(element))
            .OrderByDescending(element => DialogStateFor(element).TopLayerOrder.Value)
            .FirstOrDefault();

    private KeyboardInputResult KeyUp(KeyboardInput input, DomElement target)
    {
        var allowed = FireKeyEvent(target, "keyup", input, input.KeyCode, charCode: 0, input.Key);

        var armed = _spaceArmed;
        if (input.Key == " " && armed is not null)
        {
            _spaceArmed = null;
            NoteUserActionStateChange(hoverOrActive: true);
            if (allowed && armed.IsConnected && ReferenceEquals(FocusedElementIn(FocusedDocument), armed))
            {
                ClickByKeyboard(armed, input);
                return new KeyboardInputResult(true, false) { Handled = true };
            }
        }

        return new KeyboardInputResult(true, !allowed);
    }

    /// <summary>Enter on a focused element: a link followed, a button clicked, a form submitted, a line broken.</summary>
    private KeyboardInputResult Enter(KeyboardInput input, DomElement focused)
    {
        // A link is activated by the keydown itself, with no keypress (measured).
        if (IsHyperlink(focused))
        {
            ClickByKeyboard(focused, input);
            return new KeyboardInputResult(true, false) { Handled = true };
        }

        if (!FireKeyEvent(focused, "keypress", input, 13, charCode: 13, "Enter"))
            return new KeyboardInputResult(true, true);

        if (IsButtonLike(focused))
        {
            ClickByKeyboard(focused, input);
            return new KeyboardInputResult(true, false) { Handled = true };
        }

        if (!IsEditableTextField(focused))
            return new KeyboardInputResult(true, false);

        if (!FireEditEvent(focused, "beforeinput", "insertLineBreak", data: null))
            return new KeyboardInputResult(true, true);

        if (focused.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase))
        {
            ApplyUserEdit(focused, _formState.GetEffectiveValue(focused) + "\n", "insertLineBreak", data: null);
            return new KeyboardInputResult(true, false) { Handled = true };
        }

        SubmitImplicitly(focused, input);
        return new KeyboardInputResult(true, false) { Handled = true };
    }

    /// <summary>
    /// What a key typed, after its press: a <c>keypress</c> for each character, then, in a focused
    /// text field, the edit, with <c>beforeinput</c> and <c>input</c>.
    /// </summary>
    internal KeyboardInputResult DispatchTextInput(TextInput input)
    {
        ThrowIfDisposed();
        if (_realm is null || DocumentElement is null || KeyTarget() is not { } target)
            return default;

        var text = StripControlCharacters(input.Text);
        if (text.Length == 0)
            return new KeyboardInputResult(true, false);

        // A cancelled keydown types nothing, and its characters are no keypress either.
        if (_keyDownCancelled)
            return new KeyboardInputResult(true, true);

        var key = _lastKeyDown ?? new KeyboardInput(KeyboardInputKind.Down, text, string.Empty);
        foreach (var rune in text.EnumerateRunes())
        {
            var character = rune.ToString();
            if (!FireKeyEvent(target, "keypress", key, rune.Value, rune.Value, character))
                return new KeyboardInputResult(true, true);
        }

        var field = FocusedElementIn(FocusedDocument);
        if (field is null || !IsEditableTextField(field))
            return new KeyboardInputResult(true, false);

        if (!FireEditEvent(field, "beforeinput", "insertText", text))
            return new KeyboardInputResult(true, true);

        ApplyUserEdit(field, input.EditedValue ?? _formState.GetEffectiveValue(field) + text, "insertText", text);
        return new KeyboardInputResult(true, false) { Handled = true };
    }

    /// <summary>A change the host's editor made to the focused text field: <c>beforeinput</c>, the value, <c>input</c>.</summary>
    internal KeyboardInputResult DispatchFieldEdit(FieldEdit edit)
    {
        ThrowIfDisposed();
        if (_realm is null || FocusedElementIn(FocusedDocument) is not { } field || !IsEditableTextField(field))
            return default;

        if (!FireEditEvent(field, "beforeinput", edit.InputType, edit.Data))
            return new KeyboardInputResult(true, true);

        var value = edit.Value ?? EditMadeByThePage(_formState.GetEffectiveValue(field), field, edit.InputType);
        if (value is null)
            return new KeyboardInputResult(true, false);

        ApplyUserEdit(field, value, edit.InputType, edit.Data);
        return new KeyboardInputResult(true, false) { Handled = true };
    }

    /// <summary>The value a change with no value of its own leaves in <paramref name="field"/>, or <see langword="null"/> when it leaves it as it is.</summary>
    private static string? EditMadeByThePage(string value, DomElement field, string inputType) => inputType switch
    {
        "deleteContentBackward" when value.Length > 0 => value[..PreviousTextElementStart(value)],
        "insertLineBreak" when field.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase) => value + "\n",
        _ => null,
    };

    private static int PreviousTextElementStart(string value)
    {
        var start = 0;
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
            start = enumerator.ElementIndex;
        return start;
    }

    /// <summary>The user's edit of <paramref name="field"/>: the value it now holds, then <c>input</c>.</summary>
    private void ApplyUserEdit(DomElement field, string value, string inputType, string? data)
    {
        if (!ReferenceEquals(_changeField, field))
        {
            _changeField = field;
            _changeBaseline = _formState.GetEffectiveValue(field);
        }

        _formState.SetDirtyValue(field, value);
        _fieldEditedByUser = true;
        MarkUserEdited(field);
        PlaceCaretAfterUserEdit(field);
        FireEditEvent(field, "input", inputType, data);
    }

    /// <summary>
    /// A script set a field's value: what the user's edits are measured against from now on, so that
    /// leaving the field fires <c>change</c> only for what the user changed after it.
    /// </summary>
    internal void NoteScriptSetFieldValue(DomElement field, string value)
    {
        // Not the user's edit: minlength and maxlength judge it no more (DomBridge/ElementStates.cs).
        ForgetUserEdit(field);
        if (!ReferenceEquals(_changeField, field))
            return;

        _changeBaseline = value;
        _fieldEditedByUser = false;
    }

    /// <summary>
    /// <c>change</c> at a text field the user edited and is leaving -- by moving focus, or by pressing
    /// Enter in it -- when its value is not what it was.
    /// </summary>
    private void FireChangeIfEdited(DomElement field)
    {
        if (!ReferenceEquals(_changeField, field) || !_fieldEditedByUser)
            return;

        var value = _formState.GetEffectiveValue(field);
        _fieldEditedByUser = false;
        if (string.Equals(value, _changeBaseline, StringComparison.Ordinal))
            return;

        _changeBaseline = value;
        // The user committed the change: the field is :user-valid or :user-invalid from now on.
        MarkUserInteracted(field);
        var realm = Realm;
        DispatchKeyboardEvent(field, NewTrustedEvent(realm, "change", bubbles: true, cancelable: false, composed: false, JsValue.Missing));
    }

    /// <summary>
    /// Implicit submission (HTML §4.10.21.2): Enter in a text field of a form clicks the form's default
    /// button, or submits the form itself when it has none and this is its only text field.
    /// </summary>
    private void SubmitImplicitly(DomElement field, KeyboardInput input)
    {
        FireChangeIfEdited(field);
        if (FormOwnerOf(field) is not { } form)
            return;

        var controls = FormControlsOf(form);
        if (controls.FirstOrDefault(IsSubmitButton) is { } defaultButton)
        {
            if (!IsDisabledFormControl(defaultButton))
                ClickByKeyboard(defaultButton, input);
            return;
        }

        if (controls.Count(BlocksImplicitSubmission) <= 1)
            SubmitForm(form, submitter: null);
    }

    /// <summary>
    /// A click a key made -- Enter, or Space released -- at <paramref name="element"/>, with the click's
    /// activation: a checkbox or a radio button changes, a label clicks its control, and, which a
    /// pointer's click leaves to the host, a submit or reset button acts on its form and a link is
    /// followed.
    /// </summary>
    private void ClickByKeyboard(DomElement element, KeyboardInput key)
    {
        var document = GetOwningDocument(element);
        var frame = GetFrameForContentDocument(document);
        var window = WindowOfDocument(document);
        var hit = new InputHit(element, frame, 0, 0, 0, 0) { Window = window };
        var input = new PointerInput(PointerInputKind.Up, 0, 0)
        {
            CtrlKey = key.CtrlKey,
            ShiftKey = key.ShiftKey,
            AltKey = key.AltKey,
            MetaKey = key.MetaKey,
        };

        if (!FireClick(hit, input, detail: 0, labelDepth: 0) || !element.IsConnected)
            return;

        if (IsHyperlink(element))
            FollowHyperlink(element);
        else if (IsSubmitButton(element) && FormOwnerOf(element) is { } form)
            SubmitForm(form, element);
        else if (IsResetButton(element) && FormOwnerOf(element) is { } resetForm)
            ResetForm(resetForm);
    }

    /// <summary>
    /// Follows a link the user activated from the keyboard: its document's location -- or for
    /// <c>target="_top"</c> and <c>"_parent"</c> those windows' -- is assigned its URL, as that
    /// document's script, so a frame's link navigates the frame.
    /// </summary>
    private void FollowHyperlink(DomElement link)
    {
        if (!TryGetAttribute(link, "href", out var href))
            return;

        // A javascript: URL is the target location's to run, as the link's document's script
        // (DomBridge/JavaScriptUrl.cs): in the document the link targets, if that has its origin.
        var realm = Realm;
        var document = GetOwningDocument(link);
        var frame = GetFrameForContentDocument(document);
        var window = frame is null ? WindowHandle : _subWindows.GetOrCreate(frame);
        var target = TryGetAttribute(link, "target", out var declared) ? declared.Trim().ToLowerInvariant() : string.Empty;

        void Assign()
        {
            var url = realm.ToJsString(realm.GetProperty(WrapNode(link), "href"));
            var location = target switch
            {
                "_top" => _topLocation,
                "_parent" when frame is not null => realm.GetProperty(_subWindows.GetOrCreate(frame), "parent") is { IsObject: true } parent
                    ? realm.GetProperty(parent, "location")
                    : CurrentLocation(),
                _ => CurrentLocation(),
            };

            // Assigning href is what assign() does, and the one thing a cross-origin view's location takes.
            if (location.IsObject)
                realm.SetProperty(location, "href", JsValue.String(url.Length > 0 ? url : href));
        }

        try
        {
            if (window.IsObject && _browsingContexts.IsSubWindow(window))
                RunWithWindowContext(window, Assign);
            else
                Assign();
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.keyboard", $"Following a link failed: {ex.Message}", ex);
        }
    }

    // ── Sequential focus navigation ──────────────────────────────────────────

    /// <summary>
    /// Tab and Shift+Tab: focus moves to the next element in the page's tab order, into and out of its
    /// frames, or off the page's elements past the last, from where the next Tab starts again.
    /// </summary>
    private void NavigateSequentially(bool backward)
    {
        var order = new List<DomElement>();
        CollectTabOrder(_document, order, depth: 0);
        var current = FocusedElementIn(FocusedDocument);
        var index = current is null ? -1 : order.IndexOf(current);

        DomElement? next;
        if (index < 0)
            next = order.Count == 0 ? null : backward ? order[^1] : order[0];
        else
        {
            var at = backward ? index - 1 : index + 1;
            next = at >= 0 && at < order.Count ? order[at] : null;
        }

        if (next is null)
        {
            MoveFocus(_document, null, FocusOrigin.Keyboard);
            return;
        }

        MoveFocus(GetOwningDocument(next), next, FocusOrigin.Keyboard);
        SelectOnKeyboardFocus(next);
    }

    /// <summary>
    /// The tab order of <paramref name="document"/>: its elements a Tab stops at -- those with a
    /// positive <c>tabindex</c> first, by it, then those with 0 or focusable by what they are, in tree
    /// order -- with each frame standing for its own document's order.
    /// </summary>
    private void CollectTabOrder(DomNode document, List<DomElement> order, int depth)
    {
        var positive = new List<(int TabIndex, int Position, DomElement Element)>();
        var normal = new List<DomElement>();
        var position = 0;
        foreach (var element in document.Descendants().OfType<DomElement>())
        {
            position++;
            if (!IsFocusable(element) || TabIndexOf(element) is not { } tabIndex || tabIndex < 0 || IsSkippedRadio(element))
                continue;

            if (tabIndex > 0)
                positive.Add((tabIndex, position, element));
            else
                normal.Add(element);
        }

        foreach (var element in positive.OrderBy(static p => p.TabIndex).ThenBy(static p => p.Position).Select(static p => p.Element).Concat(normal))
        {
            if (IsFrameContainerElement(element))
            {
                if (depth < MaxInputFrameDepth && GetContentDocument(element) is { } content)
                    CollectTabOrder(content, order, depth + 1);
                continue;
            }

            order.Add(element);
        }
    }

    /// <summary>An element's <c>tabindex</c>, or 0 for one focusable by what it is, or <see langword="null"/> for one a Tab skips.</summary>
    private static int? TabIndexOf(DomElement element)
    {
        if (TryGetAttribute(element, "tabindex", out var declared) &&
            int.TryParse(declared.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var tabIndex))
        {
            return tabIndex;
        }

        return IsFocusableByDefault(element) ||
               TryGetAttribute(element, "contenteditable", out var editable) && !editable.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
            ? 0
            : null;
    }

    /// <summary>A radio button of a group a Tab reaches through another of its buttons: the checked one, or the first.</summary>
    private bool IsSkippedRadio(DomElement element)
    {
        if (!IsCheckable(element, out var isRadio) || !isRadio)
            return false;

        var group = RadioGroupOf(element).ToList();
        if (group.Count == 0)
            return false;

        if (IsChecked(element))
            return false;

        return group.Any(IsChecked) || group.Any(other => element.CompareDocumentPosition(other).HasFlag(DomDocumentPosition.Preceding));
    }

    // ── Events ───────────────────────────────────────────────────────────────

    /// <summary>The element keys go to: the focused element, or the focused document's body.</summary>
    private DomElement? KeyTarget()
    {
        var document = FocusedDocument;
        return FocusedElementIn(document) ?? BodyOrRootOf(document);
    }

    /// <summary>A trusted <c>keydown</c>, <c>keypress</c> or <c>keyup</c> at <paramref name="target"/>; answers whether it was not cancelled.</summary>
    private bool FireKeyEvent(DomElement target, string type, KeyboardInput input, int keyCode, int charCode, string key)
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, type, bubbles: true, cancelable: true, composed: true, InterfacePrototype(realm, "KeyboardEvent"));
        var window = WindowOfDocument(GetOwningDocument(target));
        Define(realm, evt, "view", window.IsObject ? window : JsValue.Null);
        Define(realm, evt, "detail", JsValue.Number(0));
        Define(realm, evt, "key", JsValue.String(key));
        Define(realm, evt, "code", JsValue.String(input.Code));
        Define(realm, evt, "keyCode", JsValue.Number(keyCode));
        Define(realm, evt, "charCode", JsValue.Number(charCode));
        Define(realm, evt, "which", JsValue.Number(keyCode));
        Define(realm, evt, "location", JsValue.Number(input.Location));
        Define(realm, evt, "repeat", JsValue.Boolean(input.Repeat));
        Define(realm, evt, "isComposing", JsValue.Boolean(_composition is not null));
        Define(realm, evt, "ctrlKey", JsValue.Boolean(input.CtrlKey));
        Define(realm, evt, "shiftKey", JsValue.Boolean(input.ShiftKey));
        Define(realm, evt, "altKey", JsValue.Boolean(input.AltKey));
        Define(realm, evt, "metaKey", JsValue.Boolean(input.MetaKey));
        realm.DefineMethod(evt, "getModifierState", 1, (in call) =>
        {
            var name = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
            return JsValue.Boolean(name switch
            {
                "Control" => input.CtrlKey,
                "Shift" => input.ShiftKey,
                "Alt" => input.AltKey,
                "Meta" => input.MetaKey,
                _ => false,
            });
        });

        return DispatchKeyboardEvent(target, evt);
    }

    /// <summary>A trusted <c>beforeinput</c> (cancelable) or <c>input</c> (not) at a text field; answers whether it was not cancelled.</summary>
    private bool FireEditEvent(DomElement field, string type, string inputType, string? data)
    {
        var realm = Realm;
        // A composition's beforeinput cannot be cancelled (Input Events §4.1); every other one can.
        var cancelable = type == "beforeinput" && inputType != "insertCompositionText";
        var evt = NewTrustedEvent(realm, type, bubbles: true, cancelable, composed: true, InterfacePrototype(realm, "InputEvent"));
        var window = WindowOfDocument(GetOwningDocument(field));
        Define(realm, evt, "view", window.IsObject ? window : JsValue.Null);
        Define(realm, evt, "detail", JsValue.Number(0));
        Define(realm, evt, "inputType", JsValue.String(inputType));
        Define(realm, evt, "data", data is null ? JsValue.Null : JsValue.String(data));
        Define(realm, evt, "isComposing", JsValue.Boolean(_composition is not null));
        Define(realm, evt, "dataTransfer", JsValue.Null);
        return DispatchKeyboardEvent(field, evt);
    }

    /// <summary>
    /// Dispatches <paramref name="evt"/> at <paramref name="target"/> as its window's script, as the end of
    /// a task: the microtask checkpoint follows, unless a script's call fired it (DomBridge/ScriptActivation.cs).
    /// Answers whether it was not cancelled.
    /// </summary>
    private bool DispatchKeyboardEvent(DomElement target, JsValue evt)
    {
        var allowed = true;
        void Dispatch() => allowed = _eventDispatch.DispatchEventOnElement(target, evt).AsBoolean;

        var window = WindowOfDocument(GetOwningDocument(target));
        try
        {
            if (window.IsObject && _browsingContexts.IsSubWindow(window))
                RunWithWindowContext(window, Dispatch);
            else
                Dispatch();
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.DispatchKeyboardInput",
                $"Dispatching a trusted event failed: {ex.Message}", ex);
        }

        if (EndsTaskWithCheckpoint)
            TaskCheckpointCallback?.Invoke();
        return allowed;
    }

    /// <summary>An interface's prototype, so a trusted event is an instance of it to <c>instanceof</c>; missing when there is none.</summary>
    private static JsValue InterfacePrototype(IJsRealm realm, string name)
    {
        var constructor = realm.GetProperty(realm.Global, name);
        if (!constructor.IsObject)
            return JsValue.Missing;

        var prototype = realm.GetProperty(constructor, "prototype");
        return prototype.IsObject ? prototype : JsValue.Missing;
    }

    // ── What things are ──────────────────────────────────────────────────────

    /// <summary>The focused text field, for a host that edits it with an editor of its own.</summary>
    internal FocusedTextField? GetFocusedTextField()
    {
        if (_realm is null || FocusedElementIn(FocusedDocument) is not { } field || !IsEditableTextField(field))
            return null;

        var type = field.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase)
            ? "textarea"
            : TryGetAttribute(field, "type", out var declared) && declared.Trim().Length > 0 ? declared.Trim().ToLowerInvariant() : "text";
        var (left, top, width, height) = WithLayoutGeometryCache(() => GetBoundingClientRectForDomElement(field, isRoot: false));
        var inFrame = GetFrameForContentDocument(GetOwningDocument(field)) is not null;
        var selection = SelectionOf(field);
        return new FocusedTextField(type, _formState.GetEffectiveValue(field), inFrame)
        {
            X = left,
            Y = top,
            Width = width,
            Height = height,
            SelectionStart = selection.Start,
            SelectionEnd = selection.End,
            SelectionBackward = selection.Direction == "backward",
        };
    }

    /// <summary>A text field the user can type in: a text-like input or a text area, neither disabled nor read-only.</summary>
    private static bool IsEditableTextField(DomElement element)
    {
        if (IsDisabledFormControl(element) || HasAttr(element, "readonly"))
            return false;

        if (element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase))
            return true;

        return element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) && IsTextInputType(InputTypeOf(element));
    }

    /// <summary>Whether an element is a text entry the user types in, which shows its focus however it got it.</summary>
    private static bool IsTextEntry(DomElement element) =>
        element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase) ||
        element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) && IsTextInputType(InputTypeOf(element)) ||
        TryGetAttribute(element, "contenteditable", out var editable) && !editable.Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

    private static string InputTypeOf(DomElement input) =>
        TryGetAttribute(input, "type", out var type) ? type.Trim().ToLowerInvariant() : "text";

    private static bool IsTextInputType(string type) =>
        type is "" or "text" or "search" or "url" or "tel" or "email" or "password" or "number";

    /// <summary>The input types whose fields keep a form from being submitted implicitly by another field's Enter (HTML §4.10.21.2).</summary>
    private static bool BlocksImplicitSubmission(DomElement element) =>
        element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
        InputTypeOf(element) is "" or "text" or "search" or "url" or "tel" or "email" or "password"
            or "date" or "month" or "week" or "time" or "datetime-local" or "number";

    private static bool IsHyperlink(DomElement element) =>
        (element.TagName.Equals("a", StringComparison.OrdinalIgnoreCase) || element.TagName.Equals("area", StringComparison.OrdinalIgnoreCase)) &&
        HasAttr(element, "href");

    private static bool IsSubmitButton(DomElement element) =>
        element.TagName.Equals("button", StringComparison.OrdinalIgnoreCase)
            ? !TryGetAttribute(element, "type", out var type) || type.Trim().ToLowerInvariant() is "submit" or "" || !IsKnownButtonType(type)
            : element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) && InputTypeOf(element) is "submit" or "image";

    private static bool IsKnownButtonType(string type) => type.Trim().ToLowerInvariant() is "submit" or "reset" or "button";

    private static bool IsResetButton(DomElement element) =>
        element.TagName.Equals("button", StringComparison.OrdinalIgnoreCase)
            ? TryGetAttribute(element, "type", out var type) && type.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase)
            : element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) && InputTypeOf(element) == "reset";

    /// <summary>A control Enter clicks: a button, or an input that is one.</summary>
    private static bool IsButtonLike(DomElement element) =>
        element.TagName.Equals("button", StringComparison.OrdinalIgnoreCase) ||
        element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) && InputTypeOf(element) is "submit" or "reset" or "button" or "image";

    /// <summary>A control Space clicks when it comes up: a button, an input that is one, a checkbox or a radio button.</summary>
    private static bool ActivatedBySpace(DomElement element) =>
        IsButtonLike(element) || IsCheckable(element, out _) ||
        element.TagName.Equals("summary", StringComparison.OrdinalIgnoreCase);

    private static bool IsModifierKey(string key) =>
        key is "Shift" or "Control" or "Alt" or "AltGraph" or "Meta" or "CapsLock" or "NumLock" or "ScrollLock" or "Fn" or "OS";

    /// <summary>The form a control belongs to: the one its <c>form</c> attribute names, or the one around it.</summary>
    private static DomElement? FormOwnerOf(DomElement control)
    {
        if (TryGetAttribute(control, "form", out var id) && id.Length > 0)
        {
            return GetOwningDocument(control).Descendants().OfType<DomElement>().FirstOrDefault(element =>
                element.TagName.Equals("form", StringComparison.OrdinalIgnoreCase) &&
                TryGetAttribute(element, "id", out var formId) && formId == id);
        }

        return NearestAncestor(control, "form");
    }

    /// <summary>The controls of <paramref name="form"/>, in tree order: those inside it that no <c>form</c> attribute takes elsewhere, and those elsewhere whose attribute names it.</summary>
    private static List<DomElement> FormControlsOf(DomElement form) =>
        GetOwningDocument(form).Descendants().OfType<DomElement>()
            .Where(element => element.TagName.ToLowerInvariant() is "input" or "button" or "select" or "textarea" &&
                              ReferenceEquals(FormOwnerOf(element), form))
            .ToList();

    private static string StripControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character >= ' ' && character != '\x7f')
                builder.Append(character);
        }

        return builder.ToString();
    }
}
