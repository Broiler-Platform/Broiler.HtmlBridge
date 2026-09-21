using Broiler.CSS;
using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The rendering half of CSS View Transitions: materialises the <c>::view-transition</c> pseudo tree
/// and its old/new snapshots, and collects the captures and author pseudo-element rules it is built
/// from. The entry point and the old-state capture are in <c>ViewTransition.cs</c>.
/// </summary>
public sealed partial class DomBridge
{
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

    /// <summary>Creates a bridge <c>&lt;div&gt;</c> whose inline style is <paramref name="baseStyle"/>
    /// with the pseudo-element's author declarations layered on top (author values win), over a reset
    /// (<see cref="DomBridgeUtils.PseudoBoxAuthorReset"/>) that keeps page-level selectors out of the pseudo tree.
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
        parent.AppendChild(child);
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
}
