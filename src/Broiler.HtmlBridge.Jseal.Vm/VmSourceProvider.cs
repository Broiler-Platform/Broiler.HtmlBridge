using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;

namespace Broiler.HtmlBridge.Jseal.Vm;

/// <summary>
/// The artifact provider that turns a source text into something the realm can run, and the one
/// place this provider decides whether the page is allowed to ask.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host script and guest source reach the profile through the same door, and telling them apart
/// is this type's whole job.</b> The Broiler.VM profile carries an evaluation request as the source
/// text and nothing else - deliberately, because what a specifier means is a language concept and a
/// provider that could see the difference might be tempted to act on it. That leaves an embedder
/// with no way to say "this evaluation is mine, not the page's", and the distinction is exactly
/// what a Content-Security-Policy is about: a policy that forbids <c>eval</c> does not forbid the
/// bridge's own polyfills.
/// </para>
/// <para>
/// <b>So the provider marks its own evaluations rather than asking the profile to.</b>
/// <see cref="EnterHostScript"/> is taken around an evaluation this repository authored, and a
/// request arriving outside one is the page's. It is a plain field rather than anything thread-aware
/// because everything it guards happens inside one step, on the guest's own thread, with the
/// instance's own lifecycle refusing any second entry - so there is no second caller to race with.
/// </para>
/// <para>
/// <b>This is a provider-side answer to a profile-side gap, and it is worth saying which.</b> If the
/// profile ever carries the distinction itself, this becomes a mark the profile makes and the
/// refusal moves where refusals belong.
/// </para>
/// </remarks>
internal sealed class VmSourceProvider : IVmArtifactProvider
{
    private readonly bool _allowGuestEval;

    private int _hostScriptDepth;

    internal VmSourceProvider(bool allowGuestEval) => _allowGuestEval = allowGuestEval;

    /// <inheritdoc />
    public VmCapabilityId CapabilityId => JavaScriptProfile.SourceProviderCapability.CapabilityId;

    /// <inheritdoc />
    public int Version => JavaScriptProfile.SourceProviderCapability.Version;

    /// <summary>Marks the evaluations this repository authored, for as long as it is held.</summary>
    internal HostScriptScope EnterHostScript() => new(this);

    /// <inheritdoc />
    public VmArtifactProviderAnswer Answer(scoped in VmArtifactRequest request)
    {
        if (request.RequestingProfileId != JavaScriptProfile.Id)
            return VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);

        // THE POLICY, AND IT IS A REFUSAL RATHER THAN AN ABSENCE. A realm built without guest
        // evaluation still has an artifact provider registered, because the bridge's own script has
        // to reach one; what the page gets instead is a provider that declines its requests, which
        // the profile reports as a run-time error the page may catch.
        if (_hostScriptDepth == 0 && !_allowGuestEval)
            return VmArtifactProviderAnswer.Refused(VmReason.ProviderRefused);

        string source;

        try
        {
            source = System.Text.Encoding.UTF8.GetString(request.RequestPayload.Span);
        }
        catch (ArgumentException)
        {
            return VmArtifactProviderAnswer.Refused(VmReason.MalformedEncoding);
        }

        var compiled = JsCompiler.Compile(
            [new JsScriptUnit("main", source, SliceParseOptions.Script)]);

        if (!compiled.Succeeded || compiled.Artifact is null)
            return VmArtifactProviderAnswer.Refused(VmReason.SemanticValidationFailed);

        var descriptor = new VmArtifactDescriptor(
            JavaScriptProfile.Id,
            Broiler.VM.Profile.JavaScript.Format.JsFormat.FormatVersion,
            JavaScriptProfile.WideManifest,
            default,
            VmCallerIdentity.FromCanonicalIdentity("broiler-jseal-vm://source-provider"));

        return VmArtifactProviderAnswer.Provided(in descriptor, compiled.Artifact);
    }

    /// <summary>Holds the host-script mark for the duration of one evaluation.</summary>
    internal readonly struct HostScriptScope : IDisposable
    {
        private readonly VmSourceProvider _provider;

        internal HostScriptScope(VmSourceProvider provider)
        {
            _provider = provider;
            provider._hostScriptDepth++;
        }

        /// <inheritdoc />
        public void Dispose() => _provider._hostScriptDepth--;
    }
}
