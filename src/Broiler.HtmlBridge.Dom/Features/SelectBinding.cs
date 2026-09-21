using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTMLSelectElement / HTMLOptionElement feature binding —
/// <c>select.add</c>/<c>options</c>/<c>selectedIndex</c>/<c>size</c> and its
/// value resolution, plus <c>option.defaultSelected</c> and <c>option.text</c>. The option-collection,
/// selected-index and value algorithms live here; the per-element form-control state
/// they touch is reached through the narrow <see cref="ISelectHost"/> contract as named primitives,
/// and neutral tree/attribute work uses the assembly's static <c>DomBridge</c> helpers. The shared
/// <c>value</c> property stays a bridge form-control handler that delegates its select branch to
/// <see cref="GetValue"/>/<see cref="SetValue"/>.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): every member is minted by the
/// realm and every body runs on a <see cref="JsCall"/>, so no part of this file names an engine
/// type.
/// </para>
/// </remarks>
internal sealed class SelectBinding(ISelectHost host)
{
    private readonly ISelectHost _host = host;

    /// <summary>Installs the select/option interface members on <paramref name="obj"/> for
    /// <paramref name="element"/> according to its <paramref name="tag"/>.</summary>
    internal void Install(JsValue obj, DomElement element, string tag)
    {
        var realm = _host.Realm;

        if (tag == "select")
        {
            realm.DefineMethod(obj, "add", 2, (in call) => Add(element, in call));
            realm.DefineAccessor(obj, "options",
                (in call) => GetOptions(call.Realm, element), null);
            realm.DefineAccessor(obj, "selectedIndex",
                (in _) => JsValue.Number(GetSelectedIndex(element)),
                (in call) => SetSelectedIndexCallback(element, in call));
            realm.DefineAccessor(obj, "size",
                (in _) => GetSize(element),
                (in call) => SetSize(element, in call));
        }

        // HTMLOptionElement is an HTML-namespace element whose local name is exactly "option": an SVG
        // <option> or createElementNS(xhtml, "OPTION") is not one, so neither gets these members.
        if (IsHtmlOption(element))
        {
            realm.DefineAccessor(obj, "defaultSelected",
                (in _) => JsValue.Boolean(_host.GetOptionDefaultSelected(element)),
                (in call) => SetDefaultSelected(element, in call));
            realm.DefineAccessor(obj, "text",
                (in _) => JsValue.String(OptionText(element)),
                (in call) => SetText(element, in call));
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

        DomElement? refEl = null;
        if (call[1].IsObject)
            refEl = _host.FindElement(call[1]);

        HtmlSelectQueries.AddOption(element, optEl, refEl);
        return JsValue.Undefined;
    }

    private JsValue GetOptions(IJsRealm realm, DomElement element)
    {
        var opts = new List<JsValue>();
        foreach (var c in DomBridgeUtils.ChildElements(element))
            if (string.Equals(c.TagName, "option", StringComparison.OrdinalIgnoreCase))
                opts.Add(_host.WrapNode(c));

        var arr = realm.NewArray([.. opts]);
        realm.DefineAccessor(arr, "length", (in _) => JsValue.Number(opts.Count), null);
        return arr;
    }

    private JsValue SetSelectedIndexCallback(DomElement element, in JsCall call)
    {
        var index = call.Length == 0 ? -1 : (int)Math.Truncate(call.Realm.ToNumber(call[0]));
        SetSelectedIndex(element, index);
        return JsValue.Undefined;
    }

    private static JsValue GetSize(DomElement element) =>
        JsValue.Number(HtmlSelectQueries.GetSize(element));

    private static JsValue SetSize(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var size = (int)Math.Truncate(call.Realm.ToNumber(call[0]));
        if (size > 0)
            DomBridgeUtils.SetAttr(element, "size", size.ToString());
        else
            DomBridgeUtils.RemoveAttr(element, "size");
        return JsValue.Undefined;
    }

    private JsValue SetDefaultSelected(DomElement element, in JsCall call)
    {
        _host.SetOptionDefaultSelected(element, call[0].AsBoolean);
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>option.text = value</c>: HTML §4.10.10's "string replace all", which is the canonical
    /// <see cref="DomNode.TextContent"/> replace-all — one child-list record and at most one text node
    /// (none for the empty string).
    /// </summary>
    private static JsValue SetText(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.TypeError,
                "Failed to set the 'text' property on 'HTMLOptionElement': 1 argument required, but only 0 present.");
        element.TextContent = call.Realm.ToJsString(call[0]);
        return JsValue.Undefined;
    }

    /// <summary>
    /// An option's <c>text</c> (HTML §4.10.10): delegates to canonical <see cref="HtmlSelectQueries.GetOptionText"/>.
    /// </summary>
    internal static string OptionText(DomElement option) => HtmlSelectQueries.GetOptionText(option);

    /// <summary>Whether <paramref name="element"/> is an HTMLOptionElement.</summary>
    internal static bool IsHtmlOption(DomElement element) => HtmlSelectQueries.IsHtmlOption(element);

    // -------- Select algorithms --------

    internal static List<DomElement> CollectSelectOptions(DomElement element) =>
        HtmlSelectQueries.GetOptions(element).ToList();

    /// <summary>The select's current selected index — the dirty index if set, else the first
    /// selected/default-selected option, else 0 (or -1 when there are no options).</summary>
    internal int GetSelectedIndex(DomElement element)
    {
        int? dirtyIndex = _host.TryGetSelectedIndex(element, out var idx) ? idx : null;
        return HtmlSelectQueries.ResolveSelectedIndex(element, dirtyIndex, _host.GetOptionDefaultSelected);
    }

    internal void SetSelectedIndex(DomElement element, int index)
    {
        var count = HtmlSelectQueries.GetOptions(element).Count;
        if (count == 0 || index < 0 || index >= count)
            index = -1;

        _host.SetSelectedIndex(element, index);
    }

    /// <summary>The select's current value — the selected option's IDL value, else its
    /// <c>value</c> attribute, else its <see cref="OptionText"/>.</summary>
    internal string GetValue(DomElement element)
    {
        int? dirtyIndex = _host.TryGetSelectedIndex(element, out var idx) ? idx : null;
        return HtmlSelectQueries.ResolveSelectValue(
            element,
            dirtyIndex,
            _host.GetOptionDefaultSelected,
            opt => _host.TryGetOptionValue(opt, out var val) ? val : null);
    }

    /// <summary>Selects the first option whose value matches <paramref name="value"/> (or clears the
    /// selection when none match).</summary>
    internal void SetValue(DomElement element, string value)
    {
        var matchingIndex = HtmlSelectQueries.FindOptionIndexByValue(
            element,
            value,
            opt => _host.TryGetOptionValue(opt, out var val) ? val : null);
        _host.SetSelectedIndex(element, matchingIndex);
    }
}
