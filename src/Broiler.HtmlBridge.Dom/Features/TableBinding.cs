using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTML table DOM interfaces feature binding —
/// <c>HTMLTableElement</c> (caption/tHead/tFoot/tBodies/rows plus the create*/delete*/
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
            realm.DefineMethod(obj, "createCaption", 0, (in _) => CreateCaption(element));
            realm.DefineMethod(obj, "createTHead", 0, (in _) => CreateTHead(element));
            realm.DefineMethod(obj, "createTFoot", 0, (in _) => CreateTFoot(element));
            realm.DefineMethod(obj, "deleteCaption", 0, (in _) => DeleteCaption(element));
            realm.DefineMethod(obj, "deleteTHead", 0, (in _) => DeleteTHead(element));
            realm.DefineMethod(obj, "deleteTFoot", 0, (in _) => DeleteTFoot(element));
            realm.DefineMethod(obj, "insertRow", 1, (in call) => TableInsertRow(element, in call));
            realm.DefineMethod(obj, "deleteRow", 1, (in call) => TableDeleteRow(element, in call));
        }

        // HTMLTableSectionElement (thead, tbody, tfoot) — rows and insertRow
        if (tag == "thead" || tag == "tbody" || tag == "tfoot")
        {
            realm.DefineAccessor(obj, "rows", (in call) => SectionGetRows(call.Realm, element), null);
            realm.DefineMethod(obj, "insertRow", 1, (in call) => SectionInsertRow(element, in call));
        }

        // HTMLTableRowElement (tr) — rowIndex, sectionRowIndex, cells, insertCell, deleteCell
        if (tag == "tr")
        {
            realm.DefineAccessor(obj, "rowIndex", (in _) => RowGetRowIndex(element), null);
            realm.DefineAccessor(obj, "sectionRowIndex", (in _) => RowGetSectionRowIndex(element), null);
            realm.DefineAccessor(obj, "cells", (in call) => RowGetCells(call.Realm, element), null);
            realm.DefineMethod(obj, "insertCell", 1, (in call) => RowInsertCell(element, in call));
            realm.DefineMethod(obj, "deleteCell", 1, (in call) => RowDeleteCell(element, in call));
        }
    }

    /// <summary>
    /// The setter <c>caption</c>/<c>tHead</c>/<c>tFoot</c> have always had: one that accepts the
    /// assignment and discards it, so the property is writable-looking rather than silently
    /// read-only. Replacing a section through the property is not implemented; the create*/delete*
    /// methods are.
    /// </summary>
    /// <remarks>
    /// <see cref="IJsMembers.DefineAccessor"/> mints both halves of an accessor non-constructable —
    /// deliberately, because <c>new el.__lookupSetter__('caption')()</c> is a <c>TypeError</c> in a
    /// browser — so this setter function object carries no <c>prototype</c>. Every accessor in the
    /// bridge crosses the same way.
    /// </remarks>
    private static JsValue IgnoredSetter(in JsCall call) => JsValue.Undefined;

    // -------- HTMLTableElement --------

    private JsValue GetCaption(DomElement element)
    {
        var cap = HtmlTableOperations.GetCaption(element);
        return cap != null ? _host.WrapNode(cap) : JsValue.Null;
    }

    private JsValue GetTHead(DomElement element)
    {
        var th = HtmlTableOperations.GetTHead(element);
        return th != null ? _host.WrapNode(th) : JsValue.Null;
    }

    private JsValue GetTFoot(DomElement element)
    {
        var tf = HtmlTableOperations.GetTFoot(element);
        return tf != null ? _host.WrapNode(tf) : JsValue.Null;
    }

    private JsValue GetTBodies(IJsRealm realm, DomElement element)
    {
        var bodies = HtmlTableOperations.GetTableBodies(element).Select(_host.WrapNode).ToArray();
        return WithLength(realm, realm.NewArray(bodies), bodies.Length);
    }

    private JsValue CreateCaption(DomElement element) =>
        _host.WrapNode(HtmlTableOperations.CreateCaption(element));

    private JsValue CreateTHead(DomElement element) =>
        _host.WrapNode(HtmlTableOperations.CreateTHead(element));

    private JsValue CreateTFoot(DomElement element) =>
        _host.WrapNode(HtmlTableOperations.CreateTFoot(element));

    private static JsValue DeleteCaption(DomElement element)
    {
        HtmlTableOperations.DeleteCaption(element);
        return JsValue.Undefined;
    }

    private static JsValue DeleteTHead(DomElement element)
    {
        HtmlTableOperations.DeleteTHead(element);
        return JsValue.Undefined;
    }

    private static JsValue DeleteTFoot(DomElement element)
    {
        HtmlTableOperations.DeleteTFoot(element);
        return JsValue.Undefined;
    }

    private JsValue TableInsertRow(DomElement element, in JsCall call)
    {
        var index = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : -1;
        var tr = HtmlTableOperations.InsertRow(element, index);
        return _host.WrapNode(tr);
    }

    private static JsValue TableDeleteRow(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var index = (int)call.Realm.ToNumber(call[0]);
        try
        {
            HtmlTableOperations.DeleteRow(element, index);
        }
        catch (DomException)
        {
        }

        return JsValue.Undefined;
    }

    // -------- HTMLTableSectionElement --------

    private JsValue SectionGetRows(IJsRealm realm, DomElement element)
    {
        var rows = HtmlTableOperations.GetSectionRows(element).Select(_host.WrapNode).ToArray();
        return WithLength(realm, realm.NewArray(rows), rows.Length);
    }

    private JsValue SectionInsertRow(DomElement element, in JsCall call)
    {
        var index = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : -1;
        var tr = HtmlTableOperations.InsertSectionRow(element, index);
        return _host.WrapNode(tr);
    }

    // -------- HTMLTableRowElement --------

    private static JsValue RowGetRowIndex(DomElement element) =>
        JsValue.Number(HtmlTableOperations.GetRowIndex(element));

    private static JsValue RowGetSectionRowIndex(DomElement element) =>
        JsValue.Number(HtmlTableOperations.GetSectionRowIndex(element));

    private JsValue RowGetCells(IJsRealm realm, DomElement element)
    {
        var cells = HtmlTableOperations.GetRowCells(element).Select(_host.WrapNode).ToArray();
        return WithLength(realm, realm.NewArray(cells), cells.Length);
    }

    private JsValue RowInsertCell(DomElement element, in JsCall call)
    {
        var index = call.Length > 0 ? (int)Math.Truncate(call.Realm.ToNumber(call[0])) : -1;
        var td = HtmlTableOperations.InsertCell(element, index);
        return _host.WrapNode(td);
    }

    private static JsValue RowDeleteCell(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'deleteCell' on 'HTMLTableRowElement': 1 argument required, but only 0 present.");
        var index = (int)Math.Truncate(call.Realm.ToNumber(call[0]));
        try
        {
            HtmlTableOperations.DeleteCell(element, index);
        }
        catch (DomException)
        {
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        }
        return JsValue.Undefined;
    }

    // -------- Helpers --------

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
}
