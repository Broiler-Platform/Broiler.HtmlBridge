using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The page's joint session history -- the entries <c>pushState</c>, <c>replaceState</c> and fragment
/// navigations make in the page and in its frames, and a frame's navigations to other documents -- and the
/// traversals <c>back</c>, <c>forward</c> and <c>go</c> make between them, as each document's <c>History</c>
/// object shows it, reported to the host so its back and forward go through them too.
/// </summary>
/// <remarks>
/// <para>
/// <b>History was a stub</b>, and then one history per document: <c>pushState</c> moved its own document's
/// URL and <c>back()</c> went back through that document's entries alone, so a page could not go back
/// through what its frame did, and no traversal put the scroll back where it was.
/// </para>
/// <para>
/// <b>As Chromium keeps it, measured.</b> <c>pushState</c> moves the document's URL -- <c>href</c>,
/// <c>pathname</c>, <c>search</c>, <c>document.URL</c> -- at once, adds an entry and makes the state a clone
/// of the one given; <c>replaceState</c> replaces the entry; a URL of another origin is a
/// <c>SecurityError</c>; neither fires an event, moves <c>:target</c> or scrolls. <c>back()</c>,
/// <c>forward()</c> and <c>go(n)</c> traverse in a later task: the URL and the state move, <c>popstate</c>
/// fires with the entry's state, <c>hashchange</c> follows in a task of its own when only the fragment
/// changed, and then the scroll is put back where the entry was left -- unless the entry's
/// <c>scrollRestoration</c> is <c>manual</c>, which is each entry's own. Past either end they do nothing,
/// and <c>go(0)</c> reloads.
/// </para>
/// <para>
/// <b>One history for the page and its frames.</b> Every entry is added at a step of the joint session
/// history, the page's and every frame's alike, and adding one forgets every step after the current one, in
/// every document. A traversal goes to a step: each document goes to its entry for it, and only those whose
/// entry changed hear of it. A frame's navigation to another document is an entry too, and traversing back
/// to it loads that document again. <c>history.length</c> counts the steps, and the window's entries
/// around the page's.
/// </para>
/// <para>
/// <b>The joint history is the host's too.</b> The host keeps the window's history, of which the page's
/// steps are a run: every step added -- a frame's at the page's URL -- and every replace and traversal is
/// reported (<see cref="TakeHistoryChanges"/>), a traversal past the steps is the host's to make, and the
/// host's back and forward between them come here (<see cref="TraverseHistory"/>).
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// One entry of a document's session history: its URL, the state its push or replace gave it, the joint
    /// step it was added at, which of its frame's documents it is in, and where its document was scrolled when
    /// it was left, which a traversal back to it restores unless the page asked it not to.
    /// </summary>
    private sealed class HistoryEntry(string url, JsValue state, int step, int document)
    {
        public string Url { get; } = url;

        public JsValue State { get; } = state;

        public int Step { get; } = step;

        public int Document { get; } = document;

        public (double X, double Y) Scroll { get; set; }

        public bool ManualScrollRestoration { get; set; }

        /// <summary>This entry with another URL and state, as a replace leaves it.</summary>
        public HistoryEntry Replaced(string url, JsValue state, int? document = null) =>
            new(url, state, Step, document ?? Document) { Scroll = Scroll, ManualScrollRestoration = ManualScrollRestoration };
    }

    /// <summary>The session history of the page, or of a frame across the documents it shows.</summary>
    private sealed class DocumentHistory
    {
        public DocumentHistory(DocumentUrl url, DomElement? frame, int step)
        {
            Url = url;
            Frame = frame;
            Entries.Add(new HistoryEntry(url.Href, JsValue.Null, step, 0));
        }

        /// <summary>The URL of the document shown, which its Location shares.</summary>
        public DocumentUrl Url { get; set; }

        /// <summary>The frame whose documents these are, or null for the page.</summary>
        public DomElement? Frame { get; }

        public List<HistoryEntry> Entries { get; } = [];

        public int Index { get; set; }

        /// <summary>Which of the frame's documents is shown, as its entries number them.</summary>
        public int Document { get; set; }

        /// <summary>The entries of the window's history before and after the page's, which only the host knows.</summary>
        public int Before { get; set; }

        public int After { get; set; }

        public JsValue Object { get; set; }

        public HistoryEntry Current => Entries[Index];
    }

    /// <summary>How a frame's next document enters its history: a new entry, in place of the current one, or as the entry a traversal went to.</summary>
    private enum FrameHistoryHandling
    {
        Push,
        Replace,
        Traverse,
    }

    /// <summary><c>history.length</c>, the page's and a frame's alike: the joint history's steps and the window's entries around them (Chromium, measured).</summary>
    private int JointHistoryLength => (_pageHistory is { } page ? page.Before + page.After : 0) + _lastStep + 1;

    private DocumentUrl? _pageDocumentUrl;
    private DocumentHistory? _pageHistory;
    private readonly Dictionary<DomElement, DocumentHistory> _frameHistories = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DomElement, FrameHistoryHandling> _frameHistoryLoads = new(ReferenceEqualityComparer.Instance);
    private readonly List<HistoryChange> _historyChanges = [];

    // The joint session history's step the documents are at, and its last.
    private int _currentStep;
    private int _lastStep;

    // The host is navigating the page to a fragment itself, so it has its entry already.
    private bool _hostNavigatingToFragment;

    /// <summary>The page's document URL as it is now -- moved by its fragment navigations and its pushState.</summary>
    private string CurrentPageUrl => _pageHistory?.Url.Href ?? _pageUrl;

    /// <summary>What the joint history did that the host has not taken yet, oldest first, and forgets it.</summary>
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
    /// The host's back or forward by <paramref name="delta"/> entries, when the entry it reaches is one of the
    /// page's steps: the documents traverse to it, with their <c>popstate</c>. Answers whether it was, and so
    /// whether they did; the host loads anything else itself.
    /// </summary>
    public bool TraverseHistory(int delta)
    {
        if (_realm is null || _pageHistory is null)
            return false;

        var target = _currentStep + delta;
        if (delta == 0 || target < 0 || target > _lastStep)
            return false;

        TraverseToStep(target, report: false);
        return true;
    }

    /// <summary>Builds the page's <c>History</c> over its document URL, at the first step.</summary>
    private JsValue BuildPageHistory(DocumentUrl url)
    {
        _currentStep = _lastStep = 0;
        _pageHistory = new DocumentHistory(url, frame: null, step: 0);
        return _pageHistory.Object = BuildHistoryObject(_pageHistory);
    }

    /// <summary>
    /// A frame's <c>History</c>, over the URL its Location shows. The frame's first document starts its
    /// history at the current step; a later one is an entry of it -- a new one, the current one replaced, or
    /// the one a traversal went to -- so that going back reaches the documents it showed before.
    /// </summary>
    internal JsValue FrameHistory(DomElement container, DocumentUrl url)
    {
        if (_frameHistories.TryGetValue(container, out var history) && container.IsConnected)
        {
            history.Url = url;
            switch (_frameHistoryLoads.Remove(container, out var handling) ? handling : FrameHistoryHandling.Replace)
            {
                case FrameHistoryHandling.Push:
                    PushEntry(history, url.Href, JsValue.Null, newDocument: true);
                    _historyChanges.Add(new HistoryChange(HistoryChangeKind.Push, CurrentPageUrl));
                    break;

                case FrameHistoryHandling.Replace:
                    history.Entries[history.Index] = history.Current.Replaced(url.Href, JsValue.Null, ++history.Document);
                    break;

                case FrameHistoryHandling.Traverse:
                    // The traversal moved the history to the entry already; this document is that entry's.
                    break;
            }
        }
        else
        {
            history = new DocumentHistory(url, container, _currentStep);
            _frameHistories[container] = history;
            _frameHistoryLoads.Remove(container);
        }

        return history.Object = BuildHistoryObject(history);
    }

    /// <summary>A frame is about to leave its document: the entry it leaves keeps where it was scrolled, and the next document comes in as <paramref name="handling"/> says.</summary>
    private void NoteFrameNavigation(DomElement container, FrameHistoryHandling handling)
    {
        if (!_frameHistories.TryGetValue(container, out var history))
            return;

        if (handling != FrameHistoryHandling.Traverse)
            SaveScroll(history);
        _frameHistoryLoads[container] = handling;
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
        realm.DefineAccessor(obj, "state", (in _) => history.Current.State, null);
        realm.DefineAccessor(obj, "scrollRestoration",
            (in _) => JsValue.String(history.Current.ManualScrollRestoration ? "manual" : "auto"),
            (in call) =>
            {
                // An enumeration: any other value is ignored.
                var value = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
                if (value is "auto" or "manual")
                    history.Current.ManualScrollRestoration = value == "manual";
                return JsValue.Undefined;
            });
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
            history.Entries[history.Index] = history.Current.Replaced(newUrl, state);
        else
            PushEntry(history, newUrl, state);

        history.Url.Set(newUrl);
        ReportEntry(history, replace);
        return JsValue.Undefined;
    }

    /// <summary>Tells the host of an entry added or replaced: the page's at its URL, a frame's added one at the page's.</summary>
    private void ReportEntry(DocumentHistory history, bool replace)
    {
        if (history.Frame is null)
            _historyChanges.Add(new HistoryChange(replace ? HistoryChangeKind.Replace : HistoryChangeKind.Push, history.Url.Href));
        else if (!replace)
            _historyChanges.Add(new HistoryChange(HistoryChangeKind.Push, CurrentPageUrl));
    }

    /// <summary>
    /// Adds <paramref name="history"/> an entry at a new step of the joint history, every document forgetting
    /// what came after the current step first; the entry left keeps where its document was scrolled -- a
    /// frame's leaving its document kept that before the document went (<see cref="NoteFrameNavigation"/>).
    /// </summary>
    private void PushEntry(DocumentHistory history, string url, JsValue state, bool newDocument = false)
    {
        if (!newDocument)
            SaveScroll(history);

        foreach (var each in AllHistories())
        {
            each.Entries.RemoveAll(entry => entry.Step > _currentStep);
            each.Index = Math.Min(each.Index, each.Entries.Count - 1);
        }

        if (_pageHistory is { } page)
            page.After = 0;

        _lastStep = ++_currentStep;
        if (newDocument)
            history.Document++;
        history.Entries.Add(new HistoryEntry(url, state, _currentStep, history.Document));
        history.Index = history.Entries.Count - 1;
    }

    /// <summary>The page's history and its frames', the page's first.</summary>
    private IEnumerable<DocumentHistory> AllHistories()
    {
        if (_pageHistory is { } page)
            yield return page;
        foreach (var frame in _frameHistories.Values)
            yield return frame;
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
    /// <c>location.replace</c> -- the current one replaced, both without state; reported to the host unless the
    /// host started it.
    /// </summary>
    private void NoteFragmentNavigation(DocumentHistory history, string newUrl, bool replace)
    {
        if (replace)
            history.Entries[history.Index] = history.Current.Replaced(newUrl, JsValue.Null);
        else
            PushEntry(history, newUrl, JsValue.Null);

        if (history.Frame is not null || !_hostNavigatingToFragment)
            ReportEntry(history, replace);
    }

    /// <summary>
    /// <c>back()</c>, <c>forward()</c> and <c>go(delta)</c>: a traversal by <paramref name="delta"/> steps in a
    /// later task -- among the joint history's steps here, past them the host's to make; <c>go(0)</c> reloads.
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

            var target = _currentStep + delta;
            if (target >= 0 && target <= _lastStep)
            {
                TraverseToStep(target, report: true);
            }
            else if (_pageHistory is { } page && target >= -page.Before && target <= _lastStep + page.After)
            {
                // Another document's entry: the host loads it.
                _historyChanges.Add(new HistoryChange(HistoryChangeKind.TraverseAway, CurrentPageUrl, delta));
            }
        });

        return JsValue.Undefined;
    }

    /// <summary>
    /// Moves the joint history to <paramref name="step"/>: each document goes to its entry for that step --
    /// the last one added at or before it -- and those whose entry changed traverse; the host hears of it
    /// when the page asked.
    /// </summary>
    private void TraverseToStep(int step, bool report)
    {
        var from = _currentStep;
        _currentStep = step;
        foreach (var history in AllHistories().ToArray())
        {
            if (history.Frame is { IsConnected: false })
                continue;

            var index = Math.Max(0, history.Entries.FindLastIndex(entry => entry.Step <= step));
            if (index != history.Index)
                TraverseDocument(history, index);
        }

        if (report)
            _historyChanges.Add(new HistoryChange(HistoryChangeKind.Traverse, CurrentPageUrl, step - from));
    }

    /// <summary>
    /// Moves <paramref name="history"/> to its entry <paramref name="index"/>. An entry of the document shown:
    /// the URL and the state move, the element its fragment names is the target, <c>popstate</c> fires with
    /// the state, <c>hashchange</c> follows in a task when only the fragment changed, and the scroll goes back
    /// to where the entry was left. An entry of another of a frame's documents: that document loads again.
    /// </summary>
    private void TraverseDocument(DocumentHistory history, int index)
    {
        var entry = history.Entries[index];
        if (history.Frame is { } frame && entry.Document != history.Document)
        {
            SaveScroll(history);
            history.Index = index;
            history.Document = entry.Document;
            RequestFrameNavigation(frame, new NavigationRequest(entry.Url, NavigationKind.Replace), history: FrameHistoryHandling.Traverse);
            return;
        }

        SaveScroll(history);
        var oldUrl = history.Url.Href;
        var oldFragment = history.Url.Fragment;
        history.Index = index;
        history.Url.Set(entry.Url);

        var document = history.Frame is { } container ? GetContentDocument(container) : _document;
        if (document is not null)
            SetTargetFromFragment(document, history.Url.Fragment);

        // hashchange only between two entries of one URL but for the fragment: Chromium fires none for a
        // traversal that moves the path too (measured).
        var fragmentChanged = !string.Equals(oldFragment, history.Url.Fragment, StringComparison.Ordinal) &&
                              string.Equals(WithoutFragment(oldUrl), WithoutFragment(entry.Url), StringComparison.Ordinal);
        if (history.Frame is { } framed)
            FireFrameHistoryEvents(framed, entry.State, fragmentChanged ? (oldUrl, entry.Url) : null);
        else
            FirePageHistoryEvents(entry.State, fragmentChanged ? (oldUrl, entry.Url) : null);

        // After popstate, which still sees the scroll the traversal left (measured).
        if (!entry.ManualScrollRestoration && DocumentRootOf(history) is { } root)
            SetElementScrollOffsetsWithBehavior(root, entry.Scroll.X, entry.Scroll.Y, behavior: "instant");
    }

    /// <summary>The current entry of <paramref name="history"/> keeps where its document is scrolled.</summary>
    private void SaveScroll(DocumentHistory history)
    {
        if (DocumentRootOf(history) is { } root)
            history.Current.Scroll = (GetElementScrollOffset(root, vertical: false), GetElementScrollOffset(root, vertical: true));
    }

    /// <summary>The root element of the document <paramref name="history"/> shows, if it is there.</summary>
    private DomElement? DocumentRootOf(DocumentHistory history) =>
        history.Frame is { } frame
            ? GetContentDocument(frame) is { } content ? ChildElements(content).FirstOrDefault(static child => !child.TagName.StartsWith('#')) : null
            : DocumentElement;

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
        _frameHistoryLoads.Clear();
        _historyChanges.Clear();
        _currentStep = _lastStep = 0;
        _hostNavigatingToFragment = false;
    }
}
