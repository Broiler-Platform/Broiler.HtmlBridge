using System.Linq;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>form.submit()</c> action, registered on every element wrapper, co-located as an HtmlBridge
/// feature module (Phase 3). On a <c>&lt;form&gt;</c> it builds a synthetic cancelable <c>submit</c>
/// event and fires the form's registered <c>submit</c> listeners; if a listener calls
/// <c>preventDefault()</c> the default action is suppressed (this engine does not navigate on submit, so
/// the prevention is logged). The listener store is read through the one-member
/// <see cref="IFormSubmitHost"/> contract; the no-op function factory, the listener invoker and the
/// render logger are the bridge's static helpers, called directly. Was the bridge's
/// <c>JsJsObjectsSubmit125Core</c>.
/// </summary>
internal static class FormSubmitBinding
{
    public static JSValue Submit(IFormSubmitHost host, DomElement element, JSObject? obj, in Arguments a)
    {
        if (string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
        {
            // Fire submit event
            var submitEvt = new JSObject();
            submitEvt.FastAddValue("type", new JSString("submit"), JSPropertyAttributes.EnumerableConfigurableValue);
            submitEvt.FastAddValue("target", obj, JSPropertyAttributes.EnumerableConfigurableValue);
            submitEvt.FastAddValue("bubbles", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
            submitEvt.FastAddValue("cancelable", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
            var prevented = false;
            submitEvt.FastAddValue("defaultPrevented", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
            JSValue PreventDefault(in Arguments _)
            {
                prevented = true;
                submitEvt[(KeyString)"defaultPrevented"] = JSBoolean.True;
                return JSUndefined.Value;
            }

            submitEvt.FastAddValue("preventDefault", new DomFunction(PreventDefault, "preventDefault", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            submitEvt.FastAddValue("stopPropagation", DomBridge.UndefinedFunction("stopPropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            if (host.GetEventListeners(element).TryGetValue("submit", out var submitListeners))
            {
                foreach (var registration in submitListeners.ToList())
                {
                    DomBridge.InvokeEventListener(registration.Listener, submitEvt, "DomBridge.submit");
                }
            }

            // If preventDefault was called, do not proceed with default action
            if (prevented)
            {
                RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.submit", "Default action prevented");
            }
        }

        return JSUndefined.Value;
    }
}
