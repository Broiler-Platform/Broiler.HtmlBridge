using System;

namespace Broiler.HtmlBridge.Dom;

/// <summary>
/// How a script asked to leave the document — the distinction a host needs to keep session history
/// right, and the one the page itself spelled out.
/// </summary>
public enum NavigationKind
{
    /// <summary><c>location.assign(url)</c> or <c>location.href = url</c>: leave, keeping this document in history.</summary>
    Assign,

    /// <summary><c>location.replace(url)</c>: leave, and drop this document from history.</summary>
    Replace,

    /// <summary><c>location.reload()</c>: load the document's own URL again.</summary>
    Reload,

    /// <summary>
    /// <c>&lt;meta http-equiv="refresh"&gt;</c>: the document asked in markup rather than in script,
    /// and — unlike the three above — asked for it after a stated wait.
    /// </summary>
    MetaRefresh,

    /// <summary>
    /// <c>form.submit()</c>: the one navigation whose target the bridge cannot finish computing, so
    /// the request names the form and the host builds the rest. See
    /// <see cref="NavigationRequest.FormIndex"/>.
    /// </summary>
    FormSubmit,
}

/// <summary>
/// A cross-document navigation a script asked for and the bridge could not perform itself.
/// <para>
/// A binding has no loader and no session history, so it cannot leave a document — what it can do is
/// say where the page wanted to go and let the host decide. The bridge records the request; the host
/// reads it once script execution has settled and either follows it or does not. That split is
/// deliberate: following is a policy question with a different answer in an interactive browser
/// (navigate, as the user expects) than in a capture pinned to one document, and the engine should
/// not have to know which one it is running inside.
/// </para>
/// <para>
/// A <b>fragment</b> navigation never appears here. That one is same-document — no fetch, just a
/// moved <c>location.hash</c> and a <c>hashchange</c> — so <c>LocationBinding</c> performs it
/// outright and the host is never involved.
/// </para>
/// </summary>
/// <param name="Url">
/// The target, already resolved against the document's own URL, so a host can act on it without
/// knowing what the page actually wrote.
/// </param>
/// <param name="Kind">Which of the navigation methods the page called.</param>
/// <param name="Delay">
/// How long the document asked to wait first. Zero for everything a script asks for — a script
/// wanting a delayed navigation uses a timer and asks when it fires. Only
/// <see cref="NavigationKind.MetaRefresh"/> states one, and the difference matters: a short wait is
/// a redirect, a long one is a notice meant to be read, and only the host knows whether it can
/// honour either.
/// </param>
public sealed record NavigationRequest(string Url, NavigationKind Kind, TimeSpan Delay = default)
{
    /// <summary>
    /// For <see cref="NavigationKind.FormSubmit"/>, which of the document's forms to submit,
    /// counted in document order. <c>-1</c> for every other kind.
    /// </summary>
    /// <remarks>
    /// A form submission is the one navigation the bridge cannot finish describing. Its target
    /// depends on the form's data set — a GET puts it in the query — and serializing that is the
    /// host's job, done once for keyboard, mouse and script rather than a second time here. What the
    /// bridge can say is <i>which</i> form, and it says it by position because that is what survives
    /// the trip: the host re-parses the serialized document rather than sharing this one's nodes, and
    /// a form with no <c>id</c> or <c>name</c> has nothing else to be identified by. Both walk the
    /// same document in the same order.
    /// <para>
    /// <see cref="Url"/> still carries the form's resolved <c>action</c>, which is the whole target
    /// for a POST and the stem of it for a GET — enough for a log line to name where the page was
    /// going.
    /// </para>
    /// </remarks>
    public int FormIndex { get; init; } = -1;
}
