using System;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>EventTarget</c> methods exposed on every node/element wrapper —
/// <c>addEventListener</c>, <c>removeEventListener</c>, <c>dispatchEvent</c>, and the synthetic-event
/// convenience methods <c>click</c>, <c>focus</c> and <c>blur</c>. These were the bridge's
/// <c>JsJsObjectsAddEventListener097Core</c>..<c>Blur103Core</c> callbacks. The registration semantics
/// (option parsing, dedup, match-by-listener+capture) live in <see cref="EventListenerBinding"/> and the
/// capture→target→bubble engine in <see cref="EventDispatchBinding"/>; this module wires the JS-facing
/// methods to them, reaching the per-node listener store, the dispatch engine and the window JS object
/// through <see cref="IEventTargetHost"/>. Node-type/attribute/runtime-state helpers, the radio-group
/// mutual-exclusion walk (<c>UncheckRadioSiblings</c>) and the no-op function factory
/// (<c>UndefinedFunction</c>) are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
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

        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("click"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("cancelable", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("defaultPrevented", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("target", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("currentTarget", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("eventPhase", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("detail", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("stopPropagation", DomBridge.UndefinedFunction("stopPropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("stopImmediatePropagation", DomBridge.UndefinedFunction("stopImmediatePropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("preventDefault", DomBridge.UndefinedFunction("preventDefault", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        host.DispatchEventOnElement(element, evt);
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
                    var submitEvt = new JSObject();
                    submitEvt.FastAddValue("type", new JSString("submit"), JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("bubbles", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("cancelable", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("defaultPrevented", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("target", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("currentTarget", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("eventPhase", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
                    JSValue JsJsObjectsPreventDefault100(in Arguments __)
                    {
                        submitEvt[(KeyString)"defaultPrevented"] = JSBoolean.True;
                        return JSUndefined.Value;
                    }

                    submitEvt.FastAddValue("preventDefault", new DomFunction(JsJsObjectsPreventDefault100, "preventDefault", 0), JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("stopPropagation", DomBridge.UndefinedFunction("stopPropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
                    submitEvt.FastAddValue("stopImmediatePropagation", DomBridge.UndefinedFunction("stopImmediatePropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
                    host.DispatchEventOnElement(form, submitEvt);
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
        var evt = new JSObject();
        evt.FastAddValue("type", new JSString(type), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("cancelable", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("defaultPrevented", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("target", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("currentTarget", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("srcElement", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("eventPhase", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("isTrusted", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("timeStamp", new JSNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("detail", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("view", host.WindowJSObject ?? JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("relatedTarget", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        host.DispatchEventOnElement(element, evt);
        return JSUndefined.Value;
    }
}
