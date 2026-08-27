using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
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
    public static JSObject Build()
    {
        var console = new JSObject();

        console.FastAddValue(
            "log",
            new DomFunction(Log, "log"),
            JSPropertyAttributes.EnumerableConfigurableValue);

        console.FastAddValue(
            "warn",
            new DomFunction(Warn, "warn"),
            JSPropertyAttributes.EnumerableConfigurableValue);

        console.FastAddValue(
            "error",
            new DomFunction(Error, "error"),
            JSPropertyAttributes.EnumerableConfigurableValue);

        console.FastAddValue(
            "info",
            new DomFunction(Info, "info"),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return console;
    }

    private static JSValue Log(in Arguments a)
    {
        RenderLogger.LogDebug(LogCategory.JavaScript, "console.log", Format(a));
        return JSUndefined.Value;
    }

    private static JSValue Warn(in Arguments a)
    {
        RenderLogger.Log(LogCategory.JavaScript, LogLevel.Warning, "console.warn", Format(a));
        return JSUndefined.Value;
    }

    private static JSValue Error(in Arguments a)
    {
        RenderLogger.Log(LogCategory.JavaScript, LogLevel.Error, "console.error", Format(a));
        return JSUndefined.Value;
    }

    private static JSValue Info(in Arguments a)
    {
        RenderLogger.LogDebug(LogCategory.JavaScript, "console.info", Format(a));
        return JSUndefined.Value;
    }

    /// <summary>Joins the call arguments with spaces, rendering a missing/undefined value as
    /// the literal <c>"undefined"</c> — the shared formatting the four sinks used identically.</summary>
    private static string Format(in Arguments a)
    {
        var parts = new List<string>(a.Length);
        for (var i = 0; i < a.Length; i++)
            parts.Add(a[i]?.ToString() ?? "undefined");
        return string.Join(" ", parts);
    }
}
