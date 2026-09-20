using System;
using Broiler.JSeal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The feature module for the DOM <c>EventTarget</c> methods exposed on every node/element wrapper —
/// <c>addEventListener</c>, <c>removeEventListener</c>, <c>dispatchEvent</c>, and the synthetic-event
/// convenience methods <c>click</c>, <c>focus</c> and <c>blur</c>. The registration semantics
/// (option parsing, dedup, match-by-listener+capture) live in <see cref="EventListenerBinding"/> and the
/// capture→target→bubble engine in <see cref="EventDispatchBinding"/>; this module wires the JS-facing
/// methods to them, reaching the realm, the per-node listener store, the dispatch engine and the window
/// JS object through <see cref="IEventTargetHost"/>. Node-type/attribute helpers are the bridge's
/// <c>internal static</c> helpers, called directly; the form-control state and the radio-group
/// mutual-exclusion walk (<c>UncheckRadioSiblings</c>) are members of that contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each <c>EventTarget</c> operation has one body, and five installers mint it through the
/// realm.</b> <c>DomBridge/Events.cs</c> routes <c>EventTarget.prototype</c>'s three by
/// receiver; <c>DomBridge/JsObjects.cs</c> once and <c>DomBridge/JsObjects.NonElementNodes.cs</c> three
/// times install per-wrapper copies. All five hand the body a JSEAL call frame, so one body serves
/// every installer.
/// </para>
/// <para>
/// <b>Registration is <see cref="EventListenerBinding"/>'s, called directly.</b> Its two operations
/// take the realm the call frame carries, which is what reading an <c>options</c> object's flags and
/// coercing anything else need; the listener record holds a handle, so nothing converts on the way
/// in. The event-type coercion is the realm's <c>ToJsString</c>, the observable ECMAScript
/// <c>ToString</c>.
/// </para>
/// <para>
/// <b>The synthetic events are built through JSEAL, and so are the three members that fire them.</b>
/// Every event object this module mints -- the <c>click</c>, the <c>submit</c> a submit button
/// triggers, and the <c>focus</c>/<c>blur</c> UIEvents -- is a <see cref="JsValue"/> assembled on
/// <see cref="IEventTargetHost.Realm"/>, with the property attributes each member always had.
/// <c>DomBridge/ElementInterface.cs</c> installs <c>click</c>, <c>focus</c> and <c>blur</c> with
/// <c>AddInterfaceMethod</c> over a JSEAL call frame, and their signatures below say so.
/// </para>
/// </remarks>
internal static class EventTargetBinding
{
    // ------------------------------------------------------------------
    //  EventTarget.prototype's three, routed by receiver — the JSEAL frame
    // ------------------------------------------------------------------

    public static JsValue AddEventListener(IEventTargetHost host, DomNode element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        if (!host.GetEventListeners(element).TryGetValue(type, out var listeners))
        {
            listeners = [];
            host.GetEventListeners(element)[type] = listeners;
        }

        EventListenerBinding.AddListener(call.Realm, listeners, call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue RemoveEventListener(IEventTargetHost host, DomNode element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        EventListenerBinding.RemoveListener(
            call.Realm,
            host.GetEventListeners(element).TryGetValue(type, out var listeners) ? listeners : null,
            call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue DispatchEvent(IEventTargetHost host, DomNode element, in JsCall call)
    {
        // A missing argument is not an object either, so this one test answers for an absent
        // argument and for a non-object one.
        if (!call[0].IsObject)
            return JsValue.True;
        return host.DispatchEvent(element, call[0]);
    }

    public static JsValue Click(IEventTargetHost host, DomElement element, in JsCall _)
    {
        // Toggle checked state for checkboxes/radio buttons (per HTML spec)
        if (string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
        {
            var inputType = DomBridgeUtils.TryGetAttribute(element, "type", out var t) ? t.ToLowerInvariant() : "text";
            if (inputType == "checkbox")
            {
                bool wasChecked = host.TryGetFormControlChecked(element, out var cv)
                    ? cv
                    : DomBridgeUtils.HasAttr(element, "checked");
                host.SetFormControlChecked(element, !wasChecked);
            }
            else if (inputType == "radio")
            {
                host.SetFormControlChecked(element, true);
            }
        }

        var realm = host.Realm;
        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String("click"));
        realm.DefineValue(evt, "bubbles", JsValue.True);
        realm.DefineValue(evt, "cancelable", JsValue.True);
        realm.DefineValue(evt, "defaultPrevented", JsValue.False);
        realm.DefineValue(evt, "target", JsValue.Null);
        realm.DefineValue(evt, "currentTarget", JsValue.Null);
        realm.DefineValue(evt, "eventPhase", JsValue.Number(0));
        realm.DefineValue(evt, "detail", JsValue.Number(0));
        realm.DefineValue(evt, "stopPropagation", NoOperation(realm, "stopPropagation"));
        realm.DefineValue(evt, "stopImmediatePropagation", NoOperation(realm, "stopImmediatePropagation"));
        realm.DefineValue(evt, "preventDefault", NoOperation(realm, "preventDefault"));
        Dispatch(host, element, evt);
        // Per HTML spec: clicking a submit button triggers form submission
        if (string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) || string.Equals(element.TagName, "button", StringComparison.OrdinalIgnoreCase))
        {
            var btnType = "text";
            if (DomBridgeUtils.TryGetAttribute(element, "type", out var bt))
                btnType = bt.ToLowerInvariant();
            else if (string.Equals(element.TagName, "button", StringComparison.OrdinalIgnoreCase))
                btnType = "submit"; // <button> defaults to type="submit" per HTML spec
            if (btnType == "submit")
            {
                // Walk up the DOM tree to find the parent <form>
                var form = DomBridgeUtils.ParentEl(element);
                while (form != null && !string.Equals(form.TagName, "form", StringComparison.OrdinalIgnoreCase))
                    form = DomBridgeUtils.ParentEl(form);
                if (form != null)
                {
                    // Dispatch a submit event on the form
                    var submitEvt = realm.NewObject();
                    realm.DefineValue(submitEvt, "type", JsValue.String("submit"));
                    realm.DefineValue(submitEvt, "bubbles", JsValue.True);
                    realm.DefineValue(submitEvt, "cancelable", JsValue.True);
                    realm.DefineValue(submitEvt, "defaultPrevented", JsValue.False);
                    realm.DefineValue(submitEvt, "target", JsValue.Null);
                    realm.DefineValue(submitEvt, "currentTarget", JsValue.Null);
                    realm.DefineValue(submitEvt, "eventPhase", JsValue.Number(0));

                    // This one really is a WebIDL operation — non-constructable, unlike its three
                    // no-op siblings; see NoOperation.
                    realm.DefineValue(submitEvt, "preventDefault",
                        realm.NewMethod("preventDefault", (in _) =>
                        {
                            realm.SetProperty(submitEvt, "defaultPrevented", JsValue.True);
                            return JsValue.Undefined;
                        }, 0));

                    realm.DefineValue(submitEvt, "stopPropagation", NoOperation(realm, "stopPropagation"));
                    realm.DefineValue(submitEvt, "stopImmediatePropagation", NoOperation(realm, "stopImmediatePropagation"));
                    Dispatch(host, form, submitEvt);
                }
            }
        }

        return JsValue.Undefined;
    }

    public static JsValue Focus(IEventTargetHost host, DomElement element, in JsCall _)
        => DispatchSyntheticFocusEvent(host, element, "focus");

    public static JsValue Blur(IEventTargetHost host, DomElement element, in JsCall _)
        => DispatchSyntheticFocusEvent(host, element, "blur");

    private static JsValue DispatchSyntheticFocusEvent(IEventTargetHost host, DomElement element, string type)
    {
        var realm = host.Realm;
        var windowWrapper = host.WindowWrapper;
        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String(type));
        realm.DefineValue(evt, "bubbles", JsValue.False);
        realm.DefineValue(evt, "cancelable", JsValue.False);
        realm.DefineValue(evt, "defaultPrevented", JsValue.False);
        realm.DefineValue(evt, "target", JsValue.Null);
        realm.DefineValue(evt, "currentTarget", JsValue.Null);
        realm.DefineValue(evt, "srcElement", JsValue.Null);
        realm.DefineValue(evt, "eventPhase", JsValue.Number(0));
        realm.DefineValue(evt, "isTrusted", JsValue.False);
        realm.DefineValue(evt, "timeStamp", JsValue.Number(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        realm.DefineValue(evt, "detail", JsValue.Number(0));
        realm.DefineValue(evt, "view", windowWrapper.IsObject ? windowWrapper : JsValue.Null);
        realm.DefineValue(evt, "relatedTarget", JsValue.Null);
        Dispatch(host, element, evt);
        return JsValue.Undefined;
    }

    /// <summary>
    /// One of the three no-op propagation-control methods a synthetic event carries.
    /// </summary>
    /// <remarks>
    /// <b><see cref="IJsValues.NewConstructor"/>, not <c>NewMethod</c>, and that is faithfulness
    /// rather than intent.</b> These are minted as plain functions, so each carries a
    /// <c>prototype</c> object and passes the engine's constructor test, which WebIDL says an
    /// operation must not. <c>NewMethod</c> would be the right shape and a behaviour change
    /// (<c>evt.stopPropagation.prototype</c> would become <c>undefined</c>), so the quirk is kept
    /// and reported rather than fixed in passing. Note that the <c>submit</c> event's real
    /// <c>preventDefault</c> above is non-constructable — the two are inconsistent.
    /// </remarks>
    private static JsValue NoOperation(IJsRealm realm, string name) =>
        realm.NewConstructor(name, static (in _) => JsValue.Undefined, 0);

    /// <summary>
    /// Hands a synthetic event to the propagation engine, and discards the "not cancelled" answer —
    /// <c>click</c>, <c>focus</c> and <c>blur</c> return <c>undefined</c>, not a boolean.
    /// </summary>
    private static void Dispatch(IEventTargetHost host, DomNode target, JsValue evt) =>
        host.DispatchEvent(target, evt);
}
