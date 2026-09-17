using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// CSS View Transitions (Level 1/2) — the <c>document.startViewTransition()</c> entry point plus
/// the static-screenshot subset of its rendering. A view transition runs an author callback that
/// mutates the DOM, then paints a top-layer tree of <c>::view-transition-*</c> pseudo-elements that
/// snapshot the old and new states of every element carrying a <c>view-transition-name</c>.
/// <para>
/// A live browser animates that pseudo tree. WPT reftests instead pause the animations and pin the
/// old/new opacities so the screenshot is a deterministic still (e.g. the new snapshot at
/// <c>opacity:1</c> over an author-coloured <c>::view-transition</c> backdrop, at the outgoing
/// element's position). This partial reproduces that still: it snapshots each named element's
/// geometry before the callback (the "old" capture) and after (the "new"), applies the
/// <c>:active-view-transition-type()</c> conditional rules a transition activates, and materialises
/// the <c>::view-transition</c> overlay tree as real positioned boxes the renderer already knows how
/// to paint. The animation timeline itself is out of scope; the tests that need it are the ones that
/// screenshot mid-animation with unpinned timing.
/// </para>
/// <para>
/// The pseudo-tree bake remains separate from <c>ApplySerializationTransforms</c> because capturing
/// old geometry probes layout during the script. <see cref="ApplyViewTransitionRendering"/> runs on
/// each fresh serialize/render projection after the compatibility transforms and skips
/// geometry-snapshot passes (<see cref="_layoutGeometryPassActive"/>).
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The active view transition, or <c>null</c> when none is running.</summary>
    private ViewTransitionState? _activeViewTransition;

    private sealed class ViewTransitionState
    {
        /// <summary>
        /// The page observed <c>finished</c> while its screenshot was still pending, so the
        /// transition is over as far as the page is concerned — but whether the page went on to
        /// release the screenshot cannot be read yet. See <see cref="FinishedThenable"/>.
        /// </summary>
        public bool FinishedObserved { get; set; }

        /// <summary>
        /// A <c>ready</c> callback is what released the reftest's screenshot, so the still being
        /// captured is the <em>running</em> transition however the page went on to observe
        /// <c>finished</c>. Without this, a page that attaches a cleanup handler to <c>finished</c>
        /// and screenshots from <c>ready</c> — the shape every css-view-transitions-2 nested test
        /// has, through <c>resources/compute-test.js</c> — looked identical at bake time to one
        /// that screenshots from <c>finished</c>: both leave <c>reftest-wait</c> gone and
        /// <see cref="FinishedObserved"/> set. See <see cref="FinishedThenable"/>.
        /// </summary>
        public bool ScreenshotReleasedByReady { get; set; }

        /// <summary>The transition's active types (the <c>types</c> option), matched by
        /// <c>:active-view-transition-type()</c>.</summary>
        public HashSet<string> Types { get; } = new(System.StringComparer.Ordinal);

        /// <summary>The "old" capture: geometry and background of each named element as it stood
        /// before the update callback ran, keyed by <c>view-transition-name</c>. The group is
        /// positioned at this start geometry, which the reftests freeze it at.</summary>
        public Dictionary<string, NamedSnapshot> OldCaptures { get; } = new(System.StringComparer.Ordinal);

        /// <summary>Generated used values for elements whose <c>view-transition-name</c> is
        /// <c>auto</c>/<c>match-element</c> (css-view-transitions-2). Keyed by element identity so the
        /// same element resolves to the same name across the old and new captures — the two snapshots
        /// must pair into one group — and stays stable for the transition's lifetime.</summary>
        public Dictionary<DomElement, string> AutoNames { get; } = new();

        /// <summary>
        /// What each nested browsing context on the page was <em>displaying</em> when the old state
        /// was captured, keyed by its browsing-context root.
        /// <para>The root snapshot is a picture of the whole page, frames included, so a document
        /// frozen showing its old snapshot must show the frames as they were then — not as they are
        /// now. WPT <c>iframe-and-main-frame-transition-old-main</c> says so in as many words: it
        /// starts a transition <em>inside</em> the frame after the main one has been captured and
        /// expects the change not to show, "because the old screenshot on the main frame still has
        /// the iframe's old content".</para>
        /// </summary>
        public Dictionary<DomNode, string> FrameMarkupAtCapture { get; } = new();
    }

    private readonly record struct NamedSnapshot(
        double Left, double Top, double Width, double Height, string BackgroundColor,
        // A detached box reproducing the element's painted border box (computed paint style baked
        // inline + a clone of its content), or null for the implicit root capture (never shown as a
        // content snapshot in the reftests) and for a name present on only one side.
        DomElement? Content = null);

    // ── JS API ──────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>document.startViewTransition(updateCallback)</c> /
    /// <c>document.startViewTransition({ update, types })</c>. Snapshots the old state, records the
    /// active transition and its types, runs the update callback synchronously (its DOM mutation is
    /// the "new" state the screenshot captures), and returns a <c>ViewTransition</c> whose
    /// <c>ready</c>/<c>finished</c>/<c>updateCallbackDone</c> promises are already resolved — the
    /// reftests gate their screenshot on <c>ready</c>, so resolving synchronously lets that fire once
    /// the new DOM is in place.
    /// </summary>
    /// <param name="options">
    /// The one argument the operation takes: the update callback, the dictionary carrying it, or
    /// <see cref="JsValue.Missing"/> when the page passed nothing.
    /// </param>
    internal JsValue StartViewTransition(JsValue options)
    {
        var realm = Realm;
        var state = new ViewTransitionState();
        var updateCallback = JsValue.Missing;

        if (options.IsFunction)
        {
            updateCallback = options;
        }
        else if (options.IsObject)
        {
            var update = realm.GetProperty(options, "update");
            if (update.IsFunction)
                updateCallback = update;
            CollectViewTransitionTypes(realm, realm.GetProperty(options, "types"), state.Types);
        }

        _activeViewTransition = state;

        // The type activates immediately, for the whole transition including the old capture (WPT
        // view-transition-types-match-early: it activates "before tag discovery"). Bake its rules
        // now so the old snapshot reflects them, then snapshot the old state before the callback
        // mutates the DOM. Both are best-effort: a probe this early must never abort the call, and
        // the pseudo-tree bake is still deferred to serialize time (the geometry probe here runs a
        // render snapshot but ApplyViewTransitionRendering skips geometry passes).
        try
        {
            ApplyActiveViewTransitionTypeRules(DocumentElement);
            CaptureOldViewTransitionState(state);
        }
        catch (System.Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.startViewTransition",
                $"Old view-transition capture failed: {ex.Message}", ex);
        }

        if (updateCallback.IsFunction)
        {
            try
            {
                // The callback is its own receiver — what the engine-typed call frame this replaces
                // passed, and what every thenable below still passes.
                realm.Invoke(updateCallback, updateCallback);
            }
            catch (System.Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.startViewTransition",
                    $"View transition update callback threw: {ex.Message}", ex);
            }
        }

        return BuildViewTransitionObject(state);
    }

    private JsValue BuildViewTransitionObject(ViewTransitionState state)
    {
        var realm = Realm;
        var transition = realm.NewObject();
        realm.DefineValue(transition, "ready", ReadyThenable(state));
        // `finished` resolving means the transition is over: the ::view-transition tree has been
        // removed and the DOM is back to its plain final state. A reftest that screenshots from
        // finished (rather than ready) therefore expects the final DOM, not the pseudo tree — so
        // realizing this thenable clears the active transition, and the serialize-time bake becomes
        // a no-op (WPT element-stops-grouping-after-animation).
        realm.DefineValue(transition, "finished", FinishedThenable());
        realm.DefineValue(transition, "updateCallbackDone", ResolvedThenable());

        var types = new JsValue[state.Types.Count];
        var next = 0;
        foreach (var type in state.Types)
            types[next++] = JsValue.String(type);
        realm.DefineValue(transition, "types", realm.NewArray(types));

        // skipTransition() ends the transition without animating; the still is already the final
        // state here, so it is a no-op beyond clearing the active state.
        realm.DefineValue(transition, "skipTransition",
            realm.NewMethod("skipTransition", (in _) => { _activeViewTransition = null; return JsValue.Undefined; }, 0));

        return transition;
    }

    /// <summary>A minimal already-resolved thenable, mirroring the bridge's synchronous-promise
    /// pattern (see FetchBinding): <c>then</c> invokes its callback immediately with
    /// <c>undefined</c> and returns a thenable so <c>.then().then()</c> chains, and the rAF the
    /// reftests schedule from it is pumped by the event loop as usual.</summary>
    /// <remarks>
    /// <c>finally</c> passes no argument and swallows a throw without logging it, where <c>then</c>
    /// passes <c>undefined</c> and logs — preserved as it stood rather than unified, because a
    /// callback that can tell the two apart is a page that can see the difference.
    /// </remarks>
    private JsValue ResolvedThenable()
    {
        var realm = Realm;
        var thenable = realm.NewObject();

        void RunThen(JsValue cb)
        {
            if (!cb.IsFunction)
                return;

            try { realm.Invoke(cb, cb, [JsValue.Undefined]); }
            catch (System.Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.viewTransition.then",
                    $"View transition promise callback threw: {ex.Message}", ex);
            }
        }

        void RunFinally(JsValue cb)
        {
            if (!cb.IsFunction)
                return;

            try { realm.Invoke(cb, cb); }
            catch { }
        }

        realm.DefineValue(thenable, "then",
            realm.NewMethod("then", (in call) => { RunThen(ThenCallback(in call)); return thenable; }, 1));
        realm.DefineValue(thenable, "catch", realm.NewMethod("catch", (in _) => thenable, 1));
        realm.DefineValue(thenable, "finally",
            realm.NewMethod("finally", (in call) => { RunFinally(ThenCallback(in call)); return thenable; }, 1));
        return thenable;
    }

    /// <summary>
    /// The <c>ready</c> promise: a resolved thenable that additionally records when its own callback
    /// is the one that released the reftest's screenshot.
    /// <para>
    /// A page that screenshots from <c>ready</c> is capturing the <em>running</em> transition — the
    /// pseudo tree must still bake — while one that screenshots from <c>finished</c> wants the plain
    /// final DOM. <see cref="FinishedThenable"/> tells them apart by watching for the
    /// <c>reftest-wait</c> class disappearing, but it can only re-read that class at bake time, long
    /// after both chains have run. A page that does both — a cleanup handler on <c>finished</c> and
    /// <c>takeScreenshot</c> on <c>ready</c>, which is exactly what the css-view-transitions-2
    /// nested tests do through <c>resources/compute-test.js</c> — is indistinguishable from the
    /// screenshot-on-finished shape at that point, and the transition was torn down: the whole
    /// nested cluster rendered its own red page instead of the green pseudo tree. Noting the
    /// release here settles it at the moment it happens.
    /// </para>
    /// </summary>
    private JsValue ReadyThenable(ViewTransitionState state)
    {
        var realm = Realm;

        bool ScreenshotPending() =>
            (GetAttr(DocumentElement, "class") ?? string.Empty)
                .Contains("reftest-wait", System.StringComparison.Ordinal);

        void Run(JsValue cb)
        {
            bool waitBefore = ScreenshotPending();
            if (cb.IsFunction)
            {
                try { realm.Invoke(cb, cb, [JsValue.Undefined]); }
                catch (System.Exception ex)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.viewTransition.ready.then",
                        $"View transition ready callback threw: {ex.Message}", ex);
                }
            }

            if (waitBefore && !ScreenshotPending())
                state.ScreenshotReleasedByReady = true;
        }

        var thenable = realm.NewObject();
        realm.DefineValue(thenable, "then",
            realm.NewMethod("then", (in call) => { Run(ThenCallback(in call)); return thenable; }, 1));
        realm.DefineValue(thenable, "catch", realm.NewMethod("catch", (in _) => thenable, 1));
        realm.DefineValue(thenable, "finally",
            realm.NewMethod("finally", (in call) => { Run(ThenCallback(in call)); return thenable; }, 1));
        return thenable;
    }

    /// <summary>The <c>finished</c> promise as an already-resolved thenable that first marks the
    /// transition complete — clearing <see cref="_activeViewTransition"/> so the serialize-time pseudo
    /// tree bake is skipped and the plain final DOM renders — then, like <see cref="ResolvedThenable"/>,
    /// invokes the callback (e.g. the reftest's <c>takeScreenshot</c>) and chains.</summary>
    private JsValue FinishedThenable()
    {
        var realm = Realm;

        // reftest-wait is removed by the reftest's takeScreenshot(). If the finished callback is the
        // one that removes it, the screenshot is being taken from `finished` — the transition is over
        // and the still must be the plain final DOM, so clear the active transition (the serialize-time
        // bake becomes a no-op; WPT element-stops-grouping-after-animation). If the callback only did
        // cleanup and left reftest-wait pending (the screenshot comes later from a `ready` chain — WPT
        // css-view-transitions-2 nested tests via compute-test.js), keep the transition so the pseudo
        // tree still bakes.
        bool ScreenshotPending() =>
            (GetAttr(DocumentElement, "class") ?? string.Empty)
                .Contains("reftest-wait", System.StringComparison.Ordinal);

        void RunAndMaybeFinish(JsValue cb)
        {
            bool waitBefore = ScreenshotPending();
            if (cb.IsFunction)
            {
                try { realm.Invoke(cb, cb, [JsValue.Undefined]); }
                catch (System.Exception ex)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.viewTransition.finished.then",
                        $"View transition finished callback threw: {ex.Message}", ex);
                }
            }
            if (!waitBefore)
                return;

            // A `ready` callback that already took the screenshot settles it: the still is of the
            // running transition, so observing `finished` afterwards must not tear the pseudo tree
            // down. (Ordering does not matter — the nested tests attach to `finished` first.)
            if (_activeViewTransition?.ScreenshotReleasedByReady == true)
                return;

            if (!ScreenshotPending())
            {
                _activeViewTransition = null;
                return;
            }

            // The callback has not released the screenshot *yet*, which does not mean it never
            // will. `await transition.finished` hands us the async function's continuation, and
            // invoking that only schedules the resumption — the `reftest-wait` removal happens in a
            // microtask, after this check has already run. Reading it here therefore says "still
            // pending" for every await-based test, which kept the transition active and left its
            // pseudo tree baked over the finished page (WPT reset-state-after-scrolled-view-
            // transition, issue #1544 problem 26, where a flat root-snapshot fill covered the whole
            // viewport). Record the observation and let the serialize-time bake re-read the class,
            // by which point any microtask has run.
            if (_activeViewTransition is not null)
                _activeViewTransition.FinishedObserved = true;
        }

        var thenable = realm.NewObject();
        realm.DefineValue(thenable, "then",
            realm.NewMethod("then", (in call) => { RunAndMaybeFinish(ThenCallback(in call)); return thenable; }, 1));
        realm.DefineValue(thenable, "catch", realm.NewMethod("catch", (in _) => thenable, 1));
        realm.DefineValue(thenable, "finally",
            realm.NewMethod("finally", (in call) => { RunAndMaybeFinish(ThenCallback(in call)); return thenable; }, 1));
        return thenable;
    }
    // ── Serialize-time rendering ────────────────────────────────────────────

    /// <summary>
    /// Renders a running view transition: applies the <c>:active-view-transition-type()</c> rules it
    /// activates and materialises the <c>::view-transition</c> pseudo tree. Invoked from the
    /// serialize/render entry points after the compatibility serialization transforms, so its own layout
    /// probes (already spent capturing the old state) cannot swallow it. Skips geometry-snapshot
    /// passes so a mid-script <c>getBoundingClientRect()</c> never bakes the overlay. Each call
    /// operates on a fresh projection, so repeated renders stay idempotent without a live-tree guard.
    /// </summary>
    private void ApplyViewTransitionRendering(DomElement root)
    {
        if (_activeViewTransition is null || _layoutGeometryPassActive)
            return;

        // A transition whose `finished` the page observed is over once that page releases its
        // screenshot; the release may have landed in a microtask after `finished` returned, so this
        // is the first point that can see it. Nothing to bake for a transition that has ended —
        // unless a `ready` callback is what released the screenshot, in which case the still is of
        // the running transition and the missing `reftest-wait` says nothing about `finished`.
        if (_activeViewTransition.FinishedObserved
            && !_activeViewTransition.ScreenshotReleasedByReady
            && !(GetAttr(DocumentElement, "class") ?? string.Empty)
                .Contains("reftest-wait", System.StringComparison.Ordinal))
        {
            _activeViewTransition = null;
            return;
        }

        ApplyActiveViewTransitionTypeRules(root);
        ApplyViewTransitionPseudoTree(root);
    }

    /// <summary>Records the geometry and background of every element with a used
    /// <c>view-transition-name</c> (plus the implicit root) as it stands now — the "old" snapshot
    /// captured before the update callback runs.</summary>
    private void CaptureOldViewTransitionState(ViewTransitionState state)
    {
        var rootStyle = UsedStyleForCapture(DocumentElement);
        var rootName = ResolveRootViewTransitionName(rootStyle);
        if (rootName is not null)
        {
            var (l, t, w, h) = GetBoundingClientRectForDomElement(DocumentElement, isRoot: true);
            // Content-less unless the live page provably cannot stand in for this snapshot — see
            // RootSnapshotNeedsContent. The fallback fill stays exactly what it was:
            // AttachSnapshotPaint only uses it when there is no content box, i.e. on the ungated
            // path, which must keep behaving as before.
            state.OldCaptures[rootName] = new NamedSnapshot(l, t, w, h,
                rootStyle.GetValueOrDefault("background-color") ?? "transparent",
                RootSnapshotNeedsContent(DocumentElement, "old")
                    ? BuildRootViewTransitionSnapshotContent(rootStyle)
                    : null);
        }

        foreach (var element in DocumentElement.Descendants().OfType<DomElement>())
        {
            var style = UsedStyleForCapture(element);
            var name = ResolveUsedViewTransitionName(element, style.GetValueOrDefault("view-transition-name"));
            // A descendant carrying the root's name would collide with the root capture; one carrying
            // the literal "root" while the root is renamed is a separate, legitimate capture.
            if (name is null || string.Equals(name, rootName, System.StringComparison.Ordinal))
                continue;

            var (l, t, w, h) = GetBoundingClientRectForDomElement(element, isRoot: false);
            (l, t) = ToSnapshotContainingBlockCoordinates(element, l, t);
            // Snapshot the old content now, before the update callback mutates (or removes) the
            // element — the "old" image must show its pre-callback state.
            state.OldCaptures[name] = new NamedSnapshot(l, t, w, h,
                style.GetValueOrDefault("background-color") ?? "transparent",
                BuildViewTransitionSnapshotContent(element));
        }

        CaptureFrameMarkup(state);
    }

    /// <summary>
    /// Records what every nested browsing context on the page is displaying right now, so a root
    /// snapshot frozen at this moment can keep showing it. See
    /// <see cref="ViewTransitionState.FrameMarkupAtCapture"/>.
    /// <para>What is recorded is the <em>effective</em> markup, not the live sub-tree: a frame already
    /// holding its own old snapshot is displaying that, and the page's root snapshot has to agree
    /// with what was on screen.</para>
    /// <para>Only frames that are <em>part of</em> the root snapshot are recorded. A frame carrying
    /// its own <c>view-transition-name</c> is captured as its own group instead, and that group's
    /// rules — not the root's — decide which of its states shows: WPT
    /// <c>sibling-frames-transition</c> and <c>-with-name-on-iframe</c> both freeze the root on its
    /// old snapshot while pinning the named frames to their <em>new</em> state, and say so in their
    /// comments ("the iframe is showing the live screenshot").</para>
    /// </summary>
    private void CaptureFrameMarkup(ViewTransitionState state)
    {
        foreach (var element in DocumentElement.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "iframe", System.StringComparison.OrdinalIgnoreCase)
                || !HasAttr(element, "srcdoc"))
            {
                continue;
            }

            var style = UsedStyleForCapture(element);
            if (ResolveUsedViewTransitionName(element, style.GetValueOrDefault("view-transition-name")) is not null)
                continue;

            if (GetContentDocument(element) is { } frameRoot
                && EffectiveSubDocumentMarkup(frameRoot) is { Length: > 0 } markup)
            {
                state.FrameMarkupAtCapture[frameRoot] = markup;
            }
        }
    }

    /// <summary>
    /// Converts a captured rect's origin from the live layout's document coordinates into the
    /// snapshot containing block — i.e. subtracts the page scroll.
    /// <para>
    /// The old and new captures both call <see cref="GetBoundingClientRectForDomElement"/>, but at
    /// different moments against different layouts, and only one of them has the scroll folded in.
    /// The new capture runs on the render projection, where the scroll is already baked into box
    /// positions; the old one runs during script, against a layout where it is not. So a page that
    /// scrolls and *then* starts a transition captured its old geometry unscrolled while the new
    /// geometry was correct — measured on WPT
    /// <c>massive-element-left-of-viewport-partially-onscreen</c> (issue #1538 problems 22/23/25/26)
    /// as <c>old=(8,8,…)</c> against <c>new=(-38986,8,…)</c>, where the page had scrolled 38 994px.
    /// The <c>-old</c> variants paint <c>::view-transition-old</c>, so they showed the element's
    /// leading edge where the reference shows its trailing one.
    /// </para>
    /// <para>
    /// A <c>position: fixed</c> element — or anything inside one — does not move with the page, so
    /// its document coordinates are already viewport coordinates and subtracting the scroll would
    /// push it off by the scroll amount. Measured: without this exception
    /// <c>new-content-transform-position-fixed</c> falls from 100% to 98.73%.
    /// </para>
    /// <para>
    /// Only the page scroll is subtracted, which is what these tests exercise and what the render
    /// bake accounts for. An element inside a scrolled sub-container is not adjusted here.
    /// </para>
    /// </summary>
    private (double Left, double Top) ToSnapshotContainingBlockCoordinates(DomElement element, double left, double top)
    {
        if (DocumentElement is not { } documentElement || HasFixedPositionAncestorOrSelf(element))
            return (left, top);

        return (left - GetElementScrollOffset(documentElement, vertical: false),
                top - GetElementScrollOffset(documentElement, vertical: true));
    }

    private bool HasFixedPositionAncestorOrSelf(DomElement element)
    {
        for (DomNode? node = element; node is not null; node = node.ParentNode)
        {
            if (node is not DomElement ancestor)
                continue;
            if (string.Equals(
                    UsedStyleForCapture(ancestor).GetValueOrDefault("position"),
                    "fixed",
                    System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the live layout transposes <paramref name="element"/> — i.e. whether it lies inside a
    /// vertical <em>rotation root</em> (a vertical-writing-mode box whose parent is not vertical),
    /// reached without first crossing an out-of-flow box, which establishes its own untransposed
    /// rotation context.
    /// <para>
    /// This deliberately mirrors <c>CssBox.WillBeVerticalTransposed</c> over the DOM rather than
    /// asking a spec question, because it decides how the <em>snapshot</em> is built and a snapshot's
    /// only job is to reproduce what the live layout painted. The engine's vertical flow is a
    /// prototype that does not rotate out-of-flow boxes, so a <c>position: fixed</c> vertical element
    /// nested in a vertical root is laid out untransposed; a snapshot of it that <em>is</em> transposed
    /// would disagree with the very page it was captured from. Both halves are exercised by the WPT
    /// <c>massive-element-*</c> family, which splits exactly along this line: the in-flow variants
    /// (<c>-partially-onscreen</c> with a scroll) need the transposition, and the <c>position: fixed</c>
    /// ones (<c>-offscreen</c>, <c>right-and-left-</c>) need its absence.
    /// </para>
    /// </summary>
    private bool CapturedElementIsVerticallyTransposed(DomElement element)
    {
        for (DomNode? node = element; node is not null; node = node.ParentNode)
        {
            if (node is not DomElement ctx)
                continue;

            var style = UsedStyleForCapture(ctx);
            var parent = ctx.ParentElement;
            bool parentVertical = parent is not null
                && CssWritingMode.IsVertical(UsedStyleForCapture(parent).GetValueOrDefault("writing-mode"));

            if (CssWritingMode.IsVertical(style.GetValueOrDefault("writing-mode")) && !parentVertical)
                return true;

            var position = style.GetValueOrDefault("position");
            if (string.Equals(position, "absolute", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(position, "fixed", System.StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return false;
    }

    private void ApplyActiveViewTransitionTypeRules(DomElement root)
    {
        foreach (var (selectorText, declarations) in EnumerateAuthorStyleRules(root))
        {
            string stripped;

            var typeMatch = ActiveViewTransitionType.Match(selectorText);
            if (typeMatch.Success)
            {
                if (!AnyTypeActive(typeMatch.Groups[1].Value, _activeViewTransition!.Types))
                    continue;
                stripped = ActiveViewTransitionType.Replace(selectorText, RootPseudo).Trim();
            }
            else if (ActiveViewTransitionBare.IsMatch(selectorText))
            {
                // The bare pseudo-class is active for the whole transition, whatever its types.
                stripped = ActiveViewTransitionBare.Replace(selectorText, RootPseudo).Trim();
            }
            else
            {
                continue;
            }

            foreach (var element in root.Descendants().OfType<DomElement>())
            {
                if (MatchesSelector(element, stripped, null))
                {
                    foreach (var declaration in declarations.Declarations)
                        BakedInlineStyle(element)[declaration.Name] = declaration.Value.Text;
                }
            }
        }
    }

    /// <summary>
    /// The used <c>view-transition-group</c> (css-view-transitions-2) of an element — the last
    /// author declaration whose selector matches. It is not part of the computed-style projection,
    /// so it is read directly from the matched author rules. Returns <c>null</c> for the initial
    /// value (absent / <c>normal</c>), i.e. a top-level group.
    /// </summary>
    private string? ResolveViewTransitionGroupValue(DomElement element, DomElement root)
    {
        string? value = null;
        foreach (var (selectorText, declarations) in EnumerateAuthorStyleRules(root))
        {
            var declared = declarations.Declarations
                .LastOrDefault(d => d.Name.Equals("view-transition-group", System.StringComparison.OrdinalIgnoreCase));
            if (declared is null)
                continue;
            if (MatchesSelector(element, selectorText, null))
                value = declared.Value.Text.Trim();
        }

        if (string.IsNullOrEmpty(value)
            || value.Equals("normal", System.StringComparison.OrdinalIgnoreCase)
            || value.Equals("none", System.StringComparison.OrdinalIgnoreCase))
            return null;
        return value;
    }

    /// <summary>
    /// The parent group name a captured element's group nests under (css-view-transitions-2
    /// <c>view-transition-group</c>):
    /// <list type="bullet">
    /// <item>a <c>&lt;custom-ident&gt;</c> nests under the group of that name (self-reference and
    /// unresolved names fall back to the flat layout);</item>
    /// <item><c>nearest</c> under the nearest ancestor captured element's group;</item>
    /// <item><c>normal</c> (the initial value) and <c>contain</c> nest under the nearest ancestor
    /// that is a <em>containing</em> group — a captured element whose own <c>view-transition-group</c>
    /// is <c>contain</c>.</item>
    /// </list>
    /// Returns <c>null</c> (a top-level group directly under <c>::view-transition</c>) when nothing
    /// resolves, so the flat VT1 layout is the default.
    /// </summary>
    private string? ResolveGroupParentName(
        DomElement element, string name, IReadOnlyDictionary<string, DomElement> elementByName, DomElement root)
    {
        var value = ResolveViewTransitionGroupValue(element, root);

        if (value is not null && value.Equals("nearest", System.StringComparison.OrdinalIgnoreCase))
            return NearestCapturedAncestorName(element, name, elementByName, root, requireContain: false);

        // normal (null) and contain both nest under the nearest *containing* ancestor group — an
        // ancestor whose used view-transition-group is `contain`. `contain` additionally makes this
        // element a container for its own descendants (handled when they resolve their parent). With
        // no containing ancestor the group stays flat (the VT1 default).
        if (value is null || value.Equals("contain", System.StringComparison.OrdinalIgnoreCase))
            return NearestCapturedAncestorName(element, name, elementByName, root, requireContain: true);

        // An explicit <custom-ident> nests under that group only when the element carrying that
        // view-transition-name is an ANCESTOR — css-view-transitions-2 resolves the name against the
        // ancestor chain, not against the whole document, so a sibling or a cousin does not qualify
        // (WPT nested/compute-explicit-name-non-ancestor, whose title is exactly that). Matching any
        // captured element nested a group under its sibling, and since the test's
        // `::view-transition-group(test) { background: inherit }` then inherited the sibling's red
        // instead of the green `::view-transition` root, the whole canvas came out red.
        //
        // A group cannot reference its own name either (a self-reference is invalid and the group
        // falls back to the flat `normal` layout) — WPT compute-explicit-name-self — and a name that
        // no element carries falls back the same way (compute-explicit-name-non-existent).
        if (string.Equals(value, name, System.StringComparison.Ordinal))
            return null;

        // `root` is the document element's name, and the document element is an ancestor of every
        // other captured element, so it always qualifies.
        if (string.Equals(value, "root", System.StringComparison.Ordinal))
            return value;

        return elementByName.TryGetValue(value, out var target) && element.IsDescendantOf(target)
            ? value
            : null;
    }

    /// <summary>
    /// Walks the ancestor chain for the nearest captured element with a view-transition-name.
    /// When <paramref name="requireContain"/> is set, only an ancestor whose own
    /// <c>view-transition-group</c> is <c>contain</c> qualifies (the containing-group parent for
    /// <c>normal</c>/<c>contain</c> children); otherwise any captured ancestor qualifies (the
    /// <c>nearest</c> keyword). Returns <c>null</c> when no ancestor qualifies.
    /// </summary>
    private string? NearestCapturedAncestorName(
        DomElement element, string name, IReadOnlyDictionary<string, DomElement> elementByName,
        DomElement root, bool requireContain)
    {
        for (var p = element.ParentNode; p != null; p = p.ParentNode)
        {
            if (p is not DomElement ancestor)
                continue;
            var ancestorName = ResolveUsedViewTransitionName(
                ancestor, UsedStyleForCapture(ancestor).GetValueOrDefault("view-transition-name"));
            if (ancestorName is null || string.Equals(ancestorName, name, System.StringComparison.Ordinal)
                || !(ancestorName == "root" || elementByName.ContainsKey(ancestorName)))
                continue;

            if (requireContain)
            {
                var ancestorGroup = ResolveViewTransitionGroupValue(ancestor, root);
                if (ancestorGroup is null || !ancestorGroup.Equals("contain", System.StringComparison.OrdinalIgnoreCase))
                    continue;
            }

            return ancestorName;
        }
        return null;
    }
}
