using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTMLSelectElement / HTMLOptionElement feature binding — <c>select.options</c>,
/// <c>selectedOptions</c>, <c>selectedIndex</c>, <c>type</c>, <c>multiple</c>, <c>add</c>, <c>remove</c>,
/// <c>length</c>, <c>item</c>, <c>namedItem</c> and <c>size</c>, the options' own <c>add</c>, <c>remove</c>,
/// <c>length</c> and <c>selectedIndex</c>, and <c>option.selected</c>, <c>index</c>, <c>defaultSelected</c> and
/// <c>text</c> -- with HTML's selectedness: each option is selected or not, a select-one keeps one of them,
/// a multiple select any number. The option-collection and selectedness algorithms live here; the
/// per-option state they keep is reached through the narrow <see cref="ISelectHost"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>A select had one selected index.</b> A multiple select held one option, submitted one, and
/// <c>option.selected</c> did not exist -- nor did <c>select.options</c>, whose read threw, a
/// <c>TypeError</c> from a <c>length</c> redefined on an array. A select-one with two options marked
/// <c>selected</c> chose the first, where HTML and Chromium choose the last.
/// </para>
/// <para>
/// <b>As HTML and Chromium have it, measured</b>. Until a script or the user
/// changes it, a select is as its markup says: each option marked <c>selected</c>, of which a select-one
/// keeps the last, or with none marked, a drop-down its first option that is not disabled. From the first
/// change on, each option's selectedness and dirtiness are held (<see cref="ISelectHost"/>), and changes to
/// the tree keep a select-one at one selected option (<see cref="OnOptionsInserted"/>,
/// <see cref="OnOptionsRemoved"/>, <see cref="OnSelectedAttributeChanged"/>). Taking away
/// <c>multiple</c> keeps the first selected option, as Chromium does; HTML says nothing of it.
/// </para>
/// <para>
/// <b>Nothing took an option out or put empty ones in.</b> <c>select.remove(1)</c> was the
/// <c>ChildNode.remove()</c> every element has, so it removed the select itself; <c>options.remove()</c>
/// did not exist, nor did <c>select.length</c>; and <c>options.length = 0</c> made an own property of the
/// collection that read 0 from then on, the options all still there. As Chromium has them (measured): an index is a WebIDL <c>long</c>, nothing happens out of range, and a
/// length past 100,000 is refused.
/// </para>
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

            // remove() is ChildNode's and takes the select out; remove(index) takes an option out.
            realm.DefineMethod(obj, "remove", 0, (in call) =>
            {
                if (call.Length == 0)
                {
                    var parent = element.ParentElement;
                    element.Remove();
                    if (parent is not null)
                        _host.InvalidateStyleScope(parent);
                }
                else
                {
                    RemoveOptionAt(element, ToWebIdlLong(call.Realm, call[0]));
                }

                return JsValue.Undefined;
            });
            realm.DefineAccessor(obj, "length",
                (in _) => JsValue.Number(OptionsOf(element).Count),
                (in call) =>
                {
                    SetLength(element, call.Realm, call.Length > 0 ? call[0] : JsValue.Undefined);
                    return JsValue.Undefined;
                });
            realm.DefineAccessor(obj, "options",
                (in _) => OptionsCollection(element), null);
            realm.DefineAccessor(obj, "selectedOptions",
                (in _) => _host.LiveCollection(element, "selectedOptions", () => SelectedOptions(element).Select(_host.WrapNode).ToList()), null);
            realm.DefineAccessor(obj, "selectedIndex",
                (in _) => JsValue.Number(GetSelectedIndex(element)),
                (in call) => SetSelectedIndexCallback(element, in call));
            realm.DefineAccessor(obj, "type",
                (in _) => JsValue.String(IsMultiple(element) ? "select-multiple" : "select-one"), null);
            realm.DefineAccessor(obj, "multiple",
                (in _) => JsValue.Boolean(IsMultiple(element)),
                (in call) =>
                {
                    if (call.Length > 0 && call.Realm.ToBoolean(call[0]))
                        DomBridgeUtils.SetAttr(element, "multiple", string.Empty);
                    else
                        DomBridgeUtils.RemoveAttr(element, "multiple");
                    return JsValue.Undefined;
                });
            realm.DefineMethod(obj, "item", 1, (in call) => Item(element, in call));
            realm.DefineMethod(obj, "namedItem", 1, (in call) => NamedItem(element, in call));
            realm.DefineAccessor(obj, "size",
                (in _) => GetSize(element),
                (in call) => SetSize(element, in call));
        }

        // HTMLOptionElement is an HTML-namespace element whose local name is exactly "option": an SVG
        // <option> or createElementNS(xhtml, "OPTION") is not one, so neither gets these members.
        if (IsHtmlOption(element))
        {
            realm.DefineAccessor(obj, "selected",
                (in _) => JsValue.Boolean(IsOptionSelected(element)),
                (in call) =>
                {
                    SetOptionSelected(element, call.Length > 0 && call.Realm.ToBoolean(call[0]));
                    return JsValue.Undefined;
                });
            realm.DefineAccessor(obj, "index",
                (in _) => JsValue.Number(SelectOf(element) is { } select ? Math.Max(0, OptionsOf(select).IndexOf(element)) : 0), null);
            realm.DefineAccessor(obj, "defaultSelected",
                (in _) => JsValue.Boolean(DomBridgeUtils.HasAttr(element, "selected")),
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

        // The reference is an element, or an index into the options, before which the new one goes.
        DomElement? refEl = null;
        if (call[1].IsObject)
            refEl = _host.FindElement(call[1]);
        else if (call[1].IsNumber && OptionsOf(element) is var options &&
                 (int)call[1].AsNumber is var at && at >= 0 && at < options.Count)
            refEl = options[at];

        HtmlSelectQueries.AddOption(element, optEl, refEl);
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>select.options</c>: one live <c>HTMLOptionsCollection</c> per select -- its options, with
    /// <c>add()</c> and <c>selectedIndex</c> of its own -- so <c>select.options === select.options</c>.
    /// </summary>
    private JsValue OptionsCollection(DomElement select) =>
        _host.LiveCollection(select, "options", () => OptionsOf(select).Select(_host.WrapNode).ToList(), collection =>
        {
            var realm = _host.Realm;
            realm.DefineMethod(collection, "add", 2, (in call) => Add(select, in call));
            realm.DefineMethod(collection, "remove", 1, (in call) =>
            {
                if (call.Length == 0)
                    throw call.Realm.Error(JsErrorKind.TypeError,
                        "Failed to execute 'remove' on 'HTMLOptionsCollection': 1 argument required, but only 0 present.");

                RemoveOptionAt(select, ToWebIdlLong(call.Realm, call[0]));
                return JsValue.Undefined;
            });
            realm.DefineAccessor(collection, "selectedIndex",
                (in _) => JsValue.Number(GetSelectedIndex(select)),
                (in call) => SetSelectedIndexCallback(select, in call));
        },
        // options.length = n: the collection answers length itself, so the assignment comes here.
        (name, value) =>
        {
            if (name != "length")
                return false;

            SetLength(select, _host.Realm, value);
            return true;
        });

    /// <summary><c>remove(index)</c>: the option at <paramref name="index"/> taken out of its parent; nothing out of range.</summary>
    private void RemoveOptionAt(DomElement select, int index)
    {
        var options = OptionsOf(select);
        if (index < 0 || index >= options.Count)
            return;

        options[index].Remove();
        _host.InvalidateStyleScope(select);
    }

    // HTML's limit, and Chromium's: past it the length setter does nothing (measured).
    private const uint MaxOptionsLength = 100_000;

    /// <summary>
    /// <c>options.length = value</c> and <c>select.length = value</c>: as many options as <paramref name="value"/>
    /// says, a WebIDL <c>unsigned long</c> -- the last ones taken out of their parents, or new empty
    /// <c>option</c> elements appended to the select in one insertion; nothing past 100,000.
    /// </summary>
    private void SetLength(DomElement select, IJsRealm realm, JsValue value)
    {
        var length = ToWebIdlUnsignedLong(realm, value);
        if (length > MaxOptionsLength)
            return;

        var options = OptionsOf(select);
        if (length < options.Count)
        {
            for (var i = options.Count - 1; i >= (int)length; i--)
                options[i].Remove();
        }
        else if (length > options.Count)
        {
            var document = select.OwnerDocument;
            var fragment = document.CreateDocumentFragment();
            for (var i = options.Count; i < (int)length; i++)
                fragment.AppendChild(document.CreateElement("option"));
            select.AppendChild(fragment);
        }
        else
        {
            return;
        }

        _host.InvalidateStyleScope(select);
    }

    /// <summary>WebIDL's conversion to <c>long</c>: NaN and the infinities 0, the rest truncated and wrapped to 32 bits.</summary>
    private static int ToWebIdlLong(IJsRealm realm, JsValue value) => unchecked((int)ToWebIdlUnsignedLong(realm, value));

    /// <summary>WebIDL's conversion to <c>unsigned long</c>: NaN and the infinities 0, the rest truncated modulo 2^32.</summary>
    private static uint ToWebIdlUnsignedLong(IJsRealm realm, JsValue value)
    {
        var number = realm.ToNumber(value);
        if (double.IsNaN(number) || double.IsInfinity(number))
            return 0;

        var wrapped = Math.Truncate(number) % 4294967296.0;
        if (wrapped < 0)
            wrapped += 4294967296.0;
        return (uint)wrapped;
    }

    private JsValue Item(DomElement select, in JsCall call)
    {
        var options = OptionsOf(select);
        var index = call.Length > 0 ? call.Realm.ToNumber(call[0]) : 0;
        return index >= 0 && index < options.Count ? _host.WrapNode(options[(int)index]) : JsValue.Null;
    }

    private JsValue NamedItem(DomElement select, in JsCall call)
    {
        var name = call.Length > 0 ? call.Realm.ToJsString(call[0]) : "undefined";
        foreach (var option in OptionsOf(select))
        {
            if (option.Id == name || DomBridgeUtils.TryGetAttribute(option, "name", out var optionName) && optionName == name)
                return _host.WrapNode(option);
        }

        return JsValue.Null;
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

    /// <summary>
    /// <c>option.defaultSelected = value</c>: the <c>selected</c> attribute it reflects, which selects an
    /// option whose selectedness nothing has changed (<see cref="OnSelectedAttributeChanged"/>).
    /// </summary>
    private static JsValue SetDefaultSelected(DomElement element, in JsCall call)
    {
        if (call.Length > 0 && call.Realm.ToBoolean(call[0]))
            DomBridgeUtils.SetAttr(element, "selected", string.Empty);
        else
            DomBridgeUtils.RemoveAttr(element, "selected");
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

    // -------- The options and their selectedness --------

    /// <summary>The select's list of options, in tree order.</summary>
    internal static List<DomElement> OptionsOf(DomElement select) =>
        HtmlSelectQueries.GetOptions(select).ToList();

    /// <summary>The same list; kept for the callers that name it so.</summary>
    internal static List<DomElement> CollectSelectOptions(DomElement element) => OptionsOf(element);

    /// <summary>The select whose list of options <paramref name="option"/> is in, or null.</summary>
    internal static DomElement? SelectOf(DomElement option)
    {
        for (var node = option.ParentNode; node is not null; node = node.ParentNode)
        {
            if (node is DomElement element && element.TagName.Equals("select", StringComparison.OrdinalIgnoreCase))
                return element;
        }

        return null;
    }

    internal static bool IsMultiple(DomElement select) => DomBridgeUtils.HasAttr(select, "multiple");

    /// <summary>HTML's display size: the <c>size</c> attribute, else 4 for a multiple select and 1 for one that is not.</summary>
    private static int DisplaySize(DomElement select, bool multiple)
    {
        var size = HtmlSelectQueries.GetSize(select);
        return size > 0 ? size : multiple ? 4 : 1;
    }

    /// <summary>Whether <paramref name="option"/> is disabled: by its own attribute or by its optgroup's.</summary>
    private static bool IsOptionDisabled(DomElement option) =>
        DomBridgeUtils.HasAttr(option, "disabled") ||
        option.ParentNode is DomElement parent && parent.TagName.Equals("optgroup", StringComparison.OrdinalIgnoreCase) &&
        DomBridgeUtils.HasAttr(parent, "disabled");

    /// <summary>Each of <paramref name="select"/>'s options' selectedness, in the order of its options.</summary>
    internal bool[] Selectedness(DomElement select)
    {
        var options = OptionsOf(select);
        if (!_host.IsSelectHeld(select))
            return MarkupSelectedness(select, options, IsMultiple(select));

        var selected = new bool[options.Count];
        for (var i = 0; i < options.Count; i++)
            selected[i] = _host.TryGetOptionState(options[i], out var held, out _) ? held : DomBridgeUtils.HasAttr(options[i], "selected");
        return selected;
    }

    /// <summary>
    /// What the markup makes selected: the options marked <c>selected</c>, of which a select-one keeps the
    /// last; and with none, a select-one shown as a drop-down its first option that is not disabled.
    /// </summary>
    private static bool[] MarkupSelectedness(DomElement select, List<DomElement> options, bool multiple)
    {
        var selected = new bool[options.Count];
        for (var i = 0; i < options.Count; i++)
            selected[i] = DomBridgeUtils.HasAttr(options[i], "selected");

        if (!multiple)
            KeepOneSelected(select, options, selected, keep: -1, multiple);
        return selected;
    }

    /// <summary>
    /// HTML's selectedness setting algorithm for a select-one: of the selected options only
    /// <paramref name="keep"/>, or with none named the last, stays selected; and when none is, a drop-down
    /// selects its first option that is not disabled.
    /// </summary>
    private static void KeepOneSelected(DomElement select, List<DomElement> options, bool[] selected, int keep, bool multiple)
    {
        if (keep < 0)
            keep = Array.LastIndexOf(selected, true);
        for (var i = 0; i < selected.Length; i++)
            selected[i] = i == keep;

        if (keep < 0 && DisplaySize(select, multiple) == 1)
        {
            var first = options.FindIndex(static option => !IsOptionDisabled(option));
            if (first >= 0)
                selected[first] = true;
        }
    }

    /// <summary>
    /// Holds <paramref name="select"/>'s selectedness from now on, as its markup made it -- read with
    /// <paramref name="multiple"/>, for the select a <c>multiple</c> just left or joined.
    /// </summary>
    private void Hold(DomElement select, bool? multiple = null)
    {
        if (_host.IsSelectHeld(select))
            return;

        var options = OptionsOf(select);
        var selected = MarkupSelectedness(select, options, multiple ?? IsMultiple(select));
        for (var i = 0; i < options.Count; i++)
            _host.SetOptionState(options[i], selected[i], dirty: false);
        _host.HoldSelect(select);
    }

    /// <summary>Writes <paramref name="selected"/> as <paramref name="select"/>'s options' selectedness, the changed ones dirty when <paramref name="dirty"/> says so.</summary>
    private void Write(DomElement select, List<DomElement> options, bool[] selected, bool dirty)
    {
        for (var i = 0; i < options.Count; i++)
        {
            var wasDirty = _host.TryGetOptionState(options[i], out var was, out var heldDirty) && heldDirty;
            _host.SetOptionState(options[i], selected[i], wasDirty || dirty && was != selected[i]);
        }

        _host.NoteSelectionChanged(select);
    }

    /// <summary>The select's selected options, in order.</summary>
    internal List<DomElement> SelectedOptions(DomElement select)
    {
        var options = OptionsOf(select);
        var selected = Selectedness(select);
        var result = new List<DomElement>();
        for (var i = 0; i < options.Count; i++)
        {
            if (selected[i])
                result.Add(options[i]);
        }

        return result;
    }

    /// <summary><c>option.selected</c>: its selectedness in its select, or for one in none, what it holds or its markup says.</summary>
    internal bool IsOptionSelected(DomElement option)
    {
        if (SelectOf(option) is { } select)
        {
            var index = OptionsOf(select).IndexOf(option);
            return index >= 0 && Selectedness(select)[index];
        }

        return _host.TryGetOptionState(option, out var held, out _) ? held : DomBridgeUtils.HasAttr(option, "selected");
    }

    /// <summary>
    /// <c>option.selected = value</c>: the option's selectedness, dirty; a select-one then keeps it alone, or,
    /// when it was deselected, a drop-down its first option (measured).
    /// </summary>
    internal void SetOptionSelected(DomElement option, bool value)
    {
        if (SelectOf(option) is not { } select)
        {
            _host.SetOptionState(option, value, dirty: true);
            return;
        }

        Hold(select);
        var options = OptionsOf(select);
        var index = options.IndexOf(option);
        var selected = Selectedness(select);
        if (index < 0)
            return;

        selected[index] = value;
        if (!IsMultiple(select))
            KeepOneSelected(select, options, selected, value ? index : -1, multiple: false);
        Write(select, options, selected, dirty: false);
        _host.SetOptionState(option, selected[index], dirty: true);
    }

    /// <summary>The select's current selected index — its first selected option's, or -1.</summary>
    internal int GetSelectedIndex(DomElement element) => Array.IndexOf(Selectedness(element), true);

    /// <summary><c>selectedIndex = index</c>: only the option at <paramref name="index"/> selected, and dirty; none for an index out of range (measured).</summary>
    internal void SetSelectedIndex(DomElement element, int index)
    {
        Hold(element);
        var options = OptionsOf(element);
        var selected = new bool[options.Count];
        if (index >= 0 && index < options.Count)
            selected[index] = true;
        Write(element, options, selected, dirty: false);
        if (index >= 0 && index < options.Count)
            _host.SetOptionState(options[index], true, dirty: true);
    }

    /// <summary>The select's current value — its first selected option's value, or the empty string.</summary>
    internal string GetValue(DomElement element)
    {
        var options = OptionsOf(element);
        var index = GetSelectedIndex(element);
        return index < 0 ? string.Empty : OptionValue(options[index]);
    }

    /// <summary>An option's value: one a script set, else its <c>value</c> attribute, else its text.</summary>
    internal string OptionValue(DomElement option) =>
        _host.TryGetOptionValue(option, out var value) ? value : HtmlSelectQueries.GetOptionValue(option);

    /// <summary><c>select.value = value</c>: only the first option with that value selected, and dirty; none when no option has it (measured).</summary>
    internal void SetValue(DomElement element, string value)
    {
        var options = OptionsOf(element);
        SetSelectedIndex(element, options.FindIndex(option => string.Equals(OptionValue(option), value, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The user's choice: exactly the options at <paramref name="indexes"/> selected -- one of them for a
    /// select-one -- the changed ones dirty. Answers whether anything changed.
    /// </summary>
    internal bool ChooseByUser(DomElement select, IReadOnlyCollection<int> indexes)
    {
        Hold(select);
        var options = OptionsOf(select);
        var before = Selectedness(select);
        var selected = new bool[options.Count];
        foreach (var index in indexes)
        {
            if (index >= 0 && index < options.Count && !IsOptionDisabled(options[index]))
                selected[index] = true;
        }

        if (!IsMultiple(select))
            KeepOneSelected(select, options, selected, Array.IndexOf(selected, true), multiple: false);

        if (before.AsSpan().SequenceEqual(selected))
            return false;

        Write(select, options, selected, dirty: true);
        return true;
    }

    /// <summary>HTML's reset algorithm for a select: each option as its markup says, none dirty, then a select-one kept at one.</summary>
    internal void Reset(DomElement select)
    {
        var options = OptionsOf(select);
        var selected = MarkupSelectedness(select, options, IsMultiple(select));
        for (var i = 0; i < options.Count; i++)
            _host.SetOptionState(options[i], selected[i], dirty: false);
        _host.HoldSelect(select);
        _host.NoteSelectionChanged(select);
    }

    // -------- The tree changing under a held select --------

    /// <summary>
    /// Options joined <paramref name="select"/>: each keeps what it holds, or takes its markup's; a
    /// select-one then keeps the last selected one that joined, or the one it had.
    /// </summary>
    internal void OnOptionsInserted(DomElement select, IReadOnlyList<DomElement> inserted)
    {
        if (!_host.IsSelectHeld(select))
        {
            // A select still as its markup says reads the new options from theirs -- unless one holds a
            // selectedness of its own, which only a held select keeps.
            if (!inserted.Any(option => _host.TryGetOptionState(option, out _, out _)))
                return;
            Hold(select);
        }

        foreach (var option in inserted)
        {
            if (!_host.TryGetOptionState(option, out _, out _))
                _host.SetOptionState(option, DomBridgeUtils.HasAttr(option, "selected"), dirty: false);
        }

        var options = OptionsOf(select);
        var selected = Selectedness(select);
        if (!IsMultiple(select))
        {
            var keep = -1;
            for (var i = 0; i < options.Count; i++)
            {
                if (selected[i] && inserted.Contains(options[i]))
                    keep = i;
            }

            KeepOneSelected(select, options, selected, keep, multiple: false);
        }

        Write(select, options, selected, dirty: false);
    }

    /// <summary>Options left <paramref name="select"/>: a drop-down with none selected any more selects its first one that is not disabled.</summary>
    internal void OnOptionsRemoved(DomElement select)
    {
        if (!_host.IsSelectHeld(select) || IsMultiple(select))
            return;

        var options = OptionsOf(select);
        var selected = Selectedness(select);
        KeepOneSelected(select, options, selected, keep: -1, multiple: false);
        Write(select, options, selected, dirty: false);
    }

    /// <summary>
    /// An option's <c>selected</c> attribute was added (<paramref name="added"/>) or removed: an option whose
    /// selectedness nothing has changed follows it, and a select-one keeps one option.
    /// </summary>
    internal void OnSelectedAttributeChanged(DomElement option, bool added)
    {
        var select = SelectOf(option);
        if (select is null || !_host.IsSelectHeld(select))
        {
            if (_host.TryGetOptionState(option, out _, out var detachedDirty) && !detachedDirty)
                _host.SetOptionState(option, added, dirty: false);
            if (select is not null)
                _host.NoteSelectionChanged(select);
            return;
        }

        if (_host.TryGetOptionState(option, out _, out var dirty) && dirty)
            return;

        var options = OptionsOf(select);
        var index = options.IndexOf(option);
        var selected = Selectedness(select);
        if (index < 0)
            return;

        selected[index] = added;
        if (!IsMultiple(select))
            KeepOneSelected(select, options, selected, added ? index : -1, multiple: false);
        Write(select, options, selected, dirty: false);
    }

    /// <summary>
    /// The select's <c>multiple</c> came or went (<paramref name="wasMultiple"/> before): leaving a select-one
    /// keeps its first selected option, as Chromium does, or with none its default; becoming multiple
    /// changes nothing.
    /// </summary>
    internal void OnMultipleChanged(DomElement select, bool wasMultiple)
    {
        Hold(select, wasMultiple);
        if (IsMultiple(select))
        {
            _host.NoteSelectionChanged(select);
            return;
        }

        var options = OptionsOf(select);
        var selected = Selectedness(select);
        KeepOneSelected(select, options, selected, Array.IndexOf(selected, true), multiple: false);
        Write(select, options, selected, dirty: false);
    }
}
