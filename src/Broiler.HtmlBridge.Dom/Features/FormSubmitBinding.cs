using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>form.submit()</c> action, registered on every element wrapper, co-located as an HtmlBridge
/// feature module. On a <c>&lt;form&gt;</c> it builds a synthetic cancelable <c>submit</c>
/// event and fires the form's registered <c>submit</c> listeners; if a listener calls
/// <c>preventDefault()</c> the default action is suppressed, and otherwise the submission is handed
/// to the host — so <c>preventDefault()</c> is the difference between the form going and staying.
/// <para>
/// The bridge names the form and resolves its <c>action</c>, and the host builds the data set —
/// serializing a form is something it already does for keyboard and mouse submissions, and doing it
/// a second time here is how the two would drift.
/// </para>
/// The listener store and the submission handover are the
/// <see cref="IFormSubmitHost"/> contract; the listener invoker and the render logger are the
/// bridge's static helpers, called directly.
/// </summary>
internal static class FormSubmitBinding
{
    /// <summary>
    /// <c>submit()</c> on an element wrapper. A no-op on anything that is not a <c>&lt;form&gt;</c>,
    /// which is what it has always been.
    /// </summary>
    /// <param name="host">The listener store and the submission handover.</param>
    /// <param name="element">The element the member was installed for.</param>
    /// <param name="target">
    /// That element's wrapper — the synthetic event's <c>target</c>, so a listener reading
    /// <c>event.target</c> gets the same object the page holds.
    /// </param>
    /// <param name="call">The call frame, for the realm the event and its methods are built in.</param>
    public static JsValue Submit(IFormSubmitHost host, DomElement element, JsValue target, in JsCall call)
    {
        if (string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
        {
            var realm = call.Realm;

            // Fire submit event
            var submitEvt = realm.NewObject();
            realm.DefineValue(submitEvt, "type", JsValue.String("submit"));
            realm.DefineValue(submitEvt, "target", target);
            realm.DefineValue(submitEvt, "bubbles", JsValue.True);
            realm.DefineValue(submitEvt, "cancelable", JsValue.True);
            var prevented = false;
            realm.DefineValue(submitEvt, "defaultPrevented", JsValue.False);
            JsValue PreventDefault(in JsCall _)
            {
                prevented = true;
                realm.SetProperty(submitEvt, "defaultPrevented", JsValue.True);
                return JsValue.Undefined;
            }

            realm.DefineMethod(submitEvt, "preventDefault", 0, PreventDefault);

            // stopPropagation takes the non-constructable shape WebIDL gives an operation, matching
            // preventDefault above. It was minted as a constructor for a long time, which gave it a
            // reachable prototype object and made `new evt.stopPropagation()` succeed.
            realm.DefineMethod(submitEvt, "stopPropagation", 0, static (in _) => JsValue.Undefined);

            if (host.GetEventListeners(element).TryGetValue("submit", out var submitListeners))
            {
                // The listener invoker is the bridge's, and deliberately reached rather than replaced:
                // InvokeEventListener is the one place a listener turn is bracketed for JsEntryTrace,
                // resolves the handleEvent form of a listener object, and swallows a listener's
                // exception into a warning. It calls through the realm itself, so a realm call here
                // would buy nothing and quietly drop all three.
                var immediateStopped = false;
                var currentListenerPassive = false;
                EventListenerBinding.InvokeListeners(submitListeners,
                    listener => DomBridgeUtils.InvokeEventListener(realm, listener, submitEvt, "DomBridge.submit"),
                    ref immediateStopped, ref currentListenerPassive);
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

        return JsValue.Undefined;
    }
}
