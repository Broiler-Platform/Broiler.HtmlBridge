using System.Runtime.CompilerServices;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISelectHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.SelectBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.8). Explicit interface members, so these
/// seams do not widen the public <c>DomBridge</c> surface. The select/option form-control state
/// stays on the per-element <see cref="ElementRuntimeState"/>; these named accessors are the seam a
/// future runtime-state consolidation re-homes.
/// </summary>
public sealed partial class DomBridge : ISelectHost
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element form-control runtime state
    // (checkbox/radio checkedness, option value/defaultSelected, select selectedIndex, dialog
    // returnValue) was the FormControl slot of the process-static ElementRuntimeState table; it is now
    // a per-bridge instance table, owned by the session's bridge. Still element-keyed, so it GCs with
    // the element and the cloneNode copy (see CloneDomElement) is preserved. Reached from the bridge's
    // own instance methods directly, and from the feature bindings that need it (EventTargetBinding's
    // click checkbox/radio toggle, and the `:checked` selector state provider) through their host
    // interfaces — the concern's static callers were threaded, not left on a process-static table.
    private readonly ConditionalWeakTable<DomElement, FormControlRuntimeState> _formControlRuntimeStates = [];

    private FormControlRuntimeState FormControlStateFor(DomElement element) =>
        _formControlRuntimeStates.GetValue(element, static _ => new FormControlRuntimeState());

    JSObject ISelectHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomElement? ISelectHost.FindDomElementByJSObject(JSObject? jsObj) =>
        jsObj is null ? null : FindDomElementByJSObject(jsObj);

    bool ISelectHost.TryGetSelectedIndex(DomElement select, out int index)
    {
        if (FormControlStateFor(select).SelectedIndex.TryGet(out var value) && value is int i)
        {
            index = i;
            return true;
        }

        index = 0;
        return false;
    }

    void ISelectHost.SetSelectedIndex(DomElement select, int index) =>
        FormControlStateFor(select).SelectedIndex.Set(index);

    bool ISelectHost.TryGetOptionValue(DomElement option, out string value)
    {
        if (FormControlStateFor(option).Value.TryGet(out var stored) && stored is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    // defaultSelected reflects the `selected` CONTENT ATTRIBUTE (HTML §4.10.10), so the runtime slot
    // is an override of it rather than the whole story. Reading the slot alone answered `false` for
    // every option that carried `selected` in the markup — including the one the select was showing,
    // so a page asking "is this the original selection?" was told no about the option it had just
    // been handed. The slot still wins when a script has written the property.
    bool ISelectHost.GetOptionDefaultSelected(DomElement option) =>
        FormControlStateFor(option).DefaultSelected.TryGet(out var ds)
            ? ds is true
            : HasAttr(option, "selected");

    // Writing the property writes the attribute it reflects, so a later reset — which clears the
    // slot — restores what was written rather than what the markup happened to say.
    void ISelectHost.SetOptionDefaultSelected(DomElement option, bool value)
    {
        FormControlStateFor(option).DefaultSelected.Set(value);
        if (value)
            SetAttr(option, "selected", string.Empty);
        else
            RemoveAttr(option, "selected");
    }
}
