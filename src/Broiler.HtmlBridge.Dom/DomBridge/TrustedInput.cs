using Broiler.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A user's pointer input, delivered to the page's scripts as a browser delivers it: hit-tested to the
/// element under the pointer -- inside a frame, the frame's element -- and dispatched as trusted
/// <c>pointerdown</c>/<c>mousedown</c>, <c>pointerup</c>/<c>mouseup</c>, <c>click</c> and
/// <c>dblclick</c> (or <c>auxclick</c>) events, with the click's activation behaviour.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing a user did reached a page's scripts.</b> The browser window handled a click itself --
/// text selection, links -- and every event a script ever saw was one script had made, with
/// <c>isTrusted</c> false. A checkbox drawn by script, a button with a click listener, a menu opened
/// on <c>mousedown</c>: none of them answered. reCAPTCHA's checkbox is a <c>span</c> in a frame,
/// listening for exactly these.
/// </para>
/// <para>
/// <b>Coordinates.</b> The point is in the page's layout coordinates -- the coordinates the bridge's
/// geometry snapshot is in, frames included, since a frame's document is laid out in place inside its
/// element. <c>clientX</c>/<c>clientY</c> are relative to the viewport for an element of the page, and
/// to the frame's content box for an element in a frame, which does not scroll.
/// </para>
/// <para>
/// <b>What is not modelled:</b> focus (the bridge has no focus state, so nothing is focused by a
/// press), pointer capture, hover (<c>mouseover</c>, <c>mouseenter</c> and the rest, which a press does
/// not imply), and the activation of a link or a submit button, which the host performs when the click
/// was not cancelled (<see cref="PointerInputResult.DefaultPrevented"/>).
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>How many frames deep a pointer is followed: <c>FrameCompositor.MaxDepth</c> in the window.</summary>
    private const int MaxInputFrameDepth = 4;

    // The press the next release completes: the element it landed on, and whether a cancelled
    // pointerdown suppressed the gesture's compatibility mouse events (Pointer Events §11).
    private DomElement? _pressTarget;
    private bool _pressSuppressesMouseEvents;

    /// <summary>
    /// Delivers <paramref name="input"/> to the page's scripts. Answers whether they were given it, and
    /// whether they cancelled what it does by default.
    /// </summary>
    internal PointerInputResult DispatchPointerInput(PointerInput input)
    {
        ThrowIfDisposed();
        if (_realm is null || DocumentElement is null)
            return default;

        // The hit test is one geometry pass, in which no script may run; the window of the frame it
        // lands in is resolved after it, since building a frame's window can run the frame's scripts.
        var located = WithLayoutGeometryCache(() => HitTestInput(input.X, input.Y));
        var hit = located with { Window = located.Frame is { } frame ? _subWindows.GetOrCreate(frame) : WindowHandle };
        var allowed = true;

        if (input.Kind == PointerInputKind.Down)
        {
            _pressTarget = hit.Target;
            _pressSuppressesMouseEvents = false;
            if (!FireInputEvent(hit, input, "pointerdown", detail: 0))
            {
                // A cancelled pointerdown keeps the gesture's mouse events from firing, though not its click.
                _pressSuppressesMouseEvents = true;
                allowed = false;
            }
            else if (!FireInputEvent(hit, input, "mousedown", input.ClickCount))
            {
                allowed = false;
            }

            return new PointerInputResult(true, !allowed);
        }

        FireInputEvent(hit, input, "pointerup", detail: 0);
        if (!_pressSuppressesMouseEvents)
            FireInputEvent(hit, input, "mouseup", input.ClickCount);

        var press = _pressTarget;
        _pressTarget = null;

        // A click goes to the nearest element both the press and the release were over; a press in one
        // document and a release in another click nothing.
        if (press is null || CommonInclusiveAncestor(press, hit.Target) is not { } clickTarget)
            return new PointerInputResult(true, false);

        var clickHit = hit with { Target = clickTarget };
        if (input.Button != 0)
        {
            allowed = FireInputEvent(clickHit, input, "auxclick", input.ClickCount);
            return new PointerInputResult(true, !allowed);
        }

        // A disabled form control is not clicked (HTML §4.10.18.5).
        if (IsDisabledFormControl(clickTarget))
            return new PointerInputResult(true, false);

        allowed = FireClick(clickHit, input, input.ClickCount, labelDepth: 0);
        if (input.ClickCount == 2)
            FireInputEvent(clickHit, input, "dblclick", 2);

        return new PointerInputResult(true, !allowed);
    }

    /// <summary>The element under the pointer, and how the coordinates of its events are measured.</summary>
    /// <param name="Target">The element.</param>
    /// <param name="Frame">The frame container whose document the element is in, or <see langword="null"/> for the page.</param>
    /// <param name="OriginX">Where its frame's content box starts across the page; 0 for an element of the page.</param>
    /// <param name="OriginY">Where its frame's content box starts down the page; 0 for an element of the page.</param>
    /// <param name="TargetLeft">Where the element's border box starts across the page, for <c>offsetX</c>.</param>
    /// <param name="TargetTop">Where the element's border box starts down the page, for <c>offsetY</c>.</param>
    /// <remarks>
    /// The element's box is measured once, in the hit test's own geometry pass: a listener that
    /// changes the page would otherwise have each later event of the gesture lay the page out again.
    /// </remarks>
    private readonly record struct InputHit(
        DomElement Target, DomElement? Frame, double OriginX, double OriginY, double TargetLeft, double TargetTop)
    {
        /// <summary>The window the element's document is shown in; resolved after the hit test.</summary>
        public JsValue Window { get; init; }

        /// <summary>Whether the element is in a frame, whose viewport does not scroll.</summary>
        public bool InFrame => Frame is not null;
    }

    /// <summary>
    /// The element at (<paramref name="x"/>, <paramref name="y"/>) in the page's layout coordinates,
    /// followed into the frame the point is in.
    /// </summary>
    /// <remarks>
    /// The same walk <c>elementFromPoint</c> makes, without its viewport bounds: a point further down
    /// than the viewport is tall is on the page once the page is scrolled. A point on no element is on
    /// the document's root element.
    /// </remarks>
    private InputHit HitTestInput(double x, double y)
    {
        var target = TopmostElementAt(DocumentElement, x, y);
        DomElement? frame = null;
        double originX = 0, originY = 0;

        for (var depth = 0; depth < MaxInputFrameDepth; depth++)
        {
            if (!IsFrameContainerElement(target) ||
                GetContentDocument(target) is not { } content ||
                ChildElements(content).FirstOrDefault(static child => !child.TagName.StartsWith('#')) is not { } frameRoot ||
                !TryGetSharedLayoutGeometry(target, out var geometry))
            {
                break;
            }

            var box = geometry.ContentBox;
            if (x < box.Left || y < box.Top || x >= box.Right || y >= box.Bottom)
                break;

            frame = target;
            originX = box.Left;
            originY = box.Top;
            target = TopmostElementAt(frameRoot, x, y);
        }

        var (left, top, _, _) = GetBoundingClientRectForDomElement(target, isRoot: false);
        return new InputHit(target, frame, originX, originY, left, top);
    }

    private DomElement TopmostElementAt(DomElement root, double x, double y)
    {
        var hits = new List<DomElement>();
        CollectHitTestMatches(root, x, y, hits);
        foreach (var hit in hits)
        {
            // The root's own rectangle is its viewport, which inside a frame is not where the frame is.
            if (!ReferenceEquals(hit, root))
                return hit;
        }

        return root;
    }

    private static bool IsFrameContainerElement(DomElement element) =>
        element.TagName.Equals("iframe", StringComparison.OrdinalIgnoreCase) ||
        element.TagName.Equals("frame", StringComparison.OrdinalIgnoreCase);

    private static DomElement? CommonInclusiveAncestor(DomElement first, DomElement second)
    {
        var ancestors = new HashSet<DomElement>(ReferenceEqualityComparer.Instance);
        for (var current = first; current != null; current = ParentEl(current))
            ancestors.Add(current);
        for (var current = second; current != null; current = ParentEl(current))
        {
            if (ancestors.Contains(current))
                return current;
        }

        return null;
    }

    /// <summary>
    /// Fires a <c>click</c> at <paramref name="hit"/>'s target with the activation behaviour of the
    /// element it activates: a checkbox or a radio button changes before the click is dispatched and
    /// changes back if it is cancelled, and fires <c>input</c> and <c>change</c> when it is not; a
    /// label clicks its control. Answers whether the click was not cancelled.
    /// </summary>
    private bool FireClick(InputHit hit, PointerInput input, int detail, int labelDepth)
    {
        var activation = ActivationTargetOf(hit.Target);
        var undo = PreActivate(activation, out var changed);

        var allowed = FireInputEvent(hit, input, "click", detail);
        if (!allowed)
        {
            undo?.Invoke();
            return false;
        }

        if (activation is null)
            return true;

        if (changed)
        {
            FireInputNotification(hit with { Target = activation }, "input", composed: true);
            FireInputNotification(hit with { Target = activation }, "change", composed: false);
        }
        else if (labelDepth == 0 &&
                 activation.TagName.Equals("label", StringComparison.OrdinalIgnoreCase) &&
                 LabeledControlOf(activation) is { } control &&
                 !IsInclusiveAncestor(control, hit.Target) &&
                 !IsDisabledFormControl(control))
        {
            // A label's activation clicks the control it labels, which then does what its own click does.
            FireClick(hit with { Target = control }, input, detail: 0, labelDepth + 1);
        }

        return true;
    }

    /// <summary>
    /// The element <paramref name="target"/>'s click activates: the target, or the nearest ancestor
    /// that does something when clicked -- here, a checkbox, a radio button or a label.
    /// </summary>
    private static DomElement? ActivationTargetOf(DomElement target)
    {
        for (var current = target; current != null; current = ParentEl(current))
        {
            if (IsCheckable(current, out _))
                return current;
            if (current.TagName.Equals("label", StringComparison.OrdinalIgnoreCase))
                return current;

            // An element that is activated itself stops the search: a click on a link inside a label
            // follows the link, it does not click the label's control.
            if (current.TagName.Equals("a", StringComparison.OrdinalIgnoreCase) && HasAttr(current, "href") ||
                current.TagName.Equals("button", StringComparison.OrdinalIgnoreCase) ||
                current.TagName.Equals("input", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsCheckable(DomElement element, out bool isRadio)
    {
        isRadio = false;
        if (!element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase))
            return false;

        var type = TryGetAttribute(element, "type", out var declared) ? declared.Trim() : string.Empty;
        isRadio = type.Equals("radio", StringComparison.OrdinalIgnoreCase);
        return isRadio || type.Equals("checkbox", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The legacy-pre-activation behaviour of a checkbox or a radio button: it changes before the
    /// click is dispatched, so the click's listeners see it changed. Answers how to change it back if
    /// the click is cancelled, and whether it changed at all.
    /// </summary>
    private Action? PreActivate(DomElement? activation, out bool changed)
    {
        changed = false;
        if (activation is null || !IsCheckable(activation, out var isRadio) || IsDisabledFormControl(activation))
            return null;

        var wasChecked = IsChecked(activation);
        if (!isRadio)
        {
            _formState.SetDirtyChecked(activation, !wasChecked);
            changed = true;
            return () => _formState.SetDirtyChecked(activation, wasChecked);
        }

        if (wasChecked)
            return null;

        var previouslyChecked = RadioGroupOf(activation).FirstOrDefault(IsChecked);
        _formState.SetDirtyChecked(activation, true);
        changed = true;
        return () =>
        {
            if (previouslyChecked is not null)
                _formState.SetDirtyChecked(previouslyChecked, true);
            else
                _formState.SetDirtyChecked(activation, false);
        };
    }

    private bool IsChecked(DomElement element) =>
        _formState.TryGetDirtyChecked(element, out var isChecked) ? isChecked : HasAttr(element, "checked");

    /// <summary>The other radio buttons of <paramref name="radio"/>'s group: the same name, in the same form, in the same document.</summary>
    private IEnumerable<DomElement> RadioGroupOf(DomElement radio)
    {
        if (!TryGetAttribute(radio, "name", out var name) || name.Length == 0)
            yield break;

        var form = NearestAncestor(radio, "form");
        foreach (var candidate in GetOwningDocument(radio).Descendants().OfType<DomElement>())
        {
            if (!ReferenceEquals(candidate, radio) &&
                IsCheckable(candidate, out var isRadio) && isRadio &&
                TryGetAttribute(candidate, "name", out var candidateName) && candidateName == name &&
                ReferenceEquals(NearestAncestor(candidate, "form"), form))
            {
                yield return candidate;
            }
        }
    }

    private static DomElement? NearestAncestor(DomElement element, string tagName)
    {
        for (var current = ParentEl(element); current != null; current = ParentEl(current))
        {
            if (current.TagName.Equals(tagName, StringComparison.OrdinalIgnoreCase))
                return current;
        }

        return null;
    }

    /// <summary>
    /// The control a label labels: the element its <c>for</c> attribute names in its document, when that
    /// is labelable, or else its first labelable descendant (HTML §4.10.4).
    /// </summary>
    private static DomElement? LabeledControlOf(DomElement label)
    {
        if (TryGetAttribute(label, "for", out var id))
        {
            var named = GetOwningDocument(label).Descendants().OfType<DomElement>()
                .FirstOrDefault(element => TryGetAttribute(element, "id", out var elementId) && elementId == id);
            return named is not null && IsLabelable(named) ? named : null;
        }

        return label.Descendants().OfType<DomElement>().FirstOrDefault(IsLabelable);
    }

    private static bool IsLabelable(DomElement element) =>
        element.TagName.ToLowerInvariant() switch
        {
            "button" or "meter" or "output" or "progress" or "select" or "textarea" => true,
            "input" => !(TryGetAttribute(element, "type", out var type) && type.Trim().Equals("hidden", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };

    private static bool IsInclusiveAncestor(DomElement ancestor, DomElement node)
    {
        for (var current = node; current != null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    private static bool IsDisabledFormControl(DomElement element) =>
        HasAttr(element, "disabled") &&
        element.TagName.ToLowerInvariant() is "button" or "input" or "select" or "textarea" or "optgroup" or "option" or "fieldset";

    /// <summary>
    /// Dispatches a trusted pointer or mouse event of <paramref name="type"/> at <paramref name="hit"/>'s
    /// target, as its window's script, and runs the microtask checkpoint after it. Answers whether it was
    /// not cancelled.
    /// </summary>
    private bool FireInputEvent(InputHit hit, PointerInput input, string type, int detail)
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, type, bubbles: true, cancelable: true, composed: true, MouseEventPrototype(realm));

        var clientX = input.X - (hit.InFrame ? hit.OriginX : input.ScrollX);
        var clientY = input.Y - (hit.InFrame ? hit.OriginY : input.ScrollY);
        var pressed = type is "pointerdown" or "mousedown";

        Define(realm, evt, "view", hit.Window.IsObject ? hit.Window : JsValue.Null);
        Define(realm, evt, "detail", JsValue.Number(detail));
        Define(realm, evt, "screenX", JsValue.Number(input.X - input.ScrollX));
        Define(realm, evt, "screenY", JsValue.Number(input.Y - input.ScrollY));
        Define(realm, evt, "clientX", JsValue.Number(clientX));
        Define(realm, evt, "clientY", JsValue.Number(clientY));
        Define(realm, evt, "x", JsValue.Number(clientX));
        Define(realm, evt, "y", JsValue.Number(clientY));
        Define(realm, evt, "pageX", JsValue.Number(hit.InFrame ? clientX : input.X));
        Define(realm, evt, "pageY", JsValue.Number(hit.InFrame ? clientY : input.Y));
        Define(realm, evt, "offsetX", JsValue.Number(input.X - hit.TargetLeft));
        Define(realm, evt, "offsetY", JsValue.Number(input.Y - hit.TargetTop));
        Define(realm, evt, "movementX", JsValue.Number(0));
        Define(realm, evt, "movementY", JsValue.Number(0));
        Define(realm, evt, "button", JsValue.Number(type is "pointerdown" or "pointerup" or "mousedown" or "mouseup" or "click" or "dblclick" or "auxclick" ? input.Button : 0));
        Define(realm, evt, "buttons", JsValue.Number(input.Buttons));
        Define(realm, evt, "ctrlKey", JsValue.Boolean(input.CtrlKey));
        Define(realm, evt, "shiftKey", JsValue.Boolean(input.ShiftKey));
        Define(realm, evt, "altKey", JsValue.Boolean(input.AltKey));
        Define(realm, evt, "metaKey", JsValue.Boolean(input.MetaKey));
        Define(realm, evt, "relatedTarget", JsValue.Null);
        realm.DefineMethod(evt, "getModifierState", 1, (in call) =>
        {
            var key = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
            return JsValue.Boolean(key switch
            {
                "Control" => input.CtrlKey,
                "Shift" => input.ShiftKey,
                "Alt" => input.AltKey,
                "Meta" => input.MetaKey,
                _ => false,
            });
        });

        if (type.StartsWith("pointer", StringComparison.Ordinal))
        {
            // The mouse is pointer 1, the primary pointer, one pixel across, half pressed while a button is down.
            Define(realm, evt, "pointerId", JsValue.Number(1));
            Define(realm, evt, "pointerType", JsValue.String("mouse"));
            Define(realm, evt, "isPrimary", JsValue.True);
            Define(realm, evt, "width", JsValue.Number(1));
            Define(realm, evt, "height", JsValue.Number(1));
            Define(realm, evt, "pressure", JsValue.Number(pressed || input.Buttons != 0 ? 0.5 : 0));
            Define(realm, evt, "tangentialPressure", JsValue.Number(0));
            Define(realm, evt, "tiltX", JsValue.Number(0));
            Define(realm, evt, "tiltY", JsValue.Number(0));
            Define(realm, evt, "twist", JsValue.Number(0));
            Define(realm, evt, "altitudeAngle", JsValue.Number(Math.PI / 2));
            Define(realm, evt, "azimuthAngle", JsValue.Number(0));
        }

        return DispatchTrusted(hit, evt);
    }

    /// <summary>A trusted <c>input</c> or <c>change</c> at a form control: not cancelable, and only <c>input</c> leaves a shadow tree.</summary>
    private void FireInputNotification(InputHit hit, string type, bool composed)
    {
        var realm = Realm;
        DispatchTrusted(hit, NewTrustedEvent(realm, type, bubbles: true, cancelable: false, composed, JsValue.Missing));
    }

    private bool DispatchTrusted(InputHit hit, JsValue evt)
    {
        var allowed = true;
        void Dispatch() => allowed = _eventDispatch.DispatchEventOnElement(hit.Target, evt).AsBoolean;

        try
        {
            // As the target's window's script, so a frame's listeners see the frame's globals.
            if (hit.InFrame && hit.Window.IsObject)
                RunWithWindowContext(hit.Window, Dispatch);
            else
                Dispatch();
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.DispatchPointerInput",
                $"Dispatching a trusted event failed: {ex.Message}", ex);
        }

        TaskCheckpointCallback?.Invoke();
        return allowed;
    }

    /// <summary>
    /// A new event object as the user agent makes one: <c>isTrusted</c> is true, and cannot be made
    /// otherwise -- a getter, not a property a script could write over.
    /// </summary>
    private static JsValue NewTrustedEvent(IJsRealm realm, string type, bool bubbles, bool cancelable, bool composed, JsValue prototype)
    {
        var evt = realm.NewObject();
        if (prototype.IsObject)
            realm.SetPrototype(evt, prototype);

        Define(realm, evt, "type", JsValue.String(type));
        Define(realm, evt, "bubbles", JsValue.Boolean(bubbles));
        Define(realm, evt, "cancelable", JsValue.Boolean(cancelable));
        Define(realm, evt, "composed", JsValue.Boolean(composed));
        Define(realm, evt, "defaultPrevented", JsValue.False);
        Define(realm, evt, "target", JsValue.Null);
        Define(realm, evt, "currentTarget", JsValue.Null);
        Define(realm, evt, "srcElement", JsValue.Null);
        Define(realm, evt, "eventPhase", JsValue.Number(0));
        Define(realm, evt, "timeStamp", JsValue.Number(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        realm.DefineAccessor(evt, "isTrusted", static (in _) => JsValue.True, null, JsPropertyFlags.Enumerable);
        return evt;
    }

    private static void Define(IJsRealm realm, JsValue target, string name, JsValue value) =>
        realm.DefineValue(target, name, value);

    /// <summary><c>MouseEvent.prototype</c>, so a trusted event is a <c>MouseEvent</c> to <c>instanceof</c>; missing when there is none.</summary>
    private static JsValue MouseEventPrototype(IJsRealm realm)
    {
        var constructor = realm.GetProperty(realm.Global, "MouseEvent");
        if (!constructor.IsObject)
            return JsValue.Missing;

        var prototype = realm.GetProperty(constructor, "prototype");
        return prototype.IsObject ? prototype : JsValue.Missing;
    }
}
