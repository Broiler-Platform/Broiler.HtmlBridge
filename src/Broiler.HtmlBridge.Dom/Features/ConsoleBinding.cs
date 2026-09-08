using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>console</c> object (<c>log</c>/<c>warn</c>/<c>error</c>/<c>info</c>), co-located
/// with its callbacks as an HtmlBridge feature module (Phase 3). It formats its arguments and
/// routes them to <see cref="RenderLogger"/>, touching no bridge instance state, so — like
/// <c>ClassListBinding</c> — it is a pure static class with no host contract. Previously split
/// between the bridge's <c>BuildConsoleObject</c> (Registration/Console.cs) and four numbered
/// callbacks buried in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class ConsoleBinding
{
    /// <summary>
    /// Builds a <c>console</c> object exposing <c>log</c>, <c>warn</c>, <c>error</c>, and
    /// <c>info</c>. The same object is shared between <c>window.console</c> and the global
    /// <c>console</c>.
    /// </summary>
    /// <param name="realm">The realm the object and its four methods belong to.</param>
    public static JsValue Build(IJsRealm realm)
    {
        var console = realm.NewObject();

        realm.DefineValue(console, "log", realm.NewMethod("log", Log));
        realm.DefineValue(console, "warn", realm.NewMethod("warn", Warn));
        realm.DefineValue(console, "error", realm.NewMethod("error", Error));
        realm.DefineValue(console, "info", realm.NewMethod("info", Info));

        return console;
    }

    private static JsValue Log(in JsCall call)
    {
        RenderLogger.LogDebug(LogCategory.JavaScript, "console.log", Format(in call));
        return JsValue.Undefined;
    }

    private static JsValue Warn(in JsCall call)
    {
        RenderLogger.Log(LogCategory.JavaScript, LogLevel.Warning, "console.warn", Format(in call));
        return JsValue.Undefined;
    }

    private static JsValue Error(in JsCall call)
    {
        RenderLogger.Log(LogCategory.JavaScript, LogLevel.Error, "console.error", Format(in call));
        return JsValue.Undefined;
    }

    private static JsValue Info(in JsCall call)
    {
        RenderLogger.LogDebug(LogCategory.JavaScript, "console.info", Format(in call));
        return JsValue.Undefined;
    }

    /// <summary>Joins the call arguments with spaces, rendering a missing/undefined value as
    /// the literal <c>"undefined"</c> — the shared formatting the four sinks used identically.</summary>
    /// <remarks>
    /// The rendering is the realm's <c>ToString</c>, not the handle's: logging an object has always
    /// shown what the object's own <c>toString</c> says, and a page that gives one a <c>toString</c>
    /// is entitled to see it in the log line rather than <c>[object]</c>. That does mean a
    /// <c>console.log</c> can run page script — which was true before this migration too, because the
    /// engine's own <c>ToString()</c> on a value <em>is</em> that coercion, and this line called it.
    /// </remarks>
    private static string Format(in JsCall call)
    {
        var parts = new List<string>(call.Length);
        for (var i = 0; i < call.Length; i++)
        {
            var argument = call[i];

            // An index past the end answered CLR null before and rendered "undefined"; it is Missing
            // now, and the same literal is the answer. The loop cannot reach one — it stops at
            // Length — but the guard is what keeps the coercion off a value that never came from the
            // engine.
            parts.Add(argument.IsMissing ? "undefined" : call.Realm.ToJsString(argument));
        }

        return string.Join(" ", parts);
    }
}
