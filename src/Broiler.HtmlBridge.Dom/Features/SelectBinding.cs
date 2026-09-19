using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTMLSelectElement / HTMLOptionElement feature binding (HtmlBridge complexity-reduction
/// roadmap Phase 3, P3.8) — <c>select.add</c>/<c>options</c>/<c>selectedIndex</c>/<c>size</c> and its
/// value resolution, plus <c>option.defaultSelected</c> and <c>option.text</c>. The option-collection,
/// selected-index and value algorithms (previously scattered as static helpers in
/// <c>LayoutMetrics.cs</c>, though never used by layout) move here; the per-element form-control state
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
/// <para>
/// The file carried one engine-typed adapter until 5282d02, and it was pinned by its caller rather
/// than by anything here: <c>DomBridge/NodeInterfaces.cs</c> installed these members onto an engine
/// wrapper it held, so <c>Install</c> took that wrapper and handed it on through
/// <see cref="Runtime.JsInterop"/>. That file passes the handle it already has, so there is one
/// installer again.
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

        // IsObject is the whole of the old four-part guard — "supplied, not null, not undefined, and
        // an object": a missing, null or undefined argument is not an object, so all four collapse
        // into the one question they were asking.
        DomElement? refEl = null;
        if (call[1].IsObject)
            refEl = _host.FindElement(call[1]);

        // optEl.Remove() detaches; the insert/append below reattaches in one canonical op. The prior
        // SetParent(optEl, element) appended at the end first, so a ref-node insert then re-moved it.
        optEl.Remove();
        if (refEl != null)
        {
            var idx = DomBridgeUtils.ChildIndexOf(element, refEl);
            if (idx >= 0)
                DomBridgeUtils.InsertChildAt(element, idx, optEl);
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
        foreach (var c in DomBridgeUtils.ChildElements(element))
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
        if (DomBridgeUtils.TryGetAttribute(element, "size", out var rawSize) && int.TryParse(rawSize, out var parsedSize) && parsedSize > 0)
            return JsValue.Number(parsedSize);
        return JsValue.Number(0);
    }

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
    /// <remarks>
    /// <c>text</c> is a plain <c>DOMString</c>, not a nullable one, so the realm's <c>ToString</c> runs
    /// first and <c>option.text = null</c> writes the string <c>"null"</c>, as <c>script.text</c> does —
    /// unlike <c>textContent</c>, whose <c>null</c> empties the element. A setter reached with no
    /// argument at all (<c>descriptor.set.call(option)</c>) is WebIDL's arity TypeError, and the
    /// children stay.
    /// </remarks>
    private static JsValue SetText(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.TypeError,
                "Failed to set the 'text' property on 'HTMLOptionElement': 1 argument required, but only 0 present.");
        element.TextContent = call.Realm.ToJsString(call[0]);
        return JsValue.Undefined;
    }

    /// <summary>
    /// An option's <c>text</c> (HTML §4.10.10): its descendant text nodes' data in tree order, leaving out
    /// every text node inside a descendant HTML or SVG <c>script</c>, with ASCII whitespace stripped and
    /// collapsed. It is also what an option without a <c>value</c> attribute is worth, so
    /// <see cref="GetValue"/>, <see cref="SetValue"/> and <c>option.value</c> read it too.
    /// </summary>
    /// <remarks>
    /// "Strip and collapse ASCII whitespace" is tab, LF, FF, CR and space only: a no-break space is text
    /// and stays where it is. The whitespace is the text's, not the tree's, so a run that spans two
    /// nodes (<c>a &lt;b&gt; b&lt;/b&gt;</c>) collapses like any other.
    /// </remarks>
    internal static string OptionText(DomElement option)
    {
        var builder = new System.Text.StringBuilder();
        AppendNonScriptText(option, builder);
        var raw = builder.ToString();

        var text = new System.Text.StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (c is '\t' or '\n' or '\f' or '\r' or ' ')
            {
                pendingSpace = text.Length > 0;
                continue;
            }

            if (pendingSpace)
                text.Append(' ');
            pendingSpace = false;
            text.Append(c);
        }

        return text.ToString();
    }

    private static void AppendNonScriptText(DomNode node, System.Text.StringBuilder text)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is DomText textNode)
                text.Append(textNode.Data);
            // The bridge parents a shadow host's #shadow-root into the host's child list, but a shadow
            // tree is not the option's descendant (DOM §4.2.2), so none of its text is the option's.
            else if (child is DomElement element && !IsHtmlOrSvgScript(element) &&
                     !string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                AppendNonScriptText(element, text);
        }
    }

    private static bool IsHtmlOrSvgScript(DomElement element) =>
        string.Equals(element.LocalName, "script", StringComparison.Ordinal) &&
        element.NamespaceUri is DomNamespaces.Html or DomNamespaces.Svg;

    /// <summary>Whether <paramref name="element"/> is an HTMLOptionElement: HTML namespace, local name
    /// exactly <c>option</c>.</summary>
    internal static bool IsHtmlOption(DomElement element) =>
        string.Equals(element.LocalName, "option", StringComparison.Ordinal) &&
        element.NamespaceUri is DomNamespaces.Html;

    // -------- Select algorithms (moved out of LayoutMetrics; never used by layout) --------

    internal static List<DomElement> CollectSelectOptions(DomElement element)
    {
        var options = new List<DomElement>();
        foreach (var child in DomBridgeUtils.ChildElements(element).Where(c => !DomBridgeUtils.IsText(c)))
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
            if (DomBridgeUtils.HasAttr(option, "selected") || _host.GetOptionDefaultSelected(option))
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
    /// <c>value</c> attribute, else its <see cref="OptionText"/>.</summary>
    internal string GetValue(DomElement element)
    {
        var options = CollectSelectOptions(element);
        var selectedIndex = GetSelectedIndex(element);
        if (selectedIndex < 0 || selectedIndex >= options.Count)
            return string.Empty;

        var option = options[selectedIndex];
        if (_host.TryGetOptionValue(option, out var stringValue))
            return stringValue;

        if (DomBridgeUtils.TryGetAttribute(option, "value", out var attrValue))
            return attrValue;

        return OptionText(option);
    }

    /// <summary>Selects the first option whose value matches <paramref name="value"/> (or clears the
    /// selection when none match).</summary>
    internal void SetValue(DomElement element, string value)
    {
        var options = CollectSelectOptions(element);
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            var optionValue = DomBridgeUtils.TryGetAttribute(option, "value", out var attrValue)
                ? attrValue
                : OptionText(option);
            if (string.Equals(optionValue, value, StringComparison.Ordinal))
            {
                _host.SetSelectedIndex(element, index);
                return;
            }
        }

        _host.SetSelectedIndex(element, -1);
    }
}
