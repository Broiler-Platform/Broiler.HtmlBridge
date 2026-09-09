using System.Runtime.CompilerServices;

using Broiler.HtmlBridge.Jseal.Vm;

namespace Broiler.HtmlBridge;

/// <summary>
/// Registers the Broiler.VM JSEAL provider in a build that links this assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is here rather than in <c>JsEngineHosting</c> because of what each assembly may
/// reference.</b> <c>JsEngineHosting</c> lives in <c>Broiler.HtmlBridge.Scripting</c>, which every
/// configuration links; a reference from there to the VM provider would drag Broiler.VM into every
/// build, which is the thing the whole conditional-reference arrangement exists to prevent. This
/// assembly is already conditional, so the registration belongs with it.
/// </para>
/// <para>
/// <b>It runs at assembly load, which is when this assembly is first touched.</b> A build that
/// links it does so because <c>BrowserApp</c> constructs a <c>VmScriptEngine</c>, so touching one
/// registers the other - and a build that does not link it registers nothing, which is exactly the
/// state every non-VM configuration should be in.
/// </para>
/// <para>
/// <b>It registers second, and the order decides the default.</b> The registry makes the first
/// registration the process default and Broiler.JS registers from its own module initializer when
/// <c>Broiler.HtmlBridge.Scripting</c> loads - which this assembly references, so that has already
/// happened. Naming <see cref="JsEngineHosting.EnsureRegistered"/> here makes the ordering something
/// this file states rather than something it inherits. The VM provider declares less than
/// <c>Document</c>, so it must not become the default by accident; a host that wants it says so with
/// <c>BROILER_JS_ENGINE=broiler-vm</c>.
/// </para>
/// </remarks>
public static class VmJsealHosting
{
    private static int _initialized;

    /// <summary>Registers the provider. Idempotent, and safe from any thread.</summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
            return;

        JsEngineHosting.EnsureRegistered();
        VmEngineProvider.Register();
    }

    [ModuleInitializer]
    internal static void Initialize() => EnsureRegistered();
}
