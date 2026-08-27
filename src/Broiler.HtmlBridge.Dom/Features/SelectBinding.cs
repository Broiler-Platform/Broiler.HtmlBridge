using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTMLSelectElement / HTMLOptionElement feature binding (HtmlBridge complexity-reduction
/// roadmap Phase 3, P3.8) — <c>select.add</c>/<c>options</c>/<c>selectedIndex</c>/<c>size</c> and its
/// value resolution, plus <c>option.defaultSelected</c>. The option-collection, selected-index and
/// value algorithms (previously scattered as static helpers in <c>LayoutMetrics.cs</c>, though never
/// used by layout) move here; the per-element form-control state they touch is reached through the
/// narrow <see cref="ISelectHost"/> contract as named primitives, and neutral tree/attribute work
/// uses the assembly's static <c>DomBridge</c> helpers. The shared <c>value</c> property stays a
/// bridge form-control handler that delegates its select branch to <see cref="GetValue"/>/
/// <see cref="SetValue"/>.
/// </summary>
internal sealed class SelectBinding(ISelectHost host)
{
    private readonly ISelectHost _host = host;

    /// <summary>Installs the select/option interface members on <paramref name="obj"/> for
    /// <paramref name="element"/> according to its <paramref name="tag"/>.</summary>
    internal void Install(JSObject obj, DomElement element, string tag)
    {
        if (tag == "select")
        {
            obj.FastAddValue("add",
                new DomFunction((in a) => Add(element, in a), "add", 2),
                JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddProperty("options",
                new DomFunction((in _) => GetOptions(element), "get options"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("selectedIndex",
                new DomFunction((in _) => new JSNumber(GetSelectedIndex(element)), "get selectedIndex"),
                new DomFunction((in a) => SetSelectedIndexCallback(element, in a), "set selectedIndex"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("size",
                new DomFunction((in _) => GetSize(element), "get size"),
                new DomFunction((in a) => SetSize(element, in a), "set size"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        if (tag == "option")
        {
            obj.FastAddProperty("defaultSelected",
                new DomFunction((in _) => _host.GetOptionDefaultSelected(element) ? JSBoolean.True : JSBoolean.False, "get defaultSelected"),
                new DomFunction((in a) => SetDefaultSelected(element, in a), "set defaultSelected"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }
    }

    // -------- Callbacks --------

    private JSValue Add(DomElement element, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject optObj)
            return JSUndefined.Value;
        var optEl = _host.FindDomElementByJSObject(optObj);
        if (optEl == null)
            return JSUndefined.Value;

        DomElement? refEl = null;
        if (a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined && a[1] is JSObject refObj)
            refEl = _host.FindDomElementByJSObject(refObj);

        // optEl.Remove() detaches; the insert/append below reattaches in one canonical op. The prior
        // SetParent(optEl, element) appended at the end first, so a ref-node insert then re-moved it.
        optEl.Remove();
        if (refEl != null)
        {
            var idx = DomBridge.ChildIndexOf(element, refEl);
            if (idx >= 0)
                DomBridge.InsertChildAt(element, idx, optEl);
            else
                element.AppendChild(optEl);
        }
        else
            element.AppendChild(optEl);
        return JSUndefined.Value;
    }

    private JSValue GetOptions(DomElement element)
    {
        var opts = new List<JSValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "option", StringComparison.OrdinalIgnoreCase))
                opts.Add(_host.ToJSObject(c));
        var arr = new JSArray(opts);
        arr.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(opts.Count), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        return arr;
    }

    private JSValue SetSelectedIndexCallback(DomElement element, in Arguments a)
    {
        var index = a.Length == 0 ? -1 : (int)Math.Truncate(a[0].DoubleValue);
        SetSelectedIndex(element, index);
        return JSUndefined.Value;
    }

    private static JSValue GetSize(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "size", out var rawSize) && int.TryParse(rawSize, out var parsedSize) && parsedSize > 0)
            return new JSNumber(parsedSize);
        return new JSNumber(0);
    }

    private static JSValue SetSize(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var size = (int)Math.Truncate(a[0].DoubleValue);
        if (size > 0)
            DomBridge.SetAttr(element, "size", size.ToString());
        else
            DomBridge.RemoveAttr(element, "size");
        return JSUndefined.Value;
    }

    private JSValue SetDefaultSelected(DomElement element, in Arguments a)
    {
        _host.SetOptionDefaultSelected(element, a.Length > 0 && a[0].BooleanValue);
        return JSUndefined.Value;
    }

    // -------- Select algorithms (moved out of LayoutMetrics; never used by layout) --------

    private static List<DomElement> CollectSelectOptions(DomElement element)
    {
        var options = new List<DomElement>();
        foreach (var child in DomBridge.ChildElements(element).Where(c => !DomBridge.IsText(c)))
        {
            if (string.Equals(child.TagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                options.Add(child);
                continue;
            }

            options.AddRange(CollectSelectOptions(child));
        }

        return options;
    }

    /// <summary>The select's current selected index — the dirty index if set, else the first
    /// selected/default-selected option, else 0 (or -1 when there are no options).</summary>
    internal int GetSelectedIndex(DomElement element)
    {
        var options = CollectSelectOptions(element);
        if (options.Count == 0)
            return -1;

        if (_host.TryGetSelectedIndex(element, out var dirtyIndex))
            return dirtyIndex >= 0 && dirtyIndex < options.Count ? dirtyIndex : -1;

        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            if (DomBridge.HasAttr(option, "selected") || _host.GetOptionDefaultSelected(option))
                return index;
        }

        return 0;
    }

    internal void SetSelectedIndex(DomElement element, int index)
    {
        var options = CollectSelectOptions(element);
        if (options.Count == 0)
        {
            _host.SetSelectedIndex(element, -1);
            return;
        }

        if (index < 0 || index >= options.Count)
            index = -1;

        _host.SetSelectedIndex(element, index);
    }

    /// <summary>The select's current value — the selected option's IDL value, else its
    /// <c>value</c> attribute, else its text content.</summary>
    internal string GetValue(DomElement element)
    {
        var options = CollectSelectOptions(element);
        var selectedIndex = GetSelectedIndex(element);
        if (selectedIndex < 0 || selectedIndex >= options.Count)
            return string.Empty;

        var option = options[selectedIndex];
        if (_host.TryGetOptionValue(option, out var stringValue))
            return stringValue;

        if (DomBridge.TryGetAttribute(option, "value", out var attrValue))
            return attrValue;

        return DomBridge.GetElementTextContent(option);
    }

    /// <summary>Selects the first option whose value matches <paramref name="value"/> (or clears the
    /// selection when none match).</summary>
    internal void SetValue(DomElement element, string value)
    {
        var options = CollectSelectOptions(element);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var optionValue = DomBridge.TryGetAttribute(option, "value", out var attrValue)
                ? attrValue
                : DomBridge.GetElementTextContent(option);
            if (string.Equals(optionValue, value, StringComparison.Ordinal))
            {
                _host.SetSelectedIndex(element, index);
                return;
            }
        }

        _host.SetSelectedIndex(element, -1);
    }
}
