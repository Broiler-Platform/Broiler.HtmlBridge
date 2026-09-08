using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private void RegisterDocumentBasics(JsValue document)
    {
        var realm = Realm;
        var context = _jsContext!;

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
        // feature module (Phase 3). Pinned — that module reads the engine's argument frame.
        realm.DefineValue(document, "elementFromPoint", PinnedConstructor("elementFromPoint", (in a) => Dom.Features.HitTestBinding.ElementFromPoint(this, in a), 2));
        realm.DefineValue(document, "elementsFromPoint", PinnedConstructor("elementsFromPoint", (in a) => Dom.Features.HitTestBinding.ElementsFromPoint(this, in a), 2));

        // document.getAnimations() — minimal Web Animations API support used by WPT.
        realm.DefineValue(document, "getAnimations", realm.NewConstructor("getAnimations", (in _) => BuildAnimationList(null), 0));

        // document node factories — createElement/createTextNode/createAttribute/createDocumentFragment,
        // co-located in the DocumentFactoryBinding feature module (Phase 3). Pinned: that module still
        // takes both the engine's argument frame and the script context it validates names against.
        realm.DefineValue(document, "createElement", PinnedConstructor("createElement", (in a) => Dom.Features.DocumentFactoryBinding.CreateElement(this, context, in a), 1));
        realm.DefineValue(document, "createTextNode", PinnedConstructor("createTextNode", (in a) => Dom.Features.DocumentFactoryBinding.CreateTextNode(this, in a), 1));
        realm.DefineValue(document, "createAttribute", PinnedConstructor("createAttribute", (in a) => Dom.Features.DocumentFactoryBinding.CreateAttribute(this, context, in a), 1));
        realm.DefineValue(document, "createDocumentFragment", PinnedConstructor("createDocumentFragment", (in a) => Dom.Features.DocumentFactoryBinding.CreateDocumentFragment(this, in a), 0));
        realm.DefineValue(document, "importNode", PinnedConstructor("importNode", (in a) => Dom.Features.DocumentFactoryBinding.ImportNode(this, context, in a), 2));
        // adoptNode moves the node itself rather than copying it, which is the half importNode
        // cannot do and the one a custom element hears as adoptedCallback.
        realm.DefineValue(document, "adoptNode", PinnedConstructor("adoptNode", (in a) => Dom.Features.DocumentFactoryBinding.AdoptNode(this, context, in a), 1));

        // document.createEvent(type) — DOM Events Level 3 (Phase 3: co-located LegacyEventBinding module)
        realm.DefineValue(document, "createEvent", realm.NewConstructor("createEvent", Dom.Features.LegacyEventBinding.Create, 1));

        // document.startViewTransition(updateCallback | { update, types }) — CSS View Transitions
        // (see DomBridge.ViewTransition.cs). Runs the callback and returns a resolved ViewTransition;
        // the pseudo tree is baked at serialize time. Pinned by that file, which is engine-typed.
        realm.DefineValue(document, "startViewTransition", PinnedConstructor("startViewTransition", (in a) => StartViewTransition(in a), 1));
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
        var context = _jsContext!;

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
        // NodeMutationBinding feature module (Phase 3). Pinned: that module reads the engine's frame.
        PinnedAccessor(document, "childNodes", (in a) => Dom.Features.NodeMutationBinding.GetChildNodes(this, in a));
        realm.DefineValue(document, "removeChild", PinnedConstructor("removeChild", (in a) => Dom.Features.NodeMutationBinding.RemoveChild(this, in a), 1));
        realm.DefineValue(document, "appendChild", PinnedConstructor("appendChild", (in a) => Dom.Features.NodeMutationBinding.AppendChild(this, in a), 1));
        realm.DefineValue(document, "insertBefore", PinnedConstructor("insertBefore", (in a) => Dom.Features.NodeMutationBinding.InsertBefore(this, in a), 2));

        // Document includes the ParentNode mixin (DOM §4.2.6), so append/prepend/replaceChildren
        // exist on the document node just as they do on an element. Only the Node-level methods
        // above were bound, which made `document.append(x)` a TypeError mid-script.
        realm.DefineValue(document, "append", PinnedConstructor("append", (in a) => Dom.Features.NodeMutationBinding.Append(this, in a), 0));
        realm.DefineValue(document, "prepend", PinnedConstructor("prepend", (in a) => Dom.Features.NodeMutationBinding.Prepend(this, in a), 0));
        realm.DefineValue(document, "replaceChildren", PinnedConstructor("replaceChildren", (in a) => Dom.Features.NodeMutationBinding.ReplaceChildren(this, in a), 0));

        // document.forms/images/links/anchors/scripts/embeds/plugins/styleSheets — the live
        // collections, each built once and closed over so the identity a browser guarantees holds.
        RegisterDocumentCollections(document);

        // document.doctype/dir/designMode — the metadata accessors DOM §4.5 and HTML §3.2 name.
        RegisterDocumentMetadata(document);

        // document.createElementNS(namespace, tagName)  — DocumentFactoryBinding (Phase 3)
        realm.DefineValue(document, "createElementNS", PinnedConstructor("createElementNS", (in a) => Dom.Features.DocumentFactoryBinding.CreateElementNS(this, context, in a), 2));

        // document.createAttributeNS(namespace, qualifiedName)  — DocumentFactoryBinding (Phase 3)
        realm.DefineValue(document, "createAttributeNS", PinnedConstructor("createAttributeNS", (in a) => Dom.Features.DocumentFactoryBinding.CreateAttributeNS(this, context, in a), 2));

        // document.currentScript — the <script> element being executed, null when none is. The
        // element the bridge already tracks for document.write's insertion point, read from the
        // property a loader script uses to find its own <src>. Pinned by DocumentCollectionBinding.
        PinnedAccessor(document, "currentScript", (in a) => Dom.Features.DocumentCollectionBinding.GetCurrentScript(this, in a));

        // document.adoptedStyleSheets — the live array of constructed stylesheets applied to the
        // document (CSSOM). Readable (supports .push) and assignable (= [sheet, …]).
        //
        // Pinned by DomBridge/ConstructedStyleSheets.cs: the assignment is copied in engine terms so
        // that every element of the assigned array — sheet objects and whatever else a page put
        // there — survives the round trip byte for byte, and that file is not owned this round.
        PinnedAccessor(
            document,
            "adoptedStyleSheets",
            (in _) => AdoptedStyleSheetsArray(),
            (in a) =>
            {
                SetAdoptedStyleSheets(a.Length > 0 ? a[0] : JavaScript.Runtime.JSUndefined.Value);
                return JavaScript.Runtime.JSUndefined.Value;
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
        // reaches exactly what these did (DomBridge.EventTargetInterface.cs).
        if (!_eventTargetRoutingReady)
        {
            realm.DefineValue(document, "addEventListener", PinnedConstructor("addEventListener", (in a) => Dom.Features.DocumentEventTargetBinding.AddEventListener(this, in a), 3));
            realm.DefineValue(document, "removeEventListener", PinnedConstructor("removeEventListener", (in a) => Dom.Features.DocumentEventTargetBinding.RemoveEventListener(this, in a), 3));
            realm.DefineValue(document, "dispatchEvent", PinnedConstructor("dispatchEvent", (in a) => Dom.Features.DocumentEventTargetBinding.DispatchEvent(this, in a), 1));
        }

        // document.contentType — returns the MIME type of the document
        PinnedAccessor(document, "contentType", (in a) => Dom.Features.WindowDocumentMiscBinding.GetContentType(this, in a));

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
        PinnedAccessor(document, "domain", (in a) => Dom.Features.WindowDocumentMiscBinding.GetDocumentDomain(this, in a));

        // document.lastModified — MM/DD/YYYY hh:mm:ss in local time; the current time when the source's
        // own modification date is unknown, which is the specification's stated fallback.
        PinnedAccessor(document, "lastModified", (in a) => Dom.Features.WindowDocumentMiscBinding.GetLastModified(in a));

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
        realm.DefineValue(document, "hasFocus", PinnedConstructor("hasFocus", (in a) => Dom.Features.WindowDocumentMiscBinding.HasFocus(in a), 0));

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
