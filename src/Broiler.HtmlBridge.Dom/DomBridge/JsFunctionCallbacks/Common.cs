using System.Text;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    /// <summary>
    /// A node's <c>textContent</c>, or <see langword="null"/> for the two node kinds DOM §4.4 gives no
    /// text at all.
    /// </summary>
    /// <remarks>
    /// The algorithm is engine-neutral and always was — it walks the tree and concatenates strings —
    /// so it is stated here in CLR terms and the two callers that still want a JavaScript value make
    /// one from it. <see cref="JsValue.String(string?)"/> turns the <see langword="null"/> into
    /// JavaScript <c>null</c>, which is exactly the distinction the next paragraph is about.
    /// </remarks>
    private string? NodeTextOrNull(DomNode node)
    {
        // RF-BRIDGE-1c Phase F (F3c part 2d): character-data nodes expose their data as textContent;
        // an element's textContent is the concatenation of its descendant text.
        if (node is DomCharacterData characterData)
            return characterData.Data;

        // DOM §4.4: `textContent` is *null* for a document and for a doctype — they are the two node
        // kinds the algorithm has no text for, rather than kinds whose text happens to be empty.
        // Both answered the empty string, so `document.textContent` was `""` where Chromium says
        // null, and a page distinguishing the two with `=== null` read the wrong branch.
        if (node is DomDocument or DomDocumentType)
            return null;

        if (node is not DomElement element)
            return string.Empty;

        if (element.ChildNodes.Count > 0)
        {
            var sb = new StringBuilder();
            CollectTextContent(element, sb);
            return sb.ToString();
        }

        // A childless element has empty textContent (its content, if any, is canonical DomText
        // children handled above — Phase 4 item 3 removed the parallel InnerHtml fallback).
        return string.Empty;
    }

    /// <summary>
    /// <see cref="NodeTextOrNull"/> as the engine value the unmigrated wrappers install.
    /// </summary>
    /// <remarks>
    /// <b>This is the adapter, and it names the engine because a primitive cannot cross the seam.</b>
    /// <c>JsInterop</c> carries an object across without converting it — the handle holds the engine's
    /// own object — and a string or a <c>null</c> is not an object, so the two arms are spelled out.
    /// Four sites outside this file still take the engine value: <c>DomBridge/ElementInterfaces.cs</c>
    /// (<c>text</c>), <c>DomBridge/CharacterDataInterface.cs</c> (<c>Node.prototype.textContent</c>),
    /// <c>DomBridge.ElementContentHost.cs</c>, and <c>Features/ElementContentBinding.cs</c> through it.
    /// It goes when they do.
    /// </remarks>
    private JSValue GetNodeTextValue(DomNode node) =>
        NodeTextOrNull(node) is { } text ? new JSString(text) : JSNull.Value;


    private bool IsCurrentIframeCrossOrigin(DomElement element)
    {
        if (HasAttr(element, "srcdoc"))
            return false;

        var iframeSrcValue = TryGetAttribute(element, "src", out var srcVal) ? srcVal : string.Empty;
        return IsCrossOrigin(iframeSrcValue, _pageUrl);
    }


    // MutationObserver option parsing and observe()/disconnect() registration moved to the Phase 3
    // MutationObserverBinding feature module (Broiler.HtmlBridge.Dom.Features).

    // Phase 4 item 1 (P4.4a): docRoot may be a legacy #subdoc-root element OR a canonical
    // DomDocument browsing-context root; ChildElements works over both. Returns the documentElement,
    // or — for an element root with none — the root itself (the prior `?? docRoot` fallback). A
    // canonical DomDocument with no documentElement yields null (per DOM; e.g. createDocument with an
    // empty qualifiedName), so callers must null-check.
    internal static DomElement? GetDocumentElement(DomNode docRoot) =>
        ChildElements(docRoot).FirstOrDefault(c => !IsText(c) && !c.TagName.StartsWith('#')) ?? docRoot as DomElement;


    private bool IsPositionAfter(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB)
    {
        // Phase 4 item 4/5: for boundary points in the SAME tree this is exactly canonical
        // Broiler.Dom.DomRange.CompareBoundaryPoints(...) > 0 (verified branch-for-branch: same-container,
        // either-descendant, and common-ancestor ordering all agree), so delegate to it instead of
        // re-implementing the walk. The bridge deliberately keeps a LENIENT cross-tree path — canonical
        // throws WrongDocument, but the bridge's compareBoundaryPoints returns an order rather than
        // throwing — so the cross-tree branch (different roots) is preserved verbatim below.
        if (ReferenceEquals(containerA.GetRootNode(), containerB.GetRootNode()))
            return DomRange.CompareBoundaryPoints(containerA, offsetA, containerB, offsetB) > 0;

        var allNodes = docRoot.InclusiveDescendants().ToList();
        var idxA = allNodes.IndexOf(containerA);
        var idxB = allNodes.IndexOf(containerB);
        if (idxA < 0 || idxB < 0)
            return false;

        return idxA > idxB || (idxA == idxB && offsetA > offsetB);
    }


    private int CompareBoundaryPosition(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB)
    {
        if (ReferenceEquals(containerA, containerB) && offsetA == offsetB)
            return 0;

        if (IsPositionAfter(docRoot, containerA, offsetA, containerB, offsetB))
            return 1;

        if (IsPositionAfter(docRoot, containerB, offsetB, containerA, offsetA))
            return -1;

        return 0;
    }
}
