using Broiler.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

// Explicit IScriptInsertionHost implementation for the ScriptInsertionRunner (the runtime owner of
// script-inserted <script> execution): the bridge exposes the watched document, the page URL and
// policy, JS evaluation, the event-loop queue and element event dispatch via explicit interface
// members, so the runner never reaches an arbitrary bridge private field and the public surface is
// unchanged.
public sealed partial class DomBridge : Dom.Runtime.IScriptInsertionHost
{
    DomDocument Dom.Runtime.IScriptInsertionHost.Document => _document;

    bool Dom.Runtime.IScriptInsertionHost.HasJsContext => _jsContext is not null;

    string Dom.Runtime.IScriptInsertionHost.PageUrl => _pageUrl;

    ContentSecurityPolicy? Dom.Runtime.IScriptInsertionHost.Csp => Csp;

    bool Dom.Runtime.IScriptInsertionHost.MutationDeliverySuppressed => _mutationDeliverySuppressionDepth > 0;

    void Dom.Runtime.IScriptInsertionHost.QueueTask(Action task) => _eventLoop.QueueTask(task);

    void Dom.Runtime.IScriptInsertionHost.EvaluateScript(string source, string label) =>
        _jsContext?.Eval(source, label);

    string Dom.Runtime.IScriptInsertionHost.TextContentOf(DomElement element) => GetTextContentRecursive(element);

    void Dom.Runtime.IScriptInsertionHost.FireSimpleEvent(DomElement target, string type)
    {
        if (_jsContext is null)
            return;

        try
        {
            // Materialise the wrapper first: that is what compiles an inline on* attribute into a
            // listener, so <script src=… onload=…> is covered as well as an assigned .onload and an
            // addEventListener registration — all three land on the one dispatch path.
            ToJSObject(target);
            var evt = new JSObject();
            evt.FastAddValue("type", new JSString(type), JSPropertyAttributes.EnumerableConfigurableValue);
            evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
            DispatchEventOnElement(target, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireScriptEvent",
                $"Error firing '{type}' at a script-inserted <script>: {ex.Message}", ex);
        }
    }
}
