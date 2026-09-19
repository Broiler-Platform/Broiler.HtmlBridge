using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    // :active-view-transition-type( a, b, … ) anywhere in a selector.
    internal static readonly System.Text.RegularExpressions.Regex ActiveViewTransitionType =
        new(@":active-view-transition-type\(\s*([^)]*)\)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // The bare :active-view-transition pseudo-class (css-view-transitions-2) — matches the document
    // element whenever a view transition is active, regardless of type. The negative lookahead keeps
    // it from also matching the :active-view-transition-type(…) functional form handled above.
    internal static readonly System.Text.RegularExpressions.Regex ActiveViewTransitionBare =
        new(@":active-view-transition(?!-type)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    // html::view-transition[-group|-image-pair|-old|-new]( name ) — the pseudo and its optional
    // name/class/`*` argument. The leading originating selector (html / :root / *) is ignored: the
    // pseudo tree always originates from the document element.
    internal static readonly System.Text.RegularExpressions.Regex ViewTransitionPseudo =
        new(@"::view-transition(?:-(group-children|group|image-pair|old|new))?\s*(?:\(\s*([^)]*)\s*\))?\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Reads the <c>types</c> option — a JS array/iterable of strings — into
    /// <paramref name="into"/>. Absent or non-array values contribute nothing.</summary>
    /// <remarks>
    /// Both reads are the engine's own coercions rather than the handle's cheap ones: <c>length</c>
    /// goes through <c>ToNumber</c> and each entry through <c>ToString</c>, because the dictionary is
    /// the page's and either member may be a string — or an object with a <c>valueOf</c>/
    /// <c>toString</c>. That is exactly what the <c>DoubleValue</c>/<c>ToString()</c> this replaces
    /// did on this engine, both of which run the coercion rather than reading a field.
    /// </remarks>
    internal static void CollectViewTransitionTypes(IJsRealm realm, JsValue types, HashSet<string> into)
    {
        if (!types.IsObject)
            return;

        var lengthValue = realm.GetProperty(types, "length");
        if (lengthValue.IsMissing || lengthValue.IsUndefined)
            return;

        var length = (int)realm.ToNumber(lengthValue);
        for (var i = 0; i < length; i++)
        {
            var item = realm.GetIndex(types, (uint)i);
            if (!item.IsNullish)
                into.Add(realm.ToJsString(item));
        }
    }

    /// <summary>The callback argument of a thenable's <c>then</c>/<c>finally</c>: the function the
    /// page passed, or <see cref="JsValue.Missing"/> for anything else — the same narrowing the
    /// former engine-typed narrowing cast did, an argument that was never passed included.</summary>
    internal static JsValue ThenCallback(in JsCall call) => call[0].IsFunction ? call[0] : JsValue.Missing;

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
    internal const string RootPseudo = ":root";

    internal static bool AnyTypeActive(string argumentList, HashSet<string> activeTypes)
    {
        foreach (var raw in argumentList.Split(','))
        {
            if (activeTypes.Contains(raw.Trim()))
                return true;
        }
        return false;
    }

    /// <summary>The base style of a <c>::view-transition-old</c>/<c>-new</c> box: the captured rect's
    /// size at the group's scale, plus the <c>writing-mode</c> reset when the captured element is one
    /// the live layout transposes (<paramref name="writingMode"/> is null when it is not, leaving the
    /// inherited mode alone).</summary>
    internal static Dictionary<string, string> SnapshotBoxStyle(double width, double height, string? writingMode)
    {
        var style = BaseStyle(
            ("position", "absolute"), ("left", "0"), ("top", "0"),
            ("width", Px(width)), ("height", Px(height)));

        if (writingMode is not null)
            style["writing-mode"] = writingMode;

        return style;
    }

    internal static Dictionary<string, string> BaseStyle(params (string Key, string Value)[] entries)
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
    internal static readonly (string Key, string Value)[] PseudoBoxAuthorReset =
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
    internal static bool AuthorPaintsBackground(Dictionary<string, string> pseudoDeclarations)
    {
        foreach (var key in pseudoDeclarations.Keys)
        {
            if (key.StartsWith("background", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
    internal static double SnapshotScale(double capturedWidth, double groupWidth)
    {
        if (capturedWidth <= 0 || groupWidth <= 0)
            return 1;

        var scale = groupWidth / capturedWidth;
        return double.IsFinite(scale) && scale > 0 ? scale : 1;
    }

    // The paint- and text-affecting computed properties baked onto a snapshot's content box so it
    // renders like the captured element once re-parented under the overlay, where the element's
    // original ancestors and matched author selectors no longer apply. Deliberately excludes
    // geometry (position/inset/margin/width/height) — the group and box size the snapshot from the
    // captured rect and place the content at 0,0 — and view-transition-name (to avoid re-capturing
    // the clone).
    internal static readonly string[] SnapshotPaintProperties =
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

    internal static double Interpolate(double from, double to, double progress) =>
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
    internal static double FrozenGroupProgress(Dictionary<string, string> groupDeclarations)
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

    /// <summary>Whether the document holds a frame whose content the root snapshot would have to
    /// reproduce.</summary>
    internal static bool ContainsNestedBrowsingContext(DomElement root) =>
        root.Descendants().OfType<DomElement>().Any(element =>
            element.TagName.Equals("iframe", System.StringComparison.OrdinalIgnoreCase) ||
            element.TagName.Equals("frame", System.StringComparison.OrdinalIgnoreCase) ||
            element.TagName.Equals("object", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether these declarations stop a snapshot painting at all — as opposed to merely
    /// changing how it composites.</summary>
    internal static bool SuppressesSnapshotPaint(Dictionary<string, string> declarations)
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
    internal static Dictionary<string, string> LookupRootPseudo(
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
    /// placement (<c>transform</c>) are not here — see <c>DomBridge.RootSnapshotNeedsContent</c>.</summary>
    private static readonly string[] SnapshotAlteringProperties =
    {
        "filter", "backdrop-filter", "mix-blend-mode", "mask", "mask-image", "clip-path",
    };

    internal static bool HasSnapshotAlteringEffect(Dictionary<string, string> declarations)
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
    internal static bool IsNonRenderedSnapshotChild(DomNode node) =>
        node is DomElement element &&
        element.TagName is { } tag &&
        (tag.Equals("head", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("style", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("script", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("link", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("meta", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("title", System.StringComparison.OrdinalIgnoreCase) ||
         tag.Equals("base", System.StringComparison.OrdinalIgnoreCase));

    /// <summary>The colour when it actually paints, or null when it is absent or fully transparent
    /// (<c>transparent</c> and the <c>rgba(…, 0)</c> the computed-style engine serializes it as).</summary>
    internal static string? PaintsBackground(string? value)
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

    /// <summary>
    /// Whether the captured element establishes a paint clip on its descendants — a non-visible
    /// <c>overflow</c>, <c>contain: paint/content/strict</c>, or a <c>clip-path</c>. Such an element's
    /// view-transition snapshot clips to the border box; a default <c>overflow: visible</c> element's
    /// snapshot instead shows its ink overflow (WPT capture-with-offscreen-child-translated).
    /// </summary>
    internal static bool CapturedElementClipsContent(Dictionary<string, string> style)
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

    internal static bool IsNoneName(string? name) =>
        string.IsNullOrWhiteSpace(name) ||
        string.Equals(name.Trim(), "none", System.StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name.Trim(), "normal", System.StringComparison.OrdinalIgnoreCase);

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
    internal static string? ResolveRootViewTransitionName(Dictionary<string, string> rootStyle)
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

    /// <summary>The declarations that apply to a pseudo of <paramref name="kind"/> for a given
    /// capture, merging the universal (<c>*</c>) and name-specific buckets in cascade order (specific
    /// wins). <paramref name="capture"/> is <c>null</c> for the bare overlay pseudo.</summary>
    internal static Dictionary<string, string> LookupPseudo(
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
    internal static bool HasRootOverlayKeepAliveAnimation(
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

    internal static string Px(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";
}

internal readonly record struct ViewTransitionCapture(
    string Name,
    // The captured element's `view-transition-class` list, matched by a pseudo argument's
    // `.class` part (css-view-transitions-2 <pt-class-selector>). Empty when it has none.
    string Classes,
    double GroupLeft, double GroupTop,
    bool HasOld, double OldLeft, double OldTop, double OldWidth, double OldHeight, string OldBackground, DomElement? OldContent,
    bool HasNew, double NewLeft, double NewTop, double NewWidth, double NewHeight, string NewBackground, DomElement? NewContent);

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Whether the pseudo rules pin the root's <em>old</em> snapshot over its new one — the
    /// <c>::view-transition-old(root) { opacity: 1 }</c> / <c>-new(root) { opacity: 0 }</c> pairing
    /// the reftests use to freeze a paused transition at its start. Anything else (including the
    /// unpinned default, whose animation would have run to the new state) shows the new state.
    /// </summary>
    internal static bool RootSnapshotShowsOldState(Dictionary<string, Dictionary<string, string>> pseudoRules)
    {
        return IsOpaqueOpacity(OpacityFor("old")) && IsTransparentOpacity(OpacityFor("new"));

        // A name-specific rule wins over the universal one, matching LookupPseudo's cascade order.
        string? OpacityFor(string kind)
        {
            if (pseudoRules.TryGetValue($"{kind}|root", out var byName)
                && byName.TryGetValue("opacity", out var specific))
                return specific;
            return pseudoRules.TryGetValue($"{kind}|*", out var universal)
                && universal.TryGetValue("opacity", out var shared) ? shared : null;
        }
    }

    private static bool IsOpaqueOpacity(string? value) =>
        TryParseOpacity(value, out double opacity) && opacity >= 0.5;

    private static bool IsTransparentOpacity(string? value) =>
        TryParseOpacity(value, out double opacity) && opacity < 0.5;

    private static bool TryParseOpacity(string? value, out double opacity) =>
        double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out opacity);
}
