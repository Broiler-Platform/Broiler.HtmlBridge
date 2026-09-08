using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;
using Broiler.VM.Profile.JavaScript.Format;

namespace Broiler.HtmlBridge;

/// <summary>
/// This engine's answer to a guest-initiated load: <c>eval</c>, <c>new Function</c>, and the module
/// a dynamic <c>import()</c> names.
/// </summary>
/// <remarks>
/// <para>
/// <b>REGISTERING THIS IS THE PERMISSION, AND NOT REGISTERING IT IS THE CONTENT POLICY.</b> The
/// profile cannot turn a string into bytes on its own — a profile that could would carry a compiler
/// in every image whether or not the composition wanted one, and no registration could take it
/// away. So a page whose CSP forbids <c>eval</c> is served by a runtime with no provider, and every
/// guest-initiated load is refused by the core, deterministically, as a contract outcome the guest
/// may catch. That is a policy expressed in the shape of the composition rather than as a check
/// somewhere inside an engine. <see cref="VmScriptEngine.RuntimeOptions"/> is where the decision
/// is taken.
/// </para>
/// <para>
/// <b>Two questions arrive through one door and the payload says which.</b> <c>eval</c> and the
/// <c>Function</c> constructor ask for the program a string is; a dynamic <c>import()</c> asks for
/// the module a specifier names from a referrer, and marks its payload so. They are two
/// compilations of two different goals, and folding them together would make a module graph out of
/// whatever text a page happened to pass to <c>eval</c>.
/// </para>
/// <para>
/// <b>A refusal is an answer, not an exception.</b> A provider that threw would be a broken host
/// and the core would report a host fault; one that declines a program the front end will not admit
/// is a working host saying so, and the guest sees a rejected promise or a thrown error carrying
/// the reason.
/// </para>
/// </remarks>
internal sealed class VmSourceProvider : IVmArtifactProvider
{
    private const string Caller = "broiler-browser://htmlbridge/source-provider";

    private readonly VmModuleMap _modules;
    private readonly JsCompileRequest _request;
    private readonly VmCompilationCache _cache;

    internal VmSourceProvider(VmModuleMap modules, JsCompileRequest request, VmCompilationCache cache)
    {
        _modules = modules;
        _request = request;
        _cache = cache;
    }

    /// <summary>The identity this provider is registered under.</summary>
    public VmCapabilityId CapabilityId => JavaScriptProfile.SourceProviderCapability.CapabilityId;

    /// <summary>Its exact version.</summary>
    public int Version => JavaScriptProfile.SourceProviderCapability.Version;

    /// <summary>Answers one request by compiling what it names.</summary>
    public VmArtifactProviderAnswer Answer(scoped in VmArtifactRequest request)
    {
        // A provider may only answer with an artifact of the requesting profile, so a request from
        // another one is not this provider's to answer at all.
        if (request.RequestingProfileId != JavaScriptProfile.Id)
            return VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);

        if (JsFormat.TryReadModuleRequest(request.RequestPayload.Span, out var referrer, out var specifier))
            return Module(referrer, specifier);

        string source;

        try
        {
            source = System.Text.Encoding.UTF8.GetString(request.RequestPayload.Span);
        }
        catch (ArgumentException)
        {
            return VmArtifactProviderAnswer.Refused(VmReason.MalformedEncoding);
        }

        // THROUGH THE CACHE, which for guest-supplied source is this ENGINE's and not the shared
        // one. A page that evaluates one body from a loop compiles it once; a page cannot evict
        // another page's compiled documents, and cannot time whether another page evaluated a
        // string. VmScriptEngine.GuestLoadCache says why that scope rather than the wider one.
        // NO ForceStrict HERE, AND THAT IS DELIBERATE RATHER THAN AN OVERSIGHT. The engine passes
        // StrictModeEnabled to the compiler for the DOCUMENT's scripts, and not for this one — so a
        // strict-mode page gets strict scripts and a sloppy eval, which reads like an inconsistency
        // until you check the other engine. Broiler.JS does exactly the same: its PrepareSource
        // prepends the directive to the scripts it runs and leaves the native eval untouched. The
        // agreement is what matters, and it is also what the specification says, because an
        // indirect eval evaluates a new script whose strictness comes from its own source. Forcing
        // it here would make one page behave two ways depending on which engine ran it.
        // StrictModeReachesDocumentScriptsAndNotEval pins both engines against each other.
        return Compiled(_cache.GetOrCompile(
            [new JsScriptUnit("main", source, SliceParseOptions.Script)], [], _request));
    }

    /// <summary>Answers a request for the module one specifier names from one referrer.</summary>
    /// <remarks>
    /// NOT FOUND when this document declared no such module, REFUSED when it did and the source is
    /// not a program this manifest admits. The two are the answers the core's vocabulary already
    /// has, and they mean here what a reader would expect: "the page never named that" versus
    /// "it did, and it will not compile". A browser fetches nothing on the guest's behalf at this
    /// point — the document's modules were collected when it was parsed — so an unresolvable
    /// specifier is the first answer and not a network error.
    /// </remarks>
    private VmArtifactProviderAnswer Module(string referrer, string specifier)
    {
        if (!_modules.TryResolve(referrer, specifier, out var key))
            return VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);

        var graph = _modules.GraphFrom(key);

        if (graph is null || graph.Count == 0)
            return VmArtifactProviderAnswer.NotFound(VmReason.ProviderArtifactNotFound);

        return Compiled(_cache.GetOrCompile([], graph, _request));
    }

    /// <summary>Turns a compilation into the answer the core's vocabulary has for it.</summary>
    /// <remarks>
    /// The diagnostic itself does not cross the boundary: a provider answers with an artifact or a
    /// reason, and the reason vocabulary is the core's. What the page sees is that its <c>eval</c>
    /// threw, which is what it would see from any engine handed source it cannot compile.
    /// </remarks>
    private static VmArtifactProviderAnswer Compiled(JsCompilation compilation)
    {
        if (!compilation.Succeeded || compilation.Artifact is null)
            return VmArtifactProviderAnswer.Refused(VmReason.SemanticValidationFailed);

        var descriptor = new VmArtifactDescriptor(
            JavaScriptProfile.Id,
            JsFormat.FormatVersion,
            JavaScriptProfile.WideManifest,
            default,
            VmCallerIdentity.FromCanonicalIdentity(Caller));

        return VmArtifactProviderAnswer.Provided(in descriptor, compilation.Artifact);
    }
}
