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
public sealed record NavigationRequest(string Url, NavigationKind Kind);
