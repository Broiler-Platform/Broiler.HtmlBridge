using System.Runtime.ExceptionServices;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Hands the call back to the function the engine installed, for a receiver this bridge does not
    /// own — <c>new EventTarget()</c>, an <c>AbortSignal</c>, anything else engine-side. Its own
    /// receiver check is what still rejects a receiver that is neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver and every supplied argument go through unchanged: an argument the page did not
    /// pass is not invented, so the engine's own arity checks see the call the page actually made.
    /// </para>
    /// <para>
    /// <b>The rethrow is not tidiness.</b> A realm's <c>Invoke</c> reports what the callee threw as a
    /// JSEAL exception carrying the thrown value, and JSEAL has no "throw this value again" operation
    /// — only "throw a new error of this kind". Letting that wrapper escape into the engine would
    /// hand the page a freshly synthesised <c>Error</c> built from a CLR exception in place of the
    /// engine's own, so <c>EventTarget.prototype.addEventListener.call({}, …)</c> would stop being
    /// catchable as a <c>TypeError</c>. Rethrowing the inner exception with its stack intact keeps
    /// the object the page catches the object the engine threw.
    /// </para>
    /// </remarks>
    internal static JsValue InvokeEngineEventTargetMethod(JsValue engineMethod, string name, in JsCall call)
    {
        if (!engineMethod.IsFunction)
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                $"Failed to execute '{name}' on 'EventTarget': Illegal invocation");

        try
        {
            return call.Realm.Invoke(engineMethod, call.This, call.Arguments);
        }
        catch (JsEngineException wrapped) when (wrapped.InnerException is { } thrownByEngine)
        {
            ExceptionDispatchInfo.Throw(thrownByEngine);
            throw; // Unreachable: ExceptionDispatchInfo.Throw never returns.
        }
    }
}
