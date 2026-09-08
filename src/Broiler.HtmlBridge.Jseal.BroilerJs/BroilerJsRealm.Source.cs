using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Jseal.BroilerJs;

/// <summary>
/// <see cref="IJsSource"/>: turning JavaScript source into something that runs, and keeping the two
/// reasons a browser does that apart.
/// </summary>
/// <remarks>
/// <b>Both reach the same <c>JSContext.Eval</c>, and that is the point rather than a shortcut.</b>
/// Broiler.JS carries a run-time compiler and cannot tell host script from guest source — nothing in
/// the engine distinguishes them, because the distinction is not the engine's. It is the host's, and
/// the provider is where it is enforced: the same call is permitted unconditionally for source this
/// repository authored and refused for source the page supplied when the realm was built without
/// <see cref="JsCapabilities.GuestEval"/>. An engine with no run-time compiler would implement the
/// two differently — compiling the first when it is built and refusing the second outright — and the
/// contract is shaped so that it can.
/// </remarks>
internal sealed partial class BroilerJsRealm
{
    /// <inheritdoc />
    public JsValue EvaluateHostScript(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Evaluate(source, label);
    }

    /// <summary>
    /// Runs JavaScript the page supplied, on the page's behalf.
    /// </summary>
    /// <remarks>
    /// The refusal is a capability failure rather than an engine one: the page asked for something
    /// this realm was deliberately built without, which is a different fact from the source being
    /// wrong. A host that read a restrictive Content-Security-Policy and passed
    /// <c>AllowGuestEval: false</c> gets it here, at the one call that can produce it, without the
    /// engine consulting a policy object mid-execution.
    /// </remarks>
    public JsValue EvaluateGuestSource(string source, string label)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!_allowGuestEval)
            throw new JsCapabilityUnavailableException(EngineName, JsCapabilities.GuestEval);

        return Evaluate(source, label);
    }

    private JsValue Evaluate(string source, string label)
    {
        using var scope = Enter();

        try
        {
            return BroilerJsMarshal.Wrap(_context.Eval(ApplyStrictMode(source), label));
        }
        catch (JSException engineException)
        {
            throw Translate(engineException);
        }
    }

    /// <summary>
    /// <c>ForceStrictMode</c>, expressed the only way this engine offers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Broiler.JS has no realm-wide "everything is strict" switch — <c>JSContextOptions</c> carries
    /// none — so the directive is prepended to the source instead, which is what the language itself
    /// says makes a script strict.
    /// </para>
    /// <para>
    /// <b>No newline, deliberately.</b> The prologue goes on the same line as the source's own first
    /// line so that every line number a stack frame reports is the line the host handed in. A
    /// <c>\n</c> here would shift every reported line by one, which is the kind of defect that is
    /// invisible until someone is reading a stack trace at the moment they can least afford a wrong
    /// answer. Column one of line one moves; nothing else does.
    /// </para>
    /// </remarks>
    private string ApplyStrictMode(string source) =>
        _forceStrictMode ? "\"use strict\";" + source : source;
}
