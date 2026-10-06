using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A document's session history -- the entries <c>pushState</c>, <c>replaceState</c> and its fragment
/// navigations make, and the traversals <c>back</c>, <c>forward</c> and <c>go</c> make between them -- as
/// its <c>History</c> object shows it, with the page's reported to the host so its back and forward go
/// through them too.
/// </summary>
/// <remarks>
/// <para>
/// <b>History was a stub.</b> <c>pushState</c> stored the state and left the URL alone, so a router that
/// pushes a path and reads <c>location.pathname</c> back read the old one; <c>history.length</c> was always
/// one; <c>back()</c>, <c>forward()</c> and <c>go()</c> did nothing.
/// </para>
/// <para>
/// <b>As Chromium keeps it, measured.</b> <c>pushState</c> moves the document's URL -- <c>href</c>,
/// <c>pathname</c>, <c>search</c>, <c>document.URL</c> -- at once, adds an entry and makes the state a
/// clone of the one given; <c>replaceState</c> replaces the entry; a URL of another origin is a
/// <c>SecurityError</c>; neither fires an event, moves <c>:target</c> or scrolls. <c>back()</c>,
/// <c>forward()</c> and <c>go(n)</c> traverse in a later task: the URL and the state move, <c>popstate</c>
/// fires with the entry's state, and <c>hashchange</c> follows in a task of its own when the fragment
/// changed; past either end they do nothing, and <c>go(0)</c> reloads.
/// </para>
/// <para>
/// <b>The page's history is the host's too.</b> The host keeps the window's history, of which the page's
/// entries are a run: every push, replace and traversal the page makes is reported
/// (<see cref="TakeHistoryChanges"/>), a traversal past the page's own entries is the host's to make, and
/// the host's back and forward between the page's entries come here (<see cref="TraverseHistory"/>). A
/// frame keeps a history of its own, which the host is not told about.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>One entry of a document's session history: its URL, and the state its push or replace gave it.</summary>
    private sealed record HistoryEntry(string Url, JsValue State);

    /// <summary>A document's session history: the page's, or a frame's.</summary>
    private sealed class DocumentHistory(DocumentUrl url, DomElement? frame)
    {
        public DocumentUrl Url { get; } = url;

        /// <summary>The frame whose document this is, or null for the page.</summary>
        public DomElement? Frame { get; } = frame;

        public List<HistoryEntry> Entries { get; } = [new(url.Href, JsValue.Null)];

        public int Index { get; set; }

        /// <summary>The entries of the window's history before and after this document's, which only the host knows.</summary>
        public int Before { get; set; }

        public int After { get; set; }

        public JsValue Object { get; set; }
    }

    /// <summary>
    /// <c>history.length</c>, which in a frame is the page's too: the window's entries -- the page's own and
    /// those around them -- and every entry a frame added (Chromium, measured).
    /// </summary>
    private int JointHistoryLength
    {
        get
        {
            var length = _pageHistory is { } page ? page.Before + page.Entries.Count + page.After : 1;
            foreach (var frame in _frameHistories.Values)
                length += frame.Entries.Count - 1;
            return length;
        }
    }

    private DocumentUrl? _pageDocumentUrl;
    private DocumentHistory? _pageHistory;
    private readonly Dictionary<DomElement, DocumentHistory> _frameHistories = new(ReferenceEqualityComparer.Instance);
    private readonly List<HistoryChange> _historyChanges = [];

    // The host is navigating the page to a fragment itself, so it has its entry already.
    private bool _hostNavigatingToFragment;

    /// <summary>The page's document URL as it is now -- moved by its fragment navigations and its pushState.</summary>
    private string CurrentPageUrl => _pageHistory?.Url.Href ?? _pageUrl;

    /// <summary>What the page's history did that the host has not taken yet, oldest first, and forgets it.</summary>
    public IReadOnlyList<HistoryChange> TakeHistoryChanges()
    {
        var changes = _historyChanges.ToArray();
        _historyChanges.Clear();
        return changes;
    }

    /// <summary>
    /// Tells the page how many entries of the window's history come before and after its own, which
    /// <c>history.length</c> counts and <c>back()</c> and <c>forward()</c> may reach.
    /// </summary>
    public void SetSessionHistory(int before, int after)
    {
        if (_pageHistory is { } history)
        {
            history.Before = Math.Max(0, before);
            history.After = Math.Max(0, after);
        }
    }

    /// <summary>
    /// The host's back or forward by <paramref name="delta"/> entries, when the entry it reaches is one of
    /// the page's own: the page traverses to it, with its <c>popstate</c>. Answers whether it was, and so
    /// whether the page did; the host loads anything else itself.
    /// </summary>
    public bool TraverseHistory(int delta)
    {
        if (_realm is null || _pageHistory is not { } history)
            return false;

        var target = history.Index + delta;
        if (delta == 0 || target < 0 || target >= history.Entries.Count)
            return false;

        TraverseTo(history, target, report: false);
        return true;
    }

    /// <summary>Builds the page's <c>History</c> over its document URL.</summary>
    private JsValue BuildPageHistory(DocumentUrl url)
    {
        _pageHistory = new DocumentHistory(url, frame: null);
        return _pageHistory.Object = BuildHistoryObject(_pageHistory);
    }

    /// <summary>A frame's <c>History</c>, over the URL its Location shows.</summary>
    internal JsValue FrameHistory(DomElement container, DocumentUrl url)
    {
        var history = new DocumentHistory(url, container);
        _frameHistories[container] = history;
        return history.Object = BuildHistoryObject(history);
    }

    /// <summary>The <c>History</c> of the document whose script is running: a frame's, or the page's.</summary>
    private JsValue CurrentHistory()
    {
        if (_windowContext.ResolveCurrentSubWindow() is { } frameWindow)
        {
            var frameHistory = Realm.GetProperty(frameWindow, "history");
            if (frameHistory.IsObject)
                return frameHistory;
        }

        return _pageHistory?.Object ?? JsValue.Undefined;
    }

    private JsValue BuildHistoryObject(DocumentHistory history)
    {
        var realm = Realm;
        var obj = realm.NewObject();
        realm.DefineAccessor(obj, "length", (in _) => JsValue.Number(JointHistoryLength), null);
        realm.DefineAccessor(obj, "state", (in _) => history.Entries[history.Index].State, null);
        realm.DefineValue(obj, "scrollRestoration", JsValue.String("auto"));
        realm.DefineMethod(obj, "pushState", 2, (in call) => AddHistoryEntry(history, in call, replace: false, "pushState"));
        realm.DefineMethod(obj, "replaceState", 2, (in call) => AddHistoryEntry(history, in call, replace: true, "replaceState"));
        realm.DefineMethod(obj, "back", 0, (in _) => Go(history, -1));
        realm.DefineMethod(obj, "forward", 0, (in _) => Go(history, 1));
        realm.DefineMethod(obj, "go", 0, (in call) =>
            Go(history, call.Length > 0 && !call[0].IsUndefined ? (int)Math.Truncate(ToFiniteOrZero(call.Realm.ToNumber(call[0]))) : 0));
        return obj;
    }

    private static double ToFiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    /// <summary><c>pushState</c> and <c>replaceState</c> (HTML "shared history push/replace state steps").</summary>
    private JsValue AddHistoryEntry(DocumentHistory history, in JsCall call, bool replace, string member)
    {
        var realm = call.Realm;
        var state = CloneHistoryState(realm, call.Length > 0 ? call[0] : JsValue.Undefined);

        var newUrl = history.Url.Href;
        if (call.Length > 2 && !call[2].IsNullish)
        {
            var text = realm.ToJsString(call[2]);
            var baseUrl = history.Frame is { } frame ? GetSubDocumentBaseUrl(frame) : DocumentBaseUrl();
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || !Uri.TryCreate(baseUri, text, out var resolved) ||
                !Uri.TryCreate(history.Url.Href, UriKind.Absolute, out var current) || !CanRewriteUrl(current, resolved))
            {
                throw realm.DomError("SecurityError",
                    $"Failed to execute '{member}' on 'History': A history state object with URL '{text}' cannot be created in a document with URL '{history.Url.Href}'.");
            }

            newUrl = resolved.ToString();
        }

        if (replace)
        {
            history.Entries[history.Index] = new HistoryEntry(newUrl, state);
        }
        else
        {
            PushEntry(history, new HistoryEntry(newUrl, state));
        }

        history.Url.Set(newUrl);
        if (history.Frame is null)
            _historyChanges.Add(new HistoryChange(replace ? HistoryChangeKind.Replace : HistoryChangeKind.Push, newUrl));
        return JsValue.Undefined;
    }

    private static void PushEntry(DocumentHistory history, HistoryEntry entry)
    {
        history.Entries.RemoveRange(history.Index + 1, history.Entries.Count - history.Index - 1);
        history.Entries.Add(entry);
        history.Index = history.Entries.Count - 1;
        history.After = 0;
    }

    /// <summary>
    /// The state a push or replace keeps: a structured clone of <paramref name="data"/>, which throws
    /// <c>DataCloneError</c> for what cannot be cloned; <c>undefined</c> is kept as <c>null</c>. On an engine
    /// that cannot clone, the value itself, rather than a <c>pushState</c> that always throws.
    /// </summary>
    private static JsValue CloneHistoryState(IJsRealm realm, JsValue data)
    {
        if (data.IsNullish)
            return JsValue.Null;

        if (!Dom.Features.WorkerTransfer.CanStructuredClone(realm))
            return data;

        try
        {
            return realm.Clone(data, []);
        }
        catch (JsEngineException)
        {
            throw realm.DomError("DataCloneError", "Failed to execute 'pushState' on 'History': The object could not be cloned.");
        }
    }

    /// <summary>
    /// HTML's "can have its URL rewritten": the same scheme, host, port and credentials; for http(s) any
    /// path, query and fragment; for <c>file:</c> the same path; for anything else only another fragment.
    /// </summary>
    private static bool CanRewriteUrl(Uri document, Uri target)
    {
        if (!string.Equals(document.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(document.UserInfo, target.UserInfo, StringComparison.Ordinal) ||
            !string.Equals(document.Host, target.Host, StringComparison.OrdinalIgnoreCase) ||
            document.Port != target.Port)
        {
            return false;
        }

        if (document.Scheme is "http" or "https")
            return true;

        if (document.IsFile)
            return string.Equals(document.AbsolutePath, target.AbsolutePath, StringComparison.Ordinal);

        return string.Equals(document.GetLeftPart(UriPartial.Query), target.GetLeftPart(UriPartial.Query), StringComparison.Ordinal);
    }

    /// <summary>
    /// A fragment navigation of <paramref name="history"/>'s document: an entry for its new URL, or -- for
    /// <c>location.replace</c> -- the current one replaced, both without state; the page's reported to the
    /// host unless the host started it.
    /// </summary>
    private void NoteFragmentNavigation(DocumentHistory history, string newUrl, bool replace)
    {
        if (replace)
            history.Entries[history.Index] = new HistoryEntry(newUrl, JsValue.Null);
        else
            PushEntry(history, new HistoryEntry(newUrl, JsValue.Null));

        if (history.Frame is null && !_hostNavigatingToFragment)
            _historyChanges.Add(new HistoryChange(replace ? HistoryChangeKind.Replace : HistoryChangeKind.Push, newUrl));
    }

    /// <summary>
    /// <c>back()</c>, <c>forward()</c> and <c>go(delta)</c>: a traversal by <paramref name="delta"/> in a later
    /// task -- among the document's own entries here, past them the host's to make; <c>go(0)</c> reloads.
    /// </summary>
    private JsValue Go(DocumentHistory history, int delta)
    {
        if (delta == 0)
        {
            if (history.Frame is { } frame)
                RequestFrameNavigation(frame, new NavigationRequest(history.Url.Href, NavigationKind.Reload));
            else
                RequestNavigation(new NavigationRequest(history.Url.Href, NavigationKind.Reload));
            return JsValue.Undefined;
        }

        var document = history.Frame is { } container ? GetContentDocument(container) : _document;
        _eventLoop.QueueTask(() =>
        {
            if (_realm is null || (history.Frame is { } frameContainer && !ReferenceEquals(GetContentDocument(frameContainer), document)))
                return;

            var target = history.Index + delta;
            if (target >= 0 && target < history.Entries.Count)
            {
                TraverseTo(history, target, report: true);
            }
            else if (history.Frame is null && target >= -history.Before && target < history.Entries.Count + history.After)
            {
                // Another document's entry: the host loads it.
                _historyChanges.Add(new HistoryChange(HistoryChangeKind.TraverseAway, history.Url.Href, delta));
            }
        });

        return JsValue.Undefined;
    }

    /// <summary>
    /// Moves <paramref name="history"/> to its entry <paramref name="target"/>: the URL and the state move,
    /// the element its fragment names is the target, <c>popstate</c> fires with the state, and
    /// <c>hashchange</c> follows in a task when the fragment changed.
    /// </summary>
    private void TraverseTo(DocumentHistory history, int target, bool report)
    {
        var delta = target - history.Index;
        var oldUrl = history.Url.Href;
        var oldFragment = history.Url.Fragment;
        history.Index = target;
        var entry = history.Entries[target];
        history.Url.Set(entry.Url);

        var document = history.Frame is { } frame ? GetContentDocument(frame) : _document;
        if (document is not null)
            SetTargetFromFragment(document, history.Url.Fragment);

        if (report && history.Frame is null)
            _historyChanges.Add(new HistoryChange(HistoryChangeKind.Traverse, entry.Url, delta));

        // hashchange only between two entries of one URL but for the fragment: Chromium fires none for a
        // traversal that moves the path too (measured).
        var fragmentChanged = !string.Equals(oldFragment, history.Url.Fragment, StringComparison.Ordinal) &&
                              string.Equals(WithoutFragment(oldUrl), WithoutFragment(entry.Url), StringComparison.Ordinal);
        if (history.Frame is { } container)
            FireFrameHistoryEvents(container, entry.State, fragmentChanged ? (oldUrl, entry.Url) : null);
        else
            FirePageHistoryEvents(entry.State, fragmentChanged ? (oldUrl, entry.Url) : null);
    }

    private static string WithoutFragment(string url)
    {
        var hash = url.IndexOf('#');
        return hash >= 0 ? url[..hash] : url;
    }

    private void ResetSessionHistories()
    {
        _pageDocumentUrl = null;
        _pageHistory = null;
        _frameHistories.Clear();
        _historyChanges.Clear();
        _hostNavigatingToFragment = false;
    }
}
