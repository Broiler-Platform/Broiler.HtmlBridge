using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Pointer capture (Pointer Events §10): <c>setPointerCapture</c>, <c>releasePointerCapture</c> and
/// <c>hasPointerCapture</c> for the one pointer the bridge has, the mouse, with the
/// <c>gotpointercapture</c> and <c>lostpointercapture</c> they cause and the pointer's events going to
/// the element that captured it.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of it existed</b>, nor did <c>PointerEvent</c>, so a page that feature-detects pointer events
/// took its mouse-event path. With <c>PointerEvent</c> there, the commonest way a page drags -- capture
/// the pointer in <c>pointerdown</c>, follow its <c>pointermove</c>s wherever it goes -- needs the three
/// methods, or its <c>pointerdown</c> listener throws.
/// </para>
/// <para>
/// <b>As Chromium captures, measured.</b> In a <c>pointerdown</c> listener <c>setPointerCapture(1)</c>
/// makes <c>hasPointerCapture(1)</c> true at once; <c>gotpointercapture</c> fires before the next pointer
/// event, the gesture's pointer and mouse events then go to the capturing element wherever the pointer
/// is, and after <c>pointerup</c> and <c>mouseup</c> the capture is released with
/// <c>lostpointercapture</c>, before the <c>click</c>. With no button down nothing is captured and nothing
/// thrown; a pointer id the page has never had throws <c>NotFoundError</c>.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>The mouse's pointer id, which its pointer events carry.</summary>
    private const int MousePointerId = 1;

    // The element the mouse's events go to, and the one they will go to once the pending capture is
    // processed (Pointer Events "pointer capture target override" and its pending counterpart).
    private DomElement? _pointerCaptureTarget;
    private DomElement? _pendingPointerCapture;

    // Whether a mouse button is down: the "active buttons state" a capture needs.
    private bool _pointerButtonsDown;

    /// <summary>Installs the three capture methods onto <c>Element.prototype</c>, or one wrapper.</summary>
    private void InstallPointerCaptureMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        Realm.DefineMethod(target, "setPointerCapture", 1, (in call) =>
        {
            var captured = element(in call, "setPointerCapture");
            SetPointerCapture(captured, RequireActivePointerId(in call, "setPointerCapture"));
            return JsValue.Undefined;
        });
        Realm.DefineMethod(target, "releasePointerCapture", 1, (in call) =>
        {
            var captured = element(in call, "releasePointerCapture");
            RequireActivePointerId(in call, "releasePointerCapture");
            if (ReferenceEquals(_pendingPointerCapture, captured))
                _pendingPointerCapture = null;
            return JsValue.Undefined;
        });
        Realm.DefineMethod(target, "hasPointerCapture", 1, (in call) =>
        {
            var captured = element(in call, "hasPointerCapture");
            var id = call.Length > 0 ? call.Realm.ToNumber(call[0]) : double.NaN;
            return JsValue.Boolean(id == MousePointerId && ReferenceEquals(_pendingPointerCapture, captured));
        });
    }

    /// <summary>The pointer id a capture method was given, or the <c>NotFoundError</c> one the page has never had throws.</summary>
    private static double RequireActivePointerId(in JsCall call, string member)
    {
        var id = call.Length > 0 ? call.Realm.ToNumber(call[0]) : double.NaN;
        if (id != MousePointerId)
            throw call.Realm.DomError("NotFoundError", $"Failed to execute '{member}' on 'Element': No active pointer with the given id is found.");
        return id;
    }

    private void SetPointerCapture(DomElement element, double _)
    {
        if (!element.IsConnected)
            throw Realm.DomError("InvalidStateError", "Failed to execute 'setPointerCapture' on 'Element': InvalidStateError");

        // Without a button down there is nothing to capture, and nothing is thrown (Chromium, measured).
        if (_pointerButtonsDown)
            _pendingPointerCapture = element;
    }

    /// <summary>
    /// Pointer Events' "process pending pointer capture", before the mouse's next pointer event and after
    /// its <c>pointerup</c>: <c>lostpointercapture</c> at the element that had it, <c>gotpointercapture</c> at
    /// the one that asked for it.
    /// </summary>
    private void ProcessPendingPointerCapture(PointerInput input)
    {
        // An element taken out of its document cannot keep the capture.
        if (_pendingPointerCapture is { IsConnected: false })
            _pendingPointerCapture = null;

        if (ReferenceEquals(_pointerCaptureTarget, _pendingPointerCapture))
            return;

        var previous = _pointerCaptureTarget;
        _pointerCaptureTarget = _pendingPointerCapture;
        if (previous is not null)
        {
            if (previous.IsConnected)
                FireInputEvent(LevelsFor(previous)[^1], input, "lostpointercapture", detail: 0);
            else
                FireLostCaptureAtDocument(GetOwningDocument(previous));
        }

        if (_pointerCaptureTarget is { } next)
            FireInputEvent(LevelsFor(next)[^1], input, "gotpointercapture", detail: 0);
    }

    /// <summary>Releases the capture a gesture held once its button came up (Pointer Events "implicit release").</summary>
    private void ReleasePointerCaptureImplicitly(PointerInput input)
    {
        _pendingPointerCapture = null;
        ProcessPendingPointerCapture(input);
    }

    /// <summary><c>lostpointercapture</c> at the document of a capturing element that was removed from it.</summary>
    private void FireLostCaptureAtDocument(DomDocument document)
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, "lostpointercapture", bubbles: true, cancelable: false, composed: false, PointerEventPrototype(realm));
        Define(realm, evt, "pointerId", JsValue.Number(MousePointerId));
        Define(realm, evt, "pointerType", JsValue.String("mouse"));
        Define(realm, evt, "isPrimary", JsValue.True);
        _eventDispatch.DispatchEventOnElement(document, evt);
    }

    /// <summary>
    /// The levels a pointer at <paramref name="element"/> is in -- the page's element it is in, then each
    /// frame's inward -- as a hit test would report them, so a captured pointer's events go where they would
    /// go with the pointer over the element.
    /// </summary>
    private List<InputHit> LevelsFor(DomElement element)
    {
        var chain = new List<DomElement> { element };
        for (var document = GetOwningDocument(element); GetFrameForContentDocument(document) is { } container; document = GetOwningDocument(container))
            chain.Add(container);
        chain.Reverse();

        // One geometry pass, in which no script may run; each frame's window is resolved after it, since
        // building one can run the frame's scripts (as DispatchPointerInput does for a hit test).
        var levels = WithLayoutGeometryCache(() =>
        {
            var measured = new List<InputHit>(chain.Count);
            DomElement? frame = null;
            double originX = 0, originY = 0;
            foreach (var target in chain)
            {
                var (left, top, _, _) = GetBoundingClientRectForDomElement(target, isRoot: false);
                measured.Add(new InputHit(target, frame, originX, originY, left, top));

                if (IsFrameContainerElement(target) && TryGetSharedLayoutGeometry(target, out var geometry))
                {
                    frame = target;
                    originX = geometry.ContentBox.Left;
                    originY = geometry.ContentBox.Top;
                }
            }

            return measured;
        });

        for (var i = 0; i < levels.Count; i++)
            levels[i] = levels[i] with { Window = levels[i].Frame is { } frame ? _subWindows.GetOrCreate(frame) : WindowHandle };
        return levels;
    }

    /// <summary>The levels of the element that has captured the mouse, or null when nothing has.</summary>
    private List<InputHit>? CapturedLevels() =>
        _pointerCaptureTarget is { IsConnected: true } captured ? LevelsFor(captured) : null;

    /// <summary><c>PointerEvent.prototype</c>, so a pointer event is a <c>PointerEvent</c> to <c>instanceof</c>; missing when there is none.</summary>
    private static JsValue PointerEventPrototype(IJsRealm realm) => InterfacePrototype(realm, "PointerEvent");

    /// <summary>Whether a mouse event of <paramref name="type"/> is a <c>PointerEvent</c> in Chromium: the pointer events, and a click.</summary>
    private static bool IsPointerEventType(string type) =>
        type.StartsWith("pointer", StringComparison.Ordinal) ||
        type is "gotpointercapture" or "lostpointercapture" or "click" or "auxclick" or "contextmenu";

    private void ResetPointerCapture()
    {
        _pointerCaptureTarget = null;
        _pendingPointerCapture = null;
        _pointerButtonsDown = false;
    }
}
