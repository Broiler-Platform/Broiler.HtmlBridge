using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;
using Broiler.CSS;
using System.Globalization;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>LayoutMetrics.cs</c> (Phase 3 ratchet, 2026-07-17) to keep it
/// under the 750-line guard: the scrolling behaviour surface — <c>scrollIntoView</c> option/argument
/// parsing, element scroll-offset get/set with behaviour, scroll-event dispatch, visual-viewport
/// scroll/scale, and programmatic-scrollability / overflow analysis. Pure partial-class relocation —
/// no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element JS-visible scroll offset was
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

    private (string Block, string Inline, string? Behavior) GetScrollIntoViewOptions(in Arguments args)
    {
        const string defaultBlock = "start";
        const string defaultInline = "nearest";

        if (args.Length == 0)
            return (defaultBlock, "start-if-needed", null);

        var first = args[0];
        if (first is JSObject options)
        {
            return (
                NormalizeScrollIntoViewAlignment(GetOptionalStringOption(options, "block"), defaultBlock),
                NormalizeScrollIntoViewAlignment(GetOptionalStringOption(options, "inline"), defaultInline),
                GetOptionalScrollBehavior(options));
        }

        if (first.IsBoolean)
        {
            return first.BooleanValue
                ? (defaultBlock, defaultInline, null)
                : ("end", defaultInline, null);
        }

        return (defaultBlock, defaultInline, null);
    }

    private (double? Left, double? Top, string? Behavior) GetScrollArguments(in Arguments args)
    {
        if (args.Length == 0)
            return (null, null, null);

        if (args[0] is JSObject options)
        {
            return (
                GetOptionalScrollCoordinate(options, "left"),
                GetOptionalScrollCoordinate(options, "top"),
                GetOptionalScrollBehavior(options));
        }

        return (args.Length > 0 ? args[0].DoubleValue : null, args.Length > 1 ? args[1].DoubleValue : null, null);
    }

    private static double? GetOptionalScrollCoordinate(JSObject options, string propertyName)
    {
        var value = options[(KeyString)propertyName];
        return value == null || value.IsUndefined || value.IsNull ? null : value.DoubleValue;
    }

    private static string? GetOptionalScrollBehavior(JSObject options)
    {
        var value = options[(KeyString)"behavior"];
        if (value == null || value.IsUndefined || value.IsNull)
            return null;

        var behavior = value.ToString();
        return string.IsNullOrWhiteSpace(behavior) ? null : behavior;
    }

    private static string? GetOptionalStringOption(JSObject options, string propertyName)
    {
        var value = options[(KeyString)propertyName];
        if (value == null || value.IsUndefined || value.IsNull)
            return null;

        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string NormalizeScrollIntoViewAlignment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "start" or "center" or "end" or "nearest" or "start-if-needed" or "end-if-needed"
            ? normalized
            : fallback;
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

    private static string ResolvePhysicalAxisAlignment(string? alignment, bool startMapsToPhysicalStart)
    {
        var normalized = NormalizeScrollIntoViewAlignment(alignment, "start");
        if (normalized is "center" or "nearest" || startMapsToPhysicalStart)
            return normalized;

        return normalized switch
        {
            "start" => "end",
            "end" => "start",
            "start-if-needed" => "end-if-needed",
            "end-if-needed" => "start-if-needed",
            _ => normalized
        };
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
        // This runs even under `clamp: false` (window.scrollTo/scrollBy and the element
        // scroll/scrollTo/scrollBy bindings), which is deliberate: snapping is a property of
        // the container, not of the API used to reach it, and a snap position is by definition
        // inside the scrollable range. Callers that opt out of clamping still get no clamping
        // on an ordinary scroll container — only on one that asked to snap.
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
        var evt = new JSObject();
        evt.FastAddValue("type", new JSString(eventType), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        DispatchEventOnElement(element, evt);
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

    private static string NormalizeScrollBehavior(string? behavior)
    {
        if (string.IsNullOrWhiteSpace(behavior))
            return "auto";

        var normalized = behavior.Trim().ToLowerInvariant();
        return normalized is "instant" or "smooth" ? normalized : "auto";
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
        if (_visualViewportJSObject == null || _eventTargets.VisualViewportScrollListeners.Count == 0)
            return;

        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("scroll"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("target", _visualViewportJSObject, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("currentTarget", _visualViewportJSObject, JSPropertyAttributes.EnumerableConfigurableValue);

        foreach (var listener in _eventTargets.VisualViewportScrollListeners.ToList())
        {
            try
            {
                listener.InvokeFunction(new Arguments(listener, evt));
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.visualViewport", $"Visual viewport listener error: {ex.Message}", ex);
            }
        }
    }

    private static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.0001;

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

        return EnablesScrollingBox(axisValue);
    }

    private bool CanProgrammaticallyScrollRoot(DomElement rootElement, bool vertical)
    {
        var documentElement = GetOwningDocumentElement(rootElement);
        var htmlOverflow = GetOverflowAxisValue(GetComputedProps(documentElement), vertical);
        var body = FindBodyElement(documentElement);
        var bodyOverflow = body != null ? GetOverflowAxisValue(GetComputedProps(body), vertical) : null;

        if (DisablesRootScrolling(htmlOverflow) || DisablesRootScrolling(bodyOverflow))
            return false;

        return true;
    }

    private static string? GetOverflowAxisValue(Dictionary<string, string> props, bool vertical)
    {
        var axisValue = props.GetValueOrDefault(vertical ? "overflow-y" : "overflow-x");
        if (string.IsNullOrWhiteSpace(axisValue))
            axisValue = props.GetValueOrDefault("overflow");
        return axisValue;
    }

    /// <summary>
    /// Whether a root-propagated <c>overflow</c> value makes the viewport non-scrollable.
    /// Only <c>clip</c> does: it suppresses the scroll container entirely (CSS Overflow 3
    /// §3.3), so the scrollport has no scroll offset to set.
    /// <para><c>overflow: hidden</c> does <em>not</em> belong here. It still establishes a
    /// scroll container — it only removes the <em>user-interaction</em> affordance, while
    /// programmatic scrolling (<c>scrollTop</c>/<c>scrollLeft</c>, <c>scrollTo</c>,
    /// <c>scrollIntoView</c>) keeps working. Treating it as non-scrollable pinned every such
    /// document at offset 0, which silently broke the very common WPT reftest idiom
    /// <c>:root { overflow: hidden; /* hide scrollbars for reftest analysis */ }</c> on any
    /// test that also scrolls (issue #1439: css-scroll-snap/scroll-snap-root-001 and -002
    /// both rendered their unscrolled red FAIL block).</para>
    /// </summary>
    private static bool DisablesRootScrolling(string? overflowValue)
    {
        if (string.IsNullOrWhiteSpace(overflowValue))
            return false;

        return overflowValue.Trim().ToLowerInvariant().Contains("clip");
    }

    private static bool EnablesScrollingBox(string? overflowValue)
    {
        if (string.IsNullOrWhiteSpace(overflowValue))
            return false;

        var value = overflowValue.Trim().ToLowerInvariant();
        return value.Contains("hidden") || value.Contains("scroll") || value.Contains("auto") || value.Contains("clip");
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
        double totalBlockExtent = Math.Max(clientBlockExtent, optionCount * rowExtent);

        extent = verticalAxis
            ? (verticalWritingMode ? clientInlineExtent : totalBlockExtent)
            : (verticalWritingMode ? totalBlockExtent : clientInlineExtent);
        return true;
    }

    private static int CountSelectOptions(DomElement element)
    {
        int count = 0;
        foreach (var child in ChildElements(element).Where(c => !IsText(c)))
        {
            if (string.Equals(child.TagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }

            count += CountSelectOptions(child);
        }

        return count;
    }

}
