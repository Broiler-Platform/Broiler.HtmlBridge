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

    private static JSValue JsScriptEngineWeakRef004Core(in Arguments args)
            {
                if (args.Length == 0)
                    throw new InvalidOperationException("WeakRef requires a target object.");
                var target = args[0];
                var weakRef = new WeakReference<JSValue>(target);
                var instance = new JSObject();
                JSValue JsScriptEngineDeref003(in Arguments _)
                {
                    return weakRef.TryGetTarget(out var t) ? t : JSUndefined.Value;
                }

                instance.FastAddValue("deref", new JSFunction(JsScriptEngineDeref003, "deref", 0), JSPropertyAttributes.EnumerableConfigurableValue);
                return instance;
            }

    private static JSValue JsScriptEngineFinalizationRegistry007Core(in Arguments args)
            {
                // The callback is stored but invocation depends on .NET GC
                var callback = args.Length > 0 ? args[0] as JSFunction : null;
                var instance = new JSObject();
                JSValue JsScriptEngineRegister005(in Arguments regArgs)
                {
                    // No-op in this polyfill; real cleanup requires GC integration
                    return JSUndefined.Value;
                }

                // register(target, heldValue [, unregisterToken])
                instance.FastAddValue("register", new JSFunction(JsScriptEngineRegister005, "register", 3), JSPropertyAttributes.EnumerableConfigurableValue);
                JSValue JsScriptEngineUnregister006(in Arguments unregArgs)
                {
                    return JSBoolean.False;
                }

                // unregister(unregisterToken)
                instance.FastAddValue("unregister", new JSFunction(JsScriptEngineUnregister006, "unregister", 1), JSPropertyAttributes.EnumerableConfigurableValue);
                return instance;
            }

}
