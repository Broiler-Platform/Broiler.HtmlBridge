using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The bridge services the <see cref="SubDocumentBinding"/> feature module consumes (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.13). The nested-browsing-context <c>document</c> object is
/// essentially the whole DOM re-projected onto a sub-document root, so — unlike the small feature
/// contracts — it genuinely needs many bridge services: JS-wrapper identity, the node-construction
/// funnels, and the shared builders for the sub-surfaces a document exposes (Range, TreeWalker,
/// NodeIterator, style-sheets, hit testing). Every seam is explicit, so no handler reaches an arbitrary
/// <c>DomBridge</c> private field; the assembly's neutral static tree/selector helpers on
/// <c>DomBridge</c> (ChildElements, ChildAt, GetDocumentElement, CollectTextContent, MatchesSelector,
/// SetParent, ValidateElementName, …) are called directly and are not part of this contract.
/// </summary>
internal interface ISubDocumentHost
{
    /// <summary>The bridge's JS context (for name-validation diagnostics).</summary>
    JSContext JsContext { get; }

    /// <summary>The main window JS object, used for the sub-document's <c>defaultView</c>.</summary>
    JSObject? WindowJSObject { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);

    /// <summary>Points a wrapper at a named interface's prototype. A sub-document object is built
    /// rather than minted as a node wrapper, so it does not pass the choke point that links every
    /// other one.</summary>
    void LinkToInterface(JSObject wrapper, string interfaceName);

    /// <summary>
    /// Whether the node interface prototypes carry their members, so a linked wrapper inherits the
    /// <c>Node</c> constants instead of needing its own eighteen copies.
    /// </summary>
    bool NodeInterfacePrototypesReady { get; }

    /// <summary>Reverse wrapper lookup: the element whose JS wrapper is <paramref name="jsObj"/>.</summary>
    DomElement? FindDomElementByJSObject(JSObject jsObj);

    /// <summary>Reverse wrapper lookup: the node whose JS wrapper is <paramref name="jsObj"/>.</summary>
    DomNode? FindDomNodeByJSObject(JSObject jsObj);

    /// <summary>Registers <paramref name="doc"/> as both the node wrapper and the document wrapper for
    /// the sub-document root, so <c>ToJSObject(root)</c> and strict <c>=== doc</c> checks resolve.</summary>
    void RegisterDocumentWrapper(DomNode docRoot, JSObject doc);

    /// <summary>The JS wrapper already registered for <paramref name="node"/>, if any.</summary>
    bool TryGetNodeWrapper(DomNode node, out JSObject wrapper);

    /// <summary>Adopts a freshly-created, still-detached <paramref name="node"/> into the sub-document
    /// <paramref name="docRoot"/> (a canonical <c>DomDocument</c>) so its canonical
    /// <c>ownerDocument</c> is the sub-document, not the main document it was minted from (P4.4c).</summary>
    void AdoptDetachedNode(DomNode node, DomNode docRoot);

    // -------- node construction funnels --------
    DomElement CreateElement(string tagName);
    DomElement CreateElementNS(string ns, string localName);
    DomText CreateTextNode(string data);
    DomComment CreateComment(string data);
    DomDocumentType CreateDocumentType(string name, string publicId, string systemId);

    /// <summary>Mints a canonical <c>DomDocument</c> browsing-context root (P4.4a funnel).</summary>
    DomDocument CreateBrowsingContextDocument();

    /// <summary>Parses a leading DOCTYPE out of an HTML string for <c>document.write</c>.</summary>
    DomDocumentType? ParseDocType(string html);

    // -------- shared document sub-surface builders --------
    void SetElementTextContent(DomElement element, string? value);
    IReadOnlyList<DomElement> HitTestDocumentPoint(DomNode docRoot, double x, double y);

    /// <summary>The per-element <c>CSSStyleSheet</c> object, and whether the element has an
    /// associated sheet at all — the two services <see cref="DocumentCollectionBinding.StyleSheets"/>
    /// needs, mirroring <see cref="IDocumentCollectionHost"/>. They replace the sub-document's own
    /// <c>BuildStyleSheetsCollection</c>, which handed back a <c>JSArray</c> where CSSOM §6.1 requires
    /// a live <c>StyleSheetList</c>; see <see cref="SubDocumentCollectionHost"/>.</summary>
    JSObject BuildStyleSheetObject(DomElement styleElement);

    /// <inheritdoc cref="IDocumentCollectionHost.HasAssociatedStyleSheet"/>
    bool HasAssociatedStyleSheet(DomElement element);

    JSObject BuildRange(DomNode docRoot);

    /// <summary>This sub-document's own <c>Selection</c>, or <c>null</c> when the document has no
    /// browsing context to be selected in.</summary>
    JSValue GetSelection(DomNode docRoot);
    JSObject BuildTreeWalker(DomElement root, int whatToShow, JSFunction? filterFn);
    JSObject BuildNodeIterator(DomElement root, int whatToShow, JSFunction? filterFn);
    // The two tree-walking collectors this contract used to carry — CollectByTagName and
    // CollectMatching — are gone with the snapshot collections that were their only callers. A
    // sub-document's element list is now read the way the main document's is, from the root's own
    // InclusiveDescendants, so there is one definition of "the elements of this document" rather
    // than a second walk that could order or filter them differently.
    // Selector matching moved onto the host (Phase 2 item 4 de-globalization): it reads the per-bridge
    // `:checked` state, so it is now a bridge-instance method rather than a static helper.
    bool MatchesSelector(DomElement element, string selector, DomElement? scope = null);

    // -------- mutation seams (append/remove on the sub-document) --------
    List<DomNode> BuildChildNodeArgumentNodes(in Arguments arguments);
    void InsertNodeAt(DomNode parent, DomNode node, int index);
    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomElement parent, DomNode removedChild, int index);

    // -------- view transitions --------

    /// <summary>Runs <c>startViewTransition()</c> scoped to the sub-document rooted at
    /// <paramref name="docRoot"/> — a page drives a nested browsing context's transition through
    /// <c>frame.contentDocument</c>, and it must not touch the main document's.</summary>
    JSValue StartViewTransition(DomNode docRoot, in Arguments arguments);
}
