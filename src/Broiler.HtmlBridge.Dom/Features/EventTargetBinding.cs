using System;
using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>EventTarget</c> methods exposed on every node/element wrapper —
/// <c>addEventListener</c>, <c>removeEventListener</c>, <c>dispatchEvent</c>, and the synthetic-event
/// convenience methods <c>click</c>, <c>focus</c> and <c>blur</c>. These were the bridge's
/// <c>JsJsObjectsAddEventListener097Core</c>..<c>Blur103Core</c> callbacks. The registration semantics
/// (option parsing, dedup, match-by-listener+capture) live in <see cref="EventListenerBinding"/> and the
/// capture→target→bubble engine in <see cref="EventDispatchBinding"/>; this module wires the JS-facing
/// methods to them, reaching the realm, the per-node listener store, the dispatch engine and the window
/// JS object through <see cref="IEventTargetHost"/>. Node-type/attribute/runtime-state helpers and the
/// radio-group mutual-exclusion walk (<c>UncheckRadioSiblings</c>) are the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>The three <c>EventTarget</c> operations exist twice, and that is the shape of a half-migrated
/// installer set rather than a duplication anybody chose.</b> They are installed from four places:
/// <c>DomBridge/EventTargetInterface.cs</c> routes <c>EventTarget.prototype</c>'s three by receiver
/// and mints them through the realm, so it needs a JSEAL call frame; and
/// <c>DomBridge/JsObjects.cs</c> and <c>JsObjects.NonElementNodes.cs</c> (twice) still install
/// per-wrapper copies for a wrapper minted before the realm carried <c>EventTarget</c>, with the
/// engine's own function type, so they need an engine argument frame. There is no adapter between two
/// call frames — only between two object types — so a single body cannot serve both, and the pair
/// below is what lets the routed methods migrate without the pre-realm path changing. The engine-typed
/// three go with those two files; the pair collapses to one then.
/// </para>
/// <para>
/// <b>The two are the same operation, and each line of the pair is meant to read as the same line.</b>
/// The event-type coercion is <c>a[0].ToString()</c> on the engine side and the realm's
/// <c>ToJsString</c> on the JSEAL side, which are the same observable ECMAScript <c>ToString</c>; the
/// arity guards are the same; and where the engine side calls <see cref="EventListenerBinding"/>
/// directly with values it already holds, the JSEAL side goes through
/// <see cref="IEventTargetHost.AddListener"/>, which does that conversion in the host — the seam the
/// document and window contracts use for the same record.
/// </para>
/// <para>
/// <b>The synthetic events are built through JSEAL either way.</b> Every event object this module
/// mints — the <c>click</c>, the <c>submit</c> a submit button triggers, and the
/// <c>focus</c>/<c>blur</c> UIEvents — is a <see cref="JsValue"/> assembled on
/// <see cref="IEventTargetHost.Realm"/>, with the property attributes each member always had.
/// <c>click</c>, <c>focus</c> and <c>blur</c> themselves keep the engine frame because
/// <c>DomBridge/HtmlElementInterface.cs</c> is their only installer and it has not migrated; they
/// ignore their arguments entirely, so the frame is a signature and nothing more.
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

        host.AddListener(listeners, call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue RemoveEventListener(IEventTargetHost host, DomNode element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        host.RemoveListener(
            host.GetEventListeners(element).TryGetValue(type, out var listeners) ? listeners : null,
            call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    public static JsValue DispatchEvent(IEventTargetHost host, DomNode element, in JsCall call)
    {
        // A missing argument is not an object either, which is the two guards the engine-typed
        // sibling spells separately.
        if (!call[0].IsObject)
            return JsValue.True;
        return host.DispatchEvent(element, call[0]);
    }

    public static JsValue Click(IEventTargetHost host, DomElement element, in JsCall _)
    {
        // Toggle checked state for checkboxes/radio buttons (per HTML spec)
        if (string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase))
        {
            var inputType = DomBridge.TryGetAttribute(element, "type", out var t) ? t.ToLowerInvariant() : "text";
            if (inputType == "checkbox")
            {
                var checkedState = host.FormControlStateFor(element).Checked;
                bool wasChecked = checkedState.TryGet(out var cv) && cv is true || (!checkedState.IsSet && DomBridge.HasAttr(element, "checked"));
                checkedState.Set(!wasChecked);
            }
            else if (inputType == "radio")
            {
                host.FormControlStateFor(element).Checked.Set(true);
                // Radio mutual exclusion
                if (DomBridge.TryGetAttribute(element, "name", out var radioName) && !string.IsNullOrEmpty(radioName))
                {
                    var scope = element;
                    while (DomBridge.ParentEl(scope) != null)
                        scope = DomBridge.ParentEl(scope);
                    host.UncheckRadioSiblings(scope, element, radioName);
                }
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
            if (DomBridge.TryGetAttribute(element, "type", out var bt))
                btnType = bt.ToLowerInvariant();
            else if (string.Equals(element.TagName, "button", StringComparison.OrdinalIgnoreCase))
                btnType = "submit"; // <button> defaults to type="submit" per HTML spec
            if (btnType == "submit")
            {
                // Walk up the DOM tree to find the parent <form>
                var form = DomBridge.ParentEl(element);
                while (form != null && !string.Equals(form.TagName, "form", StringComparison.OrdinalIgnoreCase))
                    form = DomBridge.ParentEl(form);
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
    /// rather than intent.</b> These were minted by <c>DomBridge.UndefinedFunction</c>, which builds
    /// a plain <c>JSFunction</c> — so each carries a <c>prototype</c> object and passes the engine's
    /// constructor test, which WebIDL says an operation must not. <c>NewMethod</c> would be the right
    /// shape and a behaviour change (<c>evt.stopPropagation.prototype</c> would become
    /// <c>undefined</c>), so this refactor keeps the quirk and reports it rather than fixing it in
    /// passing. Note that the <c>submit</c> event's real <c>preventDefault</c> above was already a
    /// <c>DomFunction</c> and stays non-constructable — the two were inconsistent before this change
    /// and still are.
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
