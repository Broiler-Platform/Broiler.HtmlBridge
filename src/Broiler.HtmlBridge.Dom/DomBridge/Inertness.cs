using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Inert nodes -- what an <c>inert</c> attribute covers, and while a modal dialog is open everything in its
/// document but the dialog -- which can be neither focused nor hit; and the dialog's own focus, which goes
/// into it as it opens and back as it closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a press knew.</b> A modal dialog kept the pointer from what was behind it, but <c>focus()</c>,
/// Tab and a press's focus all reached the page behind; the <c>inert</c> attribute did nothing at all; and
/// opening a dialog left focus where it was, so a key went to the page behind it.
/// </para>
/// <para>
/// <b>As Chromium does it, measured</b>. <c>focus()</c> on an inert element
/// does nothing; a hit passes through an inert one to what is behind it. <c>show()</c> and
/// <c>showModal()</c> focus an <c>autofocus</c> element in the dialog, else its first focusable one, else the
/// dialog itself, and <c>close()</c> gives focus back to what had it. Tab in a modal dialog goes round its
/// own elements and never leaves it. An open dialog takes focus from a script and from a press on it,
/// though a Tab passes over it.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The dialogs open as modal ones: what blocks their documents, looked up without walking a document.
    private readonly HashSet<DomElement> _modalDialogs = new(ReferenceEqualityComparer.Instance);

    // What had focus when each open dialog opened, which its close gives focus back to.
    private readonly Dictionary<DomElement, DomElement?> _dialogPreviouslyFocused = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Whether <paramref name="element"/> is inert: in what an <c>inert</c> attribute covers, or in a document
    /// a modal dialog blocks and outside the dialog and what is in the top layer above it; an element of a
    /// frame whose element is inert is inert too.
    /// </summary>
    internal bool IsInert(DomElement element)
    {
        var node = element;
        for (var depth = 0; depth <= MaxInputFrameDepth; depth++)
        {
            var document = GetOwningDocument(node);
            if (BlockingModalDialog(document) is { } dialog && !EscapesModalInertness(node, dialog))
                return true;

            if (HasInertAttributeAbove(node))
                return true;

            if (GetFrameForContentDocument(document) is not { } frame)
                return false;

            node = frame;
        }

        return false;
    }

    /// <summary>Whether <paramref name="element"/> or an element it is in has an <c>inert</c> attribute -- short of a modal dialog, which escapes it.</summary>
    private bool HasInertAttributeAbove(DomElement element)
    {
        for (var current = element; current is not null; current = ParentEl(current))
        {
            if (HasAttr(current, "inert"))
                return true;
            if (_modalDialogs.Contains(current) && IsModalDialog(current))
                return false;
        }

        return false;
    }

    /// <summary>Whether <paramref name="element"/> is in <paramref name="dialog"/>, or in what went into the top layer after it.</summary>
    private bool EscapesModalInertness(DomElement element, DomElement dialog)
    {
        var dialogOrder = DialogStateFor(dialog).TopLayerOrder.Value;
        for (var current = element; current is not null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, dialog))
                return true;

            if (_dialogRuntimeStates.TryGetValue(current, out var state) && IsInTopLayer(current) && state.TopLayerOrder.Value > dialogOrder)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The dialog focusing steps, as Chromium takes them (measured): focus to an <c>autofocus</c> element in
    /// <paramref name="dialog"/>, else its first focusable one, else the dialog; what had focus is
    /// remembered for its close.
    /// </summary>
    private void RunDialogFocusingSteps(DomElement dialog)
    {
        var document = GetOwningDocument(dialog);
        _dialogPreviouslyFocused[dialog] = FocusedElementIn(document);

        var control = AutofocusDelegateOf(dialog) ??
                      dialog.Descendants().OfType<DomElement>().FirstOrDefault(IsFocusable) ??
                      dialog;
        FocusElement(control);
    }

    /// <summary>
    /// What a dialog's close does to focus: what had it before the dialog opened gets it back, when focus is in
    /// the dialog or the dialog was modal.
    /// </summary>
    private void RestoreFocusAfterDialog(DomElement dialog, bool wasModal)
    {
        if (!_dialogPreviouslyFocused.Remove(dialog, out var previous) || previous is not { IsConnected: true })
            return;

        var focused = FocusedElementIn(GetOwningDocument(dialog));
        if (wasModal || focused is not null && IsInclusiveAncestor(dialog, focused))
            FocusElement(previous);
    }

    /// <summary>
    /// A modal dialog taken out of its document, or moved within it: HTML's dialog removing steps take it out of
    /// the top layer and it is modal no more -- still open, as a non-modal dialog, so that <c>showModal()</c>
    /// throws once it is back -- and the document is no longer blocked (measured).
    /// It stayed modal, and blocked the page again as soon as it was put back.
    /// </summary>
    private void UnblockRemovedModalDialogs(DomNode removed)
    {
        if (_modalDialogs.Count == 0)
            return;

        foreach (var dialog in _modalDialogs.ToList())
        {
            if (!ReferenceEquals(dialog, removed) && !(removed is DomElement root && IsInclusiveAncestor(root, dialog)))
                continue;

            _modalDialogs.Remove(dialog);
            DialogStateFor(dialog).Modal.Remove();
            InvalidateStyleScope(dialog);
            NoteElementStateChange();
        }
    }

    /// <summary>Forgets the modal dialogs and the focus they remember, with the document they were in.</summary>
    private void ResetInertness()
    {
        _modalDialogs.Clear();
        _dialogPreviouslyFocused.Clear();
    }
}
