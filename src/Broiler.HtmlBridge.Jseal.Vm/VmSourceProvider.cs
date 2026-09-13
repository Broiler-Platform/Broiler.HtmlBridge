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
    private readonly bool _forceStrictMode;

    private int _hostScriptDepth;

    /// <summary>
    /// Permission for exactly ONE compilation: the classic script a host is handing over.
    /// </summary>
    /// <remarks>
    /// <b>Single-use, and the host-script mark could not be reused for this.</b> That one is a DEPTH
    /// held for the whole evaluation, so every artifact request issued while the marked code RUNS is
    /// permitted. That is right for this repository's own script, which calls nothing of the page's.
    /// It is wrong for a page's script, because a script element whose first statement is an eval is
    /// an everyday page: under <c>script-src 'unsafe-inline'</c> with no <c>'unsafe-eval'</c> the
    /// element must run and the eval must not, and a held mark would permit both. A permit spent by
    /// the compile it authorises is spent before the page's first statement runs.
    /// </remarks>
    private bool _classicScriptPermit;

    internal VmSourceProvider(bool allowGuestEval, bool forceStrictMode)
    {
        _allowGuestEval = allowGuestEval;
        _forceStrictMode = forceStrictMode;
    }

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
        if (_classicScriptPermit)
        {
            // SPENT BEFORE COMPILING, not after. A compile that fails must not leave the permit armed
            // for whatever asks next, and the page's own code -- which runs after this returns -- has
            // to meet a provider with nothing armed.
            _classicScriptPermit = false;
        }
        else if (_hostScriptDepth == 0 && !_allowGuestEval)
        {
            return VmArtifactProviderAnswer.Refused(VmReason.ProviderRefused);
        }

        string source;

        try
        {
            source = System.Text.Encoding.UTF8.GetString(request.RequestPayload.Span);
        }
        catch (ArgumentException)
        {
            return VmArtifactProviderAnswer.Refused(VmReason.MalformedEncoding);
        }

        // FORCED STRICTNESS IS THE HOST'S, AND THE MARK IS WHAT TELLS THEM APART.
        //
        // The realm's ForceStrictMode used to reach only the bootstrap unit (VmEngineProvider), so
        // every compile answered here was sloppy whatever the host asked for -- while the Broiler.JS
        // provider forced it on everything, including the page's own evaluations. Two providers, two
        // wrong answers, in opposite directions, with nothing asking either.
        //
        // The rule both now keep is the specification's, and docs/vm-javascript-profile.md measures
        // it: a host may force its OWN script strict, and may not force what the page evaluates,
        // because an indirect eval evaluates a new script whose strictness comes from its own source.
        // The host-script depth is already the thing that distinguishes the two, so it decides this
        // as well rather than a second flag being threaded alongside it.
        var forceStrict = _hostScriptDepth > 0 && _forceStrictMode;

        var compiled = JsCompiler.Compile(
            [new JsScriptUnit("main", source, SliceParseOptions.Script, forceStrict)]);

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

    /// <summary>Arms the single-use permit for one classic script.</summary>
    internal ClassicScriptScope EnterClassicScript() => new(this);

    /// <summary>
    /// Arms <see cref="_classicScriptPermit"/> for one compilation, and suspends the host-script
    /// depth for the duration.
    /// </summary>
    /// <remarks>
    /// <b>Suspending the depth is not tidiness; it is the second half of the guarantee.</b> Without
    /// it, a classic script handed over while a host script happened to be on the stack would run its
    /// page code under the host's held mark and reach the compiler by that route instead. Suspending
    /// makes the permission exactly one compile REGARDLESS of what the host was doing when it asked,
    /// which is what the member's contract promises. Restoring on dispose keeps nesting honest: a
    /// polyfill invoked from inside a running page script takes its own host mark and gets its depth
    /// back, and a page script that sets an event-handler attribute re-enters with a fresh permit,
    /// which is correct because that compile is its own <c>script-src-attr</c> decision.
    /// </remarks>
    internal readonly struct ClassicScriptScope : IDisposable
    {
        private readonly VmSourceProvider _provider;
        private readonly bool _hadPermit;
        private readonly int _suspendedDepth;

        internal ClassicScriptScope(VmSourceProvider provider)
        {
            _provider = provider;
            _hadPermit = provider._classicScriptPermit;
            _suspendedDepth = provider._hostScriptDepth;

            provider._classicScriptPermit = true;
            provider._hostScriptDepth = 0;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _provider._classicScriptPermit = _hadPermit;
            _provider._hostScriptDepth = _suspendedDepth;
        }
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
