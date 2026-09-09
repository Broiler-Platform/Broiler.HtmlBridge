using System.Runtime.CompilerServices;

using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Jseal.BroilerJs;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

/// <summary>
/// Makes the JavaScript engines this build linked available to <see cref="JsEngineRegistry"/>, and
/// tells them the things only a host knows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why registration is not left to the provider's own module initializer.</b> The provider has one,
/// and it is correct — but a module initializer runs when the CLR first loads the assembly, and the
/// CLR loads an assembly when a type in it is first touched. A build that references the provider but
/// never names one of its types may never load it, and then <c>DomBridge</c> would ask an empty
/// registry to adopt a realm and fail at the first page. Naming the type here is what makes the
/// reference real.
/// </para>
/// <para>
/// <b>The module-support answer travels in the same direction.</b> Whether Broiler.JS binds a static
/// ES-module import is not something the provider can assert — it depends on which submodule commit is
/// checked out, and finding out costs a probe on a worker thread behind a five-second timeout because
/// an unpatched engine <em>hangs</em> rather than failing. The bridge already pays for that probe
/// exactly once per process, in <see cref="EngineModuleSupport"/>, so the answer is published to the
/// provider from here rather than discovered twice.
/// </para>
/// </remarks>
public static class JsEngineHosting
{
    private static int _initialized;

    /// <summary>
    /// Registers every engine provider this build linked. Idempotent, and safe to call from any
    /// thread; the first call wins and the rest return immediately.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        BroilerJsEngineProvider.ModuleSupport = static () => EngineModuleSupport.Available;
        BroilerJsEngineProvider.Register();
    }

    /// <summary>
    /// Runs <see cref="EnsureRegistered"/> when this assembly loads, so a host that only ever
    /// constructs a <see cref="ScriptEngine"/> gets a registry with an engine in it.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() => EnsureRegistered();
}
