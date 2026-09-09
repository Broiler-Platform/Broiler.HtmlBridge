using System.Collections.Immutable;
using System.Runtime.CompilerServices;

using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.HtmlBridge.Jseal.Vm;

/// <summary>
/// The Broiler.VM JavaScript profile, as an engine the browser can choose.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it declares is narrower than <see cref="JsCapabilities.Document"/>, and the gap is
/// stated rather than papered over.</b> The profile's host surface can mint objects, install
/// members and accessors, call back into the guest synchronously, and complete a lookup for an
/// object whose members are not a fixed list - which is most of what binding a document needs. What
/// it has no seam for is a promise a host can settle from outside the guest, and
/// <see cref="JsCapabilities.Promises"/> is one of the five bits <c>Document</c> is made of. So a
/// host branching on <c>Document</c> correctly declines to load a page on this engine today, and
/// gets an honest answer rather than a page that half works.
/// </para>
/// <para>
/// <b>Every realm is its own runtime, artifact and instance, and that is the profile's shape rather
/// than a choice made here.</b> A Broiler.VM realm belongs to an instance, an instance to a
/// verified artifact, and an artifact to a runtime whose capability table was fixed when it was
/// created - so a realm built with a different content policy is a different runtime, and there is
/// no lighter object to make one out of.
/// </para>
/// </remarks>
public sealed class VmEngineProvider : IJsEngineProvider
{
    /// <summary>The caller identity this provider verifies its artifacts under.</summary>
    private const string Caller = "broiler-jseal-vm://realm";

    /// <inheritdoc />
    public string Name => "broiler-vm";

    /// <inheritdoc />
    public string Description =>
        "The Broiler.VM JavaScript profile, bound through its in-realm host surface.";

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <see cref="JsCapabilities.ReentrantHostCalls"/> is declarable because a host object's method
    /// is an ordinary function in the realm: calling a guest listener from inside one is the
    /// interpreter's own call path and reaches no lifecycle gate.
    /// <see cref="JsCapabilities.GlobalIsVariableScope"/> is declarable because a top-level
    /// <c>var</c> on this engine becomes an own property of the global object, which was measured
    /// rather than assumed.
    /// </para>
    /// <para>
    /// <see cref="JsCapabilities.Promises"/>, <see cref="JsCapabilities.WorkerRealms"/>,
    /// <see cref="JsCapabilities.Modules"/> and <see cref="JsCapabilities.DynamicImport"/> are
    /// absent, and each absence is a member of this provider that refuses rather than a member that
    /// misbehaves.
    /// </para>
    /// </remarks>
    public JsCapabilities Capabilities =>
        JsCapabilities.HostScriptSource |
        JsCapabilities.GuestEval |
        JsCapabilities.ExoticObjects |
        JsCapabilities.GlobalIsVariableScope |
        JsCapabilities.ReentrantHostCalls;

    /// <inheritdoc />
    public IJsRealm CreateRealm(JsRealmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var bridge = new VmHostBridge();
        var sources = new VmSourceProvider(options.AllowGuestEval);

        var catalog = VmCatalog.CreateBuilder()
            .Add(JavaScriptProfile.DescriptorHostingRealms(bridge))
            .Build();

        var created = VmRuntime.Create(catalog, Options(sources));

        if (!created.TryGetRuntime(out var runtime))
        {
            throw new JsEngineException(
                $"the Broiler.VM runtime refused creation: {created.Outcome}/{created.Reason}");
        }

        try
        {
            // THE REALM NEEDS AN INSTANCE AND AN INSTANCE NEEDS AN ARTIFACT, so the smallest legal
            // program is compiled to make one. Nothing in it runs: what matters is that
            // instantiating it is the moment the profile builds a realm and hands it over.
            var compiled = JsCompiler.Compile(
                [new JsScriptUnit("bootstrap", string.Empty, ParseOptions(options), options.ForceStrictMode, Caller)],
                [],
                new JsCompileRequest());

            if (!compiled.Succeeded || compiled.Artifact is null)
                throw new JsEngineException("the Broiler.VM front end refused an empty program");

            var descriptor = new VmArtifactDescriptor(
                JavaScriptProfile.Id,
                Broiler.VM.Profile.JavaScript.Format.JsFormat.FormatVersion,
                JavaScriptProfile.WideManifest,
                default,
                VmCallerIdentity.FromCanonicalIdentity(Caller));

            var verified = runtime.Verify(in descriptor, compiled.Artifact, CancellationToken.None);

            if (!verified.TryGetArtifact(out var artifact))
            {
                throw new JsEngineException(
                    $"the Broiler.VM verifier refused a bootstrap program: "
                        + $"{verified.Outcome}/{verified.Reason}");
            }

            var instantiated = runtime.Instantiate(artifact, CancellationToken.None);

            if (!instantiated.TryGetInstance(out var instance))
            {
                artifact.Dispose();

                throw new JsEngineException(
                    $"the Broiler.VM runtime refused to instantiate: "
                        + $"{instantiated.Outcome}/{instantiated.Reason}");
            }

            if (bridge.Realm is null)
            {
                instance.Dispose();
                artifact.Dispose();

                throw new JsEngineException(
                    "the Broiler.VM instance was created without handing over a realm, which means "
                        + "the host-surface capability was not bound");
            }

            var capabilities = options.AllowGuestEval
                ? Capabilities
                : Capabilities & ~JsCapabilities.GuestEval;

            return new VmRealm(runtime, artifact, instance, bridge, sources, capabilities, Name);
        }
        catch
        {
            runtime.Dispose();
            throw;
        }
    }

    private static SliceParseOptions ParseOptions(JsRealmOptions options) =>
        SliceParseOptions.Script;

    /// <summary>
    /// The runtime this provider builds: the host-surface permission, and a compiler behind
    /// <c>eval</c>.
    /// </summary>
    /// <remarks>
    /// <b>The host-surface capability is registered with a handler that does nothing, and that is
    /// what it is.</b> The profile asks whether the slot is bound and never invokes it; registering
    /// it is a composition saying that this runtime's realms may have host objects in them, which is
    /// the permission the whole seam hangs on.
    /// </remarks>
    private static VmRuntimeCreationOptions Options(VmSourceProvider sources)
    {
        var ceilings = ImmutableArray.CreateBuilder<VmCeilingSpec>();

        foreach (var dimension in VmBudgetDimensions.All)
        {
            ceilings.Add(dimension is VmBudgetDimension.LiveRuntimes
                ? VmCeilingSpec.AdoptParentRemaining(dimension)
                : VmCeilingSpec.AdoptProfileDefault(dimension));
        }

        var capabilities = ImmutableArray.CreateBuilder<VmCapabilityRegistration>();

        capabilities.Add(VmCapabilityRegistration.Value(
            JavaScriptProfile.HostSurfaceCapability,
            static (VmBytes argument, out VmOpaqueRef answer) =>
            {
                answer = default;
                return VmHostCallOutcome.Completed;
            }));

        capabilities.Add(VmCapabilityRegistration.ArtifactProvider(
            JavaScriptProfile.SourceProviderCapability, sources));

        return new VmRuntimeCreationOptions(
            aggregateBudget: null,
            ceilings: ceilings.ToImmutable(),
            maxSuspendedResidency: TimeSpan.FromMinutes(1),
            maxLiveSuspendedOperations: 1,
            guestLoadBounds: VmGuestLoadBoundsSpec.AdoptProfileMaxima,
            externalSuspension: VmExternalSuspensionMode.Disabled,
            capabilities: capabilities.ToImmutable());
    }

    /// <summary>Registers this provider. Idempotent.</summary>
    public static void Register() => JsEngineRegistry.Register(new VmEngineProvider());

    /// <summary>
    /// Registers on assembly load, for a host that reaches this assembly without naming this type.
    /// </summary>
    /// <remarks>
    /// A module initializer runs when the CLR first loads the assembly, and the CLR loads it when a
    /// type in it is first touched - so this is a convenience for a host that touches one, and not a
    /// substitute for a host that names the provider deliberately.
    /// </remarks>
    [ModuleInitializer]
    internal static void AutoRegister() => Register();
}
