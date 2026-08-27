using System.Text;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.Dom;

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
internal sealed partial class SubDocumentBinding(ISubDocumentHost host)
{
    private readonly ISubDocumentHost _host = host;

    /// <summary>
    /// Builds the JS <c>document</c> object for the sub-document rooted at <paramref name="docRoot"/> and
    /// registers it as that root's wrapper identity. Was <c>DomBridge.BuildSubDocument</c>.
    /// </summary>
    internal JSObject BuildDocument(DomNode docRoot)
    {
        var doc = new JSObject();
        _host.RegisterDocumentWrapper(docRoot, doc);

        // A frame's document is a document like any other, so it reports HTMLDocument too. Built
        // here rather than minted as a node wrapper, so it needs the explicit link.
        _host.LinkToInterface(doc, "HTMLDocument");

        // This sub-document projected onto the contract DocumentCollectionBinding consumes, so its
        // collections are the main document's, over this root's sub-tree.
        var collections = new SubDocumentCollectionHost(_host, docRoot);

        doc.FastAddProperty("documentElement",
            new DomFunction((in _) => DomBridge.GetDocumentElement(docRoot) is { } de ? _host.ToJSObject(de) : JSNull.Value, "get documentElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        doc.FastAddProperty("scrollingElement",
            new DomFunction((in _) => DomBridge.GetDocumentElement(docRoot) is { } se ? _host.ToJSObject(se) : JSNull.Value, "get scrollingElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // body
        doc.FastAddProperty("body",
            new DomFunction((in _) => GetBody(docRoot), "get body"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // head
        doc.FastAddProperty("head",
            new DomFunction((in _) => GetHead(docRoot), "get head"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // title (dynamic getter from <title> element in <head>)
        doc.FastAddProperty("title",
            new DomFunction((in _) => GetTitle(docRoot), "get title"),
            new DomFunction((in a) => SetTitle(docRoot, in a), "set title"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // forms/images/links/anchors/scripts/embeds/plugins/styleSheets — the document collection
        // family, built by the shared binding rather than by this module's own snapshot builders.
        RegisterCollections(doc, collections, _host.JsContext);

        // doctype/dir/designMode — the three document metadata accessors that came with that family.
        RegisterMetadata(doc, docRoot);

        // childNodes
        doc.FastAddProperty("childNodes",
            new DomFunction((in _) => GetChildNodes(docRoot), "get childNodes"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // firstChild
        doc.FastAddProperty("firstChild",
            new DomFunction((in _) => docRoot.ChildNodes.Count > 0 ? _host.ToJSObject(DomBridge.ChildAt(docRoot, 0)) : JSNull.Value, "get firstChild"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // lastChild
        doc.FastAddProperty("lastChild",
            new DomFunction((in _) => docRoot.ChildNodes.Count > 0 ? _host.ToJSObject(DomBridge.ChildAt(docRoot, ^1)) : JSNull.Value, "get lastChild"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // hasChildNodes()
        doc.FastAddValue("hasChildNodes",
            new DomFunction((in _) => docRoot.ChildNodes.Count > 0 ? JSBoolean.True : JSBoolean.False, "hasChildNodes", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // nodeType = DOCUMENT_NODE (9)
        doc.FastAddProperty("nodeType",
            new DomFunction((in _) => new JSNumber(9), "get nodeType"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // nodeName = "#document"
        doc.FastAddProperty("nodeName",
            new DomFunction((in _) => new JSString("#document"), "get nodeName"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // localName = null for document
        doc.FastAddProperty("localName",
            DomBridge.NullFunction("get localName"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // getElementById(id)
        doc.FastAddValue("getElementById",
            new DomFunction((in a) => GetElementById(docRoot, in a), "getElementById", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // getElementsByTagName(tag)
        doc.FastAddValue("getElementsByTagName",
            new DomFunction((in a) => GetElementsByTagName(docRoot, in a), "getElementsByTagName", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // getElementsByClassName(names) / getElementsByName(name) — the two collection lookups a
        // frame's document was missing while the main document had them. A script in a frame is a
        // script like any other: absent, these read as undefined rather than as missing methods, so
        // calling one threw and took the frame's whole <script> with it.
        doc.FastAddValue("getElementsByClassName",
            new DomFunction((in a) => GetElementsByClassName(docRoot, in a), "getElementsByClassName", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        doc.FastAddValue("getElementsByName",
            new DomFunction((in a) => GetElementsByName(docRoot, in a), "getElementsByName", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createElement(tag)
        doc.FastAddValue("createElement",
            new DomFunction((in a) => CreateElement(docRoot, in a), "createElement", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createTextNode(text)
        doc.FastAddValue("createTextNode",
            new DomFunction((in a) => CreateTextNode(docRoot, in a), "createTextNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createComment(data)
        doc.FastAddValue("createComment",
            new DomFunction((in a) => CreateComment(docRoot, in a), "createComment", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createElementNS(ns, localName)
        doc.FastAddValue("createElementNS",
            new DomFunction((in a) => CreateElementNS(docRoot, in a), "createElementNS", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // adoptNode(node) — the one document method that moves a node between documents rather than
        // copying it. A frame's document needs it as much as the page's: the interesting direction is
        // adopting *into* this document, which is exactly what the page's own adoptNode cannot do.
        doc.FastAddValue("adoptNode",
            new DomFunction((in a) => AdoptNode(docRoot, in a), "adoptNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createEvent(type)
        doc.FastAddValue("createEvent",
            new DomFunction((in a) => CreateEvent(in a), "createEvent", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // querySelector / querySelectorAll
        doc.FastAddValue("querySelector",
            new DomFunction((in a) => QuerySelector(docRoot, in a), "querySelector", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        doc.FastAddValue("querySelectorAll",
            new DomFunction((in a) => QuerySelectorAll(docRoot, in a), "querySelectorAll", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        doc.FastAddValue("elementFromPoint",
            new DomFunction((in a) => ElementFromPoint(docRoot, in a), "elementFromPoint", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        doc.FastAddValue("elementsFromPoint",
            new DomFunction((in a) => ElementsFromPoint(docRoot, in a), "elementsFromPoint", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // document.open()
        doc.FastAddValue("open",
            new DomFunction((in _) => Open(doc, docRoot), "open", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // document.close()
        doc.FastAddValue("close",
            DomBridge.UndefinedFunction("close", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // document.write(html)
        doc.FastAddValue("write",
            new DomFunction((in a) => Write(docRoot, in a), "write", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // removeChild on document
        doc.FastAddValue("removeChild",
            new DomFunction((in a) => RemoveChild(docRoot, in a), "removeChild", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // appendChild on document
        doc.FastAddValue("appendChild",
            new DomFunction((in a) => AppendChild(docRoot, in a), "appendChild", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        doc.FastAddValue("append",
            new DomFunction((in a) => Append(docRoot, in a), "append", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        doc.FastAddValue("prepend",
            new DomFunction((in a) => Prepend(docRoot, in a), "prepend", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Node interface constants — types and the DOCUMENT_POSITION_* bits. On Node.prototype, which
        // this document object reaches through the HTMLDocument link above; it installs its own only
        // when the realm does not carry the interfaces.
        if (!_host.NodeInterfacePrototypesReady)
            NodeConstantsBinding.Install(doc);

        // document.implementation on sub-documents
        var subImpl = new JSObject();
        subImpl.FastAddValue("hasFeature",
            DomBridge.TrueFunction("hasFeature", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
        subImpl.FastAddValue("createDocumentType",
            new DomFunction((in a) => CreateDocumentType(in a), "createDocumentType", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);
        subImpl.FastAddValue("createDocument",
            new DomFunction((in a) => CreateDocument(in a), "createDocument", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);
        subImpl.FastAddValue("createHTMLDocument",
            new DomFunction((in a) => CreateHTMLDocument(in a), "createHTMLDocument", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        doc.FastAddValue("implementation",
            subImpl, JSPropertyAttributes.EnumerableConfigurableValue);

        // defaultView — return the main window object so getComputedStyle is accessible
        if (_host.WindowJSObject != null)
        {
            doc.FastAddValue("defaultView",
                _host.WindowJSObject, JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // createTreeWalker(root, whatToShow, filter)
        doc.FastAddValue("createTreeWalker",
            new DomFunction((in a) => CreateTreeWalker(in a), "createTreeWalker", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createNodeIterator(root, whatToShow, filter)
        doc.FastAddValue("createNodeIterator",
            new DomFunction((in a) => CreateNodeIterator(in a), "createNodeIterator", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // startViewTransition() — CSS View Transitions, scoped to this nested browsing context.
        // Absent here, a page driving a transition inside its <iframe> through contentDocument hit a
        // TypeError that aborted the rest of its script, so the main frame's own transition never ran
        // either (WPT css-view-transitions/iframe-and-main-frame-transition-*).
        doc.FastAddValue("startViewTransition",
            new DomFunction((in a) => _host.StartViewTransition(docRoot, in a), "startViewTransition", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // createRange()
        doc.FastAddValue("createRange",
            new DomFunction((in _) => _host.BuildRange(docRoot), "createRange", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // getSelection() — this document's own selection, distinct from the containing page's. The
        // method exists on every document, but only one being displayed has a selection to report, so
        // a createDocument/createHTMLDocument result answers null; the host draws that line.
        doc.FastAddValue("getSelection",
            new DomFunction((in _) => _host.GetSelection(docRoot), "getSelection", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

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
    private static void RegisterCollections(
        JSObject doc, IDocumentCollectionHost collections, JSContext? context)
    {
        Live("forms", DocumentCollectionBinding.Forms);
        Live("images", DocumentCollectionBinding.Images);
        Live("links", DocumentCollectionBinding.Links);
        Live("anchors", DocumentCollectionBinding.Anchors);
        Live("scripts", DocumentCollectionBinding.Scripts);
        Live("styleSheets", DocumentCollectionBinding.StyleSheets);

        JSValue? embeds = null;
        JSValue Embeds() => embeds ??= DocumentCollectionBinding.Embeds(collections, context);
        Getter("embeds", Embeds);
        Getter("plugins", Embeds);

        void Live(string name, Func<IDocumentCollectionHost, JSContext?, JSValue> build)
        {
            JSValue? collection = null;
            Getter(name, () => collection ??= build(collections, context));
        }

        void Getter(string name, Func<JSValue> read) =>
            doc.FastAddProperty(
                name, new DomFunction((in _) => read(), $"get {name}"), null,
                JSPropertyAttributes.EnumerableConfigurableProperty);
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
    private void RegisterMetadata(JSObject doc, DomNode docRoot)
    {
        doc.FastAddProperty(
            "doctype",
            new DomFunction((in _) => DocumentTypeNode(docRoot) is { } doctype ? _host.ToJSObject(doctype) : JSNull.Value, "get doctype"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // HTML §3.2.6: the getter is limited to only known values — the canonical lower-case keyword
        // or the empty string — while the setter writes the assigned text through unchanged.
        doc.FastAddProperty(
            "dir",
            new DomFunction((in _) => new JSString(DocumentDirection(docRoot)), "get dir"),
            new DomFunction((in a) =>
            {
                if (DomBridge.GetDocumentElement(docRoot) is { } documentElement)
                    DomBridge.SetAttr(documentElement, "dir", a.Length > 0 ? a[0].ToString() : string.Empty);
                return JSUndefined.Value;
            }, "set dir"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // HTML §3.2.7: an enumerated document state rather than an attribute. Anything but "on"/"off"
        // (ASCII case-insensitively) is ignored rather than stored.
        var designMode = "off";
        doc.FastAddProperty(
            "designMode",
            new DomFunction((in _) => new JSString(designMode), "get designMode"),
            new DomFunction((in a) =>
            {
                var requested = a.Length > 0 ? a[0].ToString() : string.Empty;
                if (string.Equals(requested, "on", StringComparison.OrdinalIgnoreCase))
                    designMode = "on";
                else if (string.Equals(requested, "off", StringComparison.OrdinalIgnoreCase))
                    designMode = "off";
                return JSUndefined.Value;
            }, "set designMode"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
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

    private JSValue GetBody(DomNode docRoot)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JSNull.Value;
        foreach (var child in DomBridge.ChildElements(htmlEl))
        {
            if (string.Equals(child.TagName, "body", StringComparison.OrdinalIgnoreCase))
                return _host.ToJSObject(child);
        }

        return JSNull.Value;
    }

    private JSValue GetHead(DomNode docRoot)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JSNull.Value;
        foreach (var child in DomBridge.ChildElements(htmlEl))
        {
            if (string.Equals(child.TagName, "head", StringComparison.OrdinalIgnoreCase))
                return _host.ToJSObject(child);
        }

        return JSNull.Value;
    }

    private JSValue GetTitle(DomNode docRoot)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return new JSString(string.Empty);
        var head = DomBridge.ChildElements(htmlEl).FirstOrDefault(c => string.Equals(c.TagName, "head", StringComparison.OrdinalIgnoreCase));
        if (head != null)
        {
            var titleEl = DomBridge.ChildElements(head).FirstOrDefault(c => string.Equals(c.TagName, "title", StringComparison.OrdinalIgnoreCase));
            if (titleEl != null)
            {
                var sb = new StringBuilder();
                DomBridge.CollectTextContent(titleEl, sb);
                return new JSString(sb.ToString());
            }
        }

        return new JSString(string.Empty);
    }

    private JSValue SetTitle(DomNode docRoot, in Arguments a)
    {
        var htmlEl = DomBridge.GetDocumentElement(docRoot);
        if (htmlEl == null)
            return JSUndefined.Value;
        var head = DomBridge.ChildElements(htmlEl).FirstOrDefault(c => string.Equals(c.TagName, "head", StringComparison.OrdinalIgnoreCase));
        if (head != null)
        {
            var titleEl = DomBridge.ChildElements(head).FirstOrDefault(c => string.Equals(c.TagName, "title", StringComparison.OrdinalIgnoreCase));
            if (titleEl != null)
                _host.SetElementTextContent(titleEl, a.Length > 0 ? a[0].ToString() : string.Empty);
        }

        return JSUndefined.Value;
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
    private JSValue GetChildNodes(DomNode docRoot) =>
        DomCollectionBinding.NodeList(_host.JsContext, () =>
        {
            var children = new List<JSValue>();
            foreach (var child in docRoot.ChildNodes)
                children.Add(_host.ToJSObject(child));
            return children;
        });

    private JSValue GetElementById(DomNode docRoot, in Arguments a)
    {
        var id = a.Length > 0 ? a[0].ToString() : string.Empty;
        var found = DomBridge.FindInSubTree(docRoot, el => el.Id == id);
        return found != null ? _host.ToJSObject(found) : JSNull.Value;
    }

    /// <summary>
    /// <c>getElementsByTagName(name)</c> on a frame's document — a <b>live</b> <c>HTMLCollection</c>
    /// (DOM §4.5), as the containing document's is.
    /// </summary>
    private JSValue GetElementsByTagName(DomNode docRoot, in Arguments a)
    {
        var tagName = a.Length > 0 ? a[0].ToString() : string.Empty;
        return LiveCollection(
            docRoot,
            el => tagName == "*" || string.Equals(el.TagName, tagName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>getElementsByClassName</c> on a frame's document — a <b>live</b> <c>HTMLCollection</c>
    /// (DOM §4.5). It reuses <see cref="ClassNameSet"/>, the rule the main document and the
    /// element-scoped search already share, so all three surfaces answer a class query the same way.
    /// </summary>
    private JSValue GetElementsByClassName(DomNode docRoot, in Arguments a)
    {
        var wanted = ClassNameSet.Parse(a.Length > 0 ? a[0].ToString() : string.Empty);
        return LiveCollection(docRoot, el => wanted.Length > 0 && ClassNameSet.Matches(el, wanted));
    }

    /// <summary>
    /// <c>getElementsByName</c> on a frame's document — the elements whose <c>name</c> attribute is
    /// identical to the argument, matching the main document's implementation (HTML §3.1.5): a live
    /// <c>NodeList</c>, the one by-name lookup the specification types as a NodeList rather than an
    /// <c>HTMLCollection</c>.
    /// </summary>
    private JSValue GetElementsByName(DomNode docRoot, in Arguments a)
    {
        var name = a.Length > 0 ? a[0].ToString() : string.Empty;
        return DomCollectionBinding.NodeList(
            _host.JsContext,
            () => Wrappers(
                docRoot,
                el => DomBridge.TryGetAttribute(el, "name", out var value) && string.Equals(value, name, StringComparison.Ordinal)));
    }

    private JSValue QuerySelector(DomNode docRoot, in Arguments a)
    {
        var selector = a.Length > 0 ? a[0].ToString() : string.Empty;
        DomBridge.ValidateSelector(selector, _host.JsContext);
        if (DomApiSyntax.CarriesPseudoElement(selector))
            return JSNull.Value;

        var found = DomBridge.FindInSubTree(docRoot, el => _host.MatchesSelector(el, selector));
        return found != null ? _host.ToJSObject(found) : JSNull.Value;
    }

    /// <summary>
    /// <c>querySelectorAll(selector)</c> on a frame's document — a <b>static</b> <c>NodeList</c>
    /// (DOM §4.2.6), the one collection the specification defines as a snapshot rather than live. The
    /// members are resolved once, here, and the list closes over that result.
    /// </summary>
    private JSValue QuerySelectorAll(DomNode docRoot, in Arguments a)
    {
        var selector = a.Length > 0 ? a[0].ToString() : string.Empty;
        DomBridge.ValidateSelector(selector, _host.JsContext);
        var results = DomApiSyntax.CarriesPseudoElement(selector)
            ? []
            : Wrappers(docRoot, el => _host.MatchesSelector(el, selector));
        return DomCollectionBinding.NodeList(_host.JsContext, () => results);
    }

    /// <summary>
    /// A live <c>HTMLCollection</c> over the elements of this sub-document matching
    /// <paramref name="predicate"/>, with the named getter DOM §4.2.10.2 gives one — by <c>id</c>,
    /// then by <c>name</c>, taking the first member in tree order that answers to either.
    /// </summary>
    private JSValue LiveCollection(DomNode docRoot, Func<DomElement, bool> predicate) =>
        DomCollectionBinding.HtmlCollection(
            _host.JsContext,
            () => Wrappers(docRoot, predicate),
            name =>
            {
                if (name.Length == 0)
                    return null;

                foreach (var element in Members(docRoot, predicate))
                {
                    if ((DomBridge.TryGetAttribute(element, "id", out var id) && id == name) ||
                        (DomBridge.TryGetAttribute(element, "name", out var named) && named == name))
                        return _host.ToJSObject(element);
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
    private List<JSValue> Wrappers(DomNode docRoot, Func<DomElement, bool> predicate)
    {
        var wrappers = new List<JSValue>();
        foreach (var member in Members(docRoot, predicate))
            wrappers.Add(_host.ToJSObject(member));
        return wrappers;
    }

    private JSValue ElementFromPoint(DomNode docRoot, in Arguments a)
    {
        var hit = _host.HitTestDocumentPoint(docRoot, DomBridge.GetCoordinateArgument(a, 0), DomBridge.GetCoordinateArgument(a, 1)).FirstOrDefault();
        return hit != null ? _host.ToJSObject(hit) : JSNull.Value;
    }

    private JSValue ElementsFromPoint(DomNode docRoot, in Arguments a)
    {
        var hits = _host.HitTestDocumentPoint(docRoot, DomBridge.GetCoordinateArgument(a, 0), DomBridge.GetCoordinateArgument(a, 1));
        return new JSArray(hits.Select(_host.ToJSObject).ToArray());
    }
}
