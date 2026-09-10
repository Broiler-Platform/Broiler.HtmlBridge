using Broiler.VM.Profile.JavaScript;

namespace Broiler.HtmlBridge.Jseal.Vm;

/// <summary>
/// <see cref="IJsSource"/>: running script this repository authored, and script the page supplied.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both go through the realm's own <c>eval</c>, which is the only thing that evaluates INTO an
/// existing realm.</b> The obvious alternative - compile the text to an artifact and instantiate it
/// - produces a second realm with its own globals, so a polyfill installed that way would be
/// installed somewhere the page cannot see. Asking the realm for its <c>eval</c> and invoking it is
/// what keeps the evaluation in the realm the caller meant.
/// </para>
/// <para>
/// <b>What separates the two is a mark this provider makes, not one the profile carries.</b> The
/// profile's evaluation request is the source text and nothing else, so the artifact provider cannot
/// tell whose evaluation it is answering. <see cref="VmSourceProvider.EnterHostScript"/> is how this
/// side says so; the reasoning, and the gap it stands in for, are on that type.
/// </para>
/// </remarks>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue EvaluateHostScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var host = _sources.EnterHostScript();

        return Evaluate(source, label);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The permit is what makes this expressible on an engine with no run-time compiler.</b>
    /// Compiling here means asking a registered artifact provider, and that provider is also where a
    /// forbidden evaluation is refused -- so handing a page's script over needs a permission that is
    /// narrower than the host's held mark and wider than nothing. One compile, spent by the compile
    /// it authorises.
    /// </remarks>
    public JsValue EvaluateClassicScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);

        if ((Capabilities & JsCapabilities.ClassicScriptSource) == 0)
            throw Lacking(JsCapabilities.ClassicScriptSource);

        using var permit = _sources.EnterClassicScript();

        return Evaluate(source, label);
    }

    /// <inheritdoc />
    public JsValue EvaluateDynamicSource(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);

        if ((Capabilities & JsCapabilities.GuestEval) == 0)
            throw Lacking(JsCapabilities.GuestEval);

        return Evaluate(source, label);
    }

    /// <summary>
    /// Evaluates one text in this realm, through the realm's own indirect <c>eval</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Indirect on purpose.</b> Calling the realm's <c>eval</c> value rather than the syntactic
    /// form is an indirect eval, which the language evaluates in global scope - which is what a host
    /// asking a realm to run a script means, and what a direct eval would not do.
    /// </para>
    /// <para>
    /// <b>The intrinsic, captured at realm creation, and NOT read off the global here.</b> <c>eval</c>
    /// is a writable global, so reading it at this line invoked whatever the page had assigned over
    /// it - which intercepted the bridge's own script and, because the host-script mark is held
    /// across this call, lent the page a compiler its Content-Security-Policy had taken away. See
    /// <c>VmHostBridge.Eval</c> for the measurement, and
    /// <c>APageThatReplacesEvalCannotBorrowTheHostsPermissionToCompile</c> for the case.
    /// </para>
    /// </remarks>
    private JsValue Evaluate(string source, string label) =>
        InStep(realm =>
        {
            var evaluate = _bridge.Eval;

            if (evaluate.Kind is not JsHostValueKind.Function)
            {
                throw new JsEngineException(
                    $"this realm has no 'eval', so '{label}' cannot be evaluated in it");
            }

            JsHostValue[] arguments = [JsHostValue.String(source)];

            return VmMarshal.Wrap(realm.Invoke(evaluate, JsHostValue.Undefined, arguments));
        });
}
