using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.VM;
using Broiler.VM.Profile.JavaScript;
using Broiler.VM.Profile.JavaScript.Compiler;
using Broiler.VM.Profile.JavaScript.Format;

namespace Broiler.HtmlBridge;

/// <summary>
/// An <see cref="IScriptEngine"/> that runs script on the Broiler.VM JavaScript profile.
/// Selected by the <c>Debug-VM</c> and <c>Release-VM</c> configurations.
/// </summary>
/// <remarks>
/// <para>
/// <b>IT RUNS THE SCRIPT-ONLY PATHS AND DELEGATES THE DOM-BEARING ONES, AND THAT SPLIT IS THE
/// FIRST THING A READER SHOULD KNOW.</b> <see cref="Execute(IReadOnlyList{string})"/> and
/// <see cref="ExecuteDetailed"/> compile, verify, instantiate and invoke on the VM and touch
/// Broiler.JS nowhere. Every overload that takes <c>html</c>, and
/// <see cref="ExecuteInteractive(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>,
/// is forwarded verbatim to the Broiler.JS engine passed to the constructor.
/// </para>
/// <para>
/// <b>The delegation is a property of the VM's host boundary, not a shortcut taken here.</b> A
/// host capability of the JavaScript profile is a call over opaque bytes —
/// <c>VmHostBytesCapabilityHandler</c> takes a <c>VmBytes</c> and answers a <c>VmOpaqueRef</c> —
/// and a DOM is not a byte string. Broiler.HtmlBridge.Dom projects the document by defining
/// JavaScript objects whose accessors are CLR delegates over live nodes, across 250 files of
/// <c>Broiler.JavaScript</c> types, and there is no surface on the profile through which that
/// could be re-expressed today. <see cref="InteractiveSession"/> settles it independently: its
/// constructor is internal and takes a <c>JSContext</c>, so an engine outside Broiler.JS cannot
/// produce one at all.
/// </para>
/// <para>
/// <b>So Broiler.JS is in the graph under the VM configurations too, and this class does not
/// pretend otherwise.</b> What <c>Debug-VM</c> changes is which engine the browser instantiates
/// and therefore which engine runs a page's script when no document is required. See
/// <c>docs/vm-javascript-profile.md</c>.
/// </para>
/// <para>
/// <b>One artifact, one instance, one realm per call.</b> A document's scripts are compiled into a
/// single artifact with one entry point each and invoked in order against one instance, which is
/// what makes script 1 see script 0's declarations. That is the shape Broiler.VM's own wide host
/// uses and the reason its compiler takes a LIST of scripts rather than a concatenation:
/// concatenating would change <c>this</c> inside a constructor and change what a directive
/// prologue means.
/// </para>
/// </remarks>
public sealed class VmScriptEngine : IScriptEngine
{
    /// <summary>The identity this engine presents to the verifier.</summary>
    /// <remarks>
    /// It names what is calling, not who is running it. A user or machine name here would put an
    /// environment detail into a diagnostic the log retains.
    /// </remarks>
    private const string Caller = "broiler-browser://htmlbridge";

    private const string LogContext = "VmScriptEngine";

    private readonly IScriptEngine _documentEngine;

    /// <summary>
    /// The compiled artifacts this engine reuses. Process-wide by default, so two pages running one
    /// library compile it once.
    /// </summary>
    /// <remarks>
    /// Settable only from inside the assembly, and only so a test can hold an isolated cache
    /// instead of racing every other test through the shared one. A page cannot reach it.
    /// </remarks>
    internal VmCompilationCache Cache { get; set; } = VmCompilationCache.Shared;

    /// <summary>
    /// Creates a VM-backed engine that forwards the document-bearing paths to
    /// <paramref name="documentEngine"/>.
    /// </summary>
    /// <param name="documentEngine">
    /// The engine that owns the DOM — in practice <c>ScriptEngine</c>. It is a constructor
    /// argument rather than a <c>new ScriptEngine()</c> inside this class so that the delegation is
    /// visible at the composition site instead of hidden here, and so a test can substitute one.
    /// </param>
    public VmScriptEngine(IScriptEngine documentEngine)
    {
        _documentEngine = documentEngine ?? throw new ArgumentNullException(nameof(documentEngine));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Set on both engines. On the VM path it becomes the compiler's <c>forceStrict</c>, which
    /// applies the strictness as a property of the compilation rather than by prepending a
    /// directive to the source — so a script whose own first statement is not a prologue is still
    /// made strict, and the reported column numbers still point at what the author wrote.
    /// </remarks>
    public bool StrictModeEnabled
    {
        get;
        set
        {
            field = value;
            _documentEngine.StrictModeEnabled = value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>On this engine the policy decides the SHAPE OF THE RUNTIME rather than the outcome of a
    /// check.</b> A profile cannot compile a string on its own, so <c>eval</c>, <c>new Function</c>
    /// and dynamic <c>import()</c> are answerable only by a registered artifact provider. When
    /// <see cref="ContentSecurityPolicy.AllowsEval"/> is false this engine registers none, and the
    /// core refuses every guest-initiated load deterministically — a contract outcome the page may
    /// catch, rather than an engine consulting a policy object mid-execution. See
    /// <see cref="RuntimeOptions"/> and <see cref="VmSourceProvider"/>.
    /// </remarks>
    public ContentSecurityPolicy? Csp
    {
        get;
        set
        {
            field = value;
            _documentEngine.Csp = value;
        }
    }

    /// <summary>
    /// Whether this engine will answer a guest-initiated load — <c>eval</c>, <c>new Function</c>,
    /// dynamic <c>import()</c> — for the policy currently set.
    /// </summary>
    /// <remarks>
    /// No policy means yes: a document that states none is not a document that forbids evaluation,
    /// and defaulting to refusal would make the VM engine quietly stricter than the Broiler.JS one
    /// on the same page.
    /// </remarks>
    internal bool AnswersGuestLoads => Csp is not { AllowsEval: false };

    /// <inheritdoc />
    public ScriptProfilingHook? Profiler
    {
        get;
        set
        {
            field = value;
            _documentEngine.Profiler = value;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The document engine's queue, deliberately, and not a second one. Two queues would mean a
    /// <c>queueMicrotask</c> callback landing in whichever queue the host happened to drain, and
    /// the VM profile settles its own jobs through its drain entry point rather than through this.
    /// </remarks>
    public MicroTaskQueue MicroTasks => _documentEngine.MicroTasks;

    /// <inheritdoc />
    public bool Execute(IReadOnlyList<string> scripts) => ExecuteDetailed(scripts).Success;

    /// <inheritdoc />
    public ScriptExecutionResult ExecuteDetailed(IReadOnlyList<string> scripts) =>
        ExecuteDetailed(scripts, null);

    /// <summary>
    /// Executes <paramref name="scripts"/> with the document's authorised module roots available to
    /// a dynamic <c>import()</c>.
    /// </summary>
    /// <remarks>
    /// <b>This overload is this engine's own and is not on <see cref="IScriptEngine"/>.</b> The
    /// interface carries module roots only on the overloads that also take a document, and those
    /// are the ones this engine forwards — so there was no document-free way to hand it a module
    /// map, and widening the interface would change a surface that out-of-repo consumers implement.
    /// The roots are the same <see cref="ModuleRoot"/> values <c>ScriptExtractionService</c>
    /// produces, so a caller that has extracted a document already has them.
    /// </remarks>
    public ScriptExecutionResult ExecuteDetailed(
        IReadOnlyList<string> scripts, IReadOnlyList<ModuleRoot>? moduleRoots, string? documentUrl = null)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        if (scripts.Count == 0)
            return new ScriptExecutionResult { Success = true };

        var modules = moduleRoots is { Count: > 0 }
            ? new VmModuleMap(moduleRoots, documentUrl)
            : VmModuleMap.Empty;

        var compiled = Compile(scripts, documentUrl);

        if (!compiled.Succeeded || compiled.Artifact is null)
            return new ScriptExecutionResult { Success = false, Errors = AttributeRefusal(scripts, compiled) };

        var created = VmRuntime.Create(ProfileCatalog(), RuntimeOptions(modules));

        if (!created.TryGetRuntime(out var runtime))
        {
            // A runtime this engine asked for and this engine's own composition refused is a defect
            // here rather than anything the page did, and it is reported against every script
            // because not one of them ran.
            return Failed(
                scripts,
                $"the Broiler.VM runtime refused creation: {created.Outcome}/{created.Reason}");
        }

        using (runtime)
        {
            return Run(runtime, compiled.Artifact, scripts.Count);
        }
    }

    /// <inheritdoc />
    public string? Execute(IReadOnlyList<string> scripts, string html) =>
        Delegated(nameof(Execute)).Execute(scripts, html);

    /// <inheritdoc />
    public string? Execute(IReadOnlyList<string> scripts, string html, string? url) =>
        Delegated(nameof(Execute)).Execute(scripts, html, url);

    /// <inheritdoc />
    public string? Execute(
        IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url) =>
        Delegated(nameof(Execute)).Execute(scripts, deferredScripts, html, url);

    /// <inheritdoc />
    public string? Execute(
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string html,
        string? url,
        IReadOnlyList<ModuleRoot>? moduleRoots) =>
        Delegated(nameof(Execute)).Execute(scripts, deferredScripts, html, url, moduleRoots);

    /// <inheritdoc />
    public InteractiveSession? ExecuteInteractive(
        IReadOnlyList<string> scripts, IReadOnlyList<string> deferredScripts, string html, string? url) =>
        Delegated(nameof(ExecuteInteractive)).ExecuteInteractive(scripts, deferredScripts, html, url);

    /// <inheritdoc />
    public InteractiveSession? ExecuteInteractive(
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string html,
        string? url,
        IReadOnlyList<ModuleRoot>? moduleRoots) =>
        Delegated(nameof(ExecuteInteractive)).ExecuteInteractive(scripts, deferredScripts, html, url, moduleRoots);

    /// <summary>
    /// Names the delegation in the log once per call and hands back the engine that will serve it.
    /// </summary>
    /// <remarks>
    /// It logs at debug rather than warning: this is the designed behaviour of the configuration
    /// and not a fault, but a reader looking at a <c>Debug-VM</c> session and wondering which
    /// engine rendered the page is entitled to find the answer in the log rather than in this file.
    /// </remarks>
    private IScriptEngine Delegated(string member)
    {
        RenderLogger.LogDebug(
            LogCategory.JavaScript,
            LogContext,
            $"{member} needs a document, which the Broiler.VM JavaScript profile cannot host; " +
            "serving it from the Broiler.JS engine.");

        return _documentEngine;
    }

    /// <summary>Compiles a document's scripts into one artifact, one entry point each.</summary>
    private JsCompilation Compile(IReadOnlyList<string> scripts, string? documentUrl)
    {
        var units = new List<JsScriptUnit>(scripts.Count);

        for (var index = 0; index < scripts.Count; index++)
            units.Add(Unit(scripts[index], index, documentUrl));

        // THE SURFACE AND THE FORM ARE NAMED RATHER THAN DEFAULTED, and that is worth the two
        // extra arguments: JsCompiler's parameterless request happens to default to exactly this
        // pair today, so a browser that took the default would keep compiling correctly right up
        // until Broiler.VM had a reason to change what an unstated request means -- and would then
        // compile a document's scripts under a different manifest with nothing said about it.
        // Naming them makes that a compile error in the profile's own vocabulary instead.
        //
        // THROUGH THE CACHE, so a document seen twice is compiled once. The bytes it returns are
        // still verified on every use: what is saved is the lowering and not the verification, and
        // Broiler.VM's own contract is what makes that distinction non-negotiable.
        return Cache.GetOrCompile(units, [], WideBytecode, () => JsCompiler.Compile(units, [], WideBytecode));
    }

    /// <summary>What this engine asks the front end for: the wide surface, lowered to bytecode.</summary>
    /// <remarks>
    /// The wide surface because a page's scripts are ordinary JavaScript — objects, closures,
    /// exceptions and a standard library — and the numeric manifest admits none of that. Bytecode
    /// because the native form is admitted only under the numeric manifest, so it is not a form
    /// this engine could ask for even if it wanted to.
    /// </remarks>
    private static readonly JsCompileRequest WideBytecode =
        new(JsFeatureManifest.Wide, JsOutputForm.Bytecode);

    /// <summary>One script as a compilation unit, named and referred from the document.</summary>
    /// <remarks>
    /// THE REFERRER IS THE DOCUMENT'S URL, and it is what makes `import('./a.mjs')` in a classic
    /// script mean the same thing it means in a module beside it. A module carries the key the host
    /// resolved it to and needs no second identity; a script is a text, so the host that read the
    /// text says where it was read from. Left empty, the profile still asks for the module - and
    /// VmModuleMap resolves nothing from a referrer it does not recognise, so every import() from a
    /// script would answer not-found for a reason no page could see.
    /// </remarks>
    private JsScriptUnit Unit(string source, int index, string? documentUrl) =>
        new(EntryPoint(index), source, SliceParseOptions.Script, StrictModeEnabled, documentUrl ?? string.Empty);

    /// <summary>The entry point the compiler emits for the <paramref name="index"/>-th script.</summary>
    private static string EntryPoint(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"script{index}");

    /// <summary>
    /// Says which scripts a refused compilation refuses, by compiling each one alone.
    /// </summary>
    /// <remarks>
    /// <b>A diagnostic carries a line and a column and no unit, so the batch cannot say which
    /// script it was reading.</b> Reporting every diagnostic against script 0 would name the wrong
    /// script whenever the fault is in a later one, which on a page with a dozen scripts is most
    /// of the time. Compiling each unit alone answers it exactly, and costs a second pass only on
    /// the path that already failed.
    /// <para>
    /// A batch that refuses while every script compiles alone is possible — a later script can
    /// redeclare an earlier one's lexical binding — and is reported against all of them, because
    /// that refusal genuinely belongs to no single script.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ScriptError> AttributeRefusal(IReadOnlyList<string> scripts, JsCompilation batch)
    {
        var errors = new List<ScriptError>();

        for (var index = 0; index < scripts.Count; index++)
        {
            var alone = JsCompiler.Compile([Unit(scripts[index], index, null)], [], WideBytecode);

            if (alone.Succeeded)
                continue;

            errors.Add(Error(index, "the Broiler.VM front end refused the source: " + First(alone)));
        }

        if (errors.Count != 0)
            return errors;

        var whole = "the Broiler.VM front end refused the document's scripts together, " +
            "though each compiles alone: " + First(batch);

        for (var index = 0; index < scripts.Count; index++)
            errors.Add(Error(index, whole));

        return errors;
    }

    /// <summary>Verifies, instantiates and invokes; one entry point per script, then the drain.</summary>
    private ScriptExecutionResult Run(VmRuntime runtime, byte[] artifact, int scripts)
    {
        var descriptor = new VmArtifactDescriptor(
            JavaScriptProfile.Id,
            JsFormat.FormatVersion,
            JavaScriptProfile.WideManifest,
            default,
            VmCallerIdentity.FromCanonicalIdentity(Caller));

        var verified = runtime.Verify(in descriptor, artifact, CancellationToken.None);

        if (!verified.TryGetArtifact(out var verifiedArtifact))
        {
            // AN ARTIFACT THIS ENGINE'S OWN FRONT END PRODUCED AND ITS OWN VERIFIER REFUSED IS A
            // DEFECT HERE, not a property of the page, and the message says so rather than
            // blaming the script. Exhaustion is the exception: verification is work, it is
            // charged, and running out of allowance while doing it is an answer about size.
            var reason = verified.Outcome == VmOutcome.ResourceExhaustion
                ? $"verifying the document's scripts spent the allowance: {verified.Reason}"
                : "the Broiler.VM verifier refused an artifact this engine produced: " +
                  $"{verified.Diagnostics.ProfileDiagnosticCode} ({verified.Outcome}/{verified.Reason})";

            return Failed(scripts, reason);
        }

        var instantiated = runtime.Instantiate(verifiedArtifact, CancellationToken.None);

        if (!instantiated.TryGetInstance(out var instance))
        {
            return Failed(
                scripts,
                instantiated.Outcome == VmOutcome.ResourceExhaustion
                    ? $"instantiating the document's scripts spent the allowance: {instantiated.Reason}"
                    : "the artifact verified and would not instantiate: " +
                      $"({instantiated.Outcome}/{instantiated.Reason})");
        }

        var errors = new List<ScriptError>();

        // THE LAST TIME ROUND IS THE JOB QUEUE. A queue drained at a point nobody stated is a
        // behaviour no embedder can reason about, so the profile never chooses and this engine
        // does: after the last script, which is what makes `Promise.resolve(1).then(f)` run f
        // before Execute returns. The document paths drain through MicroTasks instead — see the
        // remark on that property for why there is only one queue and it is not this one.
        for (var index = 0; index <= scripts; index++)
        {
            var drain = index == scripts;
            var name = drain ? JavaScriptProfile.DrainEntryPoint : EntryPoint(index);
            var label = drain ? "the job queue" : ScriptLabel.Inline(index);

            // Attributing a fault to a script means attributing the DRAIN's fault to the last one:
            // a rejected promise settles there, and reporting it against a script index nobody can
            // point at would be worse than naming the last script that could have queued it.
            var attributed = drain ? Math.Max(scripts - 1, 0) : index;

            VmInvocationResult result = default;

            Measured(label, () =>
            {
                var request = new VmInvocationRequest(new VmUtf8Text(Encoding.UTF8.GetBytes(name)));
                result = instance.Invoke(in request, CancellationToken.None);
            });

            if (result.Outcome == VmOutcome.ResourceExhaustion)
            {
                errors.Add(Error(
                    attributed,
                    $"{label} did not settle within its allowance: {result.Reason} " +
                    $"on {result.Diagnostics.ExhaustedDimension}"));

                // The allowance is spent for the whole runtime, not for this entry point, so every
                // later invocation would answer the same way. Stopping says it once.
                break;
            }

            if (JavaScriptProfile.TryGetUncaught(in result, out var uncaught))
            {
                errors.Add(Error(attributed, "uncaught " + uncaught.Message));

                // A script that throws does not stop the ones after it: that is what a document
                // does, and it is why ScriptExecutionResult carries a list rather than a first
                // error. The drain still runs, because a rejected job is still a job.
                continue;
            }

            if (JavaScriptProfile.TryGetWideCompletion(in result, out _))
                continue;

            errors.Add(Error(
                attributed,
                $"the invocation of {label} answered {result.Outcome}/{result.Reason} and carried no payload, " +
                "which is a defect in this engine rather than in the script"));
        }

        return new ScriptExecutionResult { Success = errors.Count == 0, Errors = errors };
    }

    /// <summary>Runs <paramref name="work"/> under the profiling hook when one is attached.</summary>
    private void Measured(string label, Action work)
    {
        if (Profiler is { } profiler)
            profiler.Measure(label, work);
        else
            work();
    }

    /// <summary>The catalog: one profile, arriving through its own static accessor.</summary>
    /// <remarks>
    /// No name is looked up, no directory scanned and no assembly loaded — which is the Native AOT
    /// contract Broiler.VM states, and the reason a profile is a typed reference here rather than a
    /// plug-in.
    /// </remarks>
    private static VmCatalog ProfileCatalog() => VmCatalog.CreateBuilder()
        .Add(JavaScriptProfile.Descriptor)
        .Build();

    /// <summary>The runtime this engine creates for one document.</summary>
    /// <remarks>
    /// <para>
    /// <b>The allowance is the profile's own declared default.</b> An engine with an opinion about
    /// how long a page's script may run would be imposing a policy the profile did not declare.
    /// What the default buys is that a script which never terminates ends in a bounded number of
    /// instructions rather than never — and ends at the same instruction on a fast machine and a
    /// slow one, because the VM charges fuel per instruction and not per second.
    /// </para>
    /// <para>
    /// <b>THE CAPABILITY SET IS WHERE THIS ENGINE'S CONTENT POLICY LIVES.</b> <c>print</c> reaches
    /// the render log unconditionally, which is this composition's decision. The other two are the
    /// interesting ones and both follow from the page:
    /// </para>
    /// <para>
    /// The <b>source provider</b> is registered only when the page's CSP permits evaluation. A
    /// profile cannot compile a string on its own, so registering it is the permission and
    /// withholding it is the prohibition — <c>eval</c>, <c>new Function</c> and <c>import()</c> are
    /// then refused by the core deterministically rather than by a check inside an engine. A page
    /// with <c>unsafe-eval</c> and one without get two differently shaped runtimes.
    /// </para>
    /// <para>
    /// The <b>resolver</b> is registered alongside it, and answers what a specifier names using the
    /// document's own module map. Admitting the module surface is the descriptor's act; saying what
    /// a specifier resolves to is this one's, and it is answered from the roots the document
    /// declared rather than by fetching anything.
    /// </para>
    /// </remarks>
    private VmRuntimeCreationOptions RuntimeOptions(VmModuleMap modules)
    {
        var ceilings = ImmutableArray.CreateBuilder<VmCeilingSpec>();

        foreach (var dimension in VmBudgetDimensions.All)
        {
            ceilings.Add(dimension == VmBudgetDimension.LiveRuntimes
                ? VmCeilingSpec.AdoptParentRemaining(dimension)
                : VmCeilingSpec.AdoptProfileDefault(dimension));
        }

        var capabilities = ImmutableArray.CreateBuilder<VmCapabilityRegistration>();
        capabilities.Add(VmCapabilityRegistration.Value(JavaScriptProfile.WriteCapability, Write));

        if (AnswersGuestLoads)
        {
            capabilities.Add(VmCapabilityRegistration.ArtifactProvider(
                JavaScriptProfile.SourceProviderCapability,
                new VmSourceProvider(modules, WideBytecode)));

            capabilities.Add(VmCapabilityRegistration.Value(
                JavaScriptProfile.ResolveCapability,
                (VmBytes argument, out VmOpaqueRef result) =>
                {
                    result = default;

                    // Refused is a POLICY answer and not a failed call, which is exactly what it
                    // means here: the artifact was resolved by rules that are not this document's,
                    // and this host declines to evaluate it.
                    return modules.Confirms(argument.Span)
                        ? VmHostCallOutcome.Completed
                        : VmHostCallOutcome.Refused;
                }));
        }
        else
        {
            RenderLogger.LogDebug(
                LogCategory.JavaScript,
                LogContext,
                "The page's content security policy forbids evaluation, so no source provider is " +
                "registered and every guest-initiated load will be refused by the runtime.");
        }

        return new VmRuntimeCreationOptions(
            aggregateBudget: null,
            ceilings: ceilings.ToImmutable(),
            maxSuspendedResidency: TimeSpan.FromMinutes(1),
            maxLiveSuspendedOperations: 1,
            guestLoadBounds: VmGuestLoadBoundsSpec.AdoptProfileMaxima,
            externalSuspension: VmExternalSuspensionMode.Disabled,
            capabilities: capabilities.ToImmutable());
    }

    /// <summary>The one host capability this engine registers: write a line to the render log.</summary>
    private static VmHostCallOutcome Write(VmBytes argument, out VmOpaqueRef result)
    {
        result = default;
        RenderLogger.LogDebug(LogCategory.JavaScript, LogContext, Encoding.UTF8.GetString(argument.Span));
        return VmHostCallOutcome.Completed;
    }

    /// <summary>The first diagnostic of a refused compilation, or a line saying there was none.</summary>
    private static string First(JsCompilation compilation) =>
        compilation.Diagnostics.Count == 0
            ? "and named no diagnostic, which is a defect in the front end rather than in the source"
            : compilation.Diagnostics[0].ToString();

    /// <summary>A result in which nothing ran, reported against every script, because nothing did.</summary>
    private static ScriptExecutionResult Failed(IReadOnlyList<string> scripts, string message) =>
        Failed(scripts.Count, message);

    private static ScriptExecutionResult Failed(int scripts, string message)
    {
        var errors = new List<ScriptError>(scripts);

        for (var index = 0; index < scripts; index++)
            errors.Add(Error(index, message));

        return new ScriptExecutionResult { Success = false, Errors = errors };
    }

    /// <summary>
    /// One error, logged as it is recorded.
    /// </summary>
    /// <remarks>
    /// The stack trace is empty and stays empty: what a caller wants for a VM fault is the guest's
    /// frames, and a CLR trace of this engine's own call stack would be a plausible-looking answer
    /// to a different question. <c>ScriptError.StackTrace</c> documents itself as a .NET trace, so
    /// leaving it empty says "none captured" rather than misreporting one.
    /// </remarks>
    private static ScriptError Error(int index, string message)
    {
        RenderLogger.LogWarning(
            LogCategory.JavaScript, LogContext, $"Script {ScriptLabel.Inline(index)} failed: {message}");

        return new ScriptError { ScriptIndex = index, Message = message, StackTrace = string.Empty };
    }
}
