using System.Reflection;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Freezes the public API surface of every assembly this component publishes, so a change to what
/// a consumer compiles against is a deliberate one. The approved surface of each is the committed
/// baseline in <c>tests/Broiler.HtmlBridge.Tests/ApiBaselines/</c>.
/// </summary>
/// <remarks>
/// <para>
/// When a change to the public surface is intentional, regenerate the baselines by running the suite
/// with the environment variable <c>UPDATE_API_BASELINES=1</c>, and commit the updated files with the
/// change. On a mismatch the surface the build produced is written beside the test assembly as
/// <c>&lt;assembly&gt;.PublicApi.received.txt</c>, to diff against the baseline.
/// </para>
/// <para>
/// This lived in the Broiler aggregate repository until the component was extracted, where it froze
/// a contract nothing here could see and went stale with every change made here. The packages are
/// consumed from nuget.org now, so the surface is this repository's to guard.
/// <c>Broiler.HtmlBridge.Scripting.Vm</c> is not listed: it builds only in the VM configuration.
/// </para>
/// </remarks>
public class PublicApiSnapshotTests
{
    public static IEnumerable<object[]> PublishedAssemblies =>
    [
        ["Broiler.HtmlBridge.Core"],
        ["Broiler.HtmlBridge.Dom"],
        ["Broiler.HtmlBridge.DomBridgeUtils"],
        ["Broiler.HtmlBridge.Scripting"],
    ];

    [Theory]
    [MemberData(nameof(PublishedAssemblies))]
    public void ThePublicApiSurfaceMatchesItsCommittedBaseline(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));
        var actual = Normalize(PublicApiSurface.Describe(assembly));
        var baselinePath = Path.Combine(BaselineDirectory(), assemblyName + ".PublicApi.txt");

        if (ShouldUpdateBaselines)
        {
            File.WriteAllText(baselinePath, actual + "\n");
            return;
        }

        Assert.True(
            File.Exists(baselinePath),
            $"Missing API baseline for {assemblyName} at {baselinePath}. " +
            "Generate it by running the suite with UPDATE_API_BASELINES=1 and commit the file.");

        var expected = Normalize(File.ReadAllText(baselinePath));
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var receivedPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".PublicApi.received.txt");
            File.WriteAllText(receivedPath, actual + "\n");
            Assert.Fail(
                $"The public API surface of {assemblyName} drifted from its committed baseline.\n" +
                $"  baseline: {baselinePath}\n" +
                $"  received: {receivedPath}\n" +
                "If the change is intentional, re-run with UPDATE_API_BASELINES=1 and commit the updated baseline.");
        }
    }

    private static bool ShouldUpdateBaselines =>
        Environment.GetEnvironmentVariable("UPDATE_API_BASELINES") is "1" or "true";

    private static string Normalize(string text) =>
        text.Replace("\r\n", "\n").TrimEnd('\n');

    /// <summary>
    /// The baseline directory, found by walking up from the test assembly to this repository's root
    /// (the folder holding <c>Broiler.HtmlBridge.slnx</c>) — which also finds it when this component
    /// is checked out inside the aggregate repository.
    /// </summary>
    private static string BaselineDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Broiler.HtmlBridge.slnx")))
                return Path.Combine(directory.FullName, "tests", "Broiler.HtmlBridge.Tests", "ApiBaselines");
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root (the folder holding Broiler.HtmlBridge.slnx) above " +
            AppContext.BaseDirectory);
    }
}
