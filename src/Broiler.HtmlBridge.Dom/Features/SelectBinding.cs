using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

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
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): every member is minted by the
/// realm and every body runs on a <see cref="JsCall"/>, so the migrated half of this file names no
/// engine type.
/// </para>
/// <para>
/// <b>The one engine-typed member is an adapter, and it is pinned from outside.</b>
/// <c>DomBridge/ElementInterfaces.cs</c> installs these members onto an engine wrapper it holds and
/// is not migrated, so <see cref="Install(JSObject, DomElement, string)"/> takes that wrapper and
/// hands it on through <see cref="Runtime.JsInterop"/> — a cast, not a conversion, because a JSEAL
/// object handle carries the engine's own object. When that installer migrates, the adapter is
/// deleted and its caller passes the handle it already has.
/// </para>
/// </remarks>
internal sealed class SelectBinding(ISelectHost host)
{
    private readonly ISelectHost _host = host;

    /// <summary>
    /// Engine-typed adapter for <c>DomBridge/ElementInterfaces.cs</c>, which still holds the element
    /// wrapper as an engine object. See the remarks on this class.
    /// </summary>
    internal void Install(JSObject obj, DomElement element, string tag) =>
        Install(Runtime.JsInterop.FromEngineObject(obj), element, tag);

    /// <summary>Installs the select/option interface members on <paramref name="obj"/> for
    /// <paramref name="element"/> according to its <paramref name="tag"/>.</summary>
    internal void Install(JsValue obj, DomElement element, string tag)
    {
        var realm = _host.Realm;

        if (tag == "select")
        {
            realm.DefineValue(obj, "add",
                realm.NewMethod("add", (in call) => Add(element, in call), 2));
            realm.DefineAccessor(obj, "options",
                (in call) => GetOptions(call.Realm, element), null);
            realm.DefineAccessor(obj, "selectedIndex",
                (in _) => JsValue.Number(GetSelectedIndex(element)),
                (in call) => SetSelectedIndexCallback(element, in call));
            realm.DefineAccessor(obj, "size",
                (in _) => GetSize(element),
                (in call) => SetSize(element, in call));
        }

        if (tag == "option")
        {
            realm.DefineAccessor(obj, "defaultSelected",
                (in _) => JsValue.Boolean(_host.GetOptionDefaultSelected(element)),
                (in call) => SetDefaultSelected(element, in call));
        }
    }

    // -------- Callbacks --------

    private JsValue Add(DomElement element, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.Undefined;
        var optEl = _host.FindElement(call[0]);
        if (optEl == null)
            return JsValue.Undefined;

        // IsObject is the whole of the old `a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined &&
        // a[1] is JSObject`: a missing, null or undefined argument is not an object, so the three
        // guards collapse into the one question they were all asking.
        DomElement? refEl = null;
        if (call[1].IsObject)
            refEl = _host.FindElement(call[1]);

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
        return JsValue.Undefined;
    }

    private JsValue GetOptions(IJsRealm realm, DomElement element)
    {
        var opts = new List<JsValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "option", StringComparison.OrdinalIgnoreCase))
                opts.Add(_host.WrapNode(c));

        // The array's own `length` is replaced by an accessor over the snapshot the array was built
        // from, exactly as before: the two agree, and the accessor is what the site has always
        // installed.
        var arr = realm.NewArray([.. opts]);
        realm.DefineAccessor(arr, "length", (in _) => JsValue.Number(opts.Count), null);
        return arr;
    }

    private JsValue SetSelectedIndexCallback(DomElement element, in JsCall call)
    {
        // ToNumber, not the handle's inline reading: `select.selectedIndex = "2"` is a string a page
        // may well write, and the ECMAScript coercion is what it observes.
        var index = call.Length == 0 ? -1 : (int)Math.Truncate(call.Realm.ToNumber(call[0]));
        SetSelectedIndex(element, index);
        return JsValue.Undefined;
    }

    private static JsValue GetSize(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "size", out var rawSize) && int.TryParse(rawSize, out var parsedSize) && parsedSize > 0)
            return JsValue.Number(parsedSize);
        return JsValue.Number(0);
    }

    private static JsValue SetSize(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var size = (int)Math.Truncate(call.Realm.ToNumber(call[0]));
        if (size > 0)
            DomBridge.SetAttr(element, "size", size.ToString());
        else
            DomBridge.RemoveAttr(element, "size");
        return JsValue.Undefined;
    }

    private JsValue SetDefaultSelected(DomElement element, in JsCall call)
    {
        _host.SetOptionDefaultSelected(element, call[0].AsBoolean);
        return JsValue.Undefined;
    }

    // -------- Select algorithms (moved out of LayoutMetrics; never used by layout) --------

    internal static List<DomElement> CollectSelectOptions(DomElement element)
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
