using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

public sealed partial class ScriptEngine
{
    private JSValue JsScriptEngineQueueMicrotask001Core(in Arguments a)
            {
                if (a.Length == 0 || a[0] is not JSFunction fn)
                    throw JSEngine.NewTypeError("Callback must be a function");
                MicroTasks.Enqueue(() =>
                {
                    try
                    {
                        fn.InvokeFunction(new Arguments(JSUndefined.Value));
                    }
                    catch (Exception ex)
                    {
                        RenderLogger.LogError(LogCategory.JavaScript, "ScriptEngine.queueMicrotask", $"Callback error: {ex.Message}", ex);
                    }
                });
                return JSUndefined.Value;
            }

    private JSValue JsScriptEngineEval002Core(in Arguments _)
    {
        throw new InvalidOperationException("Refused to evaluate a string as JavaScript because 'unsafe-eval' is not an allowed source in the Content Security Policy.");
    }
}
