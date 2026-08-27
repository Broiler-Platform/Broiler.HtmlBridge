
using Broiler.CSS;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.HtmlBridge.Logging;

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

    // :active-view-transition-type( a, b, … ) anywhere in a selector.
    private static readonly System.Text.RegularExpressions.Regex ActiveViewTransitionType =
        new(@":active-view-transition-type\(\s*([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // The bare :active-view-transition pseudo-class (css-view-transitions-2) — matches the document
    // element whenever a view transition is active, regardless of type. The negative lookahead keeps
    // it from also matching the :active-view-transition-type(…) functional form handled above.
    private static readonly System.Text.RegularExpressions.Regex ActiveViewTransitionBare =
        new(@":active-view-transition(?!-type)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // html::view-transition[-group|-image-pair|-old|-new]( name ) — the pseudo and its optional
    // name/class/`*` argument. The leading originating selector (html / :root / *) is ignored: the
    // pseudo tree always originates from the document element.
    private static readonly System.Text.RegularExpressions.Regex ViewTransitionPseudo =
        new(@"::view-transition(?:-(group-children|group|image-pair|old|new))?\s*(?:\(\s*([^)]*)\s*\))?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

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
    internal JSValue StartViewTransition(in Arguments a)
    {
        var state = new ViewTransitionState();
        JSFunction? updateCallback = null;

        if (a.Length > 0)
        {
            if (a[0] is JSFunction fn)
            {
                updateCallback = fn;
            }
            else if (a[0] is JSObject options)
            {
                if (options[(KeyString)"update"] is JSFunction updateFn)
                    updateCallback = updateFn;
                CollectViewTransitionTypes(options[(KeyString)"types"], state.Types);
            }
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

        if (updateCallback is not null)
        {
            try
            {
                updateCallback.InvokeFunction(new Arguments(updateCallback));
            }
            catch (System.Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.startViewTransition",
                    $"View transition update callback threw: {ex.Message}", ex);
            }
        }

        return BuildViewTransitionObject(state);
    }

    /// <summary>Reads the <c>types</c> option — a JS array/iterable of strings — into
    /// <paramref name="into"/>. Absent or non-array values contribute nothing.</summary>
    private static void CollectViewTransitionTypes(JSValue? types, HashSet<string> into)
    {
        if (types is not JSObject arrayLike)
            return;

        var lengthValue = arrayLike[(KeyString)"length"];
        if (lengthValue is null || lengthValue.IsUndefined)
            return;

        var length = (int)lengthValue.DoubleValue;
        for (var i = 0; i < length; i++)
        {
            var item = arrayLike[(uint)i];
            if (item is not null && !item.IsUndefined && !item.IsNull)
                into.Add(item.ToString());
        }
    }

    private JSObject BuildViewTransitionObject(ViewTransitionState state)
    {
        var transition = new JSObject();
        transition.FastAddValue("ready", ReadyThenable(state), JSPropertyAttributes.EnumerableConfigurableValue);
        // `finished` resolving means the transition is over: the ::view-transition tree has been
        // removed and the DOM is back to its plain final state. A reftest that screenshots from
        // finished (rather than ready) therefore expects the final DOM, not the pseudo tree — so
        // realizing this thenable clears the active transition, and the serialize-time bake becomes
        // a no-op (WPT element-stops-grouping-after-animation).
        transition.FastAddValue("finished", FinishedThenable(), JSPropertyAttributes.EnumerableConfigurableValue);
        transition.FastAddValue("updateCallbackDone", ResolvedThenable(), JSPropertyAttributes.EnumerableConfigurableValue);

        var typesArray = new JavaScript.BuiltIns.Array.JSArray();
        foreach (var type in state.Types)
            typesArray.Add(new JavaScript.BuiltIns.String.JSString(type));
        transition.FastAddValue("types", typesArray, JSPropertyAttributes.EnumerableConfigurableValue);

        // skipTransition() ends the transition without animating; the still is already the final
        // state here, so it is a no-op beyond clearing the active state.
        transition.FastAddValue("skipTransition",
            new DomFunction((in _) => { _activeViewTransition = null; return JSUndefined.Value; }, "skipTransition", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return transition;
    }

    /// <summary>A minimal already-resolved thenable, mirroring the bridge's synchronous-promise
    /// pattern (see FetchBinding): <c>then</c> invokes its callback immediately with
    /// <c>undefined</c> and returns a thenable so <c>.then().then()</c> chains, and the rAF the
    /// reftests schedule from it is pumped by the event loop as usual.</summary>
    private static JSObject ResolvedThenable()
    {
        var thenable = new JSObject();
        JSValue Then(in Arguments args)
        {
            if (args.Length > 0 && args[0] is JSFunction cb)
            {
                try { cb.InvokeFunction(new Arguments(cb, JSUndefined.Value)); }
                catch (System.Exception ex)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.viewTransition.then",
                        $"View transition promise callback threw: {ex.Message}", ex);
                }
            }
            return thenable;
        }
        thenable.FastAddValue("then", new DomFunction(Then, "then", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("catch", new DomFunction((in _) => thenable, "catch", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("finally",
            new DomFunction((in a) => { if (a.Length > 0 && a[0] is JSFunction cb) { try { cb.InvokeFunction(new Arguments(cb)); } catch { } } return thenable; }, "finally", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
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
    private JSObject ReadyThenable(ViewTransitionState state)
    {
        bool ScreenshotPending() =>
            (GetAttr(DocumentElement, "class") ?? string.Empty)
                .Contains("reftest-wait", System.StringComparison.Ordinal);

        void Run(JSFunction? cb)
        {
            bool waitBefore = ScreenshotPending();
            if (cb is not null)
            {
                try { cb.InvokeFunction(new Arguments(cb, JSUndefined.Value)); }
                catch (System.Exception ex)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.viewTransition.ready.then",
                        $"View transition ready callback threw: {ex.Message}", ex);
                }
            }

            if (waitBefore && !ScreenshotPending())
                state.ScreenshotReleasedByReady = true;
        }

        var thenable = new JSObject();
        JSValue Then(in Arguments args)
        {
            Run(args.Length > 0 ? args[0] as JSFunction : null);
            return thenable;
        }
        thenable.FastAddValue("then", new DomFunction(Then, "then", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("catch", new DomFunction((in _) => thenable, "catch", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("finally",
            new DomFunction((in a) => { Run(a.Length > 0 ? a[0] as JSFunction : null); return thenable; }, "finally", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        return thenable;
    }

    /// <summary>The <c>finished</c> promise as an already-resolved thenable that first marks the
    /// transition complete — clearing <see cref="_activeViewTransition"/> so the serialize-time pseudo
    /// tree bake is skipped and the plain final DOM renders — then, like <see cref="ResolvedThenable"/>,
    /// invokes the callback (e.g. the reftest's <c>takeScreenshot</c>) and chains.</summary>
    private JSObject FinishedThenable()
    {
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

        void RunAndMaybeFinish(JSFunction? cb)
        {
            bool waitBefore = ScreenshotPending();
            if (cb is not null)
            {
                try { cb.InvokeFunction(new Arguments(cb, JSUndefined.Value)); }
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

        var thenable = new JSObject();
        JSValue Then(in Arguments args)
        {
            RunAndMaybeFinish(args.Length > 0 ? args[0] as JSFunction : null);
            return thenable;
        }
        thenable.FastAddValue("then", new DomFunction(Then, "then", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("catch", new DomFunction((in _) => thenable, "catch", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("finally",
            new DomFunction((in a) => { RunAndMaybeFinish(a.Length > 0 ? a[0] as JSFunction : null); return thenable; }, "finally", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
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

    /// <summary>The writing modes whose block flow is horizontal, so a box in one is laid out in a
    /// logical frame and rotated into physical space.</summary>
    private static bool IsVerticalWritingMode(string? writingMode) =>
        writingMode?.Trim().ToLowerInvariant() is "vertical-rl" or "vertical-lr" or "sideways-rl" or "sideways-lr";

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
            var parent = ParentElementForCapture(ctx);
            bool parentVertical = parent is not null
                && IsVerticalWritingMode(UsedStyleForCapture(parent).GetValueOrDefault("writing-mode"));

            if (IsVerticalWritingMode(style.GetValueOrDefault("writing-mode")) && !parentVertical)
                return true;

            var position = style.GetValueOrDefault("position");
            if (string.Equals(position, "absolute", System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(position, "fixed", System.StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return false;
    }

    private static DomElement? ParentElementForCapture(DomElement element)
    {
        for (DomNode? node = element.ParentNode; node is not null; node = node.ParentNode)
        {
            if (node is DomElement parent)
                return parent;
        }

        return null;
    }

    /// <summary>
    /// Applies the author rules a running transition activates to the live DOM, so the "new" snapshot
    /// the pseudo tree captures reflects them. Two selector forms are handled (css-view-transitions-2):
    /// <c>:active-view-transition-type(type)</c>, gated on the transition's active types, and the bare
    /// <c>:active-view-transition</c> pseudo-class, which matches whenever any transition is active.
    /// Each matching rule is re-matched with the pseudo rewritten to <c>:root</c> and its
    /// declarations baked onto the matched elements.
    /// </summary>
    /// <remarks>
    /// The rewrite is <c>:root</c> rather than deletion because both pseudo-classes match the
    /// <em>root element only</em> (css-view-transitions-2 §
    /// <c>:active-view-transition</c>). Deleting them made the originating compound match whatever
    /// else it named, so <c>main:active-view-transition #target</c> — a selector written precisely
    /// to assert that it never matches — styled the target. Substituting <c>:root</c> keeps the
    /// compound intact and lets the ordinary selector matcher reject it: <c>main:root</c> matches
    /// nothing, <c>html:root</c> matches, and a bare <c>:active-view-transition</c> becomes
    /// <c>:root</c>, which is also what the old empty-compound special case hand-rolled.
    /// </remarks>
    /// <summary>What both view-transition pseudo-classes are rewritten to: they match the root only.</summary>
    private const string RootPseudo = ":root";

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

    private static bool AnyTypeActive(string argumentList, HashSet<string> activeTypes)
    {
        foreach (var raw in argumentList.Split(','))
        {
            if (activeTypes.Contains(raw.Trim()))
                return true;
        }
        return false;
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

        return elementByName.TryGetValue(value, out var target) && IsAncestorOf(target, element)
            ? value
            : null;
    }

    /// <summary>Whether <paramref name="candidate"/> is a strict ancestor of <paramref name="node"/>.</summary>
    private static bool IsAncestorOf(DomElement candidate, DomNode node)
    {
        for (var parent = node.ParentNode; parent != null; parent = parent.ParentNode)
        {
            if (ReferenceEquals(parent, candidate))
                return true;
        }

        return false;
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

    /// <summary>
    /// Materialises the <c>::view-transition</c> pseudo tree as real positioned boxes. Each captured
    /// name gets a group box at its old geometry (the frozen animation start) holding old and new
    /// snapshot boxes; author <c>::view-transition*</c> declarations are applied to the corresponding
    /// boxes. The whole tree hangs off an overlay painted above the page (author
    /// <c>::view-transition</c> declarations, e.g. a backdrop colour).
    /// </summary>
    private void ApplyViewTransitionPseudoTree(DomElement root)
    {
        var pseudoRules = CollectViewTransitionPseudoDeclarations(root);
        var captures = CollectViewTransitionCaptures(root, RootSnapshotNeedsContent(root, "new"));

        // A view transition that captured nothing (no element carries a used
        // view-transition-name — e.g. `:root { view-transition-name: none }` with no other
        // named element) finishes immediately, so its ::view-transition tree is already gone by
        // the reftests' screenshot time and the root overlay must not paint — UNLESS an author
        // animation on the bare ::view-transition pins it open. WPT `no-named-elements` freezes it
        // with `::view-transition { animation: no-op 300s }` and its reference is the blue overlay
        // filling the viewport; `nothing-captured` has no such animation, so its
        // `::view-transition { background: red }` must stay hidden. Approximate that timing
        // distinction here: with no captures, bake the (group-less) overlay only when the root
        // pseudo was kept alive, otherwise skip so the page renders unmodified.
        if (captures.Count == 0 && !HasRootOverlayKeepAliveAnimation(pseudoRules))
            return;

        var overlay = CreateStyledBox(BaseStyle(
            ("position", "fixed"), ("left", "0"), ("top", "0"),
            ("width", "100vw"), ("height", "100vh"),
            ("z-index", "2147483646"), ("pointer-events", "none")), LookupPseudo(pseudoRules, "", null));
        SetAttr(overlay, "data-broiler-view-transition", "");

        // Map each captured (non-root) name to its new-side element, so a group's
        // `view-transition-group` (and the ancestry `nearest` walks) can be resolved.
        var rootName = ResolveRootViewTransitionName(UsedStyleForCapture(root));
        var elementByName = new Dictionary<string, DomElement>(System.StringComparer.Ordinal);
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            var elementName = ResolveUsedViewTransitionName(
                element, UsedStyleForCapture(element).GetValueOrDefault("view-transition-name"));
            if (elementName is not null && elementName != rootName && !elementByName.ContainsKey(elementName))
                elementByName[elementName] = element;
        }

        // Pass 1: build every group box (with its old/new snapshots), keyed by name.
        var groupByName = new Dictionary<string, DomElement>(System.StringComparer.Ordinal);
        var captureByName = new Dictionary<string, ViewTransitionCapture>(System.StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            // The group animates old→new geometry; the reftests freeze it at the start, so it sits at
            // the old geometry when the element existed before the transition, else the new.
            var groupDeclarations = LookupPseudo(pseudoRules, "group", capture);
            var groupLeft = capture.GroupLeft;
            var groupTop = capture.GroupTop;
            var groupW = capture.HasOld ? capture.OldWidth : capture.NewWidth;
            var groupH = capture.HasOld ? capture.OldHeight : capture.NewHeight;

            // "Frozen at the start" is the animation's output at time 0, which is not always the old
            // geometry: an author timing function of the `steps(…, jump-start)` family jumps before it
            // advances, so at t=0 the group is already part-way to the new geometry. WPT auto-name
            // pins exactly that with `steps(2, start)` — output 1/2 at t=0 — and its reference is the
            // two items at the midpoint between their old and new positions. Every other timing
            // function (linear, the eases, cubic-bezier, the jump-end family) is 0 at t=0 and leaves
            // the group on the old geometry, which is what it has always done.
            var progress = FrozenGroupProgress(groupDeclarations);
            if (progress > 0 && capture.HasOld && capture.HasNew)
            {
                groupLeft = Interpolate(capture.OldLeft, capture.NewLeft, progress);
                groupTop = Interpolate(capture.OldTop, capture.NewTop, progress);
                groupW = Interpolate(capture.OldWidth, capture.NewWidth, progress);
                groupH = Interpolate(capture.OldHeight, capture.NewHeight, progress);
            }
            // The captured position is carried by the group's transform — its UA style, per spec, is
            // `position: absolute; inset: 0` with a transform translating to the snapshot's location.
            // Keeping left/top at 0 lets an author `::view-transition-group(name)` rule that sets
            // `top`/`left`/`inset` compose additively with the captured translation rather than
            // replacing it (WPT content-with-clip offsets a group by `top: -50vh` to cancel a
            // `top: 50vh` on the captured element; overriding a captured `top` outright would push the
            // snapshot off-screen). An author `transform` still overrides the placement, as it should.
            // The snapshot clips to the border box only when the captured element itself establishes a
            // clip (non-visible overflow, contain:paint, a clip-path). A default overflow:visible
            // element must instead show its ink overflow — content its descendants paint outside the
            // box, e.g. an absolutely-positioned child above the box (WPT
            // capture-with-offscreen-child-translated). Root and old-only/unknown captures keep the
            // clip (viewport / prior behaviour).
            var capturedElement = capture.Name == rootName
                ? DocumentElement
                : elementByName.GetValueOrDefault(capture.Name);

            bool clipsContent = true;
            if (capture.Name != rootName && capturedElement is not null)
                clipsContent = CapturedElementClipsContent(UsedStyleForCapture(capturedElement));

            var group = CreateStyledBox(BaseStyle(
                ("position", "absolute"), ("left", "0"), ("top", "0"),
                ("transform", $"translate({Px(groupLeft)}, {Px(groupTop)})"),
                ("width", Px(groupW)), ("height", Px(groupH)),
                ("overflow", clipsContent ? "hidden" : "visible")),
                groupDeclarations);

            // Between the group and its two snapshots sits ::view-transition-image-pair, the box the
            // spec gives the old/new pair so a rule can address both at once — WPT
            // old-content-captures-root hides a whole group with
            // `::view-transition-image-pair(shared) { visibility: hidden }`, which has nowhere to land
            // if old and new hang directly off the group.
            var imagePair = CreateStyledBox(BaseStyle(
                ("position", "absolute"), ("left", "0"), ("top", "0"),
                ("width", "100%"), ("height", "100%")),
                LookupPseudo(pseudoRules, "image-pair", capture));
            SetAttr(imagePair, "data-broiler-view-transition-image-pair", "");
            AppendBridgeChild(group, imagePair);

            // Snapshot boxes are stacked top-left within the pair: old under new. Each box is a
            // transparent positioned container carrying the author ::view-transition-old/-new
            // declarations (e.g. the pinned opacity); the captured content box inside it carries the
            // element's own paint (background, opacity, text) so an element's opacity composites over
            // the backdrop rather than over an opaque snapshot fill.
            //
            // A snapshot box resets `writing-mode` when — and only when — the live layout transposes
            // the element it captured, and the reason is structural rather than cosmetic.
            // css-view-transitions-1 gives the pseudo tree the *captured element's* writing mode —
            // which BuildViewTransitionSnapshotContent already bakes onto the content box — not the
            // originating root's; the pseudo boxes are real <div>s under <html>, so without a reset
            // they inherit `:root { writing-mode: vertical-lr }` as well. That is not merely
            // redundant: Broiler rotates a vertical subtree from its *rotation root*, defined as a
            // vertical box whose parent is not vertical, so an inherited vertical mode all the way
            // down means the content box is never a root, WillBeVerticalTransposed reports false,
            // and ResolvePhysicalSize maps block-size onto physical height un-swapped.
            // `.middle { block-size: 39800px }` then became a 39800px-tall band instead of a
            // 39800px-wide one — the whole of the massive-element-*-partially-onscreen failure.
            //
            // Resetting it *unconditionally* is the opposite error, and the same family catches it:
            // the engine's vertical flow does not rotate out-of-flow boxes, so the `position: fixed`
            // variants (-offscreen, right-and-left-) are laid out untransposed on the live page, and
            // a transposed snapshot of them disagrees with the page it was captured from — measured
            // as right-and-left-of-viewport-partially-onscreen falling from 100% to 2.9%. Mirroring
            // the engine's own predicate keeps the snapshot honest in both directions.
            //
            // An author `::view-transition-old(x) { writing-mode: … }` still wins: CreateStyledBox
            // layers the author declarations on top of BaseStyle. It is deliberately not in
            // PseudoBoxAuthorReset, which is skipped wholesale when the author paints a background.
            var snapshotWritingMode = capturedElement is not null
                && CapturedElementIsVerticallyTransposed(capturedElement)
                    ? "horizontal-tb"
                    : null;

            if (capture.HasOld)
            {
                var scale = SnapshotScale(capture.OldWidth, groupW);
                var oldBox = CreateStyledBox(SnapshotBoxStyle(
                    capture.OldWidth * scale, capture.OldHeight * scale, snapshotWritingMode),
                    LookupPseudo(pseudoRules, "old", capture));
                AttachSnapshotPaint(
                    oldBox, capture.OldContent, capture.OldBackground,
                    scale, capture.OldWidth, capture.OldHeight);
                AppendBridgeChild(imagePair, oldBox);
            }

            if (capture.HasNew)
            {
                var scale = SnapshotScale(capture.NewWidth, groupW);
                var newBox = CreateStyledBox(SnapshotBoxStyle(
                    capture.NewWidth * scale, capture.NewHeight * scale, snapshotWritingMode),
                    LookupPseudo(pseudoRules, "new", capture));
                AttachSnapshotPaint(
                    newBox, capture.NewContent, capture.NewBackground,
                    scale, capture.NewWidth, capture.NewHeight);
                AppendBridgeChild(imagePair, newBox);
            }

            groupByName[capture.Name] = group;
            captureByName[capture.Name] = capture;
        }

        // Pass 2: parent each group. css-view-transitions-2 `view-transition-group` nests a group
        // under another group's `::view-transition-group-children` wrapper (so its background can
        // inherit down the nesting); the default (`normal`) keeps the group directly under the
        // overlay — today's flat layout, so untouched for the common case.
        var childrenWrapperByName = new Dictionary<string, DomElement>(System.StringComparer.Ordinal);
        foreach (var capture in captures)
        {
            var group = groupByName[capture.Name];
            var parentName = elementByName.TryGetValue(capture.Name, out var el)
                ? ResolveGroupParentName(el, capture.Name, elementByName, root)
                : null;

            if (parentName is not null && groupByName.TryGetValue(parentName, out var parentGroup))
            {
                if (!childrenWrapperByName.TryGetValue(parentName, out var wrapper))
                {
                    var parentContext = captureByName.TryGetValue(parentName, out var pc) ? (ViewTransitionCapture?)pc : null;
                    wrapper = CreateStyledBox(BaseStyle(
                        ("position", "absolute"), ("left", "0"), ("top", "0"),
                        ("width", "100%"), ("height", "100%")),
                        LookupPseudo(pseudoRules, "group-children", parentContext));
                    SetAttr(wrapper, "data-broiler-view-transition-group-children", "");
                    AppendBridgeChild(parentGroup, wrapper);
                    childrenWrapperByName[parentName] = wrapper;
                }
                AppendBridgeChild(wrapper, group);
            }
            else
            {
                AppendBridgeChild(overlay, group);
            }
        }

        AppendBridgeChild(root, overlay);
    }

    /// <summary>The base style of a <c>::view-transition-old</c>/<c>-new</c> box: the captured rect's
    /// size at the group's scale, plus the <c>writing-mode</c> reset when the captured element is one
    /// the live layout transposes (<paramref name="writingMode"/> is null when it is not, leaving the
    /// inherited mode alone).</summary>
    private static Dictionary<string, string> SnapshotBoxStyle(double width, double height, string? writingMode)
    {
        var style = BaseStyle(
            ("position", "absolute"), ("left", "0"), ("top", "0"),
            ("width", Px(width)), ("height", Px(height)));

        if (writingMode is not null)
            style["writing-mode"] = writingMode;

        return style;
    }

    private static Dictionary<string, string> BaseStyle(params (string Key, string Value)[] entries)
    {
        var style = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in entries)
            style[key] = value;
        return style;
    }

    /// <summary>
    /// Initial values re-asserted on every box of the pseudo tree so a page-level author rule cannot
    /// paint it. Each box below is materialised as a real <c>&lt;div&gt;</c>, so an ordinary
    /// <c>div { … }</c> or <c>* { … }</c> rule matches it — but these are <c>::view-transition*</c>
    /// pseudo-elements, which such a rule must never reach. WPT
    /// <c>css-view-transitions/names-are-tree-scoped</c> is the case that exposed it: its
    /// <c>div { background: red }</c> matched the viewport-sized overlay root, which paints above the
    /// page at z-index 2147483646, and turned the whole canvas solid red.
    /// <para>
    /// Applied <em>under</em> each box's own base style and the author's <c>::view-transition*</c>
    /// declarations, so both still win — an author <c>::view-transition { background: red }</c> is
    /// still honoured. Geometry is not reset: every box already sets its own position/size
    /// explicitly. <c>visibility</c> is deliberately absent — re-asserting the initial value there is
    /// exactly what previously stopped an image-pair's <c>visibility: hidden</c> from reaching the
    /// snapshots it wraps (see <see cref="SnapshotPaintProperties"/>).
    /// </para>
    /// </summary>
    private static readonly (string Key, string Value)[] PseudoBoxAuthorReset =
    [
        ("background-color", "transparent"), ("background-image", "none"),
    ];

    /// <summary>
    /// Whether the author's <c>::view-transition*</c> declarations paint this box's background
    /// themselves, in which case the reset must stand aside entirely rather than merge with them.
    /// The two cannot be layered: the reset is written as longhands and an author almost always
    /// writes the <c>background</c> shorthand, so they occupy different keys in the inline-style dict
    /// and the longhands win by coming later — which silently cancelled
    /// <c>::view-transition { background: lightpink }</c> and cost 79 tests the first time this was
    /// tried.
    /// </summary>
    private static bool AuthorPaintsBackground(Dictionary<string, string> pseudoDeclarations)
    {
        foreach (var key in pseudoDeclarations.Keys)
        {
            if (key.StartsWith("background", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Creates a bridge <c>&lt;div&gt;</c> whose inline style is <paramref name="baseStyle"/>
    /// with the pseudo-element's author declarations layered on top (author values win), over a reset
    /// (<see cref="PseudoBoxAuthorReset"/>) that keeps page-level selectors out of the pseudo tree.
    /// Stored in the inline-style dict, not the attribute, so it survives <c>ReflectRenderState</c> on
    /// the render path and serializes normally.</summary>
    private DomElement CreateStyledBox(Dictionary<string, string> baseStyle, Dictionary<string, string> pseudoDeclarations)
    {
        var authorPaintsBackground = AuthorPaintsBackground(pseudoDeclarations);

        foreach (var (key, value) in pseudoDeclarations)
            baseStyle[key] = value;

        var box = CreateBridgeElement("div");
        var inline = InlineStyle(box);
        if (!authorPaintsBackground)
        {
            foreach (var (key, value) in PseudoBoxAuthorReset)
                inline[key] = value;
        }
        foreach (var (key, value) in baseStyle)
            inline[key] = value;
        return box;
    }

    private void AppendBridgeChild(DomElement parent, DomElement child)
    {
        SetParent(child, parent);
        parent.AppendChild(child);
    }

    /// <summary>
    /// The factor a snapshot is drawn at inside its group. Per the css-view-transitions UA
    /// stylesheet a <c>::view-transition-old</c>/<c>-new</c> is <c>inline-size: 100%</c> of its
    /// group with <c>block-size: auto</c> — it fills the group's width and keeps its own aspect
    /// ratio, because the snapshot is an image, not a re-laid-out box. A group whose size differs
    /// from the capture's therefore scales what it shows: WPT
    /// <c>root-to-shared-animation-incoming</c> (issue #1544 problem 19) renames the root onto a
    /// 100x120 element, so the new snapshot is drawn into a group still at the old viewport-sized
    /// geometry and must cover it.
    /// <para>
    /// Returns exactly <c>1</c> whenever the group and the capture already agree — the case for
    /// every transition that does not resize, which is nearly all of them — so those snapshots keep
    /// the boxes they have always had.
    /// </para>
    /// </summary>
    private static double SnapshotScale(double capturedWidth, double groupWidth)
    {
        if (capturedWidth <= 0 || groupWidth <= 0)
            return 1;

        var scale = groupWidth / capturedWidth;
        return double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    /// <summary>Fills a snapshot box: appends its captured content box (which carries the element's
    /// baked paint and cloned content) when present, else falls back to a flat background fill (the
    /// implicit root capture and one-sided names, which have no content box).
    /// <para>
    /// At a <paramref name="scale"/> other than 1 the content box is pinned to the captured pixel
    /// size and scaled from its top-left corner, rather than being stretched by the box sizing it
    /// would otherwise inherit (<c>100%</c> of the snapshot box). Stretching would resize the
    /// snapshot's own layout — text would reflow, a border would thicken on one axis only — where
    /// the spec scales a captured image uniformly. The flat-fill fallback needs neither: it has no
    /// content to scale, and the box it fills is already the scaled size.
    /// </para>
    /// </summary>
    private void AttachSnapshotPaint(
        DomElement box, DomElement? content, string fallbackBackground,
        double scale = 1, double capturedWidth = 0, double capturedHeight = 0)
    {
        if (content is null)
        {
            InlineStyle(box)["background-color"] = fallbackBackground;
            return;
        }

        var clone = CloneSnapshotContentForRender(content);

        if (capturedWidth > 0 && capturedHeight > 0)
        {
            var style = InlineStyle(clone);

            // Pin the content box to the captured pixel size instead of leaving it at the `100%`
            // BuildViewTransitionSnapshotContent gives it. At a scale other than 1 that is what the
            // summary above describes; at scale 1 the two are identical in a horizontal writing mode
            // (100% of a box that is itself the captured size) — but NOT in a vertical one, and that
            // is the reason this is unconditional.
            //
            // The content box carries the captured element's `writing-mode`, and the snapshot box
            // above it resets to `horizontal-tb`, so the content box is a vertical *rotation root*:
            // laid out in a logical frame whose frame-width is its inline size and frame-height its
            // block size, then transposed into physical space. ResolvePhysicalSize feeds that frame
            // from the swapped physical properties (frame-width ← CSS height, frame-height ← CSS
            // width) precisely so an authored physical width/height survives the rotation. A
            // percentage does not survive it: the swapped property still resolves against the
            // containing block's same-named axis, so `height: 100%` became frame-width = the box's
            // *width*, and the rotation handed back a content box 100x40000 where the capture was
            // 40000x100. Measured on WPT massive-element-{left,right}-of-viewport-partially-onscreen,
            // whose `:root { writing-mode: vertical-lr }` is the single line separating them from the
            // -on-top-of-/below- variants that already passed: the 100px band rendered as a
            // viewport-tall slab. A length resolves on the axis it is authored for, so it is the only
            // form that crosses the rotation intact.
            style["width"] = Px(capturedWidth);
            style["height"] = Px(capturedHeight);

            if (scale is not 1)
            {
                // The snapshot must grow from its top-left corner, but the paint walker applies every
                // transform about the box's centre and does not read `transform-origin`. Composing a
                // translate of half the growth ahead of the scale expresses a top-left-origin scale in
                // terms of the centre-origin one it does implement: a point p maps to C + T·S·(p - C),
                // so t = C(s - 1) puts the corner back at the origin. Without it the snapshot expands
                // equally in all four directions and the group clips away everything above and left of
                // its centre — which is exactly what this looked like: a 200x120 capture scaled 5.12x
                // showed 612x368 of blue instead of filling the group.
                var growthX = capturedWidth * (scale - 1) / 2;
                var growthY = capturedHeight * (scale - 1) / 2;
                style["transform"] =
                    $"translate({Px(growthX)}, {Px(growthY)}) scale({scale.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)})";
            }
        }

        AppendBridgeChild(box, clone);
    }

    // The paint- and text-affecting computed properties baked onto a snapshot's content box so it
    // renders like the captured element once re-parented under the overlay, where the element's
    // original ancestors and matched author selectors no longer apply. Deliberately excludes
    // geometry (position/inset/margin/width/height) — the group and box size the snapshot from the
    // captured rect and place the content at 0,0 — and view-transition-name (to avoid re-capturing
    // the clone).
    private static readonly string[] SnapshotPaintProperties =
    {
        "background-color", "background-image", "background-repeat", "background-position",
        "background-size", "background-clip", "background-origin", "background-attachment",
        "color", "opacity",
        "font-family", "font-size", "font-style", "font-weight", "font-variant", "font-stretch",
        "line-height", "letter-spacing", "word-spacing",
        "text-align", "text-transform", "text-indent", "text-shadow",
        "text-decoration-line", "text-decoration-color", "text-decoration-style",
        "white-space", "word-break", "overflow-wrap", "direction", "writing-mode",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "border-top-style", "border-right-style", "border-bottom-style", "border-left-style",
        "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
        "border-top-left-radius", "border-top-right-radius",
        "border-bottom-left-radius", "border-bottom-right-radius",
        "box-shadow",
        "padding-top", "padding-right", "padding-bottom", "padding-left",
        "visibility",
    };

    /// <summary>
    /// Builds a detached box reproducing <paramref name="element"/>'s painted border box for a
    /// view-transition snapshot: the element's paint/text computed values baked inline (so it paints
    /// identically once re-parented under the overlay) plus a deep clone of its content, so the
    /// snapshot shows the element's text/children rather than a blank fill. Sized to the enclosing
    /// snapshot box (100%); positioning, insets, margins, and the outer width/height come from the
    /// captured geometry on that box, not from the element's own computed values.
    /// </summary>
    /// <param name="asRootSnapshot">
    /// This is the whole-page root snapshot rather than one element's. Two things follow: children
    /// that never paint are not cloned (cloning <c>&lt;head&gt;</c> would re-insert its
    /// <c>&lt;style&gt;</c>/<c>&lt;script&gt;</c>, duplicating author rules and re-fetching external
    /// resources), and <c>id</c> attributes are kept so page-level <c>#id</c> rules still match the
    /// clone — without them a whole page of id-styled content reproduces as blank boxes. Keeping
    /// them is safe here because the pseudo tree is materialised on a fresh render projection, so
    /// the duplicate ids never reach the live tree page script can observe.
    /// </param>
    private DomElement BuildViewTransitionSnapshotContent(DomElement element, bool asRootSnapshot = false)
    {
        var used = UsedStyleForCapture(element);

        var content = CreateBridgeElement("div");
        SetAttr(content, "data-broiler-view-transition-content", "");
        var inline = InlineStyle(content);
        inline["position"] = "absolute";
        inline["left"] = "0";
        inline["top"] = "0";
        inline["width"] = "100%";
        inline["height"] = "100%";
        inline["box-sizing"] = "border-box";

        foreach (var property in SnapshotPaintProperties)
        {
            if (!used.TryGetValue(property, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            // `visibility` is inherited, and the pseudo tree uses it as a control of its own:
            // `::view-transition-image-pair(name) { visibility: hidden }` is how a reftest hides a
            // whole group (WPT old-content-captures-root). Baking the captured element's own
            // `visible` — the initial value nearly every element has — would re-show the snapshot
            // underneath that rule. Only a non-initial value is worth carrying: an element that was
            // itself hidden must stay hidden.
            if (string.Equals(property, "visibility", System.StringComparison.Ordinal) &&
                string.Equals(value.Trim(), "visible", System.StringComparison.OrdinalIgnoreCase))
                continue;

            inline[property] = value;
        }

        // Clone the element's content verbatim (text and any descendants) so the snapshot paints it.
        foreach (var child in element.ChildNodes.ToArray())
        {
            if (asRootSnapshot && IsNonRenderedSnapshotChild(child))
                continue;

            var clone = child.CloneNode(deep: true);
            StripCapturedIdentifiers(clone, preserveIds: asRootSnapshot);
            content.AppendChild(clone);
        }

        return content;
    }


    private static double Interpolate(double from, double to, double progress) =>
        from + ((to - from) * progress);

    /// <summary>
    /// The group animation's output at time 0 — the moment the reftests freeze it at. Read from the
    /// group's <c>animation-timing-function</c>, because an easing function need not be 0 at input 0.
    /// <para>
    /// Only the <c>steps()</c> family can be non-zero there. <c>steps(n, jump-start)</c> (and its
    /// <c>start</c> alias) takes its first jump immediately, so it outputs <c>1/n</c> at input 0;
    /// <c>jump-both</c> has one extra jump and outputs <c>1/(n+1)</c>; the <c>step-start</c> keyword is
    /// <c>steps(1, jump-start)</c>, so it is already fully at the new geometry. Everything else — the
    /// <c>jump-end</c>/<c>end</c>/<c>jump-none</c> steps, <c>linear</c>, the eases,
    /// <c>cubic-bezier()</c>, and an absent or unparseable value — is 0 at input 0 and leaves the
    /// group exactly where it has always been placed.
    /// </para>
    /// This is a static read of a frozen animation, not a timeline: nothing here advances with time.
    /// </summary>
    private static double FrozenGroupProgress(Dictionary<string, string> groupDeclarations)
    {
        // A zero-duration animation is already *finished* by the time anything is screenshot, so the
        // group is at its new geometry — progress 1, whatever the easing. `animation-duration: 0s`
        // on a `::view-transition-group` is the standard WPT idiom for "show me the end state", and
        // reading only the timing function left every one of those groups parked on the old
        // geometry, mis-placing and mis-scaling both snapshots inside it.
        //
        // `animation-delay` is deliberately not folded in the same way: a positive delay means the
        // animation has not started, which is the progress-0 behaviour root-to-shared-animation-start
        // depends on.
        if (groupDeclarations.TryGetValue("animation-duration", out var duration) &&
            IsZeroDuration(FirstTopLevelValue(duration)))
        {
            return 1;
        }

        if (!groupDeclarations.TryGetValue("animation-timing-function", out var raw) ||
            string.IsNullOrWhiteSpace(raw))
            return 0;

        // A comma-separated list pairs with animation-name; the group has one animation, so the
        // first entry governs. Splitting on the top-level comma keeps `steps(2, start)` intact.
        var value = FirstTopLevelValue(raw).Trim();

        if (value.Equals("step-start", System.StringComparison.OrdinalIgnoreCase))
            return 1;

        if (!value.StartsWith("steps(", System.StringComparison.OrdinalIgnoreCase) ||
            !value.EndsWith(")", System.StringComparison.Ordinal))
            return 0;

        var arguments = value[6..^1].Split(',');
        if (arguments.Length == 0 ||
            !int.TryParse(arguments[0].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var steps) ||
            steps < 1)
            return 0;

        var position = arguments.Length > 1 ? arguments[1].Trim() : "end";
        if (position.Equals("start", System.StringComparison.OrdinalIgnoreCase) ||
            position.Equals("jump-start", System.StringComparison.OrdinalIgnoreCase))
            return 1d / steps;
        if (position.Equals("jump-both", System.StringComparison.OrdinalIgnoreCase))
            return 1d / (steps + 1);

        return 0;
    }

    /// <summary>Whether a <c>&lt;time&gt;</c> is zero, in either unit and in the unitless spelling a
    /// bare <c>0</c> gives.</summary>
    private static bool IsZeroDuration(string value)
    {
        var text = value.Trim();
        if (text.Length == 0)
            return false;

        var number = text.EndsWith("ms", System.StringComparison.OrdinalIgnoreCase) ? text[..^2]
            : text.EndsWith("s", System.StringComparison.OrdinalIgnoreCase) ? text[..^1]
            : text;

        return double.TryParse(
                number.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed == 0;
    }

    /// <summary>The first entry of a comma-separated CSS value list, ignoring commas nested inside
    /// functional notation (so <c>steps(2, start)</c> survives as one entry).</summary>
    private static string FirstTopLevelValue(string value)
    {
        var depth = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '(') depth++;
            else if (character == ')') depth--;
            else if (character == ',' && depth == 0) return value[..index];
        }

        return value;
    }

    /// <summary>
    /// Whether the root's <paramref name="side"/> (<c>old</c> / <c>new</c>) snapshot has to reproduce
    /// the page rather than let the live page show through it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A root snapshot is normally left content-less: it sits over the live page, which renders the
    /// same thing pixel-exactly, where a DOM clone is only close. Cloning unconditionally was tried
    /// and reverted (+8/-7 passing across the 458 local css-view-transitions tests, -79 pixel points
    /// on <c>root-to-shared-animation-end</c>). So the clone is gated on the page provably not being
    /// able to stand in, which happens two ways:
    /// </para>
    /// <para>
    /// The author paints the bare <c>::view-transition</c>. That backdrop sits between the page and
    /// the snapshot, so the page is not visible through it at all and a content-less snapshot leaves
    /// the backdrop colour flooding the viewport.
    /// </para>
    /// <para>
    /// Or the author gives this side's pseudo an effect that re-renders the snapshot's own pixels —
    /// <c>filter</c> and friends. The page beneath is not filtered, so even a fully transparent
    /// snapshot over a perfectly visible page shows the wrong thing: WPT
    /// <c>{old,new}-content-root-scrollbar-with-fixed-background</c> invert the captured page with
    /// <c>filter: invert(1)</c> and expect an inverted viewport.
    /// </para>
    /// <para>
    /// <c>opacity</c> is deliberately not in that set, and the distinction is what keeps the old
    /// regression away: it only composites the snapshot against what is behind it, which is exactly
    /// what the live page already does. <c>root-to-shared-animation-end</c> pins
    /// <c>::view-transition-old(*) { opacity: 1 }</c>, and treating that as needing content is
    /// precisely the -79 case. <c>transform</c> is likewise excluded — the group already carries the
    /// captured placement — pending a measurement that shows a test needs it.
    /// </para>
    /// </remarks>
    private bool RootSnapshotNeedsContent(DomElement root, string side)
    {
        var pseudoRules = CollectViewTransitionPseudoDeclarations(root);
        if (AuthorPaintsBackground(LookupPseudo(pseudoRules, string.Empty, null)))
            return true;

        var rootName = ResolveRootViewTransitionName(UsedStyleForCapture(root));
        if (HasSnapshotAlteringEffect(LookupRootPseudo(pseudoRules, side, rootName)))
            return true;

        // Third way, and the one the two clauses above miss: the *other* side's snapshot is the one
        // the author has hidden. "Let the live page stand in" rests on the live page showing what
        // the snapshot would — and the live page shows the NEW state, so it can only stand in for
        // the old snapshot while the new snapshot is what is meant to be on screen. Hide the new
        // side and the old snapshot is the only thing that can supply those pixels; leaving it
        // content-less paints a flat viewport-sized rectangle of the captured root background
        // instead, which is how `{new,old}-content-has-scrollbars` came to render a plain lightpink
        // canvas where the reference has the page's checkerboard.
        //
        // This is not the `opacity` case the remarks above rule out. That one asks whether *this*
        // snapshot's own compositing needs real pixels, and the answer is no. This asks whether the
        // page underneath is still a truthful stand-in, and an author who has hidden the new
        // snapshot has said it is not.
        //
        // Not for a page holding a nested browsing context, though. What a frame displays during a
        // transition is resolved through the *live* element — TryGetFrameMarkupHeldByRootSnapshot
        // replays FrameMarkupAtCapture onto it — and a clone carries a second copy of the frame
        // that has had none of that applied, painted over the top. It shows whatever markup the
        // frame's `srcdoc` last round-tripped rather than the state at capture time, which is
        // exactly what SubDocumentViewTransitionTests pins. Reproducing a sub-document faithfully
        // in a clone is the "close, not exact" problem that got the unconditional clone reverted,
        // and it is a bigger question than this gate.
        return !ContainsNestedBrowsingContext(root) &&
            SuppressesSnapshotPaint(
                LookupRootPseudo(pseudoRules, side == "old" ? "new" : "old", rootName));
    }

    /// <summary>Whether the document holds a frame whose content the root snapshot would have to
    /// reproduce.</summary>
    private static bool ContainsNestedBrowsingContext(DomElement root) =>
        root.Descendants().OfType<DomElement>().Any(element =>
            element.TagName.Equals("iframe", System.StringComparison.OrdinalIgnoreCase) ||
            element.TagName.Equals("frame", System.StringComparison.OrdinalIgnoreCase) ||
            element.TagName.Equals("object", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether these declarations stop a snapshot painting at all — as opposed to merely
    /// changing how it composites.</summary>
    private static bool SuppressesSnapshotPaint(Dictionary<string, string> declarations)
    {
        if (declarations.TryGetValue("display", out var display) &&
            display.Trim().Equals("none", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (declarations.TryGetValue("visibility", out var visibility) &&
            visibility.Trim().Equals("hidden", System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return declarations.TryGetValue("opacity", out var opacity) &&
            double.TryParse(
                opacity.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed == 0;
    }

    /// <summary>The declarations an author aimed at the root's <paramref name="kind"/> pseudo, taking
    /// both the <c>*</c> and the by-name forms (the root has no <c>view-transition-class</c> path of
    /// its own to consider).</summary>
    private static Dictionary<string, string> LookupRootPseudo(
        Dictionary<string, Dictionary<string, string>> pseudoRules, string kind, string? rootName)
    {
        var merged = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        void Merge(string argument)
        {
            if (pseudoRules.TryGetValue($"{kind}|{argument}", out var bucket))
                foreach (var (key, value) in bucket)
                    merged[key] = value;
        }

        Merge("*");
        if (!string.IsNullOrEmpty(rootName))
            Merge(rootName!);

        return merged;
    }

    /// <summary>Properties that re-render a snapshot's own pixels, so the live page underneath cannot
    /// stand in for it however visible that page is. Compositing-only knobs (<c>opacity</c>) and
    /// placement (<c>transform</c>) are not here — see <see cref="RootSnapshotNeedsContent"/>.</summary>
    private static readonly string[] SnapshotAlteringProperties =
    {
        "filter", "backdrop-filter", "mix-blend-mode", "mask", "mask-image", "clip-path",
    };

    private static bool HasSnapshotAlteringEffect(Dictionary<string, string> declarations)
    {
        foreach (var property in SnapshotAlteringProperties)
        {
            if (declarations.TryGetValue(property, out var value) &&
                !string.IsNullOrWhiteSpace(value) &&
                !value.Trim().Equals("none", System.StringComparison.OrdinalIgnoreCase) &&
                !value.Trim().Equals("normal", System.StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Metadata and script children that never paint, so a root snapshot must not clone them:
    /// re-inserting <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> would duplicate author rules into the live
    /// document and <c>&lt;script&gt;</c>/<c>&lt;link&gt;</c> could re-fetch external resources.</summary>
    private static bool IsNonRenderedSnapshotChild(DomNode node) =>
        node is DomElement element &&
        element.TagName is { } tag &&
        (tag.Equals("head", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("style", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("script", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("link", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("meta", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("title", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("base", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The old root snapshot's content: the page as it stands before the update callback. Built like
    /// any other captured element's content box, minus the parts of <c>&lt;html&gt;</c> that never
    /// paint — cloning <c>&lt;head&gt;</c> would re-insert its <c>&lt;style&gt;</c>/<c>&lt;script&gt;</c>
    /// into the live document, duplicating author rules (including the <c>::view-transition</c> rules
    /// driving the transition) and re-fetching external resources.
    /// </summary>
    private DomElement BuildRootViewTransitionSnapshotContent(Dictionary<string, string> rootStyle)
    {
        var content = BuildViewTransitionSnapshotContent(DocumentElement, asRootSnapshot: true);
        // The root snapshot captures the viewport, so it paints the canvas background rather than
        // the root box's own — which is usually `transparent`, and would let the ::view-transition
        // background behind the snapshot show through the captured page.
        InlineStyle(content)["background-color"] = ResolveCapturedCanvasBackground(rootStyle);
        return content;
    }

    /// <summary>
    /// The canvas background at capture time, per the CSS 2.1 §14.2 propagation model: the root's own
    /// background when it paints one, else the body's (which propagates to the canvas), else the
    /// UA default. A root snapshot must be opaque — it stands in for the whole viewport.
    /// </summary>
    private string ResolveCapturedCanvasBackground(Dictionary<string, string> rootStyle)
    {
        if (PaintsBackground(rootStyle.GetValueOrDefault("background-color")) is { } rootBackground)
            return rootBackground;

        foreach (var element in DocumentElement.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "body", System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (PaintsBackground(UsedStyleForCapture(element).GetValueOrDefault("background-color")) is { } body)
                return body;
            break;
        }

        return "white";
    }

    /// <summary>The colour when it actually paints, or null when it is absent or fully transparent
    /// (<c>transparent</c> and the <c>rgba(…, 0)</c> the computed-style engine serializes it as).</summary>
    private static string? PaintsBackground(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            trimmed.Equals("transparent", System.StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("none", System.StringComparison.OrdinalIgnoreCase))
            return null;

        // rgba(…, 0) / rgb(… / 0) — a fully transparent computed colour paints nothing.
        var lastComma = trimmed.LastIndexOf(',');
        if (trimmed.EndsWith(")", System.StringComparison.Ordinal) && lastComma >= 0)
        {
            var alpha = trimmed[(lastComma + 1)..].TrimEnd(')').Trim();
            if (double.TryParse(alpha, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed == 0)
                return null;
        }

        return trimmed;
    }

    /// <summary>Strips identity/capture markers from a cloned snapshot subtree: <c>id</c> (so the
    /// clone does not duplicate a live element's id) and any inline <c>view-transition-name</c> (so a
    /// re-serialize cannot capture the clone as a named element).</summary>
    private void StripCapturedIdentifiers(DomNode node, bool preserveIds = false)
    {
        if (node is DomElement element)
        {
            if (!preserveIds && element.HasAttribute("id"))
                element.RemoveAttribute("id");
            InlineStyle(element).Remove("view-transition-name");
        }

        foreach (var child in node.ChildNodes.ToArray())
            StripCapturedIdentifiers(child, preserveIds);
    }

    /// <summary>
    /// Whether the captured element establishes a paint clip on its descendants — a non-visible
    /// <c>overflow</c>, <c>contain: paint/content/strict</c>, or a <c>clip-path</c>. Such an element's
    /// view-transition snapshot clips to the border box; a default <c>overflow: visible</c> element's
    /// snapshot instead shows its ink overflow (WPT capture-with-offscreen-child-translated).
    /// </summary>
    private static bool CapturedElementClipsContent(Dictionary<string, string> style)
    {
        var overflow = style.GetValueOrDefault("overflow", "visible");
        if (!IsVisibleOverflow(style.GetValueOrDefault("overflow-x", overflow))
            || !IsVisibleOverflow(style.GetValueOrDefault("overflow-y", overflow)))
            return true;

        var contain = style.GetValueOrDefault("contain", string.Empty);
        if (contain.Contains("paint", System.StringComparison.OrdinalIgnoreCase)
            || contain.Contains("content", System.StringComparison.OrdinalIgnoreCase)
            || contain.Contains("strict", System.StringComparison.OrdinalIgnoreCase))
            return true;

        var clipPath = style.GetValueOrDefault("clip-path", "none");
        return !string.IsNullOrWhiteSpace(clipPath)
            && !clipPath.Equals("none", System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisibleOverflow(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Equals("visible", System.StringComparison.OrdinalIgnoreCase);

    private readonly record struct ViewTransitionCapture(
        string Name,
        // The captured element's `view-transition-class` list, matched by a pseudo argument's
        // `.class` part (css-view-transitions-2 <pt-class-selector>). Empty when it has none.
        string Classes,
        double GroupLeft, double GroupTop,
        bool HasOld, double OldLeft, double OldTop, double OldWidth, double OldHeight, string OldBackground, DomElement? OldContent,
        bool HasNew, double NewLeft, double NewTop, double NewWidth, double NewHeight, string NewBackground, DomElement? NewContent);

    /// <summary>
    /// The captured names, each pairing the "old" snapshot (from before the update callback) with the
    /// "new" one (the element as it stands now), in old-then-new document order. Names appearing only
    /// on one side keep just that snapshot; the group is placed at the old geometry when present.
    /// </summary>
    private List<ViewTransitionCapture> CollectViewTransitionCaptures(DomElement root, bool newRootNeedsContent = false)
    {
        var newCaptures = new Dictionary<string, NamedSnapshot>(System.StringComparer.Ordinal);
        var order = new List<string>();
        // `view-transition-class` per captured name, so ::view-transition-group(*.item) can address a
        // group by class rather than by name (css-view-transitions-2).
        var classesByName = new Dictionary<string, string>(System.StringComparer.Ordinal);

        void AddNew(string name, NamedSnapshot snapshot)
        {
            if (newCaptures.TryAdd(name, snapshot))
                order.Add(name);
        }

        var rootStyle = UsedStyleForCapture(root);
        var rootName = ResolveRootViewTransitionName(rootStyle);
        if (rootName is not null)
        {
            var (l, t, w, h) = GetBoundingClientRectForDomElement(root, isRoot: true);
            // Content-less unless the live page provably cannot stand in for this snapshot — see
            // RootSnapshotNeedsContent for the two ways that happens and why cloning it
            // unconditionally was reverted.
            AddNew(rootName, new NamedSnapshot(l, t, w, h,
                rootStyle.GetValueOrDefault("background-color") ?? "transparent",
                newRootNeedsContent ? BuildRootViewTransitionSnapshotContent(rootStyle) : null));
            classesByName[rootName] = rootStyle.GetValueOrDefault("view-transition-class") ?? string.Empty;
        }

        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            var style = UsedStyleForCapture(element);
            var name = ResolveUsedViewTransitionName(element, style.GetValueOrDefault("view-transition-name"));
            if (name is null || string.Equals(name, rootName, System.StringComparison.Ordinal))
                continue;

            var (l, t, w, h) = GetBoundingClientRectForDomElement(element, isRoot: false);
            AddNew(name, new NamedSnapshot(l, t, w, h,
                style.GetValueOrDefault("background-color") ?? "transparent",
                BuildViewTransitionSnapshotContent(element)));
            classesByName.TryAdd(name, style.GetValueOrDefault("view-transition-class") ?? string.Empty);
        }

        var oldCaptures = _activeViewTransition!.OldCaptures;
        // Names present in the old state but gone from the new keep their old-only snapshot.
        foreach (var name in oldCaptures.Keys)
            if (!newCaptures.ContainsKey(name))
                order.Add(name);

        var captures = new List<ViewTransitionCapture>(order.Count);
        foreach (var name in order)
        {
            var hasOld = oldCaptures.TryGetValue(name, out var old);
            var hasNew = newCaptures.TryGetValue(name, out var @new);
            var anchor = hasOld ? old : @new; // group start geometry
            captures.Add(new ViewTransitionCapture(
                name, classesByName.GetValueOrDefault(name) ?? string.Empty, anchor.Left, anchor.Top,
                hasOld, old.Left, old.Top, old.Width, old.Height, old.BackgroundColor, old.Content,
                hasNew, @new.Left, @new.Top, @new.Width, @new.Height, @new.BackgroundColor, @new.Content));
        }

        return captures;
    }

    /// <summary>The element's used style for capture purposes: its computed style with the
    /// serialize-time baked overlay layered on top. The overlay carries the
    /// <c>:active-view-transition-type()</c> declarations applied just before this runs — which the
    /// computed-style engine has not re-cascaded yet — so they must be read from the overlay directly
    /// (e.g. a freshly baked <c>view-transition-name</c> or <c>background</c>).</summary>
    private Dictionary<string, string> UsedStyleForCapture(DomElement element)
    {
        var used = BuildComputedStyleMap(element);
        foreach (var (key, value) in EffectiveInlineStyle(element))
        {
            used[key] = value;
            // `background` shorthand carries the colour the capture box needs; project it.
            if (key.Equals("background", System.StringComparison.OrdinalIgnoreCase))
                used["background-color"] = value;
        }
        return used;
    }

    private static bool IsNoneName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        string.Equals(name.Trim(), "none", System.StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name.Trim(), "normal", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the used <c>view-transition-name</c> for <paramref name="element"/> per
    /// css-view-transitions-2: <c>none</c>/absent → null (not captured); a <c>&lt;custom-ident&gt;</c>
    /// → itself; <c>auto</c>/<c>match-element</c> → a generated name that is unique per element and
    /// stable for the transition (so the old and new captures pair into one group). <c>auto</c> on an
    /// element with an id derives from that id (elements sharing an id share the name).
    /// </summary>
    private string? ResolveUsedViewTransitionName(DomElement element, string? rawName)
    {
        if (IsNoneName(rawName))
            return null;

        var trimmed = rawName!.Trim();
        if (trimmed.Equals("auto", System.StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("match-element", System.StringComparison.OrdinalIgnoreCase))
            return GenerateAutoViewTransitionName(element, trimmed);

        return trimmed;
    }

    private string GenerateAutoViewTransitionName(DomElement element, string keyword)
    {
        var map = _activeViewTransition!.AutoNames;
        var identityElement = ResolveRenderSource(element);
        if (map.TryGetValue(identityElement, out var existing))
            return existing;

        // auto with an id → a stable id-derived name (two elements with the same id resolve equal, as
        // the spec requires); auto without an id, and match-element → a unique per-element name. The
        // "-ua-" prefix mirrors the spec's generated-name convention and cannot collide with a
        // <custom-ident> (which may not start with two dashes but may not be "-ua-…" either here).
        var id = identityElement.Id;
        var generated = keyword.Equals("auto", System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(id)
            ? "-ua-id-" + id
            : "-ua-el-" + (map.Count + 1);
        map[identityElement] = generated;
        return generated;
    }

    private static bool IsExplicitNoneName(string? name) =>
        name is not null && string.Equals(name.Trim(), "none", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name the document element is captured under. The UA stylesheet gives it
    /// <c>view-transition-name: root</c>, so that is the default — but an author may rename it, and
    /// then <c>root</c> is just an ordinary name that matches nothing. WPT
    /// <c>root-captured-as-different-tag</c> pins exactly that: it names the root
    /// <c>another-root</c> and paints <c>::view-transition-group(root)</c> red to assert the
    /// <c>root</c> rules no longer apply. <see langword="null"/> when the root is not captured.
    /// <c>auto</c>/<c>match-element</c> on the document element resolve to <c>root</c> rather than a
    /// generated name (css-view-transitions-2).
    /// </summary>
    private static string? ResolveRootViewTransitionName(Dictionary<string, string> rootStyle)
    {
        var raw = rootStyle.GetValueOrDefault("view-transition-name");
        if (IsExplicitNoneName(raw))
            return null;

        if (IsNoneName(raw))
            return "root";

        var trimmed = raw!.Trim();
        return trimmed.Equals("auto", System.StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("match-element", System.StringComparison.OrdinalIgnoreCase)
            ? "root"
            : trimmed;
    }

    /// <summary>Author <c>::view-transition*</c> declarations, keyed by
    /// <c>"&lt;kind&gt;|&lt;argument&gt;"</c> (kind is <c>""</c> for the bare <c>::view-transition</c>,
    /// else <c>group</c>/<c>image-pair</c>/<c>old</c>/<c>new</c>; argument is the name/class/<c>*</c>).
    /// Later rules win, matching document order.</summary>
    private Dictionary<string, Dictionary<string, string>> CollectViewTransitionPseudoDeclarations(DomElement root)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(System.StringComparer.Ordinal);

        foreach (var (selectorText, declarations) in EnumerateAuthorStyleRules(root))
        {
            if (selectorText.IndexOf("::view-transition", System.StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            var match = ViewTransitionPseudo.Match(selectorText);
            if (!match.Success)
                continue;

            var kind = match.Groups[1].Value.ToLowerInvariant();
            var argument = match.Groups[2].Success ? match.Groups[2].Value.Trim() : string.Empty;
            var key = $"{kind}|{argument}";

            if (!result.TryGetValue(key, out var bucket))
                result[key] = bucket = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var declaration in declarations.Declarations)
                bucket[declaration.Name] = declaration.Value.Text;
        }

        return result;
    }

    /// <summary>The declarations that apply to a pseudo of <paramref name="kind"/> for a given
    /// capture, merging the universal (<c>*</c>) and name-specific buckets in cascade order (specific
    /// wins). <paramref name="capture"/> is <c>null</c> for the bare overlay pseudo.</summary>
    private static Dictionary<string, string> LookupPseudo(
        Dictionary<string, Dictionary<string, string>> pseudoRules, string kind, ViewTransitionCapture? capture)
    {
        var merged = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        void Merge(string argument)
        {
            if (pseudoRules.TryGetValue($"{kind}|{argument}", out var bucket))
                foreach (var (k, v) in bucket)
                    merged[k] = v;
        }

        if (capture is null)
        {
            Merge(string.Empty);
            return merged;
        }

        // css-view-transitions-2 lets a pseudo argument select by class as well as by name —
        // `::view-transition-group(*.item)`, and the name-less `.item` shorthand — where the classes
        // come from the captured element's `view-transition-class`. Merge least-specific first so a
        // more specific rule wins: `*`, then class-only rules, then the exact name (with or without
        // classes of its own). WPT auto-name drives its whole transition off `(.item)`.
        Merge("*");

        var classes = SplitViewTransitionClasses(capture.Value.Classes);
        var prefix = $"{kind}|";
        foreach (var key in pseudoRules.Keys)
        {
            if (!key.StartsWith(prefix, System.StringComparison.Ordinal))
                continue;

            var argument = key[prefix.Length..];
            if (argument.Length == 0 || argument == "*" || argument == capture.Value.Name)
                continue; // handled by the explicit merges around this loop

            var (nameSelector, requiredClasses) = ParsePseudoArgument(argument);
            if (requiredClasses.Count == 0)
                continue; // a plain name that is not this capture's

            if (nameSelector is not "*" && !string.Equals(nameSelector, capture.Value.Name, System.StringComparison.Ordinal))
                continue;

            if (requiredClasses.All(required => classes.Contains(required)))
                Merge(argument);
        }

        Merge(capture.Value.Name);
        return merged;
    }

    /// <summary>Splits a <c>view-transition-class</c> value into its idents. <c>none</c> (the initial
    /// value) contributes nothing.</summary>
    private static HashSet<string> SplitViewTransitionClasses(string? value)
    {
        var classes = new HashSet<string>(System.StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(value))
            return classes;

        foreach (var token in value.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries))
            if (!token.Equals("none", System.StringComparison.OrdinalIgnoreCase))
                classes.Add(token);

        return classes;
    }

    /// <summary>
    /// Splits a <c>::view-transition-*()</c> argument into its name selector and class selectors —
    /// <c>&lt;pt-name-selector&gt;&lt;pt-class-selector&gt;?</c>. A leading <c>.</c> means the name
    /// selector was omitted, which is the same as <c>*</c>.
    /// </summary>
    private static (string Name, List<string> Classes) ParsePseudoArgument(string argument)
    {
        var trimmed = argument.Trim();
        var dot = trimmed.IndexOf('.');
        if (dot < 0)
            return (trimmed, []);

        var name = dot == 0 ? "*" : trimmed[..dot];
        var classes = trimmed[(dot + 1)..]
            .Split('.', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Trim())
            .Where(static part => part.Length > 0)
            .ToList();

        return (name, classes);
    }

    /// <summary>
    /// Whether the author kept the bare <c>::view-transition</c> root overlay alive with an
    /// animation. Only consulted when the transition captured nothing: an empty transition
    /// finishes at once (its pseudo tree gone by screenshot time), so the overlay paints only when
    /// an author animation on the root pseudo pins it open — the difference between WPT
    /// <c>no-named-elements</c> (blue overlay, <c>animation: no-op 300s</c>) and
    /// <c>nothing-captured</c> (red overlay, no animation, must stay hidden).
    /// </summary>
    private static bool HasRootOverlayKeepAliveAnimation(
        Dictionary<string, Dictionary<string, string>> pseudoRules)
    {
        // The bare ::view-transition bucket is keyed "<kind>|<argument>" with both empty (see
        // CollectViewTransitionPseudoDeclarations), i.e. "|".
        if (!pseudoRules.TryGetValue("|", out var rootDeclarations))
            return false;

        if (rootDeclarations.TryGetValue("animation", out var shorthand) && !IsInertAnimationValue(shorthand))
            return true;
        if (rootDeclarations.TryGetValue("animation-name", out var name) && !IsInertAnimationValue(name))
            return true;
        if (rootDeclarations.TryGetValue("animation-duration", out var duration) && !IsInertAnimationDuration(duration))
            return true;
        return false;
    }

    /// <summary>An <c>animation</c>/<c>animation-name</c> value that starts no animation
    /// (absent, <c>none</c>, or a CSS-wide keyword).</summary>
    private static bool IsInertAnimationValue(string value)
    {
        var v = value.Trim();
        return v.Length == 0 ||
            v.Equals("none", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("unset", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("initial", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("inherit", System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An <c>animation-duration</c> value that leaves the animation zero-length (so it
    /// does not keep the overlay alive).</summary>
    private static bool IsInertAnimationDuration(string value)
    {
        var v = value.Trim();
        return v.Length == 0 || v == "0" ||
            v.Equals("0s", System.StringComparison.OrdinalIgnoreCase) ||
            v.Equals("0ms", System.StringComparison.OrdinalIgnoreCase) ||
            IsInertAnimationValue(v);
    }

    /// <summary>Author style rules (selector text + declarations) across every <c>&lt;style&gt;</c>
    /// and external-stylesheet <c>&lt;link rel="stylesheet"&gt;</c> in the tree, in document order.
    /// External links are included because the <c>::view-transition-*</c> pseudo rules and the
    /// <c>view-transition-group</c> values — which are read from the raw author rules here rather than
    /// through computed style — routinely live in a linked stylesheet (the WPT
    /// <c>css-view-transitions-2 nested</c> tests keep the entire pseudo tree styling in
    /// <c>resources/*.css</c>). A disabled sheet contributes nothing (CSSOM §2.3).</summary>
    private IEnumerable<(string SelectorText, CssDeclarationBlock Declarations)> EnumerateAuthorStyleRules(DomElement root)
    {
        foreach (var styleEl in root.Descendants().OfType<DomElement>())
        {
            if (!(styleEl.TagName.Equals("style", System.StringComparison.OrdinalIgnoreCase)
                    || IsExternalStylesheet(styleEl))
                || IsStyleSheetDisabled(styleEl))
                continue;

            var source = GetStyleElementSourceText(styleEl);
            if (string.IsNullOrEmpty(source))
                continue;

            CssStyleSheet sheet;
            try { sheet = new CssParser().ParseStyleSheet(source); }
            catch { continue; }

            foreach (var rule in sheet.Rules)
            {
                if (rule is not CssStyleRule styleRule)
                    continue;
                foreach (var selector in styleRule.Selectors.Selectors)
                    yield return (selector.Text, styleRule.Declarations);
            }
        }
    }

    private static string Px(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
}
