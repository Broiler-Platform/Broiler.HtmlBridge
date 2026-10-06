using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A user's choice in a <c>&lt;select&gt;</c> the host shows a control of its own for: the page's select
/// takes it, and hears it as a user's change.
/// </summary>
/// <remarks>
/// <para>
/// <b>The choice stayed the host's.</b> A host that draws its own list for a select records what the user
/// picks for its submissions, and the page's select kept its old value: no <c>input</c>, no <c>change</c>,
/// and a script reading <c>select.value</c> read the option the user had moved away from.
/// </para>
/// <para>
/// Measured in Chromium: a user's change fires <c>input</c> -- a trusted
/// <c>Event</c>, not an <c>InputEvent</c>, bubbling, not cancelable, the value already the new one -- then
/// <c>change</c>, and the select matches <c>:user-valid</c> or <c>:user-invalid</c> from the <c>change</c> on.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// The user chose the option <paramref name="optionIndex"/> of the page's select
    /// <paramref name="selectIndex"/> (both counted in tree order) in the host's control for it. Answers whether
    /// the selection changed; the option already selected, a disabled select or one that is not there changes
    /// nothing and fires nothing.
    /// </summary>
    internal bool SelectOptionByUser(int selectIndex, int optionIndex)
    {
        if (_realm is null || selectIndex < 0)
            return false;

        var select = _document.Descendants().OfType<DomElement>()
            .Where(static element => element.TagName.Equals("select", StringComparison.OrdinalIgnoreCase))
            .ElementAtOrDefault(selectIndex);
        if (select is null || IsDisabledFormControl(select) || _select.GetSelectedIndex(select) == optionIndex)
            return false;

        _select.SetSelectedIndex(select, optionIndex);
        NoteElementStateChange();
        FireChangeNotification(select, "input", composed: true);
        if (select.IsConnected)
        {
            MarkUserInteracted(select);
            FireChangeNotification(select, "change", composed: false);
        }

        return true;
    }
}
