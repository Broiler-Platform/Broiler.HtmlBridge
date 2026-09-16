using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private void RegisterDocumentBasics(JsValue document)
    {
        var realm = Realm;

        // document.documentElement (the <html> element) — a getter, like the scrollingElement below
        // that answers with the same element, and like the accessor a browser has on
        // Document.prototype.
        //
        // It was a value, and materializing it here is the one place an element wrapper is minted
        // before the interface prototypes exist: this runs during document registration, and the
        // constructors and their members are registered by the polyfill pass afterwards. So <html>
        // alone took the pre-realm fallback and installed its own copy of every interface member,
        // and the re-link sweep at the end of registration then gave it the prototype as well — it
        // ended up with both, at 164 own properties against an ordinary element's 77, and
        // `document.documentElement.getAttribute === Element.prototype.getAttribute` was false. A
        // page patching Element.prototype reached every element except the root one.
        //
        // Deferring the mint to the first read is what makes the fallback unreachable for it rather
        // than compensated for afterwards. It also stops a re-parse handing back the previous
        // document's wrapper: the registry is cleared, and the value property was not.
        realm.DefineAccessor(document, "documentElement", (in _) => WrapNode(DocumentElement), null);

        // document.scrollingElement (getter — returns document.documentElement
        // in standards mode, or document.body in quirks mode; we always use
        // standards mode so it's always the <html> element).
        realm.DefineAccessor(document, "scrollingElement", (in _) => WrapNode(DocumentElement), null);

        // Fullscreen §document API. `fullscreenElement` is a getter over the per-element fullscreen
        // flag rather than a stored reference, so it stays correct when the element is exited or
        // detached. `fullscreenEnabled` is constant here: the runner has no user-permission model
        // and nothing in the corpus needs it to be false.
        realm.DefineAccessor(
            document, "fullscreenElement",
            (in _) => FindFullscreenElement() is { } el ? WrapNode(el) : JsValue.Null, null);
        realm.DefineAccessor(
            document, "webkitFullscreenElement",
            (in _) => FindFullscreenElement() is { } el ? WrapNode(el) : JsValue.Null, null);
        realm.DefineValue(document, "fullscreenEnabled", JsValue.True);
        realm.DefineValue(document, "exitFullscreen",
            realm.NewConstructor("exitFullscreen", (in _) => _dialogs.ExitFullscreenCore(), 0));
        realm.DefineValue(document, "webkitExitFullscreen",
            realm.NewConstructor("webkitExitFullscreen", (in _) => _dialogs.ExitFullscreenCore(), 0));

        // HTML §3.1.7 document.readyState: "loading" while parsing, "interactive" once parsing is
        // done, "complete" once the load event is about to fire. It is read, not just written to:
        // the standard way a script decides whether the DOM is ready is
        //
        //     if (document.readyState === 'interactive' || document.readyState === 'complete') go();
        //     else document.addEventListener('DOMContentLoaded', go);
        //
        // and `undefined` fails that test, so such a script takes the listener branch — after
        // DOMContentLoaded has already fired, which means `go` is never called at all. That is not
        // a degraded rendering but a missing one: it is how MediaWiki's Vector skin starts, so on
        // www.mediawiki.org none of the skin's JavaScript ran, and the appearance panel it moves
        // into the header stayed in the page column, displacing the whole article.
        realm.DefineAccessor(document, "readyState", (in _) => JsValue.String(_documentReadyState), null);

        // document structural accessors — body/head/title, co-located in the DocumentStructureBinding
        // feature module (Phase 3), and written against JSEAL.
        realm.DefineAccessor(
            document, "body", (in c) => Dom.Features.DocumentStructureBinding.GetBody(this, in c), null);
        realm.DefineAccessor(
            document, "head", (in c) => Dom.Features.DocumentStructureBinding.GetHead(this, in c), null);
        realm.DefineAccessor(
            document, "title",
            (in c) => Dom.Features.DocumentStructureBinding.GetTitle(this, in c),
            (in c) => Dom.Features.DocumentStructureBinding.SetTitle(this, in c));

        // document element-query methods — getElementById/getElementsByTagName/getElementsByClassName/
        // getElementsByName/querySelector/querySelectorAll, co-located in the DocumentQueryBinding
        // feature module (Phase 3). The six keep the name, arity and constructable shape they had.
        realm.DefineValue(document, "getElementById", realm.NewConstructor("getElementById", (in c) => Dom.Features.DocumentQueryBinding.GetElementById(this, in c), 1));
        realm.DefineValue(document, "getElementsByTagName", realm.NewConstructor("getElementsByTagName", (in c) => Dom.Features.DocumentQueryBinding.GetElementsByTagName(this, in c), 1));
        realm.DefineValue(document, "getElementsByClassName", realm.NewConstructor("getElementsByClassName", (in c) => Dom.Features.DocumentQueryBinding.GetElementsByClassName(this, in c), 1));
        realm.DefineValue(document, "getElementsByName", realm.NewConstructor("getElementsByName", (in c) => Dom.Features.DocumentQueryBinding.GetElementsByName(this, in c), 1));
        realm.DefineValue(document, "querySelector", realm.NewConstructor("querySelector", (in c) => Dom.Features.DocumentQueryBinding.QuerySelector(this, in c), 1));
        realm.DefineValue(document, "querySelectorAll", realm.NewConstructor("querySelectorAll", (in c) => Dom.Features.DocumentQueryBinding.QuerySelectorAll(this, in c), 1));
        // document.elementFromPoint / elementsFromPoint (hit-testing), co-located in the HitTestBinding
        // feature module (Phase 3). The two keep the name, arity and constructable shape they had, and
        // the module coerces its two coordinates through the realm.
        realm.DefineValue(document, "elementFromPoint", realm.NewConstructor("elementFromPoint", (in c) => Dom.Features.HitTestBinding.ElementFromPoint(this, in c), 2));
        realm.DefineValue(document, "elementsFromPoint", realm.NewConstructor("elementsFromPoint", (in c) => Dom.Features.HitTestBinding.ElementsFromPoint(this, in c), 2));

        // document.getAnimations() — minimal Web Animations API support used by WPT.
        realm.DefineValue(document, "getAnimations", realm.NewConstructor("getAnimations", (in _) => BuildAnimationList(null), 0));

        // document node factories — createElement/createTextNode/createAttribute/createDocumentFragment,
        // co-located in the DocumentFactoryBinding feature module (Phase 3). The script context that
        // used to travel with each call is gone: the name validations are on the module's host
        // contract, and its DOM exceptions are minted through the call's own realm.
        realm.DefineValue(document, "createElement", realm.NewConstructor("createElement", (in c) => Dom.Features.DocumentFactoryBinding.CreateElement(this, in c), 1));
        realm.DefineValue(document, "createTextNode", realm.NewConstructor("createTextNode", (in c) => Dom.Features.DocumentFactoryBinding.CreateTextNode(this, in c), 1));
        realm.DefineValue(document, "createAttribute", realm.NewConstructor("createAttribute", (in c) => Dom.Features.DocumentFactoryBinding.CreateAttribute(this, in c), 1));
        realm.DefineValue(document, "createDocumentFragment", realm.NewConstructor("createDocumentFragment", (in c) => Dom.Features.DocumentFactoryBinding.CreateDocumentFragment(this, in c), 0));
        realm.DefineValue(document, "importNode", realm.NewConstructor("importNode", (in c) => Dom.Features.DocumentFactoryBinding.ImportNode(this, in c), 2));
        // adoptNode moves the node itself rather than copying it, which is the half importNode
        // cannot do and the one a custom element hears as adoptedCallback.
        realm.DefineValue(document, "adoptNode", realm.NewConstructor("adoptNode", (in c) => Dom.Features.DocumentFactoryBinding.AdoptNode(this, in c), 1));

        // document.createEvent(type) — DOM Events Level 3 (Phase 3: co-located LegacyEventBinding module)
        realm.DefineValue(document, "createEvent", realm.NewConstructor("createEvent", Dom.Features.LegacyEventBinding.Create, 1));

        // document.startViewTransition(updateCallback | { update, types }) — CSS View Transitions
        // (see DomBridge/ViewTransition.cs). Runs the callback and returns a resolved ViewTransition;
        // the pseudo tree is baked at serialize time. The operation reads one argument — the update
        // callback, or the dictionary carrying it — and a handle over it is the whole of that.
        realm.DefineValue(document, "startViewTransition", realm.NewConstructor("startViewTransition", (in c) => StartViewTransition(c[0]), 1));
    }

    private void RegisterDocumentWriting(JsValue document)
    {
        var realm = Realm;

        // document.write(html) — parse and insert at the current script position (Phase 3:
        // co-located DocumentWriteBinding feature module, migrated to JSEAL).
        var writeFn = realm.NewConstructor("write", (in c) => Dom.Features.DocumentWriteBinding.Write(this, in c), 1);
        realm.DefineValue(document, "write", writeFn);

        // document.writeln(html) — same as write, with trailing newline. It is handed the very
        // function object installed above, which is what reading document["write"] back gave before.
        realm.DefineValue(document, "writeln",
            realm.NewConstructor("writeln", (in c) => Dom.Features.DocumentWriteBinding.Writeln(writeFn, in c), 1));
    }

    private void RegisterDocumentNodeAndCollectionApis(JsValue document)
    {
        var realm = Realm;

        // Node interface constants on document (a Document IS a Node) — types and the
        // DOCUMENT_POSITION_* bits.
        Dom.Features.NodeConstantsBinding.Install(realm, document);

        // document.nodeType = DOCUMENT_NODE (9)
        realm.DefineAccessor(document, "nodeType", (in _) => JsValue.Number(9), null);

        // document.nodeName = "#document"
        realm.DefineAccessor(document, "nodeName", (in _) => JsValue.String("#document"), null);

        // document.firstChild (getter — returns first child of document: DOCTYPE if present, else documentElement)
        realm.DefineAccessor(document, "firstChild",
            (in _) => _document.ChildNodes.Count > 0 ? WrapNode(ChildAt(_document, 0)) : JsValue.Null, null);

        // document.lastChild (getter — returns last child of document, typically documentElement)
        realm.DefineAccessor(document, "lastChild",
            (in _) => _document.ChildNodes.Count > 0 ? WrapNode(ChildAt(_document, ^1)) : JsValue.Null, null);

        // document-node mutation — childNodes/removeChild/appendChild/insertBefore, co-located in the
        // NodeMutationBinding feature module (Phase 3). The module raises its DOM exceptions through
        // the call's own realm now, so it takes no script context.
        realm.DefineAccessor(document, "childNodes", (in c) => Dom.Features.NodeMutationBinding.GetChildNodes(this, in c), null);
        realm.DefineValue(document, "removeChild", realm.NewConstructor("removeChild", (in c) => Dom.Features.NodeMutationBinding.RemoveChild(this, in c), 1));
        realm.DefineValue(document, "appendChild", realm.NewConstructor("appendChild", (in c) => Dom.Features.NodeMutationBinding.AppendChild(this, in c), 1));
        realm.DefineValue(document, "insertBefore", realm.NewConstructor("insertBefore", (in c) => Dom.Features.NodeMutationBinding.InsertBefore(this, in c), 2));

        // Document includes the ParentNode mixin (DOM §4.2.6), so append/prepend/replaceChildren
        // exist on the document node just as they do on an element. Only the Node-level methods
        // above were bound, which made `document.append(x)` a TypeError mid-script.
        realm.DefineValue(document, "append", realm.NewConstructor("append", (in c) => Dom.Features.NodeMutationBinding.Append(this, in c), 0));
        realm.DefineValue(document, "prepend", realm.NewConstructor("prepend", (in c) => Dom.Features.NodeMutationBinding.Prepend(this, in c), 0));
        realm.DefineValue(document, "replaceChildren", realm.NewConstructor("replaceChildren", (in c) => Dom.Features.NodeMutationBinding.ReplaceChildren(this, in c), 0));

        // document.forms/images/links/anchors/scripts/embeds/plugins/styleSheets — the live
        // collections, each built once and closed over so the identity a browser guarantees holds.
        RegisterDocumentCollections(document);

        // document.doctype/dir/designMode — the metadata accessors DOM §4.5 and HTML §3.2 name.
        RegisterDocumentMetadata(document);

        // document.createElementNS(namespace, tagName)  — DocumentFactoryBinding (Phase 3)
        realm.DefineValue(document, "createElementNS", realm.NewConstructor("createElementNS", (in c) => Dom.Features.DocumentFactoryBinding.CreateElementNS(this, in c), 2));

        // document.createAttributeNS(namespace, qualifiedName)  — DocumentFactoryBinding (Phase 3)
        realm.DefineValue(document, "createAttributeNS", realm.NewConstructor("createAttributeNS", (in c) => Dom.Features.DocumentFactoryBinding.CreateAttributeNS(this, in c), 2));

        // document.currentScript — the <script> element being executed, null when none is. The
        // element the bridge already tracks for document.write's insertion point, read from the
        // property a loader script uses to find its own <src>.
        realm.DefineAccessor(document, "currentScript", (in _) => Dom.Features.DocumentCollectionBinding.GetCurrentScript(this), null);

        // document.adoptedStyleSheets — the live array of constructed stylesheets applied to the
        // document (CSSOM). Readable (supports .push) and assignable (= [sheet, …]). The assignment
        // still copies the assigned array in engine terms — see SetAdoptedStyleSheets for the one
        // thing JSEAL cannot yet express about an array's elements.
        realm.DefineAccessor(
            document,
            "adoptedStyleSheets",
            (in _) => AdoptedStyleSheets(),
            (in c) =>
            {
                SetAdoptedStyleSheets(c[0]);
                return JsValue.Undefined;
            });

        // document.open() — for main document
        realm.DefineValue(document, "open", realm.NewConstructor("open", (in _) => document, 0));

        // document.close() — for main document
        realm.DefineValue(document, "close", UndefinedMember("close", 0));

        // document.implementation — DOMImplementation
        var implementation = realm.NewObject();

        // implementation.hasFeature() — always returns true per spec
        realm.DefineValue(implementation, "hasFeature", TrueMember("hasFeature", 2));

        // document.implementation factories — createDocumentType/createDocument/createHTMLDocument,
        // co-located in the DocumentLevelFactoryBinding feature module (Phase 3).
        realm.DefineValue(implementation, "createDocumentType", realm.NewConstructor("createDocumentType", (in c) => Dom.Features.DocumentLevelFactoryBinding.CreateDocumentType(this, in c), 3));

        // implementation.createDocument(namespace, qualifiedName, doctype)
        realm.DefineValue(implementation, "createDocument", realm.NewConstructor("createDocument", (in c) => Dom.Features.DocumentLevelFactoryBinding.CreateDocument(this, in c), 3));

        // implementation.createHTMLDocument(title)
        realm.DefineValue(implementation, "createHTMLDocument", realm.NewConstructor("createHTMLDocument", (in c) => Dom.Features.DocumentLevelFactoryBinding.CreateHTMLDocument(this, in c), 1));

        realm.DefineValue(document, "implementation", implementation);
    }

    private void RegisterDocumentEventTargetAndMetadata(JsValue document)
    {
        var realm = Realm;

        // document-level addEventListener / removeEventListener / dispatchEvent, co-located in the
        // DocumentEventTargetBinding feature module (Phase 3).
        // On EventTarget.prototype now, routed by receiver — the document's wrapper is registered
        // as its node's and its listener store is the same per-node one, so the routed method
        // reaches exactly what these did (DomBridge/Events.cs).
        if (!_eventTargetRoutingReady)
        {
            realm.DefineValue(document, "addEventListener", realm.NewConstructor("addEventListener", (in c) => Dom.Features.DocumentEventTargetBinding.AddEventListener(this, in c), 3));
            realm.DefineValue(document, "removeEventListener", realm.NewConstructor("removeEventListener", (in c) => Dom.Features.DocumentEventTargetBinding.RemoveEventListener(this, in c), 3));
            realm.DefineValue(document, "dispatchEvent", realm.NewConstructor("dispatchEvent", (in c) => Dom.Features.DocumentEventTargetBinding.DispatchEvent(this, in c), 1));
        }

        // document.contentType — returns the MIME type of the document
        realm.DefineAccessor(document, "contentType", (in c) => Dom.Features.WindowDocumentMiscBinding.GetContentType(this, in c), null);

        // document.URL — returns the document URL
        realm.DefineAccessor(document, "URL", (in _) => JsValue.String(_pageUrl), null);

        // document.documentURI — same as document.URL
        realm.DefineAccessor(document, "documentURI", (in _) => JsValue.String(_pageUrl), null);

        // document.compatMode — "CSS1Compat" for standards mode, "BackCompat" for quirks
        realm.DefineAccessor(document, "compatMode", (in _) => JsValue.String("CSS1Compat"), null);

        // document.characterSet — always UTF-8
        realm.DefineAccessor(document, "characterSet", (in _) => JsValue.String("UTF-8"), null);

        // document.inputEncoding — alias for characterSet
        realm.DefineAccessor(document, "inputEncoding", (in _) => JsValue.String("UTF-8"), null);

        // document.charset — the other historical alias of characterSet (DOM §4.5). It reads the same
        // value as the two above; it was simply not registered, so the oldest of the three spellings —
        // and the one legacy encoding-sniffing code reaches for first — was the one that answered
        // `undefined`.
        realm.DefineAccessor(document, "charset", (in _) => JsValue.String("UTF-8"), null);

        // document.referrer — the URL of the page that linked here (HTML §3.1.5). A capture navigates
        // to its URL directly, with no referring document, and the empty string is precisely what the
        // specification (and a browser following a typed URL or a bookmark) reports for that: "If the
        // document has no referrer, return the empty string." Analytics and same-site-entry checks read
        // it unguarded, where `undefined` stringifies into a bogus referrer rather than reading as none.
        realm.DefineAccessor(document, "referrer", (in _) => JsValue.String(string.Empty), null);

        // document.domain — the origin's effective domain, i.e. this document's host.
        realm.DefineAccessor(document, "domain", (in c) => Dom.Features.WindowDocumentMiscBinding.GetDocumentDomain(this, in c), null);

        // document.lastModified — MM/DD/YYYY hh:mm:ss in local time; the current time when the source's
        // own modification date is unknown, which is the specification's stated fallback.
        realm.DefineAccessor(document, "lastModified", (in c) => Dom.Features.WindowDocumentMiscBinding.GetLastModified(in c), null);

        // document.activeElement — the focused element, or, when nothing is focused, the body element:
        // HTML's algorithm ends "if candidate is null, set candidate to the body element", so `body` is
        // the answer for an unfocused document rather than null. A capture focuses nothing, so it is
        // always body. Returning it as a getter (not a stored reference) keeps it correct across
        // document mutation. Scripts commonly walk up from it — `document.activeElement.tagName`,
        // `.blur()` — which threw outright while the property was missing.
        realm.DefineAccessor(
            document, "activeElement", (in c) => Dom.Features.DocumentStructureBinding.GetBody(this, in c), null);

        // document.hasFocus() — true; see the binding for why a capture's one document is always the
        // focused one, matching visibilityState below.
        realm.DefineValue(document, "hasFocus", realm.NewConstructor("hasFocus", (in c) => Dom.Features.WindowDocumentMiscBinding.HasFocus(in c), 0));

        // document.hidden / document.visibilityState (Page Visibility, HTML §6.6). A capture
        // renders one document in one viewport and never backgrounds it, so the answer is always
        // "visible" — but it has to be *an* answer. Absent, `document.hidden` reads `undefined`,
        // and the idiom scripts spell it with is a loose comparison: Google Search's bot-check VM
        // gates its "may I yield to the event loop?" predicate on `document.hidden == 0`, which is
        // true for `false` and false for `undefined`. A missing property does not read as
        // "not hidden"; it reads as a third state no page has a branch for. See
        // docs/google-search-post-consent-challenge.md.
        realm.DefineAccessor(document, "hidden", (in _) => JsValue.False, null);
        realm.DefineAccessor(document, "visibilityState", (in _) => JsValue.String("visible"), null);

        // document.onvisibilitychange — the event handler IDL attribute that completes the pair above,
        // null until a page assigns one. Its event never fires here, and that is the accurate outcome
        // rather than a missing implementation: the capture's document is visible for its whole life,
        // so its visibility never *changes*. The slot has to exist all the same, because
        // `'onvisibilitychange' in document` is the feature test pages use to decide whether the Page
        // Visibility API is available at all — answering false sent them to legacy focus/blur polling
        // even though `visibilityState` above answers correctly.
        realm.DefineValue(document, "onvisibilitychange", JsValue.Null);
    }
}

/// <summary>
/// The <c>document</c> surface that is neither a node operation nor a factory: its eight live
/// collections, and the three metadata accessors that were simply absent — <c>doctype</c>,
/// <c>dir</c> and <c>designMode</c>.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Registers <c>forms</c>, <c>images</c>, <c>links</c>, <c>anchors</c>, <c>scripts</c>,
    /// <c>embeds</c>, <c>plugins</c> and <c>styleSheets</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each collection object is built <em>once</em> and closed over, so the getter hands back the
    /// same object on every read. That is the identity a browser guarantees
    /// (<c>document.forms === document.forms</c>), and for <c>plugins</c> it is the specification's
    /// literal requirement rather than a nicety: HTML §3.1.5 says <c>plugins</c> must return the same
    /// object <c>embeds</c> does, which one shared local expresses exactly.
    /// </para>
    /// <para>
    /// Built lazily on first read rather than here, because the interface constructors these
    /// collections take their prototypes from are registered later in the attach sequence (the
    /// polyfill pass, after the document object is populated). Constructing eagerly would leave every
    /// collection prototype-less and <c>document.forms instanceof HTMLCollection</c> false. A context
    /// is single-threaded by construction, so the null check needs no guard.
    /// </para>
    /// <para>
    /// The cached local is a <see cref="JsValue"/> and the accessor is the realm's: the module's
    /// collection builders are JSEAL's, and a handle is what "built once and closed over" now holds.
    /// <see cref="JsValue.Missing"/> is the not-yet-built state rather than a nullable, because a
    /// built collection is always an object and Missing is a kind no builder can answer with.
    /// </para>
    /// </remarks>
    private void RegisterDocumentCollections(JsValue document)
    {
        var realm = Realm;

        Live("forms", Dom.Features.DocumentCollectionBinding.Forms);
        Live("images", Dom.Features.DocumentCollectionBinding.Images);
        Live("links", Dom.Features.DocumentCollectionBinding.Links);
        Live("anchors", Dom.Features.DocumentCollectionBinding.Anchors);
        Live("scripts", Dom.Features.DocumentCollectionBinding.Scripts);
        Live("styleSheets", Dom.Features.DocumentCollectionBinding.StyleSheets);

        // embeds and plugins are one collection under two names, not two collections that agree.
        var embeds = JsValue.Missing;
        JsValue Embeds()
        {
            if (embeds.IsMissing)
                embeds = Dom.Features.DocumentCollectionBinding.Embeds(this);
            return embeds;
        }

        Getter("embeds", Embeds);
        Getter("plugins", Embeds);

        void Live(string name, Func<Dom.Features.IDocumentCollectionHost, JsValue> build)
        {
            var collection = JsValue.Missing;
            Getter(name, () =>
            {
                if (collection.IsMissing)
                    collection = build(this);
                return collection;
            });
        }

        void Getter(string name, Func<JsValue> read) =>
            realm.DefineAccessor(document, name, (in _) => read(), null);
    }

    /// <summary>
    /// <c>document.doctype</c>, <c>document.dir</c> and <c>document.designMode</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>doctype</c> is the one of the three that was not merely unimplemented but <em>invisible</em>:
    /// the parser has produced a canonical <see cref="DomDocumentType"/> and appended it as the
    /// document's first child for some time, and <c>document.firstChild</c> already returned it — only
    /// the accessor DOM §4.5 names for it was missing, so the node was reachable by position and not
    /// by name.
    /// </para>
    /// </remarks>
    private void RegisterDocumentMetadata(JsValue document)
    {
        var realm = Realm;

        realm.DefineAccessor(
            document,
            "doctype",
            (in _) => DocumentTypeNode() is { } doctype ? WrapNode(doctype) : JsValue.Null,
            null);

        // HTML §3.2.6: `dir` reflects the document element's dir attribute *limited to only known
        // values* — the getter answers the canonical lower-case keyword or the empty string, while
        // the setter writes through unchanged. So `document.dir = 'LTR'` reads back as "ltr" with
        // the attribute still spelled "LTR", and an unknown value reads back as "" with the
        // attribute set to whatever was assigned.
        //
        // The setter's coercion is the realm's ToJsString, not the handle's diagnostic rendering:
        // `document.dir = {toString(){return 'rtl'}}` is entitled to run that toString, which is what
        // the engine's own value-to-string did here before.
        realm.DefineAccessor(
            document,
            "dir",
            (in _) => JsValue.String(DocumentDirection()),
            (in c) =>
            {
                SetAttr(DocumentElement, "dir", c.Length > 0 ? c.Realm.ToJsString(c[0]) : string.Empty);
                return JsValue.Undefined;
            });

        // HTML §3.2.7: an enumerated document state, not an attribute, so it lives on the bridge.
        // Assigning anything but "on"/"off" (ASCII case-insensitively) is ignored rather than
        // stored — `document.designMode = 'zzz'` leaves the previous value in place.
        realm.DefineAccessor(
            document,
            "designMode",
            (in _) => JsValue.String(_designMode),
            (in c) =>
            {
                var requested = c.Length > 0 ? c.Realm.ToJsString(c[0]) : string.Empty;
                if (string.Equals(requested, "on", StringComparison.OrdinalIgnoreCase))
                    _designMode = "on";
                else if (string.Equals(requested, "off", StringComparison.OrdinalIgnoreCase))
                    _designMode = "off";
                return JsValue.Undefined;
            });
    }

    private string _designMode = "off";

    /// <summary>The document's <see cref="DomDocumentType"/> child, or <see langword="null"/>.</summary>
    private DomDocumentType? DocumentTypeNode()
    {
        foreach (var child in _document.ChildNodes)
        {
            if (child is DomDocumentType doctype)
                return doctype;
        }

        return null;
    }

    /// <summary>The document element's <c>dir</c>, limited to the three keywords HTML defines.</summary>
    private string DocumentDirection()
    {
        if (!TryGetAttribute(DocumentElement, "dir", out var value))
            return string.Empty;

        var keyword = value.ToLowerInvariant();
        return keyword is "ltr" or "rtl" or "auto" ? keyword : string.Empty;
    }
}

/// <summary>
/// Registers the Custom Elements surface: the <c>CustomElementRegistry</c> interface, the
/// <c>customElements</c> instance, the constructible <c>HTMLElement</c> base, and the subscription
/// that turns canonical DOM mutations into reaction callbacks.
/// </summary>
public sealed partial class DomBridge
{
    private Dom.Features.CustomElementsBinding? _customElements;

    internal Dom.Features.CustomElementsBinding CustomElements =>
        _customElements ??= new Dom.Features.CustomElementsBinding(this);

    /// <summary>
    /// Replaces the non-constructible <c>HTMLElement</c> with the base a custom element extends, and
    /// installs the registry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The base is JavaScript for one reason: it needs <c>new.target</c>. <c>new X()</c> runs
    /// <c>X</c>'s constructor, which calls <c>super()</c>, and only <c>new.target</c> says which
    /// subclass is being constructed — so which prototype the element must get and, through the
    /// registry, which tag name it has. The base reads it there and calls the host for the element
    /// itself.
    /// </para>
    /// <para>
    /// <b>The base stays JavaScript even though <see cref="JsCall.NewTarget"/> now exists.</b> A host
    /// constructor built with <c>NewConstructor</c> does receive <c>new.target</c>, so this one shim
    /// could move — but it is not the only caller of the host hook below. Every per-tag interface
    /// constructor calls it too, and those are generated by <c>DomBridgeUtils/DomInterfaces.cs</c>
    /// in exactly this <c>(new.target, interfaceName)</c> shape. The hook is one function serving both,
    /// so the argument convention is one decision; moving half of it would leave the host reading a
    /// call frame that belongs to the ordinary call the shim makes rather than to the <c>new</c> that
    /// began it, which is a different value. It moves when that file does.
    /// </para>
    /// <para>
    /// Returning an object from a base constructor is what makes this work: <c>super()</c>'s result
    /// becomes <c>this</c>, so the subclass constructor goes on to run against a real DOM element.
    /// Re-pointing its prototype at <c>new.target.prototype</c> keeps what it inherits from
    /// <c>EventTarget</c>, <c>Node</c>, <c>Element</c> and <c>HTMLElement</c> reachable: the class chain
    /// reaches <c>HTMLElement.prototype</c>, which its hyphenated tag linked it to, and what it still
    /// owns stays put. (This said all its members were its own.)
    /// </para>
    /// <para>
    /// <b>The JavaScript below is host script</b> — authored in this repository and shipped with it —
    /// so it runs through <see cref="IJsSource.EvaluateHostScript"/>, the member the page's
    /// Content-Security-Policy has no say over.
    /// </para>
    /// </remarks>
    /// <param name="window">
    /// The window object <c>customElements</c> is installed on, as a handle. Under this engine it
    /// is the global itself, but it is taken rather than derived so this pass installs on the same
    /// object the registration hub built everything else on — and a handle carries that object
    /// rather than wrapping it, so the property defined below lands on the very window every other
    /// registration pass wrote to, not on a second view of it.
    /// </param>
    private void RegisterCustomElements(JsValue window)
    {
        var realm = Realm;

        var registry = realm.NewObject();
        realm.DefineValue(registry, "define",
            realm.NewMethod("define", (in call) => CustomElements.Define(in call), 2));
        realm.DefineValue(registry, "get",
            realm.NewMethod("get", (in call) => CustomElements.Get(in call), 1));
        realm.DefineValue(registry, "getName",
            realm.NewMethod("getName", (in call) => CustomElements.GetName(in call), 1));
        realm.DefineValue(registry, "whenDefined",
            realm.NewMethod("whenDefined", (in call) => CustomElements.WhenDefined(in call), 1));
        realm.DefineValue(registry, "upgrade",
            realm.NewMethod("upgrade", (in call) => CustomElements.Upgrade(in call), 1));

        // The host half of the base, reached only from the JavaScript below and from the per-tag
        // interface constructors. Named with the bridge's reserved prefix and deleted from the global
        // once the base has closed over it, so a page cannot call it and mint an element out of band.
        realm.SetProperty(realm.Global, "__broilerConstructCustomElement",
            realm.NewMethod("constructCustomElement", (in call) => CustomElements.ConstructForNewTarget(in call), 2));
        realm.SetProperty(realm.Global, "__broilerCustomElementRegistry", registry);

        realm.EvaluateHostScript("""
            (function () {
                var construct = __broilerConstructCustomElement;
                var registry = __broilerCustomElementRegistry;

                // The per-tag interface globals were registered before this pass and left their
                // construction hook unbound, because constructing is this registry's job: a
                // customized built-in reaches HTMLButtonElement (or whichever) through super(), and
                // the element it must produce is one of ours. Binding it here, once, is what makes
                // `class Fancy extends HTMLButtonElement` work; the setter deletes itself.
                if (typeof __broilerBindInterfaceConstructor === 'function')
                    __broilerBindInterfaceConstructor(construct);

                // The custom element base. Replaces the illegal-constructor HTMLElement, which is
                // still what a bare `new HTMLElement()` gets: without a new.target there is no
                // subclass to build, and the host answers that with a TypeError.
                function HTMLElement() {
                    var target = new.target;
                    if (!target) throw new TypeError('Illegal constructor');
                    // The host answers null for a new.target with no definition — a bare
                    // `new HTMLElement()`, whose new.target is HTMLElement itself, and any subclass
                    // that was never registered — and a string when the definition exists but names
                    // a different interface, which is a customized built-in reached through
                    // HTMLElement. Throwing here rather than there is what makes either a real
                    // TypeError with a name and a message: a host throw would surface as a bare
                    // string with neither.
                    var element = construct(target, 'HTMLElement');
                    if (typeof element === 'string') throw new TypeError(element);
                    if (!element) throw new TypeError('Illegal constructor');
                    // The element carries its DOM members as own properties, so re-pointing the
                    // prototype adds the class without taking anything away.
                    Object.setPrototypeOf(element, target.prototype);
                    return element;
                }

                // Keep the prototype the interface linking already built, so the element's chain
                // still reaches Element, Node and EventTarget through it and every wrapper linked to
                // HTMLElement.prototype keeps the object it was linked to.
                HTMLElement.prototype = __broilerHTMLElementPrototype;
                Object.defineProperty(HTMLElement.prototype, 'constructor', {
                    value: HTMLElement, writable: true, enumerable: false, configurable: true
                });
                globalThis.HTMLElement = HTMLElement;

                function CustomElementRegistry() { throw new TypeError('Illegal constructor'); }
                globalThis.CustomElementRegistry = CustomElementRegistry;
                Object.setPrototypeOf(registry, CustomElementRegistry.prototype);
                globalThis.customElements = registry;

                delete globalThis.__broilerConstructCustomElement;
                delete globalThis.__broilerCustomElementRegistry;
            })();
            """,
            "broiler:custom-elements");

        realm.DefineValue(window, "customElements", registry);
        SubscribeCustomElementReactions();
    }

    /// <summary>
    /// Turns the canonical mutation stream into reaction callbacks.
    /// </summary>
    /// <remarks>
    /// <c>DomDocument.Mutated</c> is raised synchronously at mutation time, which is what this needs:
    /// a browser runs <c>connectedCallback</c> before the statement after the <c>appendChild</c> that
    /// caused it. Building reactions on <c>MutationObserver</c> instead — the obvious reuse, since it
    /// already subscribes here — would have delivered every one of them a microtask late, and a
    /// component that reads its own DOM straight after inserting itself would have seen nothing.
    /// </remarks>
    private void SubscribeCustomElementReactions()
    {
        if (_customElementReactionsSubscribed)
            return;

        _customElementReactionsSubscribed = true;
        _document.Mutated += OnCustomElementRelevantMutation;
        foreach (var other in _browsingContextDocuments)
            other.Mutated += OnCustomElementRelevantMutation;
    }

    /// <summary>
    /// Subscribes a document this bridge minted for a detached browsing context, so a node adopted
    /// <em>into</em> it is heard.
    /// </summary>
    /// <remarks>
    /// Adoption publishes its record on the document the node is moving to, not the one it is leaving
    /// — so listening to the main document alone hears an adoption into the page and misses the
    /// symmetric one out of it. There is one registry across every document this bridge owns, so the
    /// same handler serves them all.
    /// </remarks>
    private void SubscribeBrowsingContextDocument(DomDocument document)
    {
        if (!_browsingContextDocuments.Add(document) || !_customElementReactionsSubscribed)
            return;

        document.Mutated += OnCustomElementRelevantMutation;
    }

    private readonly HashSet<DomDocument> _browsingContextDocuments = [];

    private bool _customElementReactionsSubscribed;

    private void OnCustomElementRelevantMutation(DomMutationRecord record)
    {
        if (_customElements is not { } registry)
            return;

        if (record.Type == DomMutationType.Adoption)
        {
            registry.OnAdoption(record.Target, DocumentValue(record.OldDocument), DocumentValue(record.NewDocument));
            return;
        }

        if (record.Type == DomMutationType.ChildList)
        {
            // Both lists are optional on the record, and a childList record normally carries only
            // one of them.
            registry.OnChildListMutation(
                record.AddedNodes ?? [],
                record.RemovedNodes ?? []);
            // A move changes a form-associated custom element's owner and can change its disabled
            // state (an ancestor fieldset), and both are computed from the tree rather than stored.
            registry.SyncFormState();
            return;
        }

        if (record.Type == DomMutationType.Attributes && record.Target is DomElement element &&
            record.AttributeName is { } attributeName)
        {
            registry.OnAttributeMutation(element, attributeName, record.OldValue);
            // `disabled`, `form` and `id` each move a form-associated custom element between states;
            // the sweep costs one pass over the elements a page actually upgraded.
            registry.SyncFormState();
        }
    }

    /// <summary>The JS object for a document node — the window's <c>document</c> for the page, its own
    /// wrapper for a document this bridge minted, and <c>null</c> for one that has neither.</summary>
    /// <remarks>
    /// Nothing here converts: the page's document is the bridge's document root and a minted
    /// document's wrapper is the registry's entry, both handles already held, so
    /// <c>evt.oldDocument === frameDoc</c> is the question it always was. (This used to say the wrapper
    /// caches were engine-typed and each answer crossed through a cast; the registry had not been
    /// engine-typed for some time, and the root is not now.) The <c>null</c> for the page's own
    /// document before one is registered is kept deliberately: the root holds
    /// <see cref="JsValue.Missing"/> then, and this member answers a document or <c>null</c>, which is
    /// what a page can read.
    /// </remarks>
    private JsValue DocumentValue(DomDocument? document)
    {
        if (document is null)
            return JsValue.Null;

        if (ReferenceEquals(document, _document))
            return DocumentHandle.IsMissing ? JsValue.Null : DocumentHandle;

        return _jsObjects.TryGetDocument(document, out var wrapper) ? wrapper : JsValue.Null;
    }
}
