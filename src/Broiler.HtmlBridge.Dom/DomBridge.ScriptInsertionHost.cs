using Broiler.Dom;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

// Explicit IScriptInsertionHost implementation for the ScriptInsertionRunner (the runtime owner of
// script-inserted <script> execution): the bridge exposes the watched document, the page URL and
// policy, JS evaluation, the event-loop queue and element event dispatch via explicit interface
// members, so the runner never reaches an arbitrary bridge private field and the public surface is
// unchanged.
//
// The JavaScript vocabulary here is JSEAL's. A script element's program text is the page's own
// source, so it is evaluated through IJsSource.EvaluateGuestSource — the half of the source contract
// a content policy may forbid — rather than the host-script entry point this repository's own
// JavaScript uses. The one engine-typed line left is the dispatch call: DispatchEventOnElement takes
// the engine's object and is another group's file this round, so the event this file builds through
// the realm is unwrapped at that one call, which is a cast rather than a conversion.
public sealed partial class DomBridge : Dom.Runtime.IScriptInsertionHost
{
    DomDocument Dom.Runtime.IScriptInsertionHost.Document => _document;

    bool Dom.Runtime.IScriptInsertionHost.HasRealm => _realm is not null;

    string Dom.Runtime.IScriptInsertionHost.PageUrl => _pageUrl;

    ContentSecurityPolicy? Dom.Runtime.IScriptInsertionHost.Csp => Csp;

    bool Dom.Runtime.IScriptInsertionHost.MutationDeliverySuppressed => _mutationDeliverySuppressionDepth > 0;

    void Dom.Runtime.IScriptInsertionHost.QueueTask(Action task) => _eventLoop.QueueTask(task);

    void Dom.Runtime.IScriptInsertionHost.EvaluateScript(string source, string label)
    {
        // A script body is a turn too, and the one most likely to be the long pole at load; see
        // JsEntryTrace. Inactive by default.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Script, label);
        _realm?.EvaluateGuestSource(source, label);
    }

    string Dom.Runtime.IScriptInsertionHost.TextContentOf(DomElement element) => GetTextContentRecursive(element);

    void Dom.Runtime.IScriptInsertionHost.FireSimpleEvent(DomElement target, string type)
    {
        if (_realm is not { } realm)
            return;

        try
        {
            // Materialise the wrapper first: that is what compiles an inline on* attribute into a
            // listener, so <script src=… onload=…> is covered as well as an assigned .onload and an
            // addEventListener registration — all three land on the one dispatch path.
            ToJSObject(target);
            var evt = realm.NewObject();
            realm.DefineValue(evt, "type", JsValue.String(type));
            realm.DefineValue(evt, "bubbles", JsValue.False);
            DispatchEventOnElement(target, Dom.Runtime.JsInterop.ToEngineObject(evt));
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireScriptEvent",
                $"Error firing '{type}' at a script-inserted <script>: {ex.Message}", ex);
        }
    }
}
