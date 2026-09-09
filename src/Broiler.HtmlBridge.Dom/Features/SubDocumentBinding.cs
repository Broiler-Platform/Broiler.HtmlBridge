using System.Text;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The nested-browsing-context <c>document</c> object feature binding (HtmlBridge complexity-reduction
/// roadmap Phase 3, P3.13) — the JS <c>document</c> surface built over a sub-document root node
/// (an <c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c> content document, a
/// <c>createDocument</c>/<c>createHTMLDocument</c> result, or the <c>DOMImplementation</c> factories on
/// the main document): documentElement/body/head/title/forms/childNodes, getElementById/
/// getElementsByTagName/querySelector(All)/elementFromPoint(s), createElement/TextNode/Comment/
/// ElementNS/Event, open/write, images/links/styleSheets, appendChild/removeChild/append/prepend,
/// <c>document.implementation</c> and createRange/TreeWalker/NodeIterator.
/// <para>
/// This slice is what P4.4b unblocked: after the <c>#subdoc-root</c> sentinel was severed, a
/// sub-document root is a canonical <see cref="Broiler.Dom.DomNode"/>/<see cref="Broiler.Dom.DomDocument"/>,
/// so the whole surface operates cleanly over a <c>DomNode docRoot</c>. The browsing-context
/// infrastructure (the sub-document/-window caches, the content-document maps, resource loading, onload
/// and the sub-<em>window</em> object) stays bridge-owned pending a future <c>BrowsingContextManager</c>;
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
    /// <see cref="Build"/> as an engine object, for the one caller that still holds one:
    /// <c>DomBridge/DomBridge.DocumentLevelFactoryHost.cs</c>, which unwraps it straight back into a
    /// handle for <see cref="IDocumentLevelFactoryHost.BuildDocument"/> and is another group's file
    /// this round.
    /// </summary>
    /// <remarks>
    /// It is a cast and not a conversion — the handle carries the engine's own object — so the
    /// document object that caller receives is the one this module built. It goes when that caller
    /// reads the handle directly. The bridge's own sub-document cache has stopped needing it: it
    /// stores what <see cref="Build"/> answers.
    /// </remarks>
    internal JavaScript.Runtime.JSObject BuildDocument(DomNode docRoot) =>
        Runtime.JsInterop.ToEngineObject(Build(docRoot));

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
            (in _) => DomBridge.GetDocumentElement(docRoot) is { } de ? _host.ToJsObject(de) : JsValue.Null,
            null);

        realm.DefineAccessor(doc, "scrollingElement",
            (in _) => DomBridge.GetDocumentElement(docRoot) is { } se ? _host.ToJsObject(se) : JsValue.Null,
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
            (in _) => docRoot.ChildNodes.Count > 0 ? _host.ToJsObject(DomBridge.ChildAt(docRoot, 0)) : JsValue.Null,
            null);

        // lastChild
        realm.DefineAccessor(doc, "lastChild",
            (in _) => docRoot.ChildNodes.Count > 0 ? _host.ToJsObject(DomBridge.ChildAt(docRoot, ^1)) : JsValue.Null,
            null);

        // hasChildNodes()
        realm.DefineValue(doc, "hasChildNodes",
            realm.NewMethod("hasChildNodes", (in _) => JsValue.Boolean(docRoot.ChildNodes.Count > 0), 0));

        // nodeType = DOCUMENT_NODE (9)
        realm.DefineAccessor(doc, "nodeType", (in _) => JsValue.Number(9), null);

        // nodeName = "#document"
        realm.DefineAccessor(doc, "nodeName", (in _) => JsValue.String("#document"), null);

        // localName = null for document
        realm.DefineAccessor(doc, "localName", (in _) => JsValue.Null, null);

        // getElementById(id)
        realm.DefineValue(doc, "getElementById",
            realm.NewMethod("getElementById", (in call) => GetElementById(docRoot, in call), 1));

        // getElementsByTagName(tag)
        realm.DefineValue(doc, "getElementsByTagName",
            realm.NewMethod("getElementsByTagName", (in call) => GetElementsByTagName(docRoot, in call), 1));

        // getElementsByClassName(names) / getElementsByName(name) — the two collection lookups a
        // frame's document was missing while the main document had them. A script in a frame is a
        // script like any other: absent, these read as undefined rather than as missing methods, so
        // calling one threw and took the frame's whole <script> with it.
        realm.DefineValue(doc, "getElementsByClassName",
            realm.NewMethod("getElementsByClassName", (in call) => GetElementsByClassName(docRoot, in call), 1));

        realm.DefineValue(doc, "getElementsByName",
            realm.NewMethod("getElementsByName", (in call) => GetElementsByName(docRoot, in call), 1));

        // createElement(tag)
        realm.DefineValue(doc, "createElement",
            realm.NewMethod("createElement", (in call) => CreateElement(docRoot, in call), 1));

        // createTextNode(text)
        realm.DefineValue(doc, "createTextNode",
            realm.NewMethod("createTextNode", (in call) => CreateTextNode(docRoot, in call), 1));

        // createComment(data)
        realm.DefineValue(doc, "createComment",
            realm.NewMethod("createComment", (in call) => CreateComment(docRoot, in call), 1));

        // createElementNS(ns, localName)
        realm.DefineValue(doc, "createElementNS",
            realm.NewMethod("createElementNS", (in call) => CreateElementNS(docRoot, in call), 2));

        // adoptNode(node) — the one document method that moves a node between documents rather than
        // copying it. A frame's document needs it as much as the page's: the interesting direction is
        // adopting *into* this document, which is exactly what the page's own adoptNode cannot do.
        realm.DefineValue(doc, "adoptNode",
            realm.NewMethod("adoptNode", (in call) => AdoptNode(docRoot, in call), 1));

        // createEvent(type)
        realm.DefineValue(doc, "createEvent",
            realm.NewMethod("createEvent", (in call) => CreateEvent(in call), 1));

        // querySelector / querySelectorAll
        realm.DefineValue(doc, "querySelector",
            realm.NewMethod("querySelector", (in call) => QuerySelector(docRoot, in call), 1));

        realm.DefineValue(doc, "querySelectorAll",
            realm.NewMethod("querySelectorAll", (in call) => QuerySelectorAll(docRoot, in call), 1));

        realm.DefineValue(doc, "elementFromPoint",
            realm.NewMethod("elementFromPoint", (in call) => ElementFromPoint(docRoot, in call), 2));

        realm.DefineValue(doc, "elementsFromPoint",
            realm.NewMethod("elementsFromPoint", (in call) => ElementsFromPoint(docRoot, in call), 2));

        // document.open()
        realm.DefineValue(doc, "open",
            realm.NewMethod("open", (in _) => Open(doc, docRoot), 0));

        // document.close() — a no-op, and constructable: the shared `UndefinedFunction` helper it was
        // built by minted plain function objects rather than bridge methods, so `new document.close()`
        // does not throw. A pre-existing deviation from the interface, spelled faithfully.
        realm.DefineValue(doc, "close",
            realm.NewConstructor("close", (in _) => JsValue.Undefined, 0));

        // document.write(html)
        realm.DefineValue(doc, "write",
            realm.NewMethod("write", (in call) => Write(docRoot, in call), 1));

        // removeChild on document
        realm.DefineValue(doc, "removeChild",
            realm.NewMethod("removeChild", (in call) => RemoveChild(docRoot, in call), 1));

        // appendChild on document
        realm.DefineValue(doc, "appendChild",
            realm.NewMethod("appendChild", (in call) => AppendChild(docRoot, in call), 1));

        realm.DefineValue(doc, "append",
            realm.NewMethod("append", (in call) => Append(docRoot, in call), 0));

        realm.DefineValue(doc, "prepend",
            realm.NewMethod("prepend", (in call) => Prepend(docRoot, in call), 0));

        // Node interface constants — types and the DOCUMENT_POSITION_* bits. On Node.prototype, which
        // this document object reaches through the HTMLDocument link above; it installs its own only
        // when the realm does not carry the interfaces.
        if (!_host.NodeInterfacePrototypesReady)
            NodeConstantsBinding.Install(realm, doc);

        // document.implementation on sub-documents
        var subImpl = realm.NewObject();
        realm.DefineValue(subImpl, "hasFeature",
            realm.NewConstructor("hasFeature", (in _) => JsValue.True, 2));
        realm.DefineValue(subImpl, "createDocumentType",
            realm.NewMethod("createDocumentType", (in call) => CreateDocumentType(in call), 3));
        realm.DefineValue(subImpl, "createDocument",
            realm.NewMethod("createDocument", (in call) => CreateDocument(in call), 3));
        realm.DefineValue(subImpl, "createHTMLDocument",
            realm.NewMethod("createHTMLDocument", (in call) => CreateHTMLDocument(in call), 1));
        realm.DefineValue(doc, "implementation", subImpl);

        // defaultView — return the main window object so getComputedStyle is accessible
        if (_host.MainWindow is { IsObject: true } mainWindow)
            realm.DefineValue(doc, "defaultView", mainWindow);

        // createTreeWalker(root, whatToShow, filter)
        realm.DefineValue(doc, "createTreeWalker",
            realm.NewMethod("createTreeWalker", (in call) => CreateTreeWalker(in call), 3));

        // createNodeIterator(root, whatToShow, filter)
        realm.DefineValue(doc, "createNodeIterator",
            realm.NewMethod("createNodeIterator", (in call) => CreateNodeIterator(in call), 3));

        // startViewTransition() — CSS View Transitions, scoped to this nested browsing context.
        // Absent here, a page driving a transition inside its <iframe> through contentDocument hit a
        // TypeError that aborted the rest of its script, so the main frame's own transition never ran
        // either (WPT css-view-transitions/iframe-and-main-frame-transition-*).
        realm.DefineValue(doc, "startViewTransition",
            realm.NewMethod("startViewTransition", (in call) => _host.StartViewTransition(docRoot, call[0]), 1));

        // createRange()
        realm.DefineValue(doc, "createRange",
            realm.NewMethod("createRange", (in _) => _host.BuildRange(docRoot), 0));

        // getSelection() — this document's own selection, distinct from the containing page's. The
        // method exists on every document, but only one being displayed has a selection to report, so
        // a createDocument/createHTMLDocument result answers null; the host draws that line.
        realm.DefineValue(doc, "getSelection",
            realm.NewMethod("getSelection", (in _) => _host.GetSelection(docRoot), 0));

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
                if (DomBridge.GetDocumentElement(docRoot) is { } documentElement)
                {
                    DomBridge.SetAttr(documentElement, "dir",
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
        if (DomBridge.GetDocumentElement(docRoot) is not { } documentElement ||
            !DomBridge.TryGetAttribute(documentElement, "dir", out var value))
            return string.Empty;

        var keyword = value.ToLowerInvariant();
        return keyword is "ltr" or "rtl" or "auto" ? keyword : string.Empty;
    }

    // -------- read-only document getters --------

    private JsValue GetBody(DomNode docRoot)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.Null;
        foreach (var child in DomBridge.ChildElements(htmlEl))
        {
            if (string.Equals(child.TagName, "body", StringComparison.OrdinalIgnoreCase))
                return _host.ToJsObject(child);
        }

        return JsValue.Null;
    }

    private JsValue GetHead(DomNode docRoot)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.Null;
        foreach (var child in DomBridge.ChildElements(htmlEl))
        {
            if (string.Equals(child.TagName, "head", StringComparison.OrdinalIgnoreCase))
                return _host.ToJsObject(child);
        }

        return JsValue.Null;
    }

    private static JsValue GetTitle(DomNode docRoot)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.String(string.Empty);
        var head = DomBridge.ChildElements(htmlEl).FirstOrDefault(c => string.Equals(c.TagName, "head", StringComparison.OrdinalIgnoreCase));
        if (head != null)
        {
            var titleEl = DomBridge.ChildElements(head).FirstOrDefault(c => string.Equals(c.TagName, "title", StringComparison.OrdinalIgnoreCase));
            if (titleEl != null)
            {
                var sb = new StringBuilder();
                DomBridge.CollectTextContent(titleEl, sb);
                return JsValue.String(sb.ToString());
            }
        }

        return JsValue.String(string.Empty);
    }

    private JsValue SetTitle(DomNode docRoot, in JsCall call)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JsValue.Undefined;
        var head = DomBridge.ChildElements(htmlEl).FirstOrDefault(c => string.Equals(c.TagName, "head", StringComparison.OrdinalIgnoreCase));
        if (head != null)
        {
            var titleEl = DomBridge.ChildElements(head).FirstOrDefault(c => string.Equals(c.TagName, "title", StringComparison.OrdinalIgnoreCase));
            if (titleEl != null)
                _host.SetElementTextContent(titleEl, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        }

        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>document.childNodes</c> on a sub-document — a <b>live</b> <c>NodeList</c> (DOM §4.4), as the
    /// containing document's is.
    /// </summary>
    /// <remarks>
    /// It includes every node type, notably the canonical <see cref="DomDocumentType"/> — which is no
    /// longer a <see cref="DomElement"/> after Phase 4 item 1, so <c>ChildElements</c> would wrongly
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
        var found = DomBridge.FindInSubTree(docRoot, el => el.Id == id);
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
                el => DomBridge.TryGetAttribute(el, "name", out var value) && string.Equals(value, name, StringComparison.Ordinal)));
    }

    private JsValue QuerySelector(DomNode docRoot, in JsCall call)
    {
        var selector = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        _host.ValidateSelector(selector);
        if (DomApiSyntax.CarriesPseudoElement(selector))
            return JsValue.Null;

        var found = DomBridge.FindInSubTree(docRoot, el => _host.MatchesSelector(el, selector));
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
                    if ((DomBridge.TryGetAttribute(element, "id", out var id) && id == name) ||
                        (DomBridge.TryGetAttribute(element, "name", out var named) && named == name))
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
    /// is a page passing strings, and the engine's own numeric view of an argument — which the shared
    /// <c>GetCoordinateArgument</c> this replaces read directly — is that coercion. It is spelled here
    /// rather than on the host because it reads nothing but the call frame.
    /// </remarks>
    private static double Coordinate(in JsCall call, int index) =>
        call.Length > index && !call[index].IsNullish ? call.Realm.ToNumber(call[index]) : double.NaN;
}
