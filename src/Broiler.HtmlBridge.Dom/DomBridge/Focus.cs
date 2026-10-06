using Broiler.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Focus: which document of the page has it and which of that document's elements, as
/// <c>document.activeElement</c> reports it, moved by a press, <c>focus()</c> and <c>blur()</c> with
/// the events a browser fires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing was ever focused.</b> <c>document.activeElement</c> was the body whatever happened,
/// <c>focus()</c> fired an untrusted <c>focus</c> event at any element and changed nothing, and a
/// press moved no focus at all. A page that opens a menu on <c>focus</c> or validates a field on
/// <c>blur</c>, or checks <c>document.activeElement</c> before it handles a key, could not see what
/// the user had clicked.
/// </para>
/// <para>
/// <b>The order is Chromium's, measured.</b> Moving focus from one element to another of the same
/// document fires <c>blur</c> and then <c>focusout</c> at the first, with the second as
/// <c>relatedTarget</c> and <c>activeElement</c> already the body, then <c>focus</c> and
/// <c>focusin</c> at the second. A press on something that cannot be focused takes focus from what
/// had it, with no <c>relatedTarget</c>. When focus moves into another document -- a frame, or back
/// out of one -- the window that had it gets <c>blur</c> and the other <c>focus</c>, between the two
/// elements' events; the element events then name no element of the other document.
/// </para>
/// <para>
/// <b>Every document of the page is reached from one focused document.</b> The document that has
/// focus answers its focused element, or its body; each document around it answers the frame element
/// that leads to it, which is how a page sees that the user is in one of its frames.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The document whose browsing context has focus -- the page's when null -- and its focused
    // element, when one is.
    private DomDocument? _focusedDocument;
    private DomElement? _focusedElement;

    // Bumped on every change, so that a focus change a listener makes during another stops the outer one.
    private int _focusGeneration;

    /// <summary>A number that changes whenever focus moves: a host following focus with an editor of its own compares it.</summary>
    internal long FocusVersion => _focusGeneration;

    /// <summary>The document that has focus: the page's, unless the user or a script moved it into a frame that is still there.</summary>
    private DomDocument FocusedDocument =>
        _focusedDocument is { } focused &&
        (ReferenceEquals(focused, _document) || GetFrameForContentDocument(focused) is not null)
            ? focused
            : _document;

    /// <summary>
    /// <c>document.activeElement</c> for <paramref name="document"/>: its focused element, the frame
    /// element through which a document inside it has focus, or else its body.
    /// </summary>
    internal DomElement? ActiveElementOf(DomDocument document)
    {
        var focusedDocument = FocusedDocument;
        if (ReferenceEquals(focusedDocument, document))
            return FocusedElementIn(document) ?? BodyOrRootOf(document);

        for (var inner = focusedDocument; GetFrameForContentDocument(inner) is { } container;)
        {
            var outer = GetOwningDocument(container);
            if (ReferenceEquals(outer, document))
                return container;

            if (ReferenceEquals(outer, inner))
                break;

            inner = outer;
        }

        return BodyOrRootOf(document);
    }

    /// <summary><c>document.hasFocus()</c>: whether focus is in <paramref name="document"/> or in a frame inside it.</summary>
    internal bool HasFocusIn(DomDocument document)
    {
        for (DomDocument? current = FocusedDocument; current is not null;)
        {
            if (ReferenceEquals(current, document))
                return true;

            var container = GetFrameForContentDocument(current);
            var outer = container is null ? null : GetOwningDocument(container);
            current = ReferenceEquals(outer, current) ? null : outer;
        }

        return false;
    }

    /// <summary><c>element.focus()</c>: focuses <paramref name="element"/> when it can be focused; nothing otherwise.</summary>
    internal void FocusElement(DomElement element)
    {
        // A frame element's focus is its document's.
        if (IsFrameContainerElement(element) && element.IsConnected && GetContentDocument(element) is { } content)
        {
            MoveFocus(content, null, FocusOrigin.Script);
            return;
        }

        if (IsFocusable(element))
            MoveFocus(GetOwningDocument(element), element, FocusOrigin.Script);
    }

    /// <summary><c>element.blur()</c>: takes focus from <paramref name="element"/> when it has it, leaving the body active.</summary>
    internal void BlurElement(DomElement element)
    {
        var document = FocusedDocument;
        if (ReferenceEquals(FocusedElementIn(document), element))
            MoveFocus(document, null, FocusOrigin.Script);
    }

    /// <summary>
    /// What a press does to focus, unless it was cancelled: it focuses the nearest
    /// element at or above the target that can be focused, or else takes focus from whatever had it
    /// in that document.
    /// </summary>
    private void FocusForPress(DomElement target)
    {
        DomElement? focusable = null;
        for (var current = target; current is not null; current = ParentEl(current))
        {
            if (IsFocusable(current))
            {
                focusable = current;
                break;
            }
        }

        MoveFocus(GetOwningDocument(target), focusable, FocusOrigin.Pointer);
    }

    /// <summary>What moved focus: a script, a press of a pointer, a key -- Tab -- or the focus fixup.</summary>
    private enum FocusOrigin
    {
        Script,
        Pointer,
        Keyboard,
        Fixup,
    }

    // Whether a check that the focused element can still have focus waits for the next frame.
    private bool _focusFixupQueued;

    /// <summary>
    /// HTML's focus fixup, as Chromium runs it (measured): a focused element that
    /// can no longer have focus -- made inert, disabled, or not rendered -- keeps it until the next style
    /// update, which queues a task; that task, if the element still cannot have it, takes focus away with a
    /// trusted <c>blur</c> and <c>focusout</c> and no <c>relatedTarget</c>, the body already active. The
    /// frame stands for the style update here. Asked for on every change to a document's tree or attributes
    /// while an element has focus; a change undone before the frame does nothing.
    /// </summary>
    /// <remarks>It kept focus: a key went on to a field behind an inert overlay, or one a script had disabled.</remarks>
    private void QueueFocusFixup()
    {
        if (_focusFixupQueued || _focusedElement is null || _realm is null)
            return;

        _focusFixupQueued = true;
        QueueFrameAction(() =>
        {
            _focusFixupQueued = false;
            var document = FocusedDocument;
            if (_realm is null || FocusedElementIn(document) is not { } focused || IsFocusable(focused))
                return;

            _eventLoop.QueueTask(() =>
            {
                if (_realm is not null && ReferenceEquals(FocusedElementIn(document), focused) && !IsFocusable(focused))
                    MoveFocus(document, null, FocusOrigin.Fixup);
            });
        });
    }

    /// <summary>
    /// Moves focus to <paramref name="element"/> of <paramref name="document"/> -- or to the document
    /// itself, with no element focused -- firing the events of each step.
    /// </summary>
    /// <param name="origin">
    /// What moved it. When the user did, each event ends a task, and the microtask checkpoint follows
    /// it; a script's <c>focus()</c> runs its events inside the script's own task. And it decides
    /// whether the element shows its focus (<see cref="ShowsFocus"/>).
    /// </param>
    /// <remarks>
    /// A text field the user edited fires <c>change</c> as it loses focus, before <c>blur</c>, with
    /// <c>activeElement</c> already the body (measured).
    /// </remarks>
    private void MoveFocus(DomDocument document, DomElement? element, FocusOrigin origin)
    {
        var byUser = origin != FocusOrigin.Script;
        var oldDocument = FocusedDocument;
        var oldElement = FocusedElementIn(oldDocument);
        var sameDocument = ReferenceEquals(oldDocument, document);
        if (sameDocument && ReferenceEquals(oldElement, element))
            return;

        var generation = ++_focusGeneration;
        if (oldElement is not null)
        {
            _focusedElement = null;
            _focusVisible = false;
            NoteUserActionStateChange();
            // A composition does not outlive its field's focus: what it holds is committed.
            if (_composition is { } composition && ReferenceEquals(composition.Field, oldElement))
                EndComposition(composition, composition.Text);

            FireChangeIfEdited(oldElement);
            if (generation != _focusGeneration)
                return;

            FireFocusEvent(oldElement, "blur", sameDocument ? element : null, byUser);
            FireFocusEvent(oldElement, "focusout", sameDocument ? element : null, byUser);
            if (generation != _focusGeneration)
                return;
        }

        if (!sameDocument)
        {
            _focusedDocument = document;
            FireWindowFocusEvent(oldDocument, "blur", byUser);
            FireWindowFocusEvent(document, "focus", byUser);
            if (generation != _focusGeneration)
                return;
        }

        if (element is null || !element.IsConnected)
            return;

        _focusedElement = element;
        _focusVisible = ShowsFocus(element, origin);
        _changeField = IsEditableTextField(element) ? element : null;
        _changeBaseline = _changeField is null ? null : _formState.GetEffectiveValue(element);
        _fieldEditedByUser = false;
        NoteUserActionStateChange();
        FireFocusEvent(element, "focus", sameDocument ? oldElement : null, byUser);
        FireFocusEvent(element, "focusin", sameDocument ? oldElement : null, byUser);
    }

    /// <summary>
    /// Whether <paramref name="element"/>, focused by <paramref name="origin"/>, shows its focus
    /// (<c>:focus-visible</c>), by Chromium's rule as measured: always when the keyboard moved it there,
    /// and for a text field however it got there; never for anything else a pointer focused; and when a
    /// script focused it, as long as the user's last input was the keyboard, or there was none.
    /// </summary>
    private bool ShowsFocus(DomElement element, FocusOrigin origin) => origin switch
    {
        FocusOrigin.Keyboard => true,
        FocusOrigin.Pointer => IsTextEntry(element),
        _ => _keyboardModality || IsTextEntry(element),
    };

    /// <summary>The focused element of <paramref name="document"/>, when it is the focused document and the element is still in it.</summary>
    private DomElement? FocusedElementIn(DomDocument document) =>
        _focusedElement is { IsConnected: true } element &&
        ReferenceEquals(FocusedDocument, document) &&
        ReferenceEquals(GetOwningDocument(element), document)
            ? element
            : null;

    private static DomElement? BodyOrRootOf(DomDocument document)
    {
        var root = ChildElements(document).FirstOrDefault(static child => !child.TagName.StartsWith('#'));
        if (root is null)
            return null;

        return ChildElements(root).FirstOrDefault(static child =>
                   child.TagName.Equals("body", StringComparison.OrdinalIgnoreCase) ||
                   child.TagName.Equals("frameset", StringComparison.OrdinalIgnoreCase))
               ?? root;
    }

    /// <summary>
    /// Whether <paramref name="element"/> can have focus: it is in a document, rendered, not a
    /// disabled control, not inert (DomBridge/Inertness.cs), and either focusable by what it is -- a
    /// link, a form control, a frame, a <c>summary</c>, media with controls, an editing host, an open
    /// dialog -- or given a <c>tabindex</c>.
    /// </summary>
    private bool IsFocusable(DomElement element)
    {
        if (!element.IsConnected || element.TagName.StartsWith('#') || IsDisabledFormControl(element) ||
            !IsElementRenderedForHitTesting(element) || IsInert(element))
        {
            return false;
        }

        // An open dialog takes focus from a script and from a press on it, though a Tab passes it: its
        // tabIndex is -1 (measured).
        if (element.TagName.Equals("dialog", StringComparison.OrdinalIgnoreCase) && HasAttr(element, "open"))
            return true;

        if (TryGetAttribute(element, "tabindex", out var tabIndex) &&
            int.TryParse(tabIndex.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (TryGetAttribute(element, "contenteditable", out var editable) &&
            !editable.Trim().Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IsFocusableByDefault(element);
    }

    /// <summary>Whether <paramref name="element"/> is focusable without a <c>tabindex</c>: what <c>tabIndex</c> reports 0 for.</summary>
    internal static bool IsFocusableByDefault(DomElement element) =>
        element.TagName.ToLowerInvariant() switch
        {
            "a" or "area" => HasAttr(element, "href"),
            "button" or "select" or "textarea" or "iframe" or "frame" or "embed" or "object" => true,
            "input" => !(TryGetAttribute(element, "type", out var type) && type.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase)),
            "summary" => ParentEl(element) is { } details &&
                         details.TagName.Equals("details", StringComparison.OrdinalIgnoreCase) &&
                         ReferenceEquals(ChildElements(details).FirstOrDefault(static child =>
                             child.TagName.Equals("summary", StringComparison.OrdinalIgnoreCase)), element),
            "audio" or "video" => HasAttr(element, "controls"),
            _ => false,
        };

    /// <summary>A trusted <c>focus</c>, <c>blur</c>, <c>focusin</c> or <c>focusout</c> at <paramref name="target"/>, as its window's script.</summary>
    private void FireFocusEvent(DomElement target, string type, DomElement? related, bool byUser)
    {
        var realm = Realm;
        var window = WindowOfDocument(GetOwningDocument(target));
        var evt = NewTrustedEvent(realm, type, bubbles: type is "focusin" or "focusout", cancelable: false, composed: true, FocusEventPrototype(realm));
        Define(realm, evt, "view", window.IsObject ? window : JsValue.Null);
        Define(realm, evt, "detail", JsValue.Number(0));
        Define(realm, evt, "relatedTarget", related is null ? JsValue.Null : WrapNode(related));

        RunFocusEvent(window, () => _eventDispatch.DispatchEventOnElement(target, evt), byUser);
    }

    /// <summary>A trusted <c>focus</c> or <c>blur</c> at the window of <paramref name="document"/>, when it has one.</summary>
    private void FireWindowFocusEvent(DomDocument document, string type, bool byUser)
    {
        var window = WindowOfDocument(document);
        if (!window.IsObject)
            return;

        var realm = Realm;
        var evt = NewTrustedEvent(realm, type, bubbles: false, cancelable: false, composed: true, FocusEventPrototype(realm));
        Define(realm, evt, "view", window);
        Define(realm, evt, "detail", JsValue.Number(0));
        Define(realm, evt, "relatedTarget", JsValue.Null);

        RunFocusEvent(window, () => _eventDispatch.DispatchEventOnWindow(window, evt), byUser);
    }

    private void RunFocusEvent(JsValue window, Action dispatch, bool byUser)
    {
        try
        {
            if (window.IsObject && _browsingContexts.IsSubWindow(window))
                RunWithWindowContext(window, dispatch);
            else
                dispatch();
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.focus",
                $"Dispatching a focus event failed: {ex.Message}", ex);
        }

        if (byUser)
            TaskCheckpointCallback?.Invoke();
    }

    /// <summary>The window <paramref name="document"/> is shown in: the page's, or its frame's when that has been built.</summary>
    private JsValue WindowOfDocument(DomDocument document) =>
        ((Dom.Features.IEventDispatchHost)this).TryGetDocumentTargets(document, out _, out var window) && window.IsObject
            ? window
            : JsValue.Missing;

    /// <summary><c>FocusEvent.prototype</c>, so a focus event is a <c>FocusEvent</c> to <c>instanceof</c>; missing when there is none.</summary>
    private static JsValue FocusEventPrototype(IJsRealm realm)
    {
        var constructor = realm.GetProperty(realm.Global, "FocusEvent");
        if (!constructor.IsObject)
            return JsValue.Missing;

        var prototype = realm.GetProperty(constructor, "prototype");
        return prototype.IsObject ? prototype : JsValue.Missing;
    }

    /// <summary>Forgets the focus and the hover a previous document had.</summary>
    private void ResetInputState()
    {
        _focusedDocument = null;
        _focusedElement = null;
        _focusGeneration++;
        _hoverLevels = [];
        _pressTarget = null;
        _pressSuppressesMouseEvents = false;
        _lastPointerPosition = null;
        _lastActivations.Clear();
        _lastKeyDown = null;
        _keyDownCancelled = false;
        _spaceArmed = null;
        _activeTarget = null;
        _keyboardModality = true;
        _focusVisible = false;
        _changeField = null;
        _changeBaseline = null;
        _fieldEditedByUser = false;
        _focusFixupQueued = false;
    }
}
