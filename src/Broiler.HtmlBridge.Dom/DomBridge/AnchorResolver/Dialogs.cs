using System.Runtime.CompilerServices;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element dialog / popover top-layer state
    // (modal flag, top-layer order, popover-open flag) was the Dialog slot of the process-static
    // ElementRuntimeState table; it is now a per-bridge instance table, owned by the session's bridge.
    // Still element-keyed, so it GCs with the element and the cloneNode copy (see CloneDomElement) is
    // preserved. The former static TopLayerOrderOf / IsAnchorAccessible / FindModalDialogs /
    // FindOpenPopovers helpers became instance methods (all their callers were already on the bridge
    // instance), so no cross-class host threading was needed.
    private readonly ConditionalWeakTable<DomElement, DialogRuntimeState> _dialogRuntimeStates = [];

    private DialogRuntimeState DialogStateFor(DomElement element) =>
        _dialogRuntimeStates.GetValue(element, static _ => new DialogRuntimeState());

    // -----------------------------------------------------------------
    // Dialog UA default positioning
    // -----------------------------------------------------------------

    // CSS Position 4 §top-layer: benign marker the renderer's native top-layer paint pass keys
    // on (Broiler.Layout FragmentTreeBuilder → Fragment.TopLayerOrder → PaintWalker.PaintTopLayer,
    // patch 0010 — applied and pinned). The attribute value is the element's top-layer order; a
    // later-added element (higher order) paints over an earlier one. Stamping it lets the native
    // pass paint modal dialogs, open popovers, and ::backdrops above every ordinary stacking
    // context — the correct top-layer behaviour, superseding the approximate very-large-z-index
    // emulation (now written only on the retired NativeTopLayer-off rollback path).
    private const string TopLayerOrderAttr = "data-broiler-top-layer";

    // Native ::backdrop marker: the resolved backdrop background (UA modal/popover scrim default
    // folded with any author `background`) the renderer materialises into a native ::backdrop box
    // (Broiler.HTML DomParser, patch 0011 — applied and pinned). Stamped in NativeBackdrop mode;
    // the baked path inserts a styled <div> instead. The <div> path is still retained (not yet
    // deletable) because it carries author `::backdrop` position-try-fallbacks, which the native
    // path does not yet reproduce (see InsertDialogBackdrops).
    private const string BackdropBgAttr = "data-broiler-backdrop";

    private void StampTopLayerOrder(DomElement el, int order) =>
        SetAttr(el, TopLayerOrderAttr, order.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private int TopLayerOrderOf(DomElement el) =>
        DialogStateFor(el).TopLayerOrder.TryGet(out var o) && o is int oi ? oi : 0;

    /// <summary>
    /// Applies the UA default <c>position: fixed</c> to modal dialog elements
    /// that don't already have an explicit position, matching browser behaviour
    /// where top-layer elements are always treated as fixed-positioned.
    /// Must be called <em>before</em> anchor resolution so that anchor()
    /// function values are resolved with the correct positioning context.
    /// <para><paramref name="elements"/> is the document to apply it to: the main document's
    /// <see cref="Elements"/>, or a nested browsing context's — a frame is severed from the main
    /// tree, so it has to be walked separately (see <c>Dialogs.SubDocuments.cs</c>).</para>
    /// </summary>
    private void ApplyDialogUAPositioning(IEnumerable<DomElement> elements)
    {
        foreach (var el in elements)
        {
            // Fullscreen §user-agent level style sheet defaults: the fullscreen element is a
            // top-layer box laid out over the whole viewport. Applied before the dialog branch
            // because an element taken fullscreen is sized by *this* rule whatever its tag is —
            // including a <dialog>, whose modal centring it replaces.
            if (DialogStateFor(el).Fullscreen.TryGet(out var fs) && fs is true)
            {
                if (NativeTopLayer)
                    StampTopLayerOrder(el, TopLayerOrderOf(el));

                ApplyFullscreenUAGeometry(el);
                continue;
            }

            if (!string.Equals(el.TagName, "dialog", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!HasAttr(el, "open"))
                continue;
            if (!(DialogStateFor(el).Modal.TryGet(out var m) && m is true))
                continue;

            // Mark the open modal dialog as a top-layer box (native paint reads this; inert on
            // the baked path). Independent of the position-fixed default applied below.
            if (NativeTopLayer)
                StampTopLayerOrder(el, TopLayerOrderOf(el));

            // Check if position is already set (inline or CSS).
            // position:absolute dialogs keep their author position so that
            // scroll simulation can shift them, matching Chromium behaviour.
            var props = GetComputedProps(el);
            if (props.TryGetValue("position", out var pos) &&
                (pos == "fixed" || pos == "absolute"))
                continue;

            // Set position:fixed as UA default for modal dialogs that have
            // no explicit position, matching Chromium's top-layer behaviour.
            BakedInlineStyle(el)["position"] = "fixed";

            // HTML UA `dialog:modal { inset:0; margin:auto }` centring. With the box
            // fixed-positioned, both insets 0 and auto margins, the layout engine's
            // §10.3.7 / §10.6.4 auto-margin resolution centres a definite-size box in the
            // viewport (Broiler.Layout CssBox.ResolveOverconstrainedAutoMargins).
            ApplyModalCenteringDefaults(el);
        }
    }

    /// <summary>
    /// The Fullscreen UA style sheet's geometry for the fullscreen element:
    /// <c>position: fixed; inset: 0; margin: 0; width: 100%; height: 100%;</c> — a box covering the
    /// viewport, above everything, with no box-model inset of its own.
    /// </summary>
    /// <remarks>
    /// Written as baked inline style rather than as a cascade rule for the same reason the modal
    /// dialog defaults are: the renderer has no notion of the fullscreen flag, so the state has to
    /// reach it as geometry. Author declarations are not consulted — unlike <c>dialog:modal</c>
    /// centring, this UA rule is <c>!important</c> in the Fullscreen spec precisely so a page
    /// cannot leave the fullscreen element the wrong size.
    /// </remarks>
    private void ApplyFullscreenUAGeometry(DomElement el)
    {
        var style = BakedInlineStyle(el);
        style["position"] = "fixed";
        style["top"] = "0";
        style["left"] = "0";
        style["right"] = "0";
        style["bottom"] = "0";
        style["margin"] = "0";
        style["width"] = "100%";
        style["height"] = "100%";
        style["box-sizing"] = "border-box";
    }

    // The HTML UA `dialog:modal` inset/margin properties (checked both as their shorthands and
    // as longhands): any author declaration on these means the page positions the modal itself,
    // so the UA centring default must not fight it.
    private static readonly string[] ModalPositioningProps =
    [
        "inset", "inset-block", "inset-inline", "left", "right", "top", "bottom",
        "margin", "margin-block", "margin-inline",
        "margin-left", "margin-right", "margin-top", "margin-bottom",
    ];

    /// <summary>
    /// Applies the HTML user-agent <c>dialog:modal { inset:0; width:fit-content; height:fit-content;
    /// margin:auto }</c> centring default to a modal <c>&lt;dialog&gt;</c> the bridge just gave the UA
    /// <c>position:fixed</c>. The layout engine centres a box on an axis when it has a resolvable used
    /// size there and both opposing insets + auto margins (CSS2.1 §10.3.7/§10.6.4): the inline axis and
    /// definite heights are centred in-line during layout, and a content / intrinsic-keyword block size is
    /// centred by the engine's block-axis root post-pass (<c>CssBox.CenterOutOfFlowBlockAxis</c>) once the
    /// final height is known. Per axis: a modal with no author size gets the UA <c>fit-content</c> so it
    /// shrink-wraps to content and centres; an explicit or intrinsic author size centres as-is; an
    /// explicit author <c>auto</c> is left alone (with both insets it fills the viewport — no free space).
    /// The default is suppressed entirely when the author declares any inset/margin — the page owns the
    /// positioning then.
    /// </summary>
    private void ApplyModalCenteringDefaults(DomElement el)
    {
        var specified = BuildSpecifiedStyleMap(el);

        foreach (var prop in ModalPositioningProps)
            if (specified.ContainsKey(prop))
                return;

        if (ResolveModalAxisCentres(el, specified, "width"))
        {
            BakedInlineStyle(el)["left"] = "0";
            BakedInlineStyle(el)["right"] = "0";
            BakedInlineStyle(el)["margin-left"] = "auto";
            BakedInlineStyle(el)["margin-right"] = "auto";
        }

        if (ResolveModalAxisCentres(el, specified, "height"))
        {
            BakedInlineStyle(el)["top"] = "0";
            BakedInlineStyle(el)["bottom"] = "0";
            BakedInlineStyle(el)["margin-top"] = "auto";
            BakedInlineStyle(el)["margin-bottom"] = "auto";
        }
    }

    // Decides whether the modal's axis can be centred, applying the UA <c>fit-content</c> default when the
    // author gave no size so the box shrink-wraps to content. Returns false only when the author explicitly
    // set the axis to <c>auto</c> — with both insets that fills the containing block, leaving no free space
    // for the auto margins to distribute.
    private bool ResolveModalAxisCentres(DomElement el, Dictionary<string, string> specified, string sizeProperty)
    {
        if (!specified.TryGetValue(sizeProperty, out var value) || string.IsNullOrWhiteSpace(value))
        {
            BakedInlineStyle(el)[sizeProperty] = "fit-content"; // UA dialog:modal shrink-to-fit default
            return true;
        }

        return !string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Applies UA popover positioning and top-layer elevation to open popovers (HTML §popover).
    /// <para>
    /// The UA sheet's <c>[popover] { position: fixed; inset: 0 }</c> is unconditional — it is not
    /// gated on top-layer membership — so every open popover with no explicit position becomes a
    /// fixed box anchored at the viewport origin. Top-layer <em>elevation</em> is the separate,
    /// conditional half: a popover held out by a running <c>overlay</c> entry transition is still an
    /// out-of-flow fixed box, it just does not paint in the top layer yet. Later-shown popovers keep
    /// their source order, so they paint over earlier ones — matching the top-layer stacking these
    /// overlay tests probe.
    /// </para>
    /// </summary>
    // Base z-index for the synthetic top layer. The real top layer sits above
    // every painted stacking context (CSS Position §top-layer); Broiler has no
    // dedicated top-layer paint pass, so approximate it with a very large
    // z-index offset by each element's top-layer order, keeping open popovers
    // above ordinary positioned content and correctly ordered amongst themselves
    // (a later-shown popover paints over an earlier one). Kept below int.MaxValue
    // so the counter has headroom.
    private const int TopLayerZIndexBase = 2_000_000_000;

    private void ApplyPopoverUAPositioning(IEnumerable<DomElement> elements)
    {
        foreach (var el in elements)
        {
            if (!(DialogStateFor(el).PopoverOpen.TryGet(out var open) && open is true))
                continue;

            var props = GetComputedProps(el);
            bool alreadyPositioned = props.TryGetValue("position", out var pos) &&
                (pos == "fixed" || pos == "absolute");

            if (!alreadyPositioned)
            {
                BakedInlineStyle(el)["position"] = "fixed";
                if (!BakedInlineStyle(el).ContainsKey("top") && !props.ContainsKey("top"))
                    BakedInlineStyle(el)["top"] = "0";
                if (!BakedInlineStyle(el).ContainsKey("left") && !props.ContainsKey("left"))
                    BakedInlineStyle(el)["left"] = "0";
            }

            // An open popover whose `overlay` is still transitioning in at screenshot time is out of
            // the top layer: it keeps the UA fixed positioning above but gets no elevation, so it
            // paints in ordinary stacking order beneath the top layer (WPT css/css-position/overlay/
            // overlay-transition-in-rendering, -backdrop-entry). Laying it out in normal flow instead
            // would also displace the static position of following out-of-flow boxes. A page that
            // gates its screenshot on `transitionend` is asking to be rendered after the transition,
            // so it is elevated (-finished) — see PopoverHeldOutOfTopLayerForPaint.
            if (PopoverHeldOutOfTopLayerForPaint(el))
                continue;

            // Elevate into the top layer, ordered by show order, so the popover paints above
            // non-top-layer content (e.g. a plain position:fixed sibling) and later popovers paint over
            // earlier ones. Native path: the renderer's top-layer paint pass (patch 0010, pinned) keys on
            // the `data-broiler-top-layer` marker and lifts the box out of normal stacking. The very-large
            // z-index is the older approximate emulation, now needed only on the retired baked
            // (NativeTopLayer-off) rollback path — so the two are mutually exclusive rather than both.
            int order = TopLayerOrderOf(el);
            if (NativeTopLayer)
                StampTopLayerOrder(el, order);
            else
                BakedInlineStyle(el)["z-index"] = (TopLayerZIndexBase + order).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    // -----------------------------------------------------------------
    // Dialog backdrop insertion
    // -----------------------------------------------------------------

    /// <summary>
    /// The UA <c>::backdrop</c> background for a top-layer element, before any author
    /// <c>::backdrop</c> declaration overrides it: opaque black behind a fullscreen element
    /// (Fullscreen §user-agent level style sheet defaults), the dimming scrim behind a modal
    /// dialog, and nothing behind a popover.
    /// </summary>
    private string DefaultBackdropBackground(DomElement element, bool isPopover) =>
        DialogStateFor(element).Fullscreen.TryGet(out var fs) && fs is true
            ? "black"
            : isPopover ? "transparent" : "rgb(229, 229, 229)";

    private void InsertDialogBackdrops(
        DomElement root, int vpW, int vpH,
        Dictionary<string, AnchorInfo> anchorRegistry,
        Dictionary<string, Dictionary<string, string>> positionTryRules)
    {
        var modals = new List<(DomElement dialog, DomElement parent, bool isPopover)>();
        FindModalDialogs(root, modals);
        FindOpenPopovers(root, modals);
        FindFullscreenElements(root, modals);

        foreach (var (dialog, parent, isPopover) in modals)
        {
            // Collect ::backdrop CSS properties for this element. Look for
            // selectors ending with "::backdrop" that would match it (e.g.
            // "dialog::backdrop", "[popover]::backdrop", "#target::backdrop").
            // A modal dialog's ::backdrop defaults to the UA dimming scrim; a
            // popover's ::backdrop defaults to transparent (no scrim) — either
            // is overridden by an author `background`/`background-color`.
            var backdropBg = GetBackdropBackground(dialog, DefaultBackdropBackground(dialog, isPopover));

            // Author ::backdrop position-try-fallbacks are not yet reproduced by the native
            // ::backdrop box (it overlays author geometry from the cascade but does not run the
            // position-try pass for a renderer-generated box). Route those through the synthesized
            // <div> path, which resolves the fallback below; everything else goes native.
            var backdropDecls = BackdropDeclarationsFor(dialog);

            // A ::backdrop cascaded to `display: none` is not generated at all — e.g. an @container
            // query switches it off (WPT css-conditional container-queries/dialog-backdrop-remove,
            // top-layer-dialog-backdrop). Skip both the native marker and the synthesized <div>.
            if (backdropDecls.TryGetValue("display", out var backdropDisplay) &&
                backdropDisplay.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
                continue;

            var authorPositionTry = backdropDecls.ContainsKey("position-try-fallbacks") ||
                backdropDecls.ContainsKey("position-try");

            if (NativeBackdrop && !authorPositionTry)
            {
                // Native ::backdrop: don't mutate the box tree with a synthesized <div>. Stamp the
                // resolved backdrop background on the element and let the renderer generate the
                // ::backdrop box natively (Broiler.HTML DomParser, pinned) as a top-layer box beneath
                // the dialog. The resolved background already folds the UA modal/popover scrim default
                // with any author `background` (which the renderer cannot decide without the
                // modal/popover runtime state); the renderer overlays author ::backdrop *geometry*
                // from the ::backdrop cascade.
                SetAttr(dialog, BackdropBgAttr, backdropBg);
            }
            else
            {
                // Insert a backdrop div BEFORE the dialog. Reached when native backdrop is disabled
                // (rollback) or the author declared ::backdrop position-try-fallbacks (above).
                // Use 'position: fixed' with explicit pixel viewport dimensions
                // because the Broiler renderer cannot resolve opposing insets.
                // These viewport-covering defaults materialise the ::backdrop UA
                // style (position:fixed; inset:0); any author-declared geometry
                // overlaid below overrides them, so an explicitly sized/positioned
                // backdrop is honoured instead of always filling the viewport.
                var backdropStyle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["position"] = "fixed",
                    ["top"] = "0",
                    ["left"] = "0",
                    ["width"] = $"{vpW}px",
                    ["height"] = $"{vpH}px",
                    ["background-color"] = backdropBg,
                };

                OverlayBackdropAuthorGeometry(backdropDecls, backdropStyle);
                OverlayBackdropAuthorPainting(backdropDecls, backdropStyle);

                var backdrop = CreateBridgeElement("div");
                foreach (var kv in backdropStyle)
                    BakedInlineStyle(backdrop)[kv.Key] = kv.Value;
                SetParent(backdrop, parent);

                // A ::backdrop is a top-layer box just like the element that generates it (CSS
                // Position 4 §top-layer), so mark the synthesized <div> with the *same* order as
                // its dialog/popover. Without this the div stayed an ordinary in-tree box: it
                // painted inside its DOM ancestors' stacking context, so an ancestor transform
                // moved it, an ancestor filter tinted it, and any high z-index sibling covered it
                // — the scrim vanished from under the dialog, which the top-layer pass still
                // painted correctly above everything (WPT the-dialog-element/top-layer-parent-
                // transform, -filter). Sharing the dialog's order keeps the pairing exact when
                // several dialogs are open, and the div is inserted immediately before the dialog
                // below, so the pass's document-order tiebreak still paints it beneath its own.
                if (NativeTopLayer)
                    StampTopLayerOrder(backdrop, TopLayerOrderOf(dialog));

                int idx = ChildIndexOf(parent, dialog);
                if (idx >= 0)
                    InsertChildAt(parent, idx, backdrop);

                // If the ::backdrop declares position-try-fallbacks and its base
                // geometry overflows the containing block, resolve the fallback
                // now: the main position-try pass (ResolvePositionTryFallbacks) ran
                // before this backdrop div existed, so it never saw it.
                if (backdropStyle.ContainsKey("position-try-fallbacks") ||
                    backdropStyle.ContainsKey("position-try"))
                {
                    ResolvePositionTryFallbacksTree(backdrop, anchorRegistry, positionTryRules);
                }
            }

            // The modal <dialog> box chrome (display:block, border:1px solid black, padding:1em,
            // white background) is no longer baked here — the native UA rule
            // `dialog { display: block; border: 1px solid black; padding: 1em; background-color: white }`
            // (Broiler.HTML CssDefaults, patches 0001+0002 + the box-chrome patch 0004, all applied and
            // pinned) supplies it through the real cascade, with the shorthand-vs-longhand origin fix so an
            // author reset still wins. Popovers never had UA box chrome (author CSS only). So nothing
            // dialog-specific remains in this loop past the backdrop handling above.
        }
    }
    /// <summary>
    /// Property names on a <c>::backdrop</c> rule that control the backdrop's
    /// geometry and fallback positioning. When the author declares any of
    /// these they override the viewport-covering defaults so an explicitly
    /// sized or positioned backdrop is honoured (e.g. WPT
    /// <c>position-try-backdrop.html</c>, where the backdrop is a 100×100 box
    /// moved by <c>position-try-fallbacks</c>).
    /// </summary>
    private static readonly string[] BackdropGeometryProps =
    [
        "width", "height", "left", "right", "top", "bottom",
        "position", "position-anchor", "position-try-fallbacks", "position-try",
    ];

    /// <summary>
    /// Painted (non-geometry) properties an author <c>::backdrop</c> rule can set that change
    /// how the scrim composites rather than where it sits. Only <c>background</c> /
    /// <c>background-color</c> were being carried across — folded into the resolved backdrop
    /// colour by <see cref="GetBackdropBackground"/> — so everything else in the
    /// <c>::backdrop</c> cascade was silently dropped: <c>opacity: 0.5</c> on a green scrim
    /// painted fully opaque green instead of compositing to <c>rgb(127,191,127)</c> over the
    /// white canvas (WPT <c>the-dialog-element/modal-dialog-backdrop-opacity</c>, 2.2% match).
    /// <para>Deliberately narrow: these are the properties whose effect on the synthesized
    /// <c>&lt;div&gt;</c> is the same as on a real <c>::backdrop</c> box. Inherited and
    /// layout-affecting properties stay out — the div is a bridge implementation detail, not a
    /// faithful pseudo-element, and copying those across would leak into its subtree.</para>
    /// </summary>
    private static readonly string[] BackdropPaintingProps =
    [
        "opacity", "mix-blend-mode", "border-radius", "box-shadow",
    ];

    /// <summary>
    /// Overlays author-declared <c>::backdrop</c> painting properties (see
    /// <see cref="BackdropPaintingProps"/>) onto the synthesized backdrop div's style. The
    /// background is not among them — it is already resolved into the div's
    /// <c>background-color</c>, folded with the UA modal/popover scrim default.
    /// </summary>
    private static void OverlayBackdropAuthorPainting(
        IReadOnlyDictionary<string, string> declarations,
        Dictionary<string, string> backdropStyle)
    {
        foreach (var prop in BackdropPaintingProps)
        {
            if (declarations.TryGetValue(prop, out var value) &&
                !string.IsNullOrWhiteSpace(value))
                backdropStyle[prop] = value.Trim();
        }
    }

    /// <summary>
    /// Overlays author-declared <c>::backdrop</c> geometry / fallback
    /// properties onto the synthesized backdrop div's style, replacing the
    /// viewport-covering defaults where the author was explicit.
    /// </summary>
    private static void OverlayBackdropAuthorGeometry(
        IReadOnlyDictionary<string, string> declarations,
        Dictionary<string, string> backdropStyle)
    {
        foreach (var prop in BackdropGeometryProps)
        {
            if (declarations.TryGetValue(prop, out var value) &&
                !string.IsNullOrWhiteSpace(value))
                backdropStyle[prop] = value.Trim();
        }

        // The default fills the viewport with top:0/left:0 + width/height. If
        // the author positions the backdrop from the opposite edge only, drop
        // the conflicting default inset so the box is not over-constrained
        // (the renderer cannot resolve opposing left+right / top+bottom insets).
        if (declarations.ContainsKey("right") && !declarations.ContainsKey("left"))
            backdropStyle.Remove("left");
        if (declarations.ContainsKey("bottom") && !declarations.ContainsKey("top"))
            backdropStyle.Remove("top");
    }

    /// <summary>
    /// Determines the background color for a dialog's <c>::backdrop</c>
    /// pseudo-element by checking CSS rules for <c>::backdrop</c> selectors
    /// that match the given dialog element.
    /// </summary>
    /// <summary>
    /// The declarations that decide what a <c>::backdrop</c> looks like: its author cascade, with
    /// any <c>element.animate(…, { pseudoElement: "::backdrop" })</c> values layered on top.
    /// <para>
    /// A Web Animation is not in the cascade — a pseudo-element has no node, so those values are
    /// baked aside (see <c>AnimatedPseudoStyle</c>) — and it outranks author declarations, so it
    /// goes last. Without this the animation reached the renderer only through the serialized rule
    /// <c>ApplyAnimatedPseudoSerializationOverrides</c> emits, which the *synthesized* backdrop
    /// <c>&lt;div&gt;</c> cannot see: a <c>#id::backdrop</c> selector does not match a
    /// <c>&lt;div&gt;</c>. That is the whole of WPT <c>css/css-pseudo/backdrop-animate-002</c>
    /// (issue #1538 problem 11), whose <c>opacity</c> was dropped while its
    /// <c>background-color</c> — resolved here — came through.
    /// </para>
    /// </summary>
    private IReadOnlyDictionary<string, string> BackdropDeclarationsFor(DomElement dialog)
    {
        var declarations = GetSyncedScopedEngine(dialog)
            .GetCascadedDeclaredValues(dialog, "::backdrop");

        if (AnimatedPseudoStyle(dialog, "::backdrop") is not { } animated)
            return declarations;

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (property, value) in declarations)
            merged[property] = value;
        foreach (var (property, value) in animated)
            merged[property] = value;
        return merged;
    }

    private string GetBackdropBackground(DomElement dialog, string defaultBg = "rgb(229, 229, 229)")
    {
        // Default modal-dialog backdrop color: pre-composited rgba(0,0,0,0.1) over
        // white (255*(1-0.1) + 0*0.1 = 229.5 ≈ 229). Callers pass "transparent"
        // for popovers, whose ::backdrop has no UA scrim.

        var declarations = BackdropDeclarationsFor(dialog);

        if (declarations.TryGetValue("background", out var bg))
        {
            if (string.Equals(bg.Trim(), "transparent", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(bg.Trim(), "none", StringComparison.OrdinalIgnoreCase))
                return "transparent";
            return bg;
        }

        if (declarations.TryGetValue("background-color", out var bgColor))
        {
            if (string.Equals(bgColor.Trim(), "transparent", StringComparison.OrdinalIgnoreCase))
                return "transparent";
            return bgColor;
        }

        return defaultBg;
    }
    /// <summary>
    /// Checks whether an anchor element is accessible from a target element,
    /// according to CSS Anchor Positioning top-layer visibility rules.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Non-top-layer elements cannot anchor to top-layer elements.</item>
    /// <item>A top-layer element can only anchor to top-layer elements that
    /// were added to the top layer <em>before</em> it (lower order).</item>
    /// <item>Non-top-layer anchors are always accessible.</item>
    /// </list>
    /// </remarks>
    private bool IsAnchorAccessible(DomElement? anchorElement, DomElement targetElement)
    {
        if (anchorElement == null) return true;

        bool anchorIsTopLayer =
            DialogStateFor(anchorElement).Modal.TryGet(out var am) && am is true;
        bool targetIsTopLayer =
            DialogStateFor(targetElement).Modal.TryGet(out var tm) && tm is true;

        if (!anchorIsTopLayer)
            return true; // Non-top-layer anchors are accessible from anywhere.

        if (!targetIsTopLayer)
            return false; // Non-top-layer target cannot see top-layer anchor.

        // Both are in top layer — anchor must have been added BEFORE the target.
        int anchorOrder = DialogStateFor(anchorElement).TopLayerOrder.TryGet(out var ao) && ao is int aoi ? aoi : 0;
        int targetOrder = DialogStateFor(targetElement).TopLayerOrder.TryGet(out var to) && to is int toi ? toi : 0;

        return anchorOrder < targetOrder;
    }
    private void FindModalDialogs(DomElement element, List<(DomElement, DomElement, bool)> results)
    {
        if (string.Equals(element.TagName, "dialog", StringComparison.OrdinalIgnoreCase) &&
            HasAttr(element, "open") &&
            DialogStateFor(element).Modal.TryGet(out var isModal) &&
            isModal is bool modal && modal &&
            ParentEl(element) != null)
        {
            results.Add((element, ParentEl(element), false));
        }

        // Snapshot before recursing: the live child list can be mutated mid-walk
        // (concurrent/lazy DOM edit) and throw, aborting the walk. SnapshotChildren
        // tolerates that — same idiom as the other anchor-resolver tree walks.
        foreach (var child in SnapshotChildren(element))
            FindModalDialogs(child, results);
    }

    /// <summary>
    /// Fullscreen §<c>fullscreen element</c>: the element whose <c>requestFullscreen()</c> ran and
    /// has not been exited, or <c>null</c>. The spec keeps a stack; this returns its top, which is
    /// the element with the highest top-layer order among those still flagged.
    /// </summary>
    internal DomElement? FindFullscreenElement()
    {
        DomElement? best = null;
        int bestOrder = int.MinValue;

        foreach (var el in Elements)
        {
            if (!(DialogStateFor(el).Fullscreen.TryGet(out var fs) && fs is true))
                continue;

            int order = TopLayerOrderOf(el);
            if (best is null || order >= bestOrder)
            {
                best = el;
                bestOrder = order;
            }
        }

        return best;
    }

    // Fullscreen §top layer: a fullscreen element is in the top layer and generates a ::backdrop,
    // the same as a modal dialog. Collected alongside them so one pass materialises all three.
    private void FindFullscreenElements(DomElement element, List<(DomElement, DomElement, bool)> results)
    {
        if (ParentEl(element) != null &&
            DialogStateFor(element).Fullscreen.TryGet(out var fs) &&
            fs is true &&
            // A modal dialog taken fullscreen is one top-layer box with one ::backdrop, not two.
            !results.Exists(r => ReferenceEquals(r.Item1, element)))
        {
            results.Add((element, ParentEl(element), false));
        }

        foreach (var child in SnapshotChildren(element))
            FindFullscreenElements(child, results);
    }

    // Popover API (HTML §popover): an element whose showPopover() ran (and whose
    // hidePopover() did not tear it down — see PopoverKeepsOverlayOnHide) is in
    // the top layer and generates a ::backdrop, just like a modal dialog.
    private void FindOpenPopovers(DomElement element, List<(DomElement, DomElement, bool)> results)
    {
        if (ParentEl(element) != null &&
            DialogStateFor(element).PopoverOpen.TryGet(out var open) &&
            open is true &&
            // A popover held out of the top layer while `overlay` transitions in generates no
            // ::backdrop (the backdrop belongs to the top layer). Judged at screenshot time, so a
            // page waiting on `transitionend` gets the backdrop it would have by then.
            !PopoverHeldOutOfTopLayerForPaint(element))
        {
            results.Add((element, ParentEl(element), true));
        }

        foreach (var child in SnapshotChildren(element))
            FindOpenPopovers(child, results);
    }

    // CSS Position §overlay: whether hiding this popover leaves it in the top
    // layer because its `overlay` is being transitioned out with
    // `transition-behavior: allow-discrete`. A static render snapshots
    // mid-transition, so such a popover (and its ::backdrop) stays rendered.
    private bool PopoverKeepsOverlayOnHide(DomElement element) =>
        HasDiscreteOverlayTransition(element);

    // Whether the element declares a discrete transition of the `overlay` property:
    // `transition-behavior: allow-discrete` (required for a discrete property to transition at all)
    // plus `overlay` (or `all`) in the transitioned-property list. Both may appear folded into the
    // `transition` shorthand. This is the setup an `overlay` transition — in either direction — needs.
    private bool HasDiscreteOverlayTransition(DomElement element) =>
        HasDiscreteTransitionOf(element, "overlay");

    // CSS Position §overlay: whether closing this dialog leaves it in the top layer because its
    // `overlay` is being transitioned out with `transition-behavior: allow-discrete` — the same rule
    // PopoverKeepsOverlayOnHide applies to a popover. Without it a modal dialog vanished the instant
    // close() ran, and a static render that snapshots mid-transition lost the dialog and its
    // ::backdrop (WPT css/css-position/overlay/overlay-transition-dialog).
    private bool DialogKeepsOverlayOnClose(DomElement element) =>
        HasDiscreteTransitionOf(element, "overlay");

    // The companion half: `overlay` keeps the dialog in the top layer, but the UA sheet's
    // `dialog:not([open]) { display: none }` is what decides whether it generates a box at all. A
    // dialog transitioning `display` with allow-discrete still generates one for the transition's
    // duration, so the `open` attribute that rule keys on has to survive the close.
    private bool DialogKeepsDisplayOnClose(DomElement element) =>
        HasDiscreteTransitionOf(element, "display");

    // The shared shape of both: `transition-behavior: allow-discrete` (required for a discrete
    // property to transition at all) plus <paramref name="property"/> — or `all` — in the
    // transitioned-property list. Either may appear folded into the `transition` shorthand.
    private bool HasDiscreteTransitionOf(DomElement element, string property)
    {
        var props = GetComputedProps(element);

        string behavior =
            props.GetValueOrDefault("transition-behavior", string.Empty) + " " +
            props.GetValueOrDefault("transition", string.Empty);
        if (behavior.IndexOf("allow-discrete", StringComparison.OrdinalIgnoreCase) < 0)
            return false;

        string transitioned =
            props.GetValueOrDefault("transition-property", string.Empty) + " " +
            props.GetValueOrDefault("transition", string.Empty);
        foreach (var token in transitioned.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Equals(property, StringComparison.OrdinalIgnoreCase) ||
                token.Equals("all", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // CSS Position §overlay: a popover shown while its `overlay` transitions *in*
    // (`transition-behavior: allow-discrete`, `overlay`/`all` transitioned) is NOT yet in the top
    // layer — with the usual `step-end`/duration the discrete `overlay` holds `none` for the
    // transition, so the element (and its ::backdrop) render in normal flow, not the top layer, until
    // it finishes (WPT css/css-position/overlay/overlay-transition-in-rendering, -backdrop-entry).
    // A popover transitioning *out* (marked on hide) is excluded — it stays in the top layer.
    private bool PopoverHeldOutByOverlayTransitionIn(DomElement element)
    {
        if (DialogStateFor(element).PopoverTransitioningOut.TryGet(out var out_) && out_ is true)
            return false;
        return HasDiscreteOverlayTransition(element);
    }

    /// <summary>
    /// The paint-time half of <see cref="PopoverHeldOutByOverlayTransitionIn"/>: whether the popover
    /// is still out of the top layer <em>at the moment the page asks to be screenshotted</em>.
    /// <para>
    /// The CSSOM answer and the painted answer are taken at different instants, and conflating them
    /// is what made <c>overlay-transition-finished</c> unwinnable. That test reads
    /// <c>getComputedStyle(el).overlay</c> synchronously after <c>showPopover()</c> and fails itself
    /// (paints pink) unless it sees <c>none</c> — the transition must be observed as *running* at
    /// script time — and then screenshots from <c>transitionend</c>, by which point the popover must
    /// be in the top layer and covering the fixed red div. So
    /// <see cref="ComputeOverlayValue"/> keeps answering for t≈0 and only the render path moves.
    /// </para>
    /// <para>
    /// The renderer has no clock, so "which instant" has to be read from what the page says. The
    /// runner already does exactly this for <c>takeScreenshotDelayed(N)</c>, whose N becomes
    /// <c>WptTestRunner.ScreenshotPresentationTime</c>; a test that instead gates its screenshot on
    /// <c>transitionend</c> is making the same statement without a number — *render me after this
    /// transition ends*. <see cref="ScreenshotWaitsForTransitionEnd"/> recognises that shape, and
    /// nothing else in <c>css/css-position/overlay</c> matches it: the three tests that must keep the
    /// popover held out (<c>-in-rendering</c> at 60s, <c>-backdrop-entry</c> at 2s+2s,
    /// <c>-out-rendering</c>) screenshot immediately and register no such listener.
    /// </para>
    /// </summary>
    private bool PopoverHeldOutOfTopLayerForPaint(DomElement element) =>
        PopoverHeldOutByOverlayTransitionIn(element) &&
        !ScreenshotWaitsForTransitionEnd(element);

    /// <summary>
    /// Whether the page has declared that its screenshot belongs after a transition on
    /// <paramref name="element"/> finishes: the document is still <c>reftest-wait</c> (so nothing has
    /// released the screenshot yet) and a <c>transitionend</c> listener is reachable from the element
    /// — on itself, an ancestor, the document or the window, since the event bubbles.
    /// <para>
    /// The <c>reftest-wait</c> half is what keeps this from being a one-way door. Broiler dispatches
    /// no transition events today, so a page waiting on one waits forever and the class survives to
    /// serialization. If transition events are implemented later, the natural shape — dispatch
    /// <c>transitionend</c>, the listener calls <c>takeScreenshot()</c>, the class is removed — makes
    /// this predicate return <see langword="false"/> while the transition is genuinely over, and the
    /// popover is elevated by the ordinary path instead. Either way the popover ends up in the top
    /// layer, so the rule degrades into the real one rather than inverting.
    /// </para>
    /// </summary>
    private bool ScreenshotWaitsForTransitionEnd(DomElement element)
    {
        if (RenderedDocumentElement is not { } documentElement ||
            !HasClass(documentElement, "reftest-wait"))
            return false;

        for (DomElement? node = element; node is not null; node = ParentEl(node))
        {
            if (HasAttr(node, "ontransitionend") || HasTransitionEndListener(node))
                return true;
        }

        // The document node is on the propagation path too but is not a DomElement, so the walk
        // above stops one short of it — `document.addEventListener('transitionend', …)` is the
        // idiomatic way to write this gate and must count.
        return HasTransitionEndListener(_document) ||
            (_eventTargets.TryGetWindowListeners("transitionend", out var windowListeners) &&
                windowListeners.Count > 0);
    }

    private bool HasTransitionEndListener(DomNode node) =>
        _eventTargets.NodeListeners(node).TryGetValue("transitionend", out var listeners) &&
        listeners.Count > 0;

    private static bool HasClass(DomElement element, string name) =>
        (element.ClassName ?? string.Empty)
            .Split((char[])[' ', '\t', '\n', '\r', '\f'], StringSplitOptions.RemoveEmptyEntries)
            .Contains(name, StringComparer.Ordinal);

    // CSS Position 4 §overlay: the computed value of the UA-controlled `overlay` property.
    // A top-layer element (open popover / modal dialog) computes to `auto`; everything else to
    // `none`. While an `overlay` discrete transition runs *in* (allow-discrete holds the
    // before-change `none` for the transition's duration), getComputedStyle observes `none` until
    // it finishes — WPT css/css-position/overlay/overlay-transition-finished reads this
    // synchronously right after showPopover() to confirm the transition started.
    internal string ComputeOverlayValue(DomElement element)
    {
        if (PopoverHeldOutByOverlayTransitionIn(element))
            return "none";

        bool inTopLayer =
            (DialogStateFor(element).Modal.TryGet(out var modal) && modal is true) ||
            (DialogStateFor(element).PopoverOpen.TryGet(out var open) && open is true);
        return inTopLayer ? "auto" : "none";
    }
}
