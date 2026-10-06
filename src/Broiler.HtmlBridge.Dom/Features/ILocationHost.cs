using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="LocationBinding"/> needs from the bridge: somewhere to record a
/// cross-document navigation the page asked for, and the window a same-document one is announced to.
/// <para>
/// The two navigation halves are the two kinds of navigation. A fragment navigation is not a load, so
/// the binding performs it and only needs to announce it — that is <see cref="NavigatedToFragment"/>
/// and <see cref="FragmentChanged"/>. Everything else needs a loader the binding does not have, so it
/// is recorded and left to the host — that is <see cref="RequestNavigation"/>.
/// </para>
/// </summary>
/// <remarks>
/// The events are the host's to make: <c>popstate</c> fires at once and <c>hashchange</c> in a later
/// task, which only the host's event loop can queue, and both are trusted events like every other one
/// the user agent fires.
/// </remarks>
internal interface ILocationHost
{
    void RequestNavigation(NavigationRequest request);

    /// <summary>
    /// The document moved to <paramref name="fragment"/> (<c>#section</c>, or empty) -- the fragment it
    /// had, or a new one: the element it names is the document's <c>:target</c> now.
    /// </summary>
    void NavigatedToFragment(string fragment);

    /// <summary>
    /// The document's fragment changed, its URL from <paramref name="oldUrl"/> to
    /// <paramref name="newUrl"/>: a history entry is added -- or, for <c>location.replace</c>, the
    /// current one replaced -- and the window hears <c>popstate</c> now and <c>hashchange</c> in a later
    /// task (HTML §7.4.2.3.3, "navigate to a fragment").
    /// </summary>
    void FragmentChanged(string oldUrl, string newUrl, bool replace);

    /// <summary>
    /// A navigation to <paramref name="url"/>, a <c>javascript:</c> URL: its script runs in this document,
    /// in a later task, if the document's Content-Security-Policy allows inline script. Answers false,
    /// running nothing, when the document whose script navigated has another origin, which may not.
    /// </summary>
    bool RunJavaScriptUrl(string url);
}
