using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The user-action state of the page's elements -- what the pointer is over, what it is pressing, what
/// has focus and whether it shows it -- which <c>:hover</c>, <c>:active</c>, <c>:focus</c>,
/// <c>:focus-visible</c> and <c>:focus-within</c> match.
/// </summary>
/// <remarks>
/// <para>
/// <b>They matched nothing.</b> The selector matcher had no way to ask, so a page's <c>a:hover</c> rule
/// never applied, a menu opened on <c>li:hover &gt; ul</c> never opened, and a field never took its
/// <c>:focus</c> border -- whatever the user did -- while <c>querySelector(':hover')</c> found nothing.
/// </para>
/// <para>
/// <b>Two readers.</b> The bridge's own matcher -- <c>getComputedStyle</c>, <c>matches</c>,
/// <c>querySelector</c> -- asks the selector state provider, which answers from the live state here.
/// The renderer is handed the page as markup and parses it again, so each projected element in a
/// user-action state carries it as <see cref="CssUserActionStateMarkup.AttributeName"/>, which a matcher
/// with no provider reads; a frame's document, which reaches the renderer as markup inside its
/// element's, carries it the same way.
/// </para>
/// <para>
/// <b>As Chromium matches them, measured.</b> Hover is the element under the pointer and its ancestors
/// in each document the pointer is in -- a frame's element too, since the pointer is over it in its
/// document. Active is the element a press of the main button, or a space on a focused control, is held
/// on, and its ancestors. Focus is the focused element of the focused document alone: the frame
/// element of a document that has focus matches neither <c>:focus</c> nor <c>:focus-within</c>, and
/// neither do its ancestors. Focus is visible when the keyboard moved it, on a text field however it got
/// there, and after a script's <c>focus()</c> while the user's last input was a key, or before any
/// input (<see cref="ShowsFocus"/>).
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The element a press of the main pointer button is held on, which with its ancestors is :active.
    private DomElement? _activeTarget;

    // Whether the page's style sheets mention :hover and :active, and whether a rule that does can move a
    // box, as of the versions of its documents and CSSOM edits they were read at: a move that changes
    // only what is hovered then matters only if they do, and lays the page out again only if one can.
    private UserActionRules? _userActionRules;

    private sealed record UserActionRules(long Version, bool Hover, bool Active, bool MovesBoxes);

    // CSSOM edits of the page's sheets, which change what they say without changing a document.
    private long _styleSheetRuleEdits;

    // Every element's state while a render projection is stamped, worked out once rather than per element.
    private Dictionary<DomElement, CssUserActionState>? _projectionUserActionStates;

    /// <summary>The user-action pseudo-classes <paramref name="element"/>, an element of a live document, matches.</summary>
    internal CssUserActionState UserActionStateOf(DomElement element)
    {
        if (_projectionUserActionStates is { } known)
            return known.GetValueOrDefault(element);

        var state = CssUserActionState.None;
        if (!element.IsConnected)
            return state;

        var document = GetOwningDocument(element);
        foreach (var level in _hoverLevels)
        {
            if (ReferenceEquals(level.Document, document) && level.Element is { } hovered && IsInclusiveAncestor(element, hovered))
            {
                state |= CssUserActionState.Hover;
                break;
            }
        }

        if ((_activeTarget ?? _spaceArmed) is { IsConnected: true } active &&
            ReferenceEquals(GetOwningDocument(active), document) &&
            IsInclusiveAncestor(element, active))
        {
            state |= CssUserActionState.Active;
        }

        if (FocusedElementIn(FocusedDocument) is { } focused && ReferenceEquals(GetOwningDocument(focused), document))
        {
            if (ReferenceEquals(focused, element))
            {
                state |= CssUserActionState.Focus;
                if (_focusVisible)
                    state |= CssUserActionState.FocusVisible;
            }

            if (IsInclusiveAncestor(element, focused))
                state |= CssUserActionState.FocusWithin;
        }

        return state;
    }

    /// <summary>Every element in a user-action state and its state: the hover, active and focus chains, each within its document.</summary>
    private Dictionary<DomElement, CssUserActionState> CollectUserActionStates()
    {
        var states = new Dictionary<DomElement, CssUserActionState>(ReferenceEqualityComparer.Instance);
        void Chain(DomElement? from, CssUserActionState state)
        {
            for (var current = from; current is not null; current = ParentEl(current))
                states[current] = states.GetValueOrDefault(current) | state;
        }

        foreach (var level in _hoverLevels)
            Chain(level.Element, CssUserActionState.Hover);

        if ((_activeTarget ?? _spaceArmed) is { IsConnected: true } active)
            Chain(active, CssUserActionState.Active);

        if (FocusedElementIn(FocusedDocument) is { } focused)
        {
            Chain(focused, CssUserActionState.FocusWithin);
            states[focused] |= CssUserActionState.Focus | (_focusVisible ? CssUserActionState.FocusVisible : CssUserActionState.None);
        }

        return states;
    }

    /// <summary>Runs <paramref name="stamp"/> -- the stamping of a render projection -- with every element's state worked out once.</summary>
    private void WithUserActionStates(Action stamp)
    {
        var previous = _projectionUserActionStates;
        _projectionUserActionStates = CollectUserActionStates();
        try
        {
            stamp();
        }
        finally
        {
            _projectionUserActionStates = previous;
        }
    }

    /// <summary>
    /// Hover, focus or the active element changed. What the bridge resolved styles from is stale, and
    /// unless nothing the page renders can depend on it, what it renders has changed too: focus always
    /// may, since the user agent's own sheet rings a visibly focused element, and hover or the active
    /// element when the page's style sheets mention <c>:hover</c> or <c>:active</c>.
    /// </summary>
    /// <param name="hoverOrActive">Whether what changed is hover or the active element, rather than focus.</param>
    /// <remarks>
    /// A hover or a press that the page's sheets only paint -- a colour, a background, an underline, an
    /// outline -- moves no box, so the page is not laid out again for it: a retained geometry snapshot
    /// still holds. Over html5test.com, whose rows are styled <c>tr:hover</c>, that layout was most of
    /// the move after each change of row. Focus always may move one, as before.
    /// </remarks>
    private void NoteUserActionStateChange(bool hoverOrActive = false)
    {
        if (_realm is null)
            return;

        var rules = hoverOrActive ? ReadUserActionRules() : null;
        if (rules is { Hover: false, Active: false })
            return;

        ClearComputedPropsCache();
        NoteRenderStateChange(affectsLayout: rules is null || rules.MovesBoxes);
    }

    /// <summary>
    /// Whether any style sheet of the page or of its frames mentions <c>:hover</c> or <c>:active</c>, and
    /// whether a rule that does can move a box.
    /// </summary>
    /// <remarks>
    /// Whether they are mentioned is read off the sheets' text, which over-counts -- a comment that
    /// mentions <c>:hover</c> counts -- and costs one read of the page's sheets per change of what it
    /// renders. Whether a box can move is read off their rules (<see cref="MovesBoxes"/>).
    /// </remarks>
    private UserActionRules ReadUserActionRules()
    {
        long version;
        unchecked
        {
            version = (long)_document.Version + _styleSheetRuleEdits;
            foreach (var frameDocument in _browsingContexts.ContentDocuments)
                version = version * 31 + (long)frameDocument.Version + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(frameDocument);
        }

        if (_userActionRules is { } known && known.Version == version)
            return known;

        bool hover = false, active = false, movesBoxes = false;
        var documents = new List<DomElement>();
        if (GetDocumentElement(_document) is { } root)
            documents.Add(root);
        foreach (var frameDocument in _browsingContexts.ContentDocuments)
        {
            if (GetDocumentElement(frameDocument) is { } frameRoot)
                documents.Add(frameRoot);
        }

        foreach (var documentRoot in documents)
        {
            foreach (var sheet in CollectStyleSheetCandidatesInTree(documentRoot))
            {
                var text = GetStyleElementCssText(sheet);
                var mentionsHover = text.Contains(":hover", StringComparison.OrdinalIgnoreCase);
                var mentionsActive = text.Contains(":active", StringComparison.OrdinalIgnoreCase);
                hover |= mentionsHover;
                active |= mentionsActive;

                // What a sheet imports is not read here, so a sheet that imports can move anything.
                if ((mentionsHover || mentionsActive) && !movesBoxes)
                    movesBoxes = text.Contains("@import", StringComparison.OrdinalIgnoreCase) ||
                                 MovesBoxes(EnsureStyleSheetRulesCurrent(sheet));
            }
        }

        var rules = new UserActionRules(version, hover, active, movesBoxes);
        _userActionRules = rules;
        return rules;
    }

    /// <summary>
    /// Whether a style rule in <paramref name="rules"/> whose selector mentions <c>:hover</c> or
    /// <c>:active</c> declares anything that can move a box: anything but the properties that only paint
    /// (<see cref="PaintOnlyProperties"/>). A custom property counts, since it can feed any other.
    /// </summary>
    private static bool MovesBoxes(IReadOnlyList<CssRule> rules)
    {
        foreach (var rule in rules)
        {
            switch (rule)
            {
                case CssStyleRule styleRule when MentionsUserAction(styleRule):
                    foreach (var declaration in styleRule.Declarations.Declarations)
                    {
                        if (!PaintOnlyProperties.Contains(declaration.Name))
                            return true;
                    }
                    break;

                case CssAtRule { Rules.Count: > 0 } atRule when MovesBoxes(atRule.Rules):
                    return true;
            }
        }

        return false;

        static bool MentionsUserAction(CssStyleRule rule)
        {
            foreach (var selector in rule.Selectors.Selectors)
            {
                if (selector.Text.Contains(":hover", StringComparison.OrdinalIgnoreCase) ||
                    selector.Text.Contains(":active", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Properties that change how a box is painted and never where it is or how big: colours,
    /// backgrounds, borders' colours, outlines, decorations, shadows, opacity and the cursor.
    /// </summary>
    private static readonly HashSet<string> PaintOnlyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "color", "opacity", "cursor",
        "background", "background-color", "background-image", "background-position", "background-position-x",
        "background-position-y", "background-size", "background-repeat", "background-attachment",
        "background-clip", "background-origin", "background-blend-mode",
        "border-color", "border-top-color", "border-right-color", "border-bottom-color", "border-left-color",
        "border-block-color", "border-block-start-color", "border-block-end-color",
        "border-inline-color", "border-inline-start-color", "border-inline-end-color",
        "outline", "outline-color", "outline-style", "outline-width", "outline-offset",
        "text-decoration", "text-decoration-color", "text-decoration-line", "text-decoration-style",
        "text-decoration-thickness", "text-underline-offset", "text-emphasis-color",
        "text-shadow", "box-shadow",
        "fill", "fill-opacity", "stroke", "stroke-opacity",
        "caret-color", "accent-color", "column-rule-color",
        "-webkit-text-fill-color", "-webkit-text-stroke-color", "-webkit-tap-highlight-color",
    };

    /// <summary>
    /// Writes each projected element's user-action state, and its element state (DomBridge/ElementStates.cs),
    /// into its markup, in place of whatever the page put there: the renderer's matcher reads them there
    /// (<see cref="CssUserActionStateMarkup"/>, <see cref="CssElementStateMarkup"/>).
    /// </summary>
    private void StampUserActionState(DomElement projected)
    {
        var source = ResolveRenderSource(projected);
        Stamp(CssUserActionStateMarkup.AttributeName, CssUserActionStateMarkup.Format(UserActionStateOf(source)));
        Stamp(CssElementStateMarkup.AttributeName, CssElementStateMarkup.Format(ElementStateOf(source)));

        void Stamp(string attribute, string? value)
        {
            if (value is not null)
                SetAttr(projected, attribute, value);
            else if (HasAttr(projected, attribute))
                RemoveAttr(projected, attribute);
        }
    }

    /// <summary>
    /// The selector state the bridge's own matcher asks: a form control's checkedness and value, what the
    /// user is doing to each element, and its element state. Never <see cref="CssElementState.Visited"/>:
    /// this matcher answers the page's own scripts.
    /// </summary>
    private sealed class BridgeSelectorStateProvider(DomBridge bridge) : ICssSelectorStateProvider
    {
        public bool? IsChecked(DomElement element) =>
            bridge._formState.TryGetDirtyChecked(element, out var value) ? value : null;

        public CssUserActionState GetUserActionState(DomElement element) =>
            bridge.UserActionStateOf(bridge.ResolveRenderSource(element));

        public CssElementState GetElementState(DomElement element) =>
            bridge.ElementStateOf(bridge.ResolveRenderSource(element));

        public string? GetValue(DomElement element) =>
            bridge.LiveValueOf(bridge.ResolveRenderSource(element));
    }
}
