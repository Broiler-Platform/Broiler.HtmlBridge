using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>LayoutMetrics.cs</c> to keep it
/// under the 750-line guideline: the scrolling behaviour surface — <c>scrollIntoView</c> option/argument
/// parsing, element scroll-offset get/set with behaviour, scroll-event dispatch, visual-viewport
/// scroll/scale, and programmatic-scrollability / overflow analysis. Pure partial-class relocation —
/// no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    // The per-element JS-visible scroll offset was
    // the Scroll slot of the process-static ElementRuntimeState table; it is now a per-bridge
    // instance table, so the scroll memo is owned by the session's bridge rather than the process.
    // Still an element-keyed ConditionalWeakTable, so a detached element's offset GCs with it, and
    // the cloneNode copy (see CloneDomElement) is preserved. All scroll-offset access is on the
    // bridge instance, so this needed no static-helper cascade (unlike the remaining concerns).
    private readonly ConditionalWeakTable<DomElement, ScrollRuntimeState> _scrollRuntimeStates = [];

    private ScrollRuntimeState ScrollStateFor(DomElement element) =>
        _scrollRuntimeStates.GetValue(element, static _ => new ScrollRuntimeState());

    private void ScrollElementIntoView(DomElement element,
        string? block = null, string? inline = null, string? behavior = null)
    {
        var current = element;
        for (var i = 0; i < MaxScrollContinuationDepth && current != null; i++)
        {
            var scrollContainer = FindScrollContainer(current) ?? GetOwningDocumentElement(current);
            if (scrollContainer == null)
                return;

            if (IsDocumentElement(scrollContainer) && HasFixedPositionInDocument(current, scrollContainer))
            {
                if (HasActiveVisualViewport())
                {
                    ScrollFixedElementIntoVisualViewport(element, scrollContainer, block, inline);
                    current = GetOuterFrameElement(scrollContainer);
                    continue;
                }

                current = GetOuterFrameElement(scrollContainer);
                continue;
            }

            var (horizontalAlignment, verticalAlignment) = ResolvePhysicalScrollIntoViewAlignments(
                scrollContainer,
                block,
                inline);
            var scrollTop = ResolveScrollIntoViewOffset(element, scrollContainer, vertical: true, alignment: verticalAlignment);
            var scrollLeft = ResolveScrollIntoViewOffset(element, scrollContainer, vertical: false, alignment: horizontalAlignment);

            SetElementScrollOffsetsWithBehavior(scrollContainer, scrollLeft, scrollTop, clamp: true, behavior: behavior);

            var next = GetOuterScrollContinuationElement(scrollContainer);
            if (next == null || ReferenceEquals(next, current))
                return;

            current = next;
        }
    }

    // -------- the scroll-option readers, and who actually calls them --------
    //
    // These answer one member of a ScrollToOptions/ScrollIntoViewOptions dictionary. The reads are the
    // realm's: a missing, null or undefined member means "leave this alone", and a member that is
    // present goes through the realm's own ToNumber and ToString, because that is the coercion a page
    // observes when it writes `scrollTo({ left: "100" })`. The handle's own rendering deliberately does
    // not run a page's toString, so it cannot be used here.
    //
    // NOTE: these are NOT shared with the other scrolling entry points. DomBridge/Hosts.Elements.cs is
    // the only caller in the tree; the window and sub-window contracts read their own options through
    // ScrollCoordinateOption/ScrollBehaviorOption in DomBridge/Hosts.Documents.cs, which is a second
    // copy of this reading rather than a use of it. DomBridge/Hosts.Window.cs forwards to that copy.
    // These members are instance rather than static by preference, not by constraint -- the pair in
    // Hosts.Documents.cs is static and takes the realm as a parameter.

    private double? ReadScrollCoordinateOption(JsValue options, string propertyName)
    {
        var value = Realm.GetProperty(options, propertyName);
        return value.IsNullish ? null : Realm.ToNumber(value);
    }

    private string? ReadScrollBehaviorOption(JsValue options) => ReadScrollStringOption(options, "behavior");

    private string? ReadScrollStringOption(JsValue options, string propertyName)
    {
        var value = Realm.GetProperty(options, propertyName);
        if (value.IsNullish)
            return null;

        var text = Realm.ToJsString(value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private (string Horizontal, string Vertical) ResolvePhysicalScrollIntoViewAlignments(
        DomElement scrollContainer, string? block, string? inline)
    {
        var props = GetComputedProps(scrollContainer);
        var writingMode = props.GetValueOrDefault("writing-mode")?.Trim().ToLowerInvariant();
        var direction = props.GetValueOrDefault("direction");
        bool isVerticalWritingMode = CssWritingMode.IsVertical(writingMode);
        bool isRtl = string.Equals(direction, "rtl", StringComparison.OrdinalIgnoreCase);

        var horizontal = ResolvePhysicalAxisAlignment(
            alignment: isVerticalWritingMode ? block : inline,
            startMapsToPhysicalStart: !isVerticalWritingMode
                ? !isRtl
                : writingMode?.EndsWith("-rl", StringComparison.Ordinal) != true);
        var vertical = ResolvePhysicalAxisAlignment(
            alignment: isVerticalWritingMode ? inline : block,
            startMapsToPhysicalStart: !isVerticalWritingMode || !isRtl);
        return (horizontal, vertical);
    }

    private double GetElementScrollOffset(DomElement element, bool vertical)
    {
        if (!CanProgrammaticallyScroll(element, vertical))
            return 0;

        return TryGetStoredScrollOffset(element, vertical, out var scrollOffset)
            ? scrollOffset
            : 0;
    }

    private (double Left, double Top) ResolveElementScrollOffsets(DomElement element, double? left = null, double? top = null, bool relative = false, bool clamp = true)
    {
        var currentLeft = GetElementScrollOffset(element, vertical: false);
        var currentTop = GetElementScrollOffset(element, vertical: true);

        var nextLeft = left.HasValue ? (relative ? currentLeft + left.Value : left.Value) : currentLeft;
        var nextTop = top.HasValue ? (relative ? currentTop + top.Value : top.Value) : currentTop;

        if (!CanProgrammaticallyScroll(element, vertical: false))
            nextLeft = 0;
        if (!CanProgrammaticallyScroll(element, vertical: true))
            nextTop = 0;

        if (clamp)
        {
            var (minLeft, maxLeft, minTop, maxTop) = GetScrollBounds(element);
            nextLeft = Math.Clamp(nextLeft, minLeft, maxLeft);
            nextTop = Math.Clamp(nextTop, minTop, maxTop);
        }

        // A mandatory snap container must come to rest on a snap position after any scroll,
        // so this is applied to the result of every scrolling entry point rather than to any
        // one of them (CSS Scroll Snap 1 §2). A non-snapping container is unchanged.
        //
        // This runs even under `clamp: false`, which is deliberate: snapping is a property of the
        // container, not of the API used to reach it, and a snap position is by definition inside the
        // scrollable range. Only the sub-window scroll contract (DomBridge/Hosts.Documents.cs) passes it:
        // the page window's and every element's scroll/scrollTo/scrollBy clamp. A caller that opts out
        // still gets no clamping on an ordinary scroll container — only on one that asked to snap.
        nextLeft = ResolveScrollSnapPosition(element, vertical: false, nextLeft);
        nextTop = ResolveScrollSnapPosition(element, vertical: true, nextTop);

        return (nextLeft, nextTop);
    }

    private void SetElementScrollOffsetsWithBehavior(DomElement element,
        double? left = null, double? top = null,
        bool relative = false, bool clamp = true,
        string? behavior = null)
    {
        var trackVisualViewport = ReferenceEquals(element, DocumentElement);
        var previousVisualPageLeft = trackVisualViewport ? GetVisualViewportPageOffset(vertical: false) : 0;
        var previousVisualPageTop = trackVisualViewport ? GetVisualViewportPageOffset(vertical: true) : 0;
        var previousLeft = GetElementScrollOffset(element, vertical: false);
        var previousTop = GetElementScrollOffset(element, vertical: true);
        var (targetLeft, targetTop) = ResolveElementScrollOffsets(element, left, top, relative, clamp);
        var hadActiveSmoothScroll = _smoothScrollTokens.ContainsKey(element);
        var effectiveBehavior = ResolveScrollBehavior(element, behavior);
        if (hadActiveSmoothScroll && NormalizeScrollBehavior(behavior) != "smooth")
            effectiveBehavior = "instant";
        CancelSmoothScroll(element);

        if (string.Equals(effectiveBehavior, "smooth", StringComparison.OrdinalIgnoreCase))
        {
            var token = Interlocked.Increment(ref _smoothScrollTokenCounter);
            _smoothScrollTokens[element] = token;
            QueueFrameAction(() =>
            {
                if (_smoothScrollTokens.TryGetValue(element, out var activeToken) && activeToken == token)
                {
                    var queuedPreviousLeft = GetElementScrollOffset(element, vertical: false);
                    var queuedPreviousTop = GetElementScrollOffset(element, vertical: true);
                    var queuedPreviousVisualPageLeft = trackVisualViewport ? GetVisualViewportPageOffset(vertical: false) : 0;
                    var queuedPreviousVisualPageTop = trackVisualViewport ? GetVisualViewportPageOffset(vertical: true) : 0;
                    ScrollStateFor(element).Left.Set(targetLeft);
                    ScrollStateFor(element).Top.Set(targetTop);
                    NotifyVisualViewportScrollIfNeeded(queuedPreviousVisualPageLeft, queuedPreviousVisualPageTop, trackVisualViewport);
                    DispatchScrollEventIfNeeded(element, queuedPreviousLeft, queuedPreviousTop);
                    DispatchScrollEndEventIfNeeded(element, queuedPreviousLeft, queuedPreviousTop);
                    _smoothScrollTokens.TryRemove(element, out _);
                }
            });

            // Approximate smooth scrolling with a visible intermediate frame before
            // finishing on the next queued frame.
            ScrollStateFor(element).Left.Set(previousLeft + ((targetLeft - previousLeft) / 2.0));
            ScrollStateFor(element).Top.Set(previousTop + ((targetTop - previousTop) / 2.0));
            NotifyVisualViewportScrollIfNeeded(previousVisualPageLeft, previousVisualPageTop, trackVisualViewport);
            DispatchScrollEventIfNeeded(element, previousLeft, previousTop);
            return;
        }

        ScrollStateFor(element).Left.Set(targetLeft);
        ScrollStateFor(element).Top.Set(targetTop);
        NotifyVisualViewportScrollIfNeeded(previousVisualPageLeft, previousVisualPageTop, trackVisualViewport);
        DispatchScrollEventIfNeeded(element, previousLeft, previousTop);
        DispatchScrollEndEventIfNeeded(element, previousLeft, previousTop);
    }

    private void QueueFrameAction(Action callback) => _eventLoop.QueueFrameAction(callback);

    private void CancelSmoothScroll(DomElement element) => _smoothScrollTokens.TryRemove(element, out _);

    private void DispatchScrollEventIfNeeded(DomElement element, double previousLeft, double previousTop)
    {
        if (AreClose(previousLeft, GetElementScrollOffset(element, vertical: false)) &&
            AreClose(previousTop, GetElementScrollOffset(element, vertical: true)))
            return;

        DispatchElementEvent(element, "scroll");
        DispatchViewportScrollEventToWindow(element, "scroll");
    }

    private void DispatchScrollEndEventIfNeeded(DomElement element, double previousLeft, double previousTop)
    {
        if (AreClose(previousLeft, GetElementScrollOffset(element, vertical: false)) &&
            AreClose(previousTop, GetElementScrollOffset(element, vertical: true)))
            return;

        DispatchElementEvent(element, "scrollend");
        DispatchViewportScrollEventToWindow(element, "scrollend");
    }

    /// <summary>
    /// Also delivers a viewport scroll to <c>window</c>'s listeners.
    /// </summary>
    /// <remarks>
    /// CSSOM View fires a scrolling event at the <em>Document</em> when the scrolling box is the
    /// viewport, so it reaches the window through the propagation path — which is why
    /// <c>addEventListener("scroll", …)</c> at global scope (a window listener, the idiomatic
    /// spelling) is how pages observe page scrolling. This bridge dispatches the event on the
    /// document element and marks it non-bubbling, so a window listener never saw it and
    /// <c>document.documentElement.scrollTop = N</c> looked like it scrolled silently: the offset
    /// changed and the render moved, but nothing was notified. WPT
    /// <c>css-view-transitions/*-root-scrollbar-with-fixed-background</c> awaits exactly such a
    /// listener before it starts its transition, so the test simply stopped there.
    /// <para>
    /// Delivered in addition to the element dispatch rather than instead of it, so element-level
    /// and <c>onscroll</c> listeners keep seeing what they saw before.
    /// </para>
    /// </remarks>
    private void DispatchViewportScrollEventToWindow(DomElement element, string eventType)
    {
        if (ReferenceEquals(element, DocumentElement))
            DispatchWindowEvent(eventType);
    }

    private void DispatchElementEvent(DomElement element, string eventType)
    {
        var evt = Realm.NewObject();
        Realm.DefineValue(evt, "type", JsValue.String(eventType));
        Realm.DefineValue(evt, "bubbles", JsValue.False);
        // The dispatcher takes the handle as it is, so the listeners see the object built here. (This
        // said element dispatch had not migrated and the event crossed back as the engine's object.)
        _eventDispatch.DispatchEventOnElement(element, evt);
    }

    private string ResolveScrollBehavior(DomElement element, string? requestedBehavior)
    {
        var normalizedRequested = NormalizeScrollBehavior(requestedBehavior);
        if (normalizedRequested == "instant" || normalizedRequested == "smooth")
            return normalizedRequested;

        var props = GetComputedProps(element);
        return NormalizeScrollBehavior(props.GetValueOrDefault("scroll-behavior")) == "smooth"
            ? "smooth"
            : "instant";
    }

    private bool HasActiveVisualViewport() => GetVisualViewportScale() > 1.0001;

    private double GetVisualViewportScale() => _visualViewportScale > 1 ? _visualViewportScale : 1;

    private double GetVisualViewportWidth() => _viewportWidth / GetVisualViewportScale();

    private double GetVisualViewportHeight() => _viewportHeight / GetVisualViewportScale();

    private double GetVisualViewportPageOffset(bool vertical)
    {
        var layoutOffset = GetElementScrollOffset(DocumentElement, vertical);
        return layoutOffset + GetVisualViewportExtraOffset(vertical);
    }

    private void SetVisualViewportScale(double scale)
    {
        _visualViewportScale = double.IsFinite(scale) && scale > 1 ? scale : 1;
        ClampVisualViewportOffsets();
    }

    private void ScrollFixedElementIntoVisualViewport(DomElement element, DomElement scrollContainer,
        string? block, string? inline)
    {
        var targetTop = ResolveScrollIntoViewOffset(
            element,
            scrollContainer,
            vertical: true,
            alignment: block,
            viewportSizeOverride: GetVisualViewportHeight(),
            currentScrollOverride: GetVisualViewportPageOffset(vertical: true),
            offsetOverride: GetElementScrollOffset(scrollContainer, vertical: true) +
                OffsetWithinAncestorForFixedPreferShared(element, scrollContainer, vertical: true),
            coordinateSpaceIsPhysical: true);
        var targetLeft = ResolveScrollIntoViewOffset(
            element,
            scrollContainer,
            vertical: false,
            alignment: inline,
            viewportSizeOverride: GetVisualViewportWidth(),
            currentScrollOverride: GetVisualViewportPageOffset(vertical: false),
            offsetOverride: GetElementScrollOffset(scrollContainer, vertical: false) +
                OffsetWithinAncestorForFixedPreferShared(element, scrollContainer, vertical: false),
            coordinateSpaceIsPhysical: true);
        SetVisualViewportPageOffsets(left: targetLeft, top: targetTop);
    }

    private void SetVisualViewportPageOffsets(double? left = null, double? top = null)
    {
        var oldPageLeft = GetVisualViewportPageOffset(vertical: false);
        var oldPageTop = GetVisualViewportPageOffset(vertical: true);
        var layoutLeft = GetElementScrollOffset(DocumentElement, vertical: false);
        var layoutTop = GetElementScrollOffset(DocumentElement, vertical: true);

        if (left.HasValue)
        {
            _visualViewportPageLeftOffset = Math.Clamp(
                left.Value - layoutLeft,
                0,
                GetVisualViewportMaxExtraOffset(vertical: false));
        }

        if (top.HasValue)
        {
            _visualViewportPageTopOffset = Math.Clamp(
                top.Value - layoutTop,
                0,
                GetVisualViewportMaxExtraOffset(vertical: true));
        }

        if (!AreClose(oldPageLeft, GetVisualViewportPageOffset(vertical: false)) ||
            !AreClose(oldPageTop, GetVisualViewportPageOffset(vertical: true)))
        {
            DispatchVisualViewportScrollEvent();
        }
    }

    private void ClampVisualViewportOffsets()
    {
        _visualViewportPageLeftOffset = Math.Clamp(_visualViewportPageLeftOffset, 0, GetVisualViewportMaxExtraOffset(vertical: false));
        _visualViewportPageTopOffset = Math.Clamp(_visualViewportPageTopOffset, 0, GetVisualViewportMaxExtraOffset(vertical: true));
    }

    private double GetVisualViewportExtraOffset(bool vertical) =>
        vertical ? _visualViewportPageTopOffset : _visualViewportPageLeftOffset;

    private double GetVisualViewportMaxExtraOffset(bool vertical)
    {
        if (!HasActiveVisualViewport())
            return 0;

        var layoutSize = vertical ? _viewportHeight : _viewportWidth;
        var visualSize = vertical ? GetVisualViewportHeight() : GetVisualViewportWidth();
        return Math.Max(0, layoutSize - visualSize);
    }

    private void DispatchVisualViewportScrollEvent()
    {
        var target = VisualViewportHandle;
        if (target.IsMissing || _eventTargets.VisualViewportScrollListeners.Count == 0)
            return;

        // The target is the visualViewport root (DomBridge.cs), the handle the registration hub minted,
        // so the object the listeners see is the one they registered on with no conversion. The listener
        // list holds handles too, so no listener crosses below. IsMissing rather than a null test:
        // against a handle a null test compiles, is false forever, and runs the listeners against an
        // absent target.
        var evt = Realm.NewObject();
        Realm.DefineValue(evt, "type", JsValue.String("scroll"));
        Realm.DefineValue(evt, "target", target);
        Realm.DefineValue(evt, "currentTarget", target);

        foreach (var listener in _eventTargets.VisualViewportScrollListeners.ToList())
        {
            try
            {
                // `this` is the listener itself, as it has been since this dispatch was written.
                Realm.Invoke(listener, listener, [evt]);
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.visualViewport", $"Visual viewport listener error: {ex.Message}", ex);
            }
        }
    }

    private void NotifyVisualViewportScrollIfNeeded(double previousPageLeft, double previousPageTop, bool trackVisualViewport)
    {
        if (!trackVisualViewport)
            return;

        if (!AreClose(previousPageLeft, GetVisualViewportPageOffset(vertical: false)) ||
            !AreClose(previousPageTop, GetVisualViewportPageOffset(vertical: true)))
        {
            DispatchVisualViewportScrollEvent();
        }
    }

    private bool CanProgrammaticallyScroll(DomElement element, bool vertical)
    {
        if (IsDocumentElement(element) ||
            IsViewportBodyElement(element, GetOwningDocumentElement(element)))
        {
            return CanProgrammaticallyScrollRoot(element, vertical);
        }

        if (IsSelectListBox(element))
            return CanProgrammaticallyScrollSelectListBox(element, vertical);

        var props = GetComputedProps(element);
        var axisValue = GetOverflowAxisValue(props, vertical);

        // Only this axis's value goes in, which keeps the per-axis answer the bridge's own copy of this
        // test gave: GetOverflowAxisValue has already let the longhand win over the shorthand.
        //
        // That answer has a gap. CSS Overflow 3 §3.1 computes `visible` to `auto` (and `clip` to
        // `hidden`) when the other axis scrolls, so `overflow-x: hidden` alone makes the box a scroll
        // container on both axes, and Chromium takes scrollTop writes on it. Neither CssOverflow
        // overload models that adjustment and GetOverflowAxisValue answers null for the unset axis, so
        // such a box refuses scrollTop here — while FindNearestScrollParent, which uses the
        // property-map overload (an OR of all three values), already counts it as a scroll container.
        // Switching to that overload would match the specification except for the clip combinations,
        // but it changes what a page observes and needs its own test against a real layout view.
        return CssOverflow.ClipsOverflow(axisValue, null, null);
    }

    private bool CanProgrammaticallyScrollRoot(DomElement rootElement, bool vertical)
    {
        var documentElement = GetOwningDocumentElement(rootElement);
        var htmlOverflow = GetOverflowAxisValue(GetComputedProps(documentElement), vertical);
        var body = DomBridgeUtils.FindBodyElement(documentElement);
        var bodyOverflow = body != null ? GetOverflowAxisValue(GetComputedProps(body), vertical) : null;

        if (DisablesRootScrolling(htmlOverflow) || DisablesRootScrolling(bodyOverflow))
            return false;

        return true;
    }

    private (double MinLeft, double MaxLeft, double MinTop, double MaxTop) GetScrollBounds(DomElement element)
    {
        var isRoot = IsViewportElementForMetrics(element);
        var maxLeft = Math.Max(0, GetScrollWidthForDomElement(element, isRoot) - GetClientWidthForDomElement(element, isRoot));
        var maxTop = Math.Max(0, GetScrollHeightForDomElement(element, isRoot) - GetClientHeightForDomElement(element, isRoot));

        // Shared with the overflow-extent measurement, which has to agree with this about which way
        // each axis grows — see AxisUsesReversedScrollDirection.
        var usesNegativeLeft = AxisUsesReversedScrollDirection(element, vertical: false);
        var usesNegativeTop = AxisUsesReversedScrollDirection(element, vertical: true);

        var minLeft = usesNegativeLeft ? -maxLeft : 0;
        var boundedMaxLeft = usesNegativeLeft ? 0 : maxLeft;
        var minTop = usesNegativeTop ? -maxTop : 0;
        var boundedMaxTop = usesNegativeTop ? 0 : maxTop;
        return (minLeft, boundedMaxLeft, minTop, boundedMaxTop);
    }

    private bool CanProgrammaticallyScrollSelectListBox(DomElement element, bool vertical)
    {
        var props = GetComputedProps(element);
        bool verticalWritingMode = CssWritingMode.IsVertical(props.GetValueOrDefault("writing-mode"));
        bool blockAxisIsVertical = !verticalWritingMode;
        if (vertical != blockAxisIsVertical)
            return false;

        double clientExtent = vertical ? GetClientHeightForDomElement(element, isRoot: false) : GetClientWidthForDomElement(element, isRoot: false);
        double scrollExtent = vertical ? GetScrollHeightForDomElement(element, isRoot: false) : GetScrollWidthForDomElement(element, isRoot: false);
        return scrollExtent > clientExtent + 0.5;
    }

    private bool TryGetSelectListBoxScrollExtent(DomElement element, bool verticalAxis, out double extent)
    {
        if (!IsSelectListBox(element))
        {
            extent = 0;
            return false;
        }

        var props = GetComputedProps(element);
        bool verticalWritingMode = CssWritingMode.IsVertical(props.GetValueOrDefault("writing-mode"));
        int optionCount = Math.Max(1, CountSelectOptions(element));
        double rowExtent = Math.Max(16, ResolveLineHeightForElement(element));
        double clientInlineExtent = verticalWritingMode
            ? GetClientHeightForDomElement(element, isRoot: false)
            : GetClientWidthForDomElement(element, isRoot: false);
        double clientBlockExtent = verticalWritingMode
            ? GetClientWidthForDomElement(element, isRoot: false)
            : GetClientHeightForDomElement(element, isRoot: false);
        // The rows' extent is a product, so two numbers this component can represent need not have
        // a representable product: a `line-height: 1e307` resolves to a row extent a double holds
        // and six of them do not. A scrolling area that cannot be measured is one that does not
        // reach past the box — the client extent, which is already the answer a list box with room
        // for all its rows gives, rather than a number invented by clamping.
        double rowsBlockExtent = optionCount * rowExtent;
        double totalBlockExtent = double.IsFinite(rowsBlockExtent)
            ? Math.Max(clientBlockExtent, rowsBlockExtent)
            : clientBlockExtent;

        extent = verticalAxis
            ? (verticalWritingMode ? clientInlineExtent : totalBlockExtent)
            : (verticalWritingMode ? totalBlockExtent : clientInlineExtent);
        return true;
    }

}

/// <summary>
/// CSS Scroll Snap (Level 1) snap-position resolution for programmatic scrolls.
///
/// <para>A scroll container whose <c>scroll-snap-type</c> names an axis with the
/// <c>mandatory</c> strictness must come to rest on a snap position on that axis after
/// <em>any</em> scroll — including <c>scrollIntoView</c>, <c>scrollTo</c>/<c>scroll</c>,
/// <c>scrollBy</c>, and a plain <c>scrollTop</c>/<c>scrollLeft</c> assignment (CSS Scroll
/// Snap 1 §2, §6.1). Snapping is therefore applied at the single choke point where a
/// requested offset becomes the stored one (<c>ResolveElementScrollOffsets</c>), rather than
/// in any individual scrolling entry point.</para>
///
/// <para>Only <c>mandatory</c> snapping is implemented. <c>proximity</c> leaves the choice of
/// whether a candidate is "close enough" up to the UA, so declining to snap is a conforming
/// outcome and matches what the engine did before.</para>
///
/// <para>Root propagation falls out of the existing container model: for the viewport the
/// scroll container element is <c>&lt;html&gt;</c>, so <c>scroll-snap-type</c> and
/// <c>scroll-padding</c> are read from the root element and <c>&lt;body&gt;</c> is never
/// consulted — which is exactly the distinction css-scroll-snap/scroll-snap-root-002 asserts
/// (issue #1439).</para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Returns the snap position closest to <paramref name="proposedOffset"/> on
    /// <paramref name="vertical"/>'s axis, or <paramref name="proposedOffset"/> unchanged when
    /// <paramref name="scrollContainer"/> is not a mandatory snap container on that axis or
    /// has no snap areas.
    /// </summary>
    /// <param name="proposedOffset">The already-clamped offset the scroll would land on.</param>
    private double ResolveScrollSnapPosition(DomElement scrollContainer, bool vertical, double proposedOffset)
    {
        if (!double.IsFinite(proposedOffset) || !HasMandatoryScrollSnapOnAxis(scrollContainer, vertical))
            return proposedOffset;

        var (minLeft, maxLeft, minTop, maxTop) = GetScrollBounds(scrollContainer);
        var min = vertical ? minTop : minLeft;
        var max = vertical ? maxTop : maxLeft;

        var best = proposedOffset;
        var bestDistance = double.PositiveInfinity;

        foreach (var descendant in EnumerateRenderedDescendants(scrollContainer))
        {
            var alignment = GetScrollSnapAlignmentForAxis(descendant, scrollContainer, vertical);
            if (alignment == null)
                continue;

            // A box only snaps in its own nearest scroll container, so a snap area inside a
            // nested scroller must not pull this container's offset around.
            if (!ReferenceEquals(FindScrollContainer(descendant), scrollContainer))
                continue;

            var candidate = ResolveScrollIntoViewOffset(descendant, scrollContainer, vertical, alignment);
            if (!double.IsFinite(candidate))
                continue;

            candidate = Math.Clamp(candidate, min, max);

            var distance = Math.Abs(candidate - proposedOffset);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Whether <paramref name="scrollContainer"/> declares <c>scroll-snap-type</c> with
    /// <c>mandatory</c> strictness covering <paramref name="vertical"/>'s axis. The axis
    /// keyword may be physical (<c>x</c>/<c>y</c>), logical (<c>block</c>/<c>inline</c>, which
    /// resolve through the container's <c>writing-mode</c>), or <c>both</c>.
    /// </summary>
    private bool HasMandatoryScrollSnapOnAxis(DomElement scrollContainer, bool vertical)
    {
        var props = GetComputedProps(scrollContainer);
        var declared = props.GetValueOrDefault("scroll-snap-type");
        if (string.IsNullOrWhiteSpace(declared))
            return false;

        var tokens = declared.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || tokens[0] == "none")
            return false;

        // Strictness is optional and defaults to `proximity`, which this implementation
        // deliberately leaves un-snapped (see the type remarks).
        if (!tokens.Contains("mandatory"))
            return false;

        var blockAxisIsVertical = !CssWritingMode.IsVertical(props.GetValueOrDefault("writing-mode"));
        return tokens[0] switch
        {
            "both" => true,
            "x" => !vertical,
            "y" => vertical,
            "block" => vertical == blockAxisIsVertical,
            "inline" => vertical != blockAxisIsVertical,
            _ => false,
        };
    }

    /// <summary>
    /// The physical <c>start</c>/<c>end</c>/<c>center</c> alignment <paramref name="element"/>
    /// requests on <paramref name="vertical"/>'s axis, or null when it declares no
    /// <c>scroll-snap-align</c> or opts that axis out with <c>none</c>.
    ///
    /// <para><c>scroll-snap-align</c> takes one or two logical keywords in block-then-inline
    /// order, so they are mapped to physical axes through the same writing-mode/direction
    /// resolution <c>scrollIntoView</c>'s <c>block</c>/<c>inline</c> options use.</para>
    /// </summary>
    private string? GetScrollSnapAlignmentForAxis(DomElement element, DomElement scrollContainer, bool vertical)
    {
        var declared = GetComputedProps(element).GetValueOrDefault("scroll-snap-align");
        if (string.IsNullOrWhiteSpace(declared))
            return null;

        var tokens = declared.Trim().ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return null;

        var block = tokens[0];
        var inline = tokens.Length > 1 ? tokens[1] : tokens[0];
        if (!IsScrollSnapAlignmentKeyword(block) || !IsScrollSnapAlignmentKeyword(inline))
            return null;

        // Map through the container's writing mode by asking for the physical alignments of
        // this logical pair, then keep only the axis being snapped. "none" is checked on the
        // logical value first, because the physical resolution normalises it away.
        var blockAxisIsVertical = !CssWritingMode.IsVertical(
            GetComputedProps(scrollContainer).GetValueOrDefault("writing-mode"));
        var logicalForAxis = vertical == blockAxisIsVertical ? block : inline;
        if (logicalForAxis == "none")
            return null;

        var (horizontal, verticalAlignment) =
            ResolvePhysicalScrollIntoViewAlignments(scrollContainer, block, inline);
        return vertical ? verticalAlignment : horizontal;
    }
}
