using System.Runtime.CompilerServices;


namespace Broiler.Browser.Core.Tests;

/// <summary>
/// Names the Broiler.VM JSEAL provider so this build's conformance run includes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is compiled only under the configurations that link Broiler.VM</b>, which is what
/// its conditional <c>Compile</c> item in the project file says and what keeps every other build
/// free of the engine.
/// </para>
/// <para>
/// <b>It exists because a provider registers itself and nothing loads it.</b> The provider carries a
/// module initializer, which the CLR runs when it first loads that assembly - and it loads an
/// assembly when a type in it is first touched. <c>JsealConformanceTests</c> touches no engine type
/// by design, which is the property that makes it a conformance suite, so without something naming
/// the provider the registry would enumerate one engine and the suite would silently test half of
/// what this build contains. That is the failure mode worth the file: not a red test, but a green
/// one that never ran.
/// </para>
/// <para>
/// <b>It names the hosting type rather than the provider, so the ordering lives in one place.</b>
/// <c>VmJsealHosting</c> is what a browser build uses too, and it registers the VM provider second
/// on purpose - the registry makes the first registration the process default, and the VM provider
/// declares less than <c>Document</c>. Naming it here rather than the provider means the test build
/// and the browser build agree about ordering because they run the same code, not because two
/// places were written to match.
/// </para>
/// </remarks>
internal static class VmProviderRegistration
{
    [ModuleInitializer]
    internal static void Register()
    {
        Broiler.HtmlBridge.VmJsealHosting.EnsureRegistered();
    }
}
