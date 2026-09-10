using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTML table DOM interfaces feature binding (HtmlBridge complexity-reduction roadmap Phase 3,
/// P3.5) — <c>HTMLTableElement</c> (caption/tHead/tFoot/tBodies/rows plus the create*/delete*/
/// insertRow/deleteRow methods), <c>HTMLTableSectionElement</c> (rows/insertRow) and
/// <c>HTMLTableRowElement</c> (rowIndex/sectionRowIndex/cells/insertCell/deleteCell). It is pure
/// canonical-tree manipulation: it depends only on the narrow <see cref="ITableHost"/> contract
/// (the realm, JS-wrapper identity and the element-construction funnel) plus the assembly's neutral
/// static <c>DomBridge</c> tree helpers.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): every member is minted by the
/// realm and every body runs on a <see cref="JsCall"/>, so this file names no engine type at all.
/// </para>
/// <para>
/// It named one until its caller stopped handing it an engine object. The remark here used to say
/// <c>DomBridge/ElementInterfaces.cs</c> "installs these members onto an engine wrapper it holds and
/// is not migrated"; it holds a handle, and the engine object it used to pass was derived from that
/// handle one line earlier only so that the adapter could derive the handle back. Both halves are
/// gone and the caller passes what it has.
/// </para>
/// </remarks>
internal sealed class TableBinding(ITableHost host)
{
    private readonly ITableHost _host = host;

    /// <summary>
    /// Installs the table-family interface members on <paramref name="obj"/> for
    /// <paramref name="element"/> according to its <paramref name="tag"/> (table / section / row).
    /// </summary>
    internal void Install(JsValue obj, DomElement element, string tag)
    {
        var realm = _host.Realm;

        // HTMLTableElement interface
        if (tag == "table")
        {
            realm.DefineAccessor(obj, "caption", (in _) => GetCaption(element), IgnoredSetter);
            realm.DefineAccessor(obj, "tHead", (in _) => GetTHead(element), IgnoredSetter);
            realm.DefineAccessor(obj, "tFoot", (in _) => GetTFoot(element), IgnoredSetter);
            realm.DefineAccessor(obj, "tBodies", (in call) => GetTBodies(call.Realm, element), null);
            // rows (read-only) — all <tr> in spec order: thead rows, then tbody/direct-tr rows, then tfoot rows
            realm.DefineAccessor(obj, "rows", (in call) => BuildTableRows(call.Realm, element), null);
            realm.DefineValue(obj, "createCaption", realm.NewMethod("createCaption", (in _) => CreateCaption(element), 0));
            realm.DefineValue(obj, "createTHead", realm.NewMethod("createTHead", (in _) => CreateTHead(element), 0));
            realm.DefineValue(obj, "createTFoot", realm.NewMethod("createTFoot", (in _) => CreateTFoot(element), 0));
            realm.DefineValue(obj, "deleteCaption", realm.NewMethod("deleteCaption", (in _) => DeleteCaption(element), 0));
            realm.DefineValue(obj, "deleteTHead", realm.NewMethod("deleteTHead", (in _) => DeleteTHead(element), 0));
            realm.DefineValue(obj, "deleteTFoot", realm.NewMethod("deleteTFoot", (in _) => DeleteTFoot(element), 0));
            realm.DefineValue(obj, "insertRow", realm.NewMethod("insertRow", (in call) => TableInsertRow(element, in call), 1));
            realm.DefineValue(obj, "deleteRow", realm.NewMethod("deleteRow", (in call) => TableDeleteRow(element, in call), 1));
        }

        // HTMLTableSectionElement (thead, tbody, tfoot) — rows and insertRow
        if (tag == "thead" || tag == "tbody" || tag == "tfoot")
        {
            realm.DefineAccessor(obj, "rows", (in call) => SectionGetRows(call.Realm, element), null);
            realm.DefineValue(obj, "insertRow", realm.NewMethod("insertRow", (in call) => SectionInsertRow(element, in call), 1));
        }

        // HTMLTableRowElement (tr) — rowIndex, sectionRowIndex, cells, insertCell, deleteCell
        if (tag == "tr")
        {
            realm.DefineAccessor(obj, "rowIndex", (in _) => RowGetRowIndex(element), null);
            realm.DefineAccessor(obj, "sectionRowIndex", (in _) => RowGetSectionRowIndex(element), null);
            realm.DefineAccessor(obj, "cells", (in call) => RowGetCells(call.Realm, element), null);
            realm.DefineValue(obj, "insertCell", realm.NewMethod("insertCell", (in call) => RowInsertCell(element, in call), 1));
            realm.DefineValue(obj, "deleteCell", realm.NewMethod("deleteCell", (in call) => RowDeleteCell(element, in call), 1));
        }
    }

    /// <summary>
    /// The setter <c>caption</c>/<c>tHead</c>/<c>tFoot</c> have always had: one that accepts the
    /// assignment and discards it, so the property is writable-looking rather than silently
    /// read-only. Replacing a section through the property is not implemented; the create*/delete*
    /// methods are.
    /// </summary>
    /// <remarks>
    /// It was the bridge's <c>UndefinedFunction</c> helper, which mints a plain <em>constructable</em>
    /// engine function carrying a <c>prototype</c>. <see cref="IJsMembers.DefineAccessor"/> mints both
    /// halves of an accessor non-constructable — deliberately, because
    /// <c>new el.__lookupSetter__('caption')()</c> is a <c>TypeError</c> in a browser — so the setter
    /// function object loses a <c>prototype</c> a page could never legitimately have used. Every other
    /// accessor in the bridge crosses the same way; it is the one difference this file's migration
    /// makes, and it moves towards what a browser answers rather than away from it.
    /// </remarks>
    private static JsValue IgnoredSetter(in JsCall call) => JsValue.Undefined;

    // -------- HTMLTableElement --------

    private JsValue GetCaption(DomElement element)
    {
        var cap = FirstChildNamed(element, "caption");
        return cap != null ? _host.WrapNode(cap) : JsValue.Null;
    }

    private JsValue GetTHead(DomElement element)
    {
        var th = FirstChildNamed(element, "thead");
        return th != null ? _host.WrapNode(th) : JsValue.Null;
    }

    private JsValue GetTFoot(DomElement element)
    {
        var tf = FirstChildNamed(element, "tfoot");
        return tf != null ? _host.WrapNode(tf) : JsValue.Null;
    }

    private JsValue GetTBodies(IJsRealm realm, DomElement element)
    {
        var bodies = new List<JsValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "tbody", StringComparison.OrdinalIgnoreCase))
                bodies.Add(_host.WrapNode(c));
        return WithLength(realm, realm.NewArray([.. bodies]), bodies.Count);
    }

    private JsValue CreateCaption(DomElement element)
    {
        var cap = FirstChildNamed(element, "caption");
        if (cap != null)
            return _host.WrapNode(cap);
        cap = _host.CreateElement("caption");
        DomBridge.InsertChildAt(element, 0, cap);
        return _host.WrapNode(cap);
    }

    private JsValue CreateTHead(DomElement element) => CreateSection(element, "thead");

    private JsValue CreateTFoot(DomElement element) => CreateSection(element, "tfoot");

    private JsValue CreateSection(DomElement element, string tag)
    {
        var existing = FirstChildNamed(element, tag);
        if (existing != null)
            return _host.WrapNode(existing);
        var section = _host.CreateElement(tag);
        element.AppendChild(section);
        return _host.WrapNode(section);
    }

    private static JsValue DeleteCaption(DomElement element) => DeleteFirstChildNamed(element, "caption");

    private static JsValue DeleteTHead(DomElement element) => DeleteFirstChildNamed(element, "thead");

    private static JsValue DeleteTFoot(DomElement element) => DeleteFirstChildNamed(element, "tfoot");

    private static JsValue DeleteFirstChildNamed(DomElement element, string tag)
    {
        var child = FirstChildNamed(element, tag);
        if (child != null)
        {
            DomBridge.SetParent(child, null);
            DomBridge.RemoveChildFrom(element, child);
        }

        return JsValue.Undefined;
    }

    private JsValue TableInsertRow(DomElement element, in JsCall call)
    {
        // ToNumber, not the handle's inline reading: `insertRow("1")` is a string a page may pass,
        // and the ECMAScript coercion is what it observes.
        var index = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : -1;
        return InsertRowIntoTable(element, index);
    }

    private static JsValue TableDeleteRow(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var index = (int)call.Realm.ToNumber(call[0]);
        var rows = HtmlElementQueries.CollectTableRows(element);
        if (index < 0)
            index = rows.Count + index;
        if (index >= 0 && index < rows.Count)
        {
            var row = rows[index];
            row.Remove();
            DomBridge.SetParent(row, null);
        }

        return JsValue.Undefined;
    }

    // -------- HTMLTableSectionElement --------

    private JsValue SectionGetRows(IJsRealm realm, DomElement element)
    {
        var rows = new List<JsValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "tr", StringComparison.OrdinalIgnoreCase))
                rows.Add(_host.WrapNode(c));
        return WithLength(realm, realm.NewArray([.. rows]), rows.Count);
    }

    private JsValue SectionInsertRow(DomElement element, in JsCall call)
    {
        var index = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : -1;
        var tr = _host.CreateElement("tr");
        DomBridge.SetParent(tr, element);
        var trRows = DomBridge.ChildElements(element).Where(c => string.Equals(c.TagName, "tr", StringComparison.OrdinalIgnoreCase)).ToList();
        if (index < 0 || index >= trRows.Count)
        {
            element.AppendChild(tr);
        }
        else
        {
            var refRow = trRows[index];
            var idx = DomBridge.ChildIndexOf(element, refRow);
            DomBridge.InsertChildAt(element, idx, tr);
        }

        return _host.WrapNode(tr);
    }

    // -------- HTMLTableRowElement --------

    private static JsValue RowGetRowIndex(DomElement element)
    {
        // Find parent table (skipping an intervening section)
        var tableEl = DomBridge.ParentEl(element);
        if (tableEl != null && (string.Equals(tableEl.TagName, "thead", StringComparison.OrdinalIgnoreCase) || string.Equals(tableEl.TagName, "tbody", StringComparison.OrdinalIgnoreCase) || string.Equals(tableEl.TagName, "tfoot", StringComparison.OrdinalIgnoreCase)))
            tableEl = DomBridge.ParentEl(tableEl);
        if (tableEl == null || !string.Equals(tableEl.TagName, "table", StringComparison.OrdinalIgnoreCase))
            return JsValue.Number(-1);
        var rows = HtmlElementQueries.CollectTableRows(tableEl);
        return JsValue.Number(rows.IndexOf(element));
    }

    private static JsValue RowGetSectionRowIndex(DomElement element)
    {
        var section = DomBridge.ParentEl(element);
        if (section == null)
            return JsValue.Number(-1);
        var idx = 0;
        foreach (var c in DomBridge.ChildElements(section))
        {
            if (ReferenceEquals(c, element))
                return JsValue.Number(idx);
            if (string.Equals(c.TagName, "tr", StringComparison.OrdinalIgnoreCase))
                idx++;
        }

        return JsValue.Number(-1);
    }

    private JsValue RowGetCells(IJsRealm realm, DomElement element)
    {
        var cells = new List<JsValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "td", StringComparison.OrdinalIgnoreCase) || string.Equals(c.TagName, "th", StringComparison.OrdinalIgnoreCase))
                cells.Add(_host.WrapNode(c));
        return WithLength(realm, realm.NewArray([.. cells]), cells.Count);
    }

    private JsValue RowInsertCell(DomElement element, in JsCall call)
    {
        var index = call.Length > 0 ? (int)Math.Truncate(call.Realm.ToNumber(call[0])) : -1;
        var td = _host.CreateElement("td");
        DomBridge.SetParent(td, element);
        var cells = DomBridge.ChildElements(element).Where(c => !DomBridge.IsText(c) && DomBridge.IsTableCellElement(c)).ToList();
        if (index < 0 || index >= cells.Count)
        {
            element.AppendChild(td);
        }
        else
        {
            var referenceCell = cells[index];
            var childIndex = DomBridge.ChildIndexOf(element, referenceCell);
            if (childIndex < 0)
                element.AppendChild(td);
            else
                DomBridge.InsertChildAt(element, childIndex, td);
        }

        return _host.WrapNode(td);
    }

    private static JsValue RowDeleteCell(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'deleteCell' on 'HTMLTableRowElement': 1 argument required, but only 0 present.");
        var index = (int)Math.Truncate(call.Realm.ToNumber(call[0]));
        var cells = DomBridge.ChildElements(element).Where(c => !DomBridge.IsText(c) && DomBridge.IsTableCellElement(c)).ToList();
        if (index < 0)
            index = cells.Count + index;
        if (index < 0 || index >= cells.Count)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        var cell = cells[index];
        DomBridge.SetParent(cell, null);
        DomBridge.RemoveChildFrom(element, cell);
        return JsValue.Undefined;
    }

    // -------- Helpers --------

    private static DomElement? FirstChildNamed(DomElement element, string tag) =>
        DomBridge.ChildElements(element).FirstOrDefault(c => string.Equals(c.TagName, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Replaces the array's own <c>length</c> with an accessor over the snapshot the array was built
    /// from — what this site has always installed, and what keeps the two in agreement.
    /// </summary>
    private static JsValue WithLength(IJsRealm realm, JsValue array, int length)
    {
        realm.DefineAccessor(array, "length", (in _) => JsValue.Number(length), null);
        return array;
    }

    private JsValue BuildTableRows(IJsRealm realm, DomElement table)
    {
        var rows = HtmlElementQueries.CollectTableRows(table);
        var jsRows = new List<JsValue>();
        foreach (var r in rows)
            jsRows.Add(_host.WrapNode(r));
        return WithLength(realm, realm.NewArray([.. jsRows]), jsRows.Count);
    }

    /// <summary>Inserts a row into a table at the given index, per HTMLTableElement.insertRow().</summary>
    private JsValue InsertRowIntoTable(DomElement table, int index)
    {
        var tr = _host.CreateElement("tr");

        var allRows = HtmlElementQueries.CollectTableRows(table);
        if (allRows.Count == 0 || index == -1 || index == allRows.Count)
        {
            // Find the last section to append to, or create a tbody
            DomElement? lastSection = null;
            for (int i = table.ChildNodes.Count - 1; i >= 0; i--)
            {
                if (DomBridge.ChildAt(table, i) is not DomElement childElement)
                    continue;
                var ctag = childElement.TagName.ToLowerInvariant();
                if (ctag == "thead" || ctag == "tbody" || ctag == "tfoot")
                {
                    lastSection = childElement;
                    break;
                }
            }
            if (lastSection == null && allRows.Count == 0)
            {
                // No sections and no rows at all: create a new tbody per spec
                var tbody = _host.CreateElement("tbody");
                table.AppendChild(tbody);
                lastSection = tbody;
            }
            if (lastSection != null)
            {
                lastSection.AppendChild(tr);
            }
            else
            {
                table.AppendChild(tr);
            }
        }
        else if (index >= 0 && index < allRows.Count)
        {
            var refRow = allRows[index];
            var parent = DomBridge.ParentEl(refRow) ?? table;
            var idx = DomBridge.ChildIndexOf(parent, refRow);
            DomBridge.InsertChildAt(parent, idx >= 0 ? idx : parent.ChildNodes.Count, tr);
        }
        return _host.WrapNode(tr);
    }
}
