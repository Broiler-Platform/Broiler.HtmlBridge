using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// <para>
/// The whole contract is spelled in JSEAL: a JS object is a <see cref="JsValue"/>, and the
/// <c>JSContext</c> this interface used to carry beside its <see cref="Realm"/> is gone. It was there
/// for four things and only four — raising a <c>DOMException</c> from the two name validations and the
/// selector check, and being handed straight back to the two collection builders — so each is now a
/// named operation instead (<see cref="ValidateElementName"/>, <see cref="ValidateQualifiedName"/>,
/// <see cref="ValidateSelector"/>, <see cref="NodeList"/>/<see cref="HtmlCollection"/>/
/// <see cref="DocumentCollection"/>). The module says what it wants done and which engine does it is
/// the bridge's business.
/// </para>
/// <para>
/// The renames are part of that: a member called <c>ToJSObject</c> or <c>FindDomNodeByJSObject</c>
/// names an engine type in every call site that mentions it, so they are
/// <see cref="ToJsObject"/>, <see cref="FindNode"/> and <see cref="FindElement"/> here — the shape
/// <c>ITraversalHost</c> already took.
/// </para>
/// </remarks>
internal interface ISubDocumentHost
{
    /// <summary>
    /// The realm every object this module builds belongs to, and through which it raises errors.
    /// Never null while a document is attached; the sub-document surface is only reachable after
    /// attach.
    /// </summary>
    Jseal.IJsRealm Realm { get; }

    /// <summary>
    /// The main window object, used for the sub-document's <c>defaultView</c>, or a non-object when
    /// the bridge has no window yet.
    /// </summary>
    JsValue MainWindow { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue ToJsObject(DomNode node);

    /// <summary>Points a wrapper at a named interface's prototype. A sub-document object is built
    /// rather than minted as a node wrapper, so it does not pass the choke point that links every
    /// other one.</summary>
    void LinkToInterface(JsValue wrapper, string interfaceName);

    /// <summary>
    /// Whether the node interface prototypes carry their members, so a linked wrapper inherits the
    /// <c>Node</c> constants instead of needing its own eighteen copies.
    /// </summary>
    bool NodeInterfacePrototypesReady { get; }

    /// <summary>Reverse wrapper lookup: the element whose JS wrapper is <paramref name="wrapper"/>.</summary>
    DomElement? FindElement(JsValue wrapper);

    /// <summary>Reverse wrapper lookup: the node whose JS wrapper is <paramref name="wrapper"/>.</summary>
    DomNode? FindNode(JsValue wrapper);

    /// <summary>Registers <paramref name="doc"/> as both the node wrapper and the document wrapper for
    /// the sub-document root, so <c>ToJsObject(root)</c> and strict <c>=== doc</c> checks resolve.</summary>
    void RegisterDocumentWrapper(DomNode docRoot, JsValue doc);

    /// <summary>The JS wrapper already registered for <paramref name="node"/>, if any.</summary>
    bool TryGetNodeWrapper(DomNode node, out JsValue wrapper);

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

    // -------- name and selector validation --------

    /// <summary>Throws an <c>InvalidCharacterError</c> when <paramref name="name"/> is not a valid
    /// element name (DOM's Name production).</summary>
    void ValidateElementName(string name);

    /// <summary>Throws an <c>InvalidCharacterError</c> or a <c>NamespaceError</c> when
    /// <paramref name="qualifiedName"/> is malformed, or is inconsistent with <paramref name="ns"/>.</summary>
    void ValidateQualifiedName(string qualifiedName, string? ns);

    /// <summary>
    /// Throws a <c>SyntaxError</c> <c>DOMException</c> when <paramref name="selector"/> is not a valid
    /// selector list (DOM §4.2.6), and returns quietly when it is. A no-op before the bridge is
    /// attached, as it always has been — there is then no realm to raise a <c>DOMException</c> in.
    /// </summary>
    void ValidateSelector(string selector);

    // -------- collections --------

    /// <summary>
    /// A <c>NodeList</c> over what <paramref name="contents"/> answers. The function is re-asked on
    /// every read, so passing one that recomputes makes the list live and passing one that closes over
    /// a fixed list makes it the snapshot <c>querySelectorAll</c> is specified to be.
    /// </summary>
    JsValue NodeList(Func<List<JsValue>> contents);

    /// <summary>
    /// An <c>HTMLCollection</c> over what <paramref name="contents"/> answers — always live, as every
    /// collection typed <c>HTMLCollection</c> is. <paramref name="namedLookup"/> answers the named
    /// getter, returning the matching wrapper or <see langword="null"/> for a name it does not serve.
    /// </summary>
    JsValue HtmlCollection(Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup);

    /// <summary>
    /// One of the shared <c>document</c> collections, built by <see cref="DocumentCollectionBinding"/>
    /// over <paramref name="collections"/> — the same builder, interface and identity rule the
    /// containing document uses.
    /// </summary>
    /// <remarks>
    /// Asked for by name rather than built here because that builder still takes the script context;
    /// naming the collection is what keeps this contract, and the module over it, free of it. The
    /// eight collections are seven kinds because <c>embeds</c> and <c>plugins</c> are required to
    /// answer the same object (HTML §3.1.5), so the module builds <see cref="DocumentCollectionKind.Embeds"/>
    /// once and installs it twice.
    /// </remarks>
    JsValue DocumentCollection(IDocumentCollectionHost collections, DocumentCollectionKind kind);

    /// <summary>The per-element <c>CSSStyleSheet</c> object, and whether the element has an
    /// associated sheet at all — the two services <see cref="DocumentCollectionBinding.StyleSheets"/>
    /// needs, mirroring <see cref="IDocumentCollectionHost"/>. They replace the sub-document's own
    /// <c>BuildStyleSheetsCollection</c>, which handed back a snapshot array where CSSOM §6.1 requires
    /// a live <c>StyleSheetList</c>; see <see cref="SubDocumentCollectionHost"/>.</summary>
    JsValue BuildStyleSheetObject(DomElement styleElement);

    /// <inheritdoc cref="IDocumentCollectionHost.HasAssociatedStyleSheet"/>
    bool HasAssociatedStyleSheet(DomElement element);

    // -------- shared document sub-surface builders --------
    void SetElementTextContent(DomElement element, string? value);
    IReadOnlyList<DomElement> HitTestDocumentPoint(DomNode docRoot, double x, double y);

    JsValue BuildRange(DomNode docRoot);

    /// <summary>This sub-document's own <c>Selection</c>, or <c>null</c> when the document has no
    /// browsing context to be selected in.</summary>
    JsValue GetSelection(DomNode docRoot);

    /// <summary>A <c>TreeWalker</c>/<c>NodeIterator</c> over <paramref name="root"/>. No filter is
    /// <see cref="JsValue.Missing"/>, which is what the traversal module tests callability against.</summary>
    JsValue BuildTreeWalker(DomElement root, int whatToShow, JsValue filter);

    /// <inheritdoc cref="BuildTreeWalker"/>
    JsValue BuildNodeIterator(DomElement root, int whatToShow, JsValue filter);

    // The two tree-walking collectors this contract used to carry — CollectByTagName and
    // CollectMatching — are gone with the snapshot collections that were their only callers. A
    // sub-document's element list is now read the way the main document's is, from the root's own
    // InclusiveDescendants, so there is one definition of "the elements of this document" rather
    // than a second walk that could order or filter them differently.
    // Selector matching moved onto the host (Phase 2 item 4 de-globalization): it reads the per-bridge
    // `:checked` state, so it is now a bridge-instance method rather than a static helper.
    bool MatchesSelector(DomElement element, string selector, DomElement? scope = null);

    // -------- mutation seams (append/remove on the sub-document) --------

    /// <summary>
    /// The nodes an <c>append</c>/<c>prepend</c> argument list denotes: a node argument is its own
    /// wrapper's node (a <c>DocumentFragment</c> contributing its children), and anything else is
    /// coerced to a string and minted as a text node.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments);

    void InsertNodeAt(DomNode parent, DomNode node, int index);
    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomElement parent, DomNode removedChild, int index);

    // -------- view transitions --------

    /// <summary>Runs <c>startViewTransition()</c> scoped to the sub-document rooted at
    /// <paramref name="docRoot"/> — a page drives a nested browsing context's transition through
    /// <c>frame.contentDocument</c>, and it must not touch the main document's.</summary>
    /// <param name="options">The single argument the operation takes: the update callback, or an
    /// options object carrying it, or a non-object when the page passed neither.</param>
    JsValue StartViewTransition(DomNode docRoot, JsValue options);
}

/// <summary>
/// Which of the shared <c>document</c> collections <see cref="ISubDocumentHost.DocumentCollection"/>
/// is being asked for. One kind per <see cref="DocumentCollectionBinding"/> builder.
/// </summary>
internal enum DocumentCollectionKind
{
    Forms,
    Images,
    Links,
    Anchors,
    Scripts,
    StyleSheets,
    Embeds,
}
