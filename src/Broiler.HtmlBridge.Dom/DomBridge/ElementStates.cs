using System.Collections.Generic;
using System.Linq;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The states of the page's elements that <c>:target</c>, <c>:user-valid</c> and <c>:user-invalid</c>
/// match: which element each document's URL names, and which form controls the user has interacted
/// with or edited.
/// </summary>
/// <remarks>
/// <para>
/// <b>They matched nothing.</b> The selector matcher had no way to know a page's fragment or what the
/// user had done to its form, so a section shown as <c>:target</c>, a field outlined red only once the
/// user had got it wrong (<c>input:user-invalid</c>), stayed as a page nobody had touched shows them.
/// The bridge's matcher asks <see cref="ElementStateOf"/>; the renderer reads the same states from the
/// page it is handed (<see cref="CssElementStateMarkup"/>).
/// </para>
/// <para>
/// <b>As Chromium sets them, measured.</b> A document's target is the element its fragment names when
/// it is navigated to the fragment -- a load, <c>location.hash</c>, a link into the page -- and stays
/// that element until the next fragment navigation, or until it leaves the document; one inserted
/// later with the name is not the target. A control is interacted with once the user commits a change
/// to it (leaving a field they edited, clicking a checkbox) or tries to submit its form, which marks
/// every control of the form, and stops being when its form is reset; a script's <c>value</c>,
/// <c>click()</c>, <c>checkValidity()</c> or <c>change</c> event marks nothing. Only a value the user
/// edited can be too short or too long for its <c>minlength</c> and <c>maxlength</c>.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    private readonly HashSet<DomElement> _userInteracted = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DomElement> _userEdited = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DomDocument, DomElement> _targets = new(ReferenceEqualityComparer.Instance);

    /// <summary>The element states of <paramref name="element"/>, an element of a live document.</summary>
    internal CssElementState ElementStateOf(DomElement element)
    {
        var state = CssElementState.None;
        if (_targets.Count > 0 && GetOwningDocument(element) is DomDocument document &&
            _targets.TryGetValue(document, out var target) && ReferenceEquals(target, element) && element.IsConnected)
        {
            state |= CssElementState.Target;
        }

        if (_userInteracted.Contains(element))
            state |= CssElementState.UserInteracted;
        if (_userEdited.Contains(element))
            state |= CssElementState.UserEdited;

        // A popover in the top layer is :popover-open, and a dialog open as a modal one, or the fullscreen
        // element, is :modal (DomBridge/Popovers.cs). Read without minting a state for every element asked.
        if (_dialogRuntimeStates.TryGetValue(element, out var dialogState))
        {
            if (dialogState.PopoverOpen is { IsSet: true, Value: true })
                state |= CssElementState.PopoverOpen;
            if (dialogState.Modal is { IsSet: true, Value: true } && HasAttr(element, "open") ||
                dialogState.Fullscreen is { IsSet: true, Value: true })
            {
                state |= CssElementState.Modal;
            }
        }

        return state;
    }

    // ── :target ──────────────────────────────────────────────────────────

    /// <summary>
    /// Makes the element <paramref name="fragment"/> names <paramref name="document"/>'s target, or
    /// leaves it none: HTML's "find a potential indicated element", with the fragment as written and
    /// then percent-decoded -- the first element with that id, else the first <c>a</c> with that name.
    /// </summary>
    internal void SetTargetFromFragment(DomDocument document, string? fragment)
    {
        var raw = fragment?.TrimStart('#') ?? string.Empty;
        var target = raw.Length == 0 ? null : IndicatedElement(document, raw) ?? IndicatedElement(document, DecodeFragment(raw));

        var changed = target is null
            ? _targets.Remove(document)
            : !(_targets.TryGetValue(document, out var previous) && ReferenceEquals(previous, target));
        if (target is not null)
            _targets[document] = target;

        if (changed)
            NoteElementStateChange();
    }

    private static DomElement? IndicatedElement(DomDocument document, string name)
    {
        if (name.Length == 0)
            return null;

        var elements = document.Descendants().OfType<DomElement>();
        return elements.FirstOrDefault(element => element.GetAttribute("id") == name) ??
               elements.FirstOrDefault(element =>
                   element.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase) && element.GetAttribute("name") == name);
    }

    private static string DecodeFragment(string fragment)
    {
        try
        {
            return Uri.UnescapeDataString(fragment);
        }
        catch (UriFormatException)
        {
            return fragment;
        }
    }

    /// <summary>
    /// The host moved the page to a fragment of itself -- a link into the page the user followed, or back
    /// or forward between two -- which the page hears as a fragment navigation of its own Location:
    /// <c>location.hash</c>, <c>:target</c> and <c>popstate</c> follow at once, and <c>hashchange</c> in a
    /// later task (DomBridge/FragmentNavigation.cs). Answers whether it was one.
    /// </summary>
    /// <remarks>
    /// The window scrolled to the fragment and told the page nothing, so the page's <c>location.hash</c>
    /// was the one it was loaded with, no <c>hashchange</c> fired, and a section shown as <c>:target</c>
    /// never showed. A URL with no fragment -- back to the top of the page -- is left to the host.
    /// </remarks>
    internal bool NavigateToFragment(string url)
    {
        if (_realm is null || !_topLocation.IsObject || FragmentOf(url) is null ||
            !Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            !Uri.TryCreate(Realm.ToJsString(Realm.GetProperty(_topLocation, "href")), UriKind.Absolute, out var current) ||
            Uri.Compare(target, current, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                UriFormat.UriEscaped, StringComparison.Ordinal) != 0)
        {
            return false;
        }

        // The host has the entry for it already: the page's history adds its own and reports none.
        _hostNavigatingToFragment = true;
        try
        {
            Realm.SetProperty(_topLocation, "href", JsValue.String(url));
        }
        finally
        {
            _hostNavigatingToFragment = false;
        }

        return true;
    }

    /// <summary>The fragment of <paramref name="url"/>, or null when it has none.</summary>
    private static string? FragmentOf(string? url)
    {
        var hash = url?.IndexOf('#') ?? -1;
        return hash >= 0 ? url![(hash + 1)..] : null;
    }

    // ── User validity ─────────────────────────────────────────────────────

    /// <summary>The user committed a change to <paramref name="control"/>: it is <c>:user-valid</c> or <c>:user-invalid</c> from now on.</summary>
    private void MarkUserInteracted(DomElement control)
    {
        if (_userInteracted.Add(control))
            NoteElementStateChange();
    }

    /// <summary>The user tried to submit <paramref name="form"/>: every control of it has been interacted with.</summary>
    private void MarkFormInteracted(DomElement form)
    {
        var changed = false;
        foreach (var control in FormControlsOf(form))
            changed |= _userInteracted.Add(control);
        if (changed)
            NoteElementStateChange();
    }

    /// <summary>The form was reset: none of its controls has been interacted with or edited.</summary>
    private void ForgetUserValidity(DomElement form)
    {
        var changed = false;
        foreach (var control in FormControlsOf(form))
        {
            changed |= _userInteracted.Remove(control);
            changed |= _userEdited.Remove(control);
        }

        if (changed)
            NoteElementStateChange();
    }

    /// <summary>The user changed <paramref name="field"/>'s value by editing it.</summary>
    private void MarkUserEdited(DomElement field)
    {
        if (_userEdited.Add(field))
            NoteElementStateChange();
    }

    /// <summary>A script set <paramref name="field"/>'s value, which is not a user's edit.</summary>
    private void ForgetUserEdit(DomElement field)
    {
        if (_userEdited.Remove(field))
            NoteElementStateChange();
    }

    /// <summary>
    /// A target, an interaction or an edit changed. What the bridge resolved styles from is stale, and
    /// what the page renders may have changed: it is a change of render state, as focus is.
    /// </summary>
    private void NoteElementStateChange() => NoteUserActionStateChange();

    private void ResetElementStates()
    {
        _userInteracted.Clear();
        _userEdited.Clear();
        _targets.Clear();
    }

    // ── Values ────────────────────────────────────────────────────────────

    /// <summary>
    /// A form control's live value for the selector matcher: an <c>input</c>'s or a text area's effective
    /// value, a select's selected option's value; null for anything else.
    /// </summary>
    internal string? LiveValueOf(DomElement element)
    {
        switch (element.LocalName.ToLowerInvariant())
        {
            case "input":
            case "textarea":
                return _formState.GetEffectiveValue(element);
            case "select":
                return _select.GetValue(element);
            default:
                return null;
        }
    }
}
