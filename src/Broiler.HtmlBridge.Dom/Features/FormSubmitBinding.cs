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
/// <c>preventDefault()</c> the default action is suppressed, and otherwise the submission is handed
/// to the host. The default action used to be nothing at all, which made <c>preventDefault()</c> a
/// no-op cancelling a no-op; it is now the difference between the form going and staying.
/// <para>
/// The bridge names the form and resolves its <c>action</c>, and the host builds the data set —
/// serializing a form is something it already does for keyboard and mouse submissions, and doing it
/// a second time here is how the two would drift.
/// </para>
/// The listener store and the submission handover are the
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

            // preventDefault() on the synthetic event suppresses the default action, which is now a
            // real one: the submission is handed to the host rather than dropped, so a listener that
            // cancels has to be able to stop it going.
            if (prevented)
            {
                RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.submit", "Default action prevented");
            }
            else
            {
                host.RequestFormSubmission(element);
            }
        }

        return JSUndefined.Value;
    }
}
