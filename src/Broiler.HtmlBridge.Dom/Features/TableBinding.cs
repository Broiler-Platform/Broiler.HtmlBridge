using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTML table DOM interfaces feature binding (HtmlBridge complexity-reduction roadmap Phase 3,
/// P3.5) — <c>HTMLTableElement</c> (caption/tHead/tFoot/tBodies/rows plus the create*/delete*/
/// insertRow/deleteRow methods), <c>HTMLTableSectionElement</c> (rows/insertRow) and
/// <c>HTMLTableRowElement</c> (rowIndex/sectionRowIndex/cells/insertCell/deleteCell). It is pure
/// canonical-tree manipulation: it depends only on the narrow <see cref="ITableHost"/> contract
/// (JS-wrapper identity + the element-construction funnel) plus the assembly's neutral static
/// <c>DomBridge</c> tree helpers.
/// </summary>
internal sealed class TableBinding(ITableHost host)
{
    private readonly ITableHost _host = host;

    /// <summary>
    /// Installs the table-family interface members on <paramref name="obj"/> for
    /// <paramref name="element"/> according to its <paramref name="tag"/> (table / section / row).
    /// </summary>
    internal void Install(JSObject obj, DomElement element, string tag)
    {
        // HTMLTableElement interface
        if (tag == "table")
        {
            obj.FastAddProperty("caption", new DomFunction((in _) => GetCaption(element), "get caption"), DomBridge.UndefinedFunction("set caption"), JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("tHead", new DomFunction((in _) => GetTHead(element), "get tHead"), DomBridge.UndefinedFunction("set tHead"), JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("tFoot", new DomFunction((in _) => GetTFoot(element), "get tFoot"), DomBridge.UndefinedFunction("set tFoot"), JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("tBodies", new DomFunction((in _) => GetTBodies(element), "get tBodies"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
            // rows (read-only) — all <tr> in spec order: thead rows, then tbody/direct-tr rows, then tfoot rows
            obj.FastAddProperty("rows", new DomFunction((in _) => BuildTableRows(element), "get rows"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddValue("createCaption", new DomFunction((in _) => CreateCaption(element), "createCaption", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("createTHead", new DomFunction((in _) => CreateTHead(element), "createTHead", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("createTFoot", new DomFunction((in _) => CreateTFoot(element), "createTFoot", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("deleteCaption", new DomFunction((in _) => DeleteCaption(element), "deleteCaption", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("deleteTHead", new DomFunction((in _) => DeleteTHead(element), "deleteTHead", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("deleteTFoot", new DomFunction((in _) => DeleteTFoot(element), "deleteTFoot", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("insertRow", new DomFunction((in a) => TableInsertRow(element, in a), "insertRow", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("deleteRow", new DomFunction((in a) => TableDeleteRow(element, in a), "deleteRow", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // HTMLTableSectionElement (thead, tbody, tfoot) — rows and insertRow
        if (tag == "thead" || tag == "tbody" || tag == "tfoot")
        {
            obj.FastAddProperty("rows", new DomFunction((in _) => SectionGetRows(element), "get rows"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddValue("insertRow", new DomFunction((in a) => SectionInsertRow(element, in a), "insertRow", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // HTMLTableRowElement (tr) — rowIndex, sectionRowIndex, cells, insertCell, deleteCell
        if (tag == "tr")
        {
            obj.FastAddProperty("rowIndex", new DomFunction((in _) => RowGetRowIndex(element), "get rowIndex"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("sectionRowIndex", new DomFunction((in _) => RowGetSectionRowIndex(element), "get sectionRowIndex"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("cells", new DomFunction((in _) => RowGetCells(element), "get cells"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddValue("insertCell", new DomFunction((in a) => RowInsertCell(element, in a), "insertCell", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("deleteCell", new DomFunction((in a) => RowDeleteCell(element, in a), "deleteCell", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        }
    }

    // -------- HTMLTableElement --------

    private JSValue GetCaption(DomElement element)
    {
        var cap = FirstChildNamed(element, "caption");
        return cap != null ? _host.ToJSObject(cap) : JSNull.Value;
    }

    private JSValue GetTHead(DomElement element)
    {
        var th = FirstChildNamed(element, "thead");
        return th != null ? _host.ToJSObject(th) : JSNull.Value;
    }

    private JSValue GetTFoot(DomElement element)
    {
        var tf = FirstChildNamed(element, "tfoot");
        return tf != null ? _host.ToJSObject(tf) : JSNull.Value;
    }

    private JSValue GetTBodies(DomElement element)
    {
        var bodies = new List<JSValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "tbody", StringComparison.OrdinalIgnoreCase))
                bodies.Add(_host.ToJSObject(c));
        return WithLength(new JSArray(bodies), bodies.Count);
    }

    private JSValue CreateCaption(DomElement element)
    {
        var cap = FirstChildNamed(element, "caption");
        if (cap != null)
            return _host.ToJSObject(cap);
        cap = _host.CreateElement("caption");
        DomBridge.InsertChildAt(element, 0, cap);
        return _host.ToJSObject(cap);
    }

    private JSValue CreateTHead(DomElement element) => CreateSection(element, "thead");

    private JSValue CreateTFoot(DomElement element) => CreateSection(element, "tfoot");

    private JSValue CreateSection(DomElement element, string tag)
    {
        var existing = FirstChildNamed(element, tag);
        if (existing != null)
            return _host.ToJSObject(existing);
        var section = _host.CreateElement(tag);
        element.AppendChild(section);
        return _host.ToJSObject(section);
    }

    private static JSValue DeleteCaption(DomElement element) => DeleteFirstChildNamed(element, "caption");

    private static JSValue DeleteTHead(DomElement element) => DeleteFirstChildNamed(element, "thead");

    private static JSValue DeleteTFoot(DomElement element) => DeleteFirstChildNamed(element, "tfoot");

    private static JSValue DeleteFirstChildNamed(DomElement element, string tag)
    {
        var child = FirstChildNamed(element, tag);
        if (child != null)
        {
            DomBridge.SetParent(child, null);
            DomBridge.RemoveChildFrom(element, child);
        }

        return JSUndefined.Value;
    }

    private JSValue TableInsertRow(DomElement element, in Arguments a)
    {
        var index = a.Length > 0 ? (int)a[0].DoubleValue : -1;
        return InsertRowIntoTable(element, index);
    }

    private static JSValue TableDeleteRow(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var index = (int)a[0].DoubleValue;
        var rows = HtmlElementQueries.CollectTableRows(element);
        if (index < 0)
            index = rows.Count + index;
        if (index >= 0 && index < rows.Count)
        {
            var row = rows[index];
            row.Remove();
            DomBridge.SetParent(row, null);
        }

        return JSUndefined.Value;
    }

    // -------- HTMLTableSectionElement --------

    private JSValue SectionGetRows(DomElement element)
    {
        var rows = new List<JSValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "tr", StringComparison.OrdinalIgnoreCase))
                rows.Add(_host.ToJSObject(c));
        return WithLength(new JSArray(rows), rows.Count);
    }

    private JSValue SectionInsertRow(DomElement element, in Arguments a)
    {
        var index = a.Length > 0 ? (int)a[0].DoubleValue : -1;
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

        return _host.ToJSObject(tr);
    }

    // -------- HTMLTableRowElement --------

    private static JSValue RowGetRowIndex(DomElement element)
    {
        // Find parent table (skipping an intervening section)
        var tableEl = DomBridge.ParentEl(element);
        if (tableEl != null && (string.Equals(tableEl.TagName, "thead", StringComparison.OrdinalIgnoreCase) || string.Equals(tableEl.TagName, "tbody", StringComparison.OrdinalIgnoreCase) || string.Equals(tableEl.TagName, "tfoot", StringComparison.OrdinalIgnoreCase)))
            tableEl = DomBridge.ParentEl(tableEl);
        if (tableEl == null || !string.Equals(tableEl.TagName, "table", StringComparison.OrdinalIgnoreCase))
            return new JSNumber(-1);
        var rows = HtmlElementQueries.CollectTableRows(tableEl);
        return new JSNumber(rows.IndexOf(element));
    }

    private static JSValue RowGetSectionRowIndex(DomElement element)
    {
        var section = DomBridge.ParentEl(element);
        if (section == null)
            return new JSNumber(-1);
        var idx = 0;
        foreach (var c in DomBridge.ChildElements(section))
        {
            if (ReferenceEquals(c, element))
                return new JSNumber(idx);
            if (string.Equals(c.TagName, "tr", StringComparison.OrdinalIgnoreCase))
                idx++;
        }

        return new JSNumber(-1);
    }

    private JSValue RowGetCells(DomElement element)
    {
        var cells = new List<JSValue>();
        foreach (var c in DomBridge.ChildElements(element))
            if (string.Equals(c.TagName, "td", StringComparison.OrdinalIgnoreCase) || string.Equals(c.TagName, "th", StringComparison.OrdinalIgnoreCase))
                cells.Add(_host.ToJSObject(c));
        return WithLength(new JSArray(cells), cells.Count);
    }

    private JSValue RowInsertCell(DomElement element, in Arguments a)
    {
        var index = a.Length > 0 ? (int)Math.Truncate(a[0].DoubleValue) : -1;
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

        return _host.ToJSObject(td);
    }

    private static JSValue RowDeleteCell(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'deleteCell' on 'HTMLTableRowElement': 1 argument required, but only 0 present.");
        var index = (int)Math.Truncate(a[0].DoubleValue);
        var cells = DomBridge.ChildElements(element).Where(c => !DomBridge.IsText(c) && DomBridge.IsTableCellElement(c)).ToList();
        if (index < 0)
            index = cells.Count + index;
        if (index < 0 || index >= cells.Count)
            throw new JSException("INDEX_SIZE_ERR");
        var cell = cells[index];
        DomBridge.SetParent(cell, null);
        DomBridge.RemoveChildFrom(element, cell);
        return JSUndefined.Value;
    }

    // -------- Helpers --------

    private static DomElement? FirstChildNamed(DomElement element, string tag) =>
        DomBridge.ChildElements(element).FirstOrDefault(c => string.Equals(c.TagName, tag, StringComparison.OrdinalIgnoreCase));

    private static JSObject WithLength(JSArray array, int length)
    {
        array.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(length), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        return array;
    }

    private JSObject BuildTableRows(DomElement table)
    {
        var rows = HtmlElementQueries.CollectTableRows(table);
        var jsRows = new List<JSValue>();
        foreach (var r in rows)
            jsRows.Add(_host.ToJSObject(r));
        return WithLength(new JSArray(jsRows), jsRows.Count);
    }

    /// <summary>Inserts a row into a table at the given index, per HTMLTableElement.insertRow().</summary>
    private JSValue InsertRowIntoTable(DomElement table, int index)
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
        return _host.ToJSObject(tr);
    }
}
