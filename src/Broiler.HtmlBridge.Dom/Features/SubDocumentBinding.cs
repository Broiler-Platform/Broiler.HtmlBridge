using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The nested-browsing-context <c>document</c> object feature binding — the JS <c>document</c>
/// surface built over a sub-document root node
/// (an <c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c> content document, a
/// <c>createDocument</c>/<c>createHTMLDocument</c> result, or the <c>DOMImplementation</c> factories on
/// the main document): documentElement/body/head/title/forms/childNodes, getElementById/
/// getElementsByTagName/querySelector(All)/elementFromPoint(s), createElement/TextNode/Comment/
/// ElementNS/Event, open/write, images/links/styleSheets, appendChild/removeChild/append/prepend,
/// <c>document.implementation</c> and createRange/TreeWalker/NodeIterator.
/// <para>
/// A sub-document root is a canonical <see cref="Broiler.Dom.DomNode"/>/<see cref="Broiler.Dom.DomDocument"/>,
/// so the whole surface operates cleanly over a <c>DomNode docRoot</c>. The browsing-context
/// state (the sub-document/-window caches and the content-document maps) is
/// <c>BrowsingContextManager</c>'s and the sub-<em>window</em> object <c>SubWindowBinding</c>'s; resource
/// loading and onload dispatch stay bridge-owned;
/// the module reaches the bridge only through the explicit <see cref="ISubDocumentHost"/> contract and
/// the assembly's neutral static <c>DomBridge</c> tree/selector helpers.
/// </para>
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): objects and functions are minted
/// through the realm, an argument frame arrives as a <see cref="JsCall"/>, and every argument read goes
/// through the realm's <c>ToString</c>/<c>ToNumber</c> rather than the handle's, because that is the
/// coercion a page observes — <c>getElementById({toString(){…}})</c> has always run the object's own
/// <c>toString</c> here, and the handle's rendering deliberately does not.
/// </remarks>
internal sealed partial class SubDocumentBinding(ISubDocumentHost host)
{
    private readonly ISubDocumentHost _host = host;

    /// <summary>
    /// Builds the JS <c>document</c> object for the sub-document rooted at <paramref name="docRoot"/> and
    /// registers it as that root's wrapper identity. Was <c>DomBridge.BuildSubDocument</c>.
    /// </summary>
    internal JsValue Build(DomNode docRoot)
    {
        var realm = _host.Realm;
        var doc = realm.NewObject();
        _host.RegisterDocumentWrapper(docRoot, doc);

        // A frame's document is a document like any other, so it reports HTMLDocument too. Built
        // here rather than minted as a node wrapper, so it needs the explicit link.
        _host.LinkToInterface(doc, "HTMLDocument");

        // This sub-document projected onto the contract DocumentCollectionBinding consumes, so its
        // collections are the main document's, over this root's sub-tree.
        var collections = new SubDocumentCollectionHost(_host, docRoot);

        realm.DefineAccessor(doc, "documentElement",
            (in _) => DomBridgeUtils.GetDocumentElement(docRoot) is { } de ? _host.ToJsObject(de) : JsValue.Null,
            null);

        realm.DefineAccessor(doc, "scrollingElement",
            (in _) => DomBridgeUtils.GetDocumentElement(docRoot) is { } se ? _host.ToJsObject(se) : JsValue.Null,
            null);

        // body
        realm.DefineAccessor(doc, "body", (in _) => GetBody(docRoot), null);

        // head
        realm.DefineAccessor(doc, "head", (in _) => GetHead(docRoot), null);

        // title (dynamic getter from <title> element in <head>)
        realm.DefineAccessor(doc, "title",
            (in _) => GetTitle(docRoot),
            (in call) => SetTitle(docRoot, in call));

        // forms/images/links/anchors/scripts/embeds/plugins/styleSheets — the document collection
        // family, built by the shared binding rather than by this module's own snapshot builders.
        RegisterCollections(realm, doc, collections);

        // doctype/dir/designMode — the three document metadata accessors that came with that family.
        RegisterMetadata(realm, doc, docRoot);

        // childNodes
        realm.DefineAccessor(doc, "childNodes", (in _) => GetChildNodes(docRoot), null);

        // firstChild
        realm.DefineAccessor(doc, "firstChild",
            (in _) => docRoot.ChildNodes.Count > 0 ? _host.ToJsObject(DomBridgeUtils.ChildAt(docRoot, 0)) : JsValue.Null,
            null);

        // lastChild
        realm.DefineAccessor(doc, "lastChild",
            (in _) => docRoot.ChildNodes.Count > 0 ? _host.ToJsObject(DomBridgeUtils.ChildAt(docRoot, ^1)) : JsValue.Null,
            null);

        // hasChildNodes()
        realm.DefineMethod(doc, "hasChildNodes", 0, (in _) => JsValue.Boolean(docRoot.ChildNodes.Count > 0));

        // nodeType = DOCUMENT_NODE (9)
        realm.DefineAccessor(doc, "nodeType", (in _) => JsValue.Number(9), null);

        // nodeName = "#document"
        realm.DefineAccessor(doc, "nodeName", (in _) => JsValue.String("#document"), null);

        // localName = null for document
        realm.DefineAccessor(doc, "localName", (in _) => JsValue.Null, null);

        // getElementById(id)
        realm.DefineMethod(doc, "getElementById", 1, (in call) => GetElementById(docRoot, in call));

        // getElementsByTagName(tag)
        realm.DefineMethod(doc, "getElementsByTagName", 1, (in call) => GetElementsByTagName(docRoot, in call));

        // getElementsByClassName(names) / getElementsByName(name) — the two collection lookups a
        // frame's document was missing while the main document had them. A script in a frame is a
        // script like any other: absent, these read as undefined rather than as missing methods, so
        // calling one threw and took the frame's whole <script> with it.
        realm.DefineMethod(doc, "getElementsByClassName", 1, (in call) => GetElementsByClassName(docRoot, in call));

        realm.DefineMethod(doc, "getElementsByName", 1, (in call) => GetElementsByName(docRoot, in call));

        // createElement(tag)
        realm.DefineMethod(doc, "createElement", 1, (in call) => CreateElement(docRoot, in call));

        // createTextNode(text)
        realm.DefineMethod(doc, "createTextNode", 1, (in call) => CreateTextNode(docRoot, in call));

        // createComment(data)
        realm.DefineMethod(doc, "createComment", 1, (in call) => CreateComment(docRoot, in call));

        // createElementNS(ns, localName)
        realm.DefineMethod(doc, "createElementNS", 2, (in call) => CreateElementNS(docRoot, in call));

        // adoptNode(node) — the one document method that moves a node between documents rather than
        // copying it. A frame's document needs it as much as the page's: the interesting direction is
        // adopting *into* this document, which is exactly what the page's own adoptNode cannot do.
        realm.DefineMethod(doc, "adoptNode", 1, (in call) => AdoptNode(docRoot, in call));

        // createEvent(type)
        realm.DefineMethod(doc, "createEvent", 1, LegacyEventBinding.Create);

        // querySelector / querySelectorAll
        realm.DefineMethod(doc, "querySelector", 1, (in call) => QuerySelector(docRoot, in call));

        realm.DefineMethod(doc, "querySelectorAll", 1, (in call) => QuerySelectorAll(docRoot, in call));

        realm.DefineMethod(doc, "elementFromPoint", 2, (in call) => ElementFromPoint(docRoot, in call));

        realm.DefineMethod(doc, "elementsFromPoint", 2, (in call) => ElementsFromPoint(docRoot, in call));

        // document.open()
        realm.DefineMethod(doc, "open", 0, (in _) => Open(doc, docRoot));

        // document.close() — a no-op, and constructable: it is minted as a plain function object
        // rather than a bridge method, so `new document.close()` does not throw. A pre-existing
        // deviation from the interface, spelled faithfully.
        realm.DefineConstructor(doc, "close", 0, (in _) => JsValue.Undefined);

        // document.write(html)
        realm.DefineMethod(doc, "write", 1, (in call) => Write(docRoot, in call));

        // removeChild on document
        realm.DefineMethod(doc, "removeChild", 1, (in call) => RemoveChild(docRoot, in call));

        // appendChild on document
        realm.DefineMethod(doc, "appendChild", 1, (in call) => AppendChild(docRoot, in call));

        realm.DefineMethod(doc, "append", 0, (in call) => Append(docRoot, in call));

        realm.DefineMethod(doc, "prepend", 0, (in call) => Prepend(docRoot, in call));

        // Node interface constants — types and the DOCUMENT_POSITION_* bits. On Node.prototype, which
        // this document object reaches through the HTMLDocument link above; it installs its own only
        // when the realm does not carry the interfaces.
        if (!_host.NodeInterfacePrototypesReady)
            NodeConstantsBinding.Install(realm, doc);

        // document.implementation on sub-documents
        var subImpl = realm.NewObject();
        realm.DefineConstructor(subImpl, "hasFeature", 2, (in _) => JsValue.True);
        realm.DefineMethod(subImpl, "createDocumentType", 3, (in call) => CreateDocumentType(in call));
        realm.DefineMethod(subImpl, "createDocument", 3, (in call) => CreateDocument(in call));
        realm.DefineMethod(subImpl, "createHTMLDocument", 1, (in call) => CreateHTMLDocument(in call));
        realm.DefineValue(doc, "implementation", subImpl);

        // defaultView — return the main window object so getComputedStyle is accessible
        if (_host.MainWindow is { IsObject: true } mainWindow)
            realm.DefineValue(doc, "defaultView", mainWindow);

        // createTreeWalker(root, whatToShow, filter)
        realm.DefineMethod(doc, "createTreeWalker", 3, (in call) => CreateTreeWalker(in call));

        // createNodeIterator(root, whatToShow, filter)
        realm.DefineMethod(doc, "createNodeIterator", 3, (in call) => CreateNodeIterator(in call));

        // startViewTransition() — CSS View Transitions, scoped to this nested browsing context.
        // Absent here, a page driving a transition inside its <iframe> through contentDocument hit a
        // TypeError that aborted the rest of its script, so the main frame's own transition never ran
        // either (WPT css-view-transitions/iframe-and-main-frame-transition-*).
        realm.DefineMethod(doc, "startViewTransition", 1, (in call) => _host.StartViewTransition(docRoot, call[0]));

        // createRange()
        realm.DefineMethod(doc, "createRange", 0, (in _) => _host.BuildRange(docRoot));

        // getSelection() — this document's own selection, distinct from the containing page's. The
        // method exists on every document, but only one being displayed has a selection to report, so
        // a createDocument/createHTMLDocument result answers null; the host draws that line.
        realm.DefineMethod(doc, "getSelection", 0, (in _) => _host.GetSelection(docRoot));

        return doc;
    }

    // -------- the document collection family --------

    /// <summary>
    /// Registers this sub-document's eight live collections through
    /// <see cref="DocumentCollectionBinding"/> — the same builders, the same interfaces and the same
    /// identity rule the containing document uses.
    /// </summary>
    /// <remarks>
    /// Each collection object is built once, on first read, and closed over, so the getter hands back
    /// the same object every time: <c>d.forms === d.forms</c>, which the snapshot arrays this replaces
    /// answered <see langword="false"/>. <c>embeds</c> and <c>plugins</c> share one local because
    /// HTML §3.1.5 requires them to return the same object, not merely equal ones. Building lazily
    /// matters more here than on the main document: a sub-document can be minted by
    /// <c>createHTMLDocument</c> at any point, but the interface constructors these take their
    /// prototypes from are registered once during attach, so an eager build during attach — while an
    /// <c>&lt;iframe&gt;</c>'s content document is being wired — would capture no prototype at all.
    /// </remarks>
    private void RegisterCollections(IJsRealm realm, JsValue doc, IDocumentCollectionHost collections)
    {
        Live("forms", DocumentCollectionKind.Forms);
        Live("images", DocumentCollectionKind.Images);
        Live("links", DocumentCollectionKind.Links);
        Live("anchors", DocumentCollectionKind.Anchors);
        Live("scripts", DocumentCollectionKind.Scripts);
        Live("styleSheets", DocumentCollectionKind.StyleSheets);

        // Missing, not undefined, as the "not built yet" mark: a builder that answered undefined would
        // then be re-asked on every read, and the two names below have to answer one object.
        var embeds = JsValue.Missing;
        JsValue Embeds() =>
            embeds.IsMissing ? embeds = _host.DocumentCollection(collections, DocumentCollectionKind.Embeds) : embeds;
        Getter("embeds", Embeds);
        Getter("plugins", Embeds);

        void Live(string name, DocumentCollectionKind kind)
        {
            var collection = JsValue.Missing;
            Getter(name, () =>
                collection.IsMissing ? collection = _host.DocumentCollection(collections, kind) : collection);
        }

        void Getter(string name, Func<JsValue> read) =>
            realm.DefineAccessor(doc, name, (in _) => read(), null);
    }

    /// <summary>
    /// <c>doctype</c>, <c>dir</c> and <c>designMode</c> on a sub-document — the metadata accessors the
    /// containing document gained beside its collections, and which a frame's document was missing for
    /// the same reason: nothing ever registered them.
    /// </summary>
    /// <remarks>
    /// <c>doctype</c> matters most of the three, and for the same reason it did on the main document:
    /// the node is already there — <c>createDocument(ns, qname, doctype)</c> appends it and
    /// <c>d.firstChild</c> returns it — so it was reachable by position and not by the name DOM §4.5
    /// gives it. <c>designMode</c> is per-document state (HTML §3.2.7), so each sub-document carries
    /// its own rather than sharing the containing document's.
    /// </remarks>
    private void RegisterMetadata(IJsRealm realm, JsValue doc, DomNode docRoot)
    {
        realm.DefineAccessor(doc, "doctype",
            (in _) => DocumentTypeNode(docRoot) is { } doctype ? _host.ToJsObject(doctype) : JsValue.Null,
            null);

        // HTML §3.2.6: the getter is limited to only known values — the canonical lower-case keyword
        // or the empty string — while the setter writes the assigned text through unchanged.
        realm.DefineAccessor(doc, "dir",
            (in _) => JsValue.String(DocumentDirection(docRoot)),
            (in call) =>
            {
                if (DomBridgeUtils.GetDocumentElement(docRoot) is { } documentElement)
                {
                    DomBridgeUtils.SetAttr(documentElement, "dir",
                        call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
                }

                return JsValue.Undefined;
            });

        // HTML §3.2.7: an enumerated document state rather than an attribute. Anything but "on"/"off"
        // (ASCII case-insensitively) is ignored rather than stored.
        var designMode = "off";
        realm.DefineAccessor(doc, "designMode",
            (in _) => JsValue.String(designMode),
            (in call) =>
            {
                var requested = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
                if (string.Equals(requested, "on", StringComparison.OrdinalIgnoreCase))
                    designMode = "on";
                else if (string.Equals(requested, "off", StringComparison.OrdinalIgnoreCase))
                    designMode = "off";
                return JsValue.Undefined;
            });
    }

    /// <summary>This sub-document's <see cref="DomDocumentType"/> child, or <see langword="null"/>.</summary>
    private static DomDocumentType? DocumentTypeNode(DomNode docRoot)
    {
        foreach (var child in docRoot.ChildNodes)
        {
            if (child is DomDocumentType doctype)
                return doctype;
        }

        return null;
    }

    /// <summary>The document element's <c>dir</c>, limited to the three keywords HTML defines.</summary>
    private static string DocumentDirection(DomNode docRoot)
    {
        if (DomBridgeUtils.GetDocumentElement(docRoot) is not { } documentElement ||
            !DomBridgeUtils.TryGetAttribute(documentElement, "dir", out var value))
            return string.Empty;

        var keyword = value.ToLowerInvariant();
        return keyword is "ltr" or "rtl" or "auto" ? keyword : string.Empty;
    }

    // -------- read-only document getters --------

    private JsValue GetBody(DomNode docRoot)
    {
        var htmlEl = DomBridgeUtils.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.Null;
        foreach (var child in DomBridgeUtils.ChildElements(htmlEl))
        {
            if (string.Equals(child.TagName, "body", StringComparison.OrdinalIgnoreCase))
                return _host.ToJsObject(child);
        }

        return JsValue.Null;
    }

    private JsValue GetHead(DomNode docRoot)
    {
        var htmlEl = DomBridgeUtils.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.Null;
        foreach (var child in DomBridgeUtils.ChildElements(htmlEl))
        {
            if (string.Equals(child.TagName, "head", StringComparison.OrdinalIgnoreCase))
                return _host.ToJsObject(child);
        }

        return JsValue.Null;
    }

    private static JsValue GetTitle(DomNode docRoot)
    {
        var htmlEl = DomBridgeUtils.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.String(string.Empty);
        var head = DomBridgeUtils.ChildElements(htmlEl).FirstOrDefault(c => string.Equals(c.TagName, "head", StringComparison.OrdinalIgnoreCase));
        if (head != null)
        {
            var titleEl = DomBridgeUtils.ChildElements(head).FirstOrDefault(c => string.Equals(c.TagName, "title", StringComparison.OrdinalIgnoreCase));
            if (titleEl != null)
                return JsValue.String(titleEl.TextContent);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue SetTitle(DomNode docRoot, in JsCall call)
    {
        var htmlEl = DomBridgeUtils.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.Undefined;
        var head = DomBridgeUtils.ChildElements(htmlEl).FirstOrDefault(c => string.Equals(c.TagName, "head", StringComparison.OrdinalIgnoreCase));
        if (head != null)
        {
            var titleEl = DomBridgeUtils.ChildElements(head).FirstOrDefault(c => string.Equals(c.TagName, "title", StringComparison.OrdinalIgnoreCase));
            if (titleEl != null)
                titleEl.TextContent = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        }

        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>document.childNodes</c> on a sub-document — a <b>live</b> <c>NodeList</c> (DOM §4.4), as the
    /// containing document's is.
    /// </summary>
    /// <remarks>
    /// It includes every node type, notably the canonical <see cref="DomDocumentType"/> — which is not
    /// a <see cref="DomElement"/>, so <c>ChildElements</c> would wrongly
    /// drop it. That matches the sub-document's <c>firstChild</c> (the raw first child) and the main
    /// document's <c>childNodes</c>.
    /// </remarks>
    private JsValue GetChildNodes(DomNode docRoot) =>
        _host.NodeList(() =>
        {
            var children = new List<JsValue>();
            foreach (var child in docRoot.ChildNodes)
                children.Add(_host.ToJsObject(child));
            return children;
        });

    private JsValue GetElementById(DomNode docRoot, in JsCall call)
    {
        var id = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        var found = DomBridgeUtils.FindInSubTree(docRoot, el => el.Id == id);
        return found != null ? _host.ToJsObject(found) : JsValue.Null;
    }

    /// <summary>
    /// <c>getElementsByTagName(name)</c> on a frame's document — a <b>live</b> <c>HTMLCollection</c>
    /// (DOM §4.5), as the containing document's is.
    /// </summary>
    private JsValue GetElementsByTagName(DomNode docRoot, in JsCall call)
    {
        var tagName = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        return LiveCollection(
            docRoot,
            el => tagName == "*" || string.Equals(el.TagName, tagName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>getElementsByClassName</c> on a frame's document — a <b>live</b> <c>HTMLCollection</c>
    /// (DOM §4.5). It reuses <see cref="ClassNameSet"/>, the rule the main document and the
    /// element-scoped search already share, so all three surfaces answer a class query the same way.
    /// </summary>
    private JsValue GetElementsByClassName(DomNode docRoot, in JsCall call)
    {
        var wanted = ClassNameSet.Parse(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return LiveCollection(docRoot, el => wanted.Length > 0 && ClassNameSet.Matches(el, wanted));
    }

    /// <summary>
    /// <c>getElementsByName</c> on a frame's document — the elements whose <c>name</c> attribute is
    /// identical to the argument, matching the main document's implementation (HTML §3.1.5): a live
    /// <c>NodeList</c>, the one by-name lookup the specification types as a NodeList rather than an
    /// <c>HTMLCollection</c>.
    /// </summary>
    private JsValue GetElementsByName(DomNode docRoot, in JsCall call)
    {
        var name = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        return _host.NodeList(
            () => Wrappers(
                docRoot,
                el => DomBridgeUtils.TryGetAttribute(el, "name", out var value) && string.Equals(value, name, StringComparison.Ordinal)));
    }

    private JsValue QuerySelector(DomNode docRoot, in JsCall call)
    {
        var selector = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        _host.ValidateSelector(selector);
        if (DomApiSyntax.CarriesPseudoElement(selector))
            return JsValue.Null;

        var found = DomBridgeUtils.FindInSubTree(docRoot, el => _host.MatchesSelector(el, selector));
        return found != null ? _host.ToJsObject(found) : JsValue.Null;
    }

    /// <summary>
    /// <c>querySelectorAll(selector)</c> on a frame's document — a <b>static</b> <c>NodeList</c>
    /// (DOM §4.2.6), the one collection the specification defines as a snapshot rather than live. The
    /// members are resolved once, here, and the list closes over that result.
    /// </summary>
    private JsValue QuerySelectorAll(DomNode docRoot, in JsCall call)
    {
        var selector = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        _host.ValidateSelector(selector);
        var results = DomApiSyntax.CarriesPseudoElement(selector)
            ? []
            : Wrappers(docRoot, el => _host.MatchesSelector(el, selector));
        return _host.NodeList(() => results);
    }

    /// <summary>
    /// A live <c>HTMLCollection</c> over the elements of this sub-document matching
    /// <paramref name="predicate"/>, with the named getter DOM §4.2.10.2 gives one — by <c>id</c>,
    /// then by <c>name</c>, taking the first member in tree order that answers to either.
    /// </summary>
    private JsValue LiveCollection(DomNode docRoot, Func<DomElement, bool> predicate) =>
        _host.HtmlCollection(
            () => Wrappers(docRoot, predicate),
            name =>
            {
                if (name.Length == 0)
                    return null;

                foreach (var element in Members(docRoot, predicate))
                {
                    if ((DomBridgeUtils.TryGetAttribute(element, "id", out var id) && id == name) ||
                        (DomBridgeUtils.TryGetAttribute(element, "name", out var named) && named == name))
                        return _host.ToJsObject(element);
                }

                return null;
            });

    /// <summary>The matching elements of this sub-document, in tree order, recomputed per call — which
    /// is what makes a collection built over it live.</summary>
    private static List<DomElement> Members(DomNode docRoot, Func<DomElement, bool> predicate)
    {
        var members = new List<DomElement>();
        foreach (var element in docRoot.InclusiveDescendants().OfType<DomElement>())
        {
            if (predicate(element))
                members.Add(element);
        }

        return members;
    }

    /// <summary><see cref="Members"/>, as JS wrappers.</summary>
    private List<JsValue> Wrappers(DomNode docRoot, Func<DomElement, bool> predicate)
    {
        var wrappers = new List<JsValue>();
        foreach (var member in Members(docRoot, predicate))
            wrappers.Add(_host.ToJsObject(member));
        return wrappers;
    }

    private JsValue ElementFromPoint(DomNode docRoot, in JsCall call)
    {
        var hit = _host.HitTestDocumentPoint(docRoot, Coordinate(in call, 0), Coordinate(in call, 1)).FirstOrDefault();
        return hit != null ? _host.ToJsObject(hit) : JsValue.Null;
    }

    private JsValue ElementsFromPoint(DomNode docRoot, in JsCall call)
    {
        var hits = _host.HitTestDocumentPoint(docRoot, Coordinate(in call, 0), Coordinate(in call, 1));
        var wrappers = new JsValue[hits.Count];
        for (var i = 0; i < hits.Count; i++)
            wrappers[i] = _host.ToJsObject(hits[i]);
        return call.Realm.NewArray(wrappers);
    }

    /// <summary>
    /// A hit-testing coordinate argument: absent, <c>null</c> or <c>undefined</c> is
    /// <see cref="double.NaN"/> — which the hit test rejects as not finite — and anything else is
    /// coerced.
    /// </summary>
    /// <remarks>
    /// The realm's <c>ToNumber</c>, not the handle's <c>AsNumber</c>: <c>elementFromPoint("10", "20")</c>
    /// is a page passing strings, and the realm's coercion is the one ECMAScript performs. It is
    /// spelled here rather than on the host because it reads nothing but the call frame.
    /// </remarks>
    private static double Coordinate(in JsCall call, int index) =>
        call.Length > index && !call[index].IsNullish ? call.Realm.ToNumber(call[index]) : double.NaN;
}
