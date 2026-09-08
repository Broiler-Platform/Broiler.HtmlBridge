using System;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
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
/// <b>The synthetic events are built through JSEAL; the call frame is not.</b> Every event object this
/// module mints — the <c>click</c>, the <c>submit</c> a submit button triggers, and the
/// <c>focus</c>/<c>blur</c> UIEvents — is a <see cref="JsValue"/> assembled on
/// <see cref="IEventTargetHost.Realm"/>, with the property attributes each member always had. What
/// has <em>not</em> moved is the entry point: the six operations are installed by
/// <c>DomBridge/JsObjects.cs</c>, <c>JsObjects.NonElementNodes.cs</c>,
/// <c>DomBridge/HtmlElementInterface.cs</c> and <c>DomBridge/EventTargetInterface.cs</c>, none of
/// which this migration round owns, so an argument frame still arrives as an engine one and each
/// signature here is the adapter that keeps those four call sites compiling.
/// </para>
/// <para>
/// Consequently the two argument reads stay as they were — <c>a[0].ToString()</c> is the observable
/// ECMAScript coercion of the event-type argument, unchanged — and the built event is cast back to
/// the engine's object at the one point it is dispatched. The cast costs nothing: a JSEAL handle
/// carries the engine's own object.
/// </para>
/// </remarks>
internal static class EventTargetBinding
{
    public static JSValue AddEventListener(IEventTargetHost host, DomNode element, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        if (!host.GetEventListeners(element).TryGetValue(type, out var listeners))
        {
            listeners = [];
            host.GetEventListeners(element)[type] = listeners;
        }

        EventListenerBinding.AddListener(listeners, a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    public static JSValue RemoveEventListener(IEventTargetHost host, DomNode element, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        EventListenerBinding.RemoveListener(
            host.GetEventListeners(element).TryGetValue(type, out var listeners) ? listeners : null,
            a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    public static JSValue DispatchEvent(IEventTargetHost host, DomNode element, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.True;
        if (a[0] is not JSObject evt)
            return JSBoolean.True;
        return host.DispatchEventOnElement(element, evt);
    }

    public static JSValue Click(IEventTargetHost host, DomElement element, in Arguments _)
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

        return JSUndefined.Value;
    }

    public static JSValue Focus(IEventTargetHost host, DomElement element, in Arguments _)
        => DispatchSyntheticFocusEvent(host, element, "focus");

    public static JSValue Blur(IEventTargetHost host, DomElement element, in Arguments _)
        => DispatchSyntheticFocusEvent(host, element, "blur");

    private static JSValue DispatchSyntheticFocusEvent(IEventTargetHost host, DomElement element, string type)
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
        return JSUndefined.Value;
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
    /// Hands a synthetic event to the propagation engine.
    /// </summary>
    /// <remarks>
    /// The engine-typed seam: <see cref="IEventTargetHost.DispatchEventOnElement"/> still takes the
    /// engine's object because the page-supplied event of <c>dispatchEvent</c> arrives from an
    /// unmigrated call frame. <c>ToEngineObject</c> is a cast over the object this handle already
    /// carries, so the listeners see the same object.
    /// </remarks>
    private static void Dispatch(IEventTargetHost host, DomNode target, JsValue evt) =>
        host.DispatchEventOnElement(target, JsInterop.ToEngineObject(evt));
}
