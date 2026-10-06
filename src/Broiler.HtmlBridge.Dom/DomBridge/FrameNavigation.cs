using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge;

/// <summary>
/// A frame navigated through its <c>location</c>: <c>location.href = url</c>, <c>assign</c>,
/// <c>replace</c> and <c>reload</c> in the frame's script, the same on its window from the page, and
/// the page navigating a frame of another origin through the window it is handed for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each was logged and dropped.</b> A frame's Location had no host to hand a navigation to, so the
/// frame kept the document it had. A page that loads a frame and then points it somewhere else --
/// <c>frames[0].location = url</c>, the commonest way to drive a frame from script -- and a frame that
/// moves itself on to its next step saw nothing happen.
/// </para>
/// <para>
/// <b>A navigation is a later task, as in a browser</b> (HTML §7.4.2). The script that asked goes on
/// running against the document it is in; then the frame's document is torn down, the new URL is
/// loaded into the frame -- in place of its <c>src</c> or <c>srcdoc</c>, which a navigation does not
/// change -- and the frame element's <c>load</c> fires. A second request before the first has run
/// supersedes it.
/// </para>
/// <para>
/// <b>A frame that keeps reloading itself is stopped.</b> A frame's own script may ask for the URL it
/// already shows <see cref="MaxFrameSelfReloads"/> times in a row; the next request is logged and
/// dropped, rather than the frame loading forever. A frame-busting <c>top.location = self.location</c>
/// is not such a request: <c>top</c> is a view of the top window (<c>SubWindowBinding.TopWindowAsSeen</c>),
/// and assigning its location asks the host to navigate the page.
/// </para>
/// <para>
/// <b>The frame keeps its window.</b> The old document's window is emptied and the new document is
/// built into it (<c>BrowsingContextManager.RemoveContainerCaches</c>), so a <c>contentWindow</c> the
/// page kept is the frame's window still.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>How many times in a row a frame's own script may have it load the URL it shows.</summary>
    private const int MaxFrameSelfReloads = 3;

    /// <summary>
    /// Where a frame was navigated to, by which document's script, with what body when it was a <c>post</c>,
    /// and with what document when a <c>javascript:</c> URL's script answered one (<see cref="Html"/>).
    /// </summary>
    private sealed record FrameNavigationTarget(
        string Url, DocumentRequestContext Initiator, FrameRequestBody? Body = null, string? Html = null,
        FrameHistoryHandling History = FrameHistoryHandling.Push);

    /// <summary>What a <c>method="post"</c> submission into a frame sends (DomBridge/FrameSubmission.cs).</summary>
    private sealed record FrameRequestBody(byte[] Content, string ContentType);

    private readonly Dictionary<DomElement, FrameNavigationTarget> _frameNavigations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DomElement, int> _frameNavigationGenerations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DomElement, int> _frameSelfReloads = new(ReferenceEqualityComparer.Instance);

    /// <summary>The page's own Location, which the global's <c>location</c> answers outside a frame's script.</summary>
    private JsValue _topLocation;

    /// <summary>The page's own <c>postMessage</c>, which the global answers outside a frame's script.</summary>
    private JsValue _topPostMessage;

    /// <summary>
    /// The Location of the window whose script is running: a frame's, inside its script, and the
    /// page's otherwise. What the global's <c>location</c> answers, and what assigning it navigates.
    /// </summary>
    private JsValue CurrentLocation() =>
        _windowContext.ResolveCurrentSubWindow() is { } frame ? Realm.GetProperty(frame, "location") : _topLocation;

    /// <summary>The URL a frame was navigated to through its location, which its attributes no longer say.</summary>
    private bool TryGetFrameNavigation(DomElement container, out string url)
    {
        if (_frameNavigations.TryGetValue(container, out var navigation))
        {
            url = navigation.Url;
            return true;
        }

        url = string.Empty;
        return false;
    }

    /// <summary>The body <paramref name="container"/>'s frame was navigated with: a submission's <c>post</c>.</summary>
    private FrameRequestBody? FrameNavigationBody(DomElement container) =>
        _frameNavigations.TryGetValue(container, out var navigation) ? navigation.Body : null;

    /// <summary>The document <paramref name="container"/>'s frame was navigated to by a <c>javascript:</c> URL's string, if it was.</summary>
    private string? FrameNavigationDocument(DomElement container) =>
        _frameNavigations.TryGetValue(container, out var navigation) ? navigation.Html : null;

    /// <summary>The document whose script navigated <paramref name="container"/>'s frame, when it was navigated.</summary>
    private DocumentRequestContext? FrameNavigationInitiator(DomElement container) =>
        _frameNavigations.TryGetValue(container, out var navigation) ? navigation.Initiator : null;

    /// <summary>The frame's own <c>src</c> or <c>srcdoc</c> was set: what it shows is what they say again.</summary>
    private void ForgetFrameNavigation(DomElement container)
    {
        _frameNavigations.Remove(container);
        _frameSelfReloads.Remove(container);
    }

    private void ClearFrameNavigations()
    {
        _frameNavigations.Clear();
        _frameNavigationGenerations.Clear();
        _frameSelfReloads.Clear();
    }

    /// <summary>The host a frame's Location hands its navigations and its fragment's changes to.</summary>
    internal Dom.Features.ILocationHost FrameLocationHost(DomElement container) => new FrameLocationHostImpl(this, container);

    private sealed class FrameLocationHostImpl(DomBridge bridge, DomElement container) : Dom.Features.ILocationHost
    {
        public void RequestNavigation(NavigationRequest request) => bridge.RequestFrameNavigation(container, request);

        public void NavigatedToFragment(string fragment)
        {
            if (bridge.GetContentDocument(container) is { } document)
                bridge.SetTargetFromFragment(document, fragment);
        }

        public void FragmentChanged(string oldUrl, string newUrl, bool replace) => bridge.FrameFragmentChanged(container, oldUrl, newUrl, replace);

        public bool RunJavaScriptUrl(string url) => bridge.RunJavaScriptUrl(url, container, bridge.CurrentScriptDocumentContext());
    }

    /// <summary>Queues the navigation of <paramref name="container"/>'s frame to what <paramref name="request"/> names.</summary>
    private void RequestFrameNavigation(
        DomElement container, NavigationRequest request, FrameRequestBody? body = null, string? html = null,
        FrameHistoryHandling? history = null)
    {
        // A reload or a replace takes the frame's current entry, a traversal the entry it went to; anything
        // else is a new entry of the joint history (DomBridge/SessionHistory.cs).
        var handling = history ?? (request.Kind is NavigationKind.Reload or NavigationKind.Replace
            ? FrameHistoryHandling.Replace
            : FrameHistoryHandling.Push);

        var current = _browsingContexts.TryGetLocation(container, out var location) ? location : null;
        var url = request.Kind == NavigationKind.Reload ? current : request.Url;
        if (string.IsNullOrWhiteSpace(url))
            return;

        // A javascript: URL runs script rather than loading a document; `location.href =
        // "javascript:void(0)"` is a page doing nothing, and must not tear its frame down.
        if (url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.location",
                $"A frame's navigation to a javascript: URL is not performed: {url}");
            return;
        }

        // A javascript: URL's document is shown at the URL the frame already shows; it is not a reload.
        if (html is null && ReferenceEquals(CurrentScriptFrame(), container) && IsSameDocumentUrl(url, current))
        {
            var reloads = _frameSelfReloads.GetValueOrDefault(container) + 1;
            _frameSelfReloads[container] = reloads;
            if (reloads > MaxFrameSelfReloads)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.location",
                    $"A frame asked to load the URL it shows {reloads} times in a row, which is a loop; {url} is not loaded again");
                return;
            }
        }
        else
        {
            _frameSelfReloads.Remove(container);
        }

        var generation = _frameNavigationGenerations.GetValueOrDefault(container) + 1;
        _frameNavigationGenerations[container] = generation;
        var initiator = CurrentScriptDocumentContext();
        _eventLoop.QueueTask(() => NavigateFrame(container, new FrameNavigationTarget(url, initiator, body, html, handling), generation));
    }

    private void NavigateFrame(DomElement container, FrameNavigationTarget target, int generation)
    {
        // Superseded by a later request, or the frame is gone.
        if (_frameNavigationGenerations.GetValueOrDefault(container) != generation || !container.IsConnected || _realm is null)
            return;

        NoteFrameNavigation(container, target.History);
        _frameNavigations[container] = target;
        InvalidateCachedSubDocument(container);
        _browsingContexts.ClearOnloadFired(container);

        // Loads the new document, which runs its scripts, and fires the frame element's load.
        FireSubDocumentOnload(container);
    }

    private static bool IsSameDocumentUrl(string url, string? current) =>
        current is not null &&
        Uri.TryCreate(url, UriKind.Absolute, out var target) &&
        Uri.TryCreate(current, UriKind.Absolute, out var shown) &&
        Uri.Compare(target, shown, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
            UriFormat.UriEscaped, StringComparison.Ordinal) == 0;
}
