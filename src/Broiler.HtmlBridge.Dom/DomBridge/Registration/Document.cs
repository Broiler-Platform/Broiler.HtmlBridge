using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private void RegisterDocumentBasics(JSContext context, JSObject document)
    {
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
        document.FastAddProperty("documentElement",
            new JSFunction((in _) => ToJSObject(DocumentElement), "get documentElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.scrollingElement (getter — returns document.documentElement
        // in standards mode, or document.body in quirks mode; we always use
        // standards mode so it's always the <html> element).
        document.FastAddProperty("scrollingElement", new JSFunction((in _) => ToJSObject(DocumentElement), "get scrollingElement"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // Fullscreen §document API. `fullscreenElement` is a getter over the per-element fullscreen
        // flag rather than a stored reference, so it stays correct when the element is exited or
        // detached. `fullscreenEnabled` is constant here: the runner has no user-permission model
        // and nothing in the corpus needs it to be false.
        document.FastAddProperty("fullscreenElement",
            new JSFunction((in _) => FindFullscreenElement() is { } el ? ToJSObject(el) : JSNull.Value, "get fullscreenElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        document.FastAddProperty("webkitFullscreenElement",
            new JSFunction((in _) => FindFullscreenElement() is { } el ? ToJSObject(el) : JSNull.Value, "get webkitFullscreenElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        document.FastAddValue("fullscreenEnabled", JavaScript.BuiltIns.Boolean.JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("exitFullscreen",
            new JSFunction((in _) => _dialogs.ExitFullscreen(), "exitFullscreen", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("webkitExitFullscreen",
            new JSFunction((in _) => _dialogs.ExitFullscreen(), "webkitExitFullscreen", 0), JSPropertyAttributes.EnumerableConfigurableValue);

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
        document.FastAddProperty(
            "readyState",
            new JSFunction((in _) => new JSString(_documentReadyState), "get readyState"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // document structural accessors — body/head/title, co-located in the DocumentStructureBinding
        // feature module (Phase 3).
        document.FastAddProperty("body", new JSFunction((in a) => Dom.Features.DocumentStructureBinding.GetBody(this, in a), "get body"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        document.FastAddProperty("head", new JSFunction((in a) => Dom.Features.DocumentStructureBinding.GetHead(this, in a), "get head"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        document.FastAddProperty("title", new JSFunction((in a) => Dom.Features.DocumentStructureBinding.GetTitle(this, in a), "get title"), new JSFunction((in a) => Dom.Features.DocumentStructureBinding.SetTitle(this, in a), "set title"), JSPropertyAttributes.EnumerableConfigurableProperty);

        // document element-query methods — getElementById/getElementsByTagName/getElementsByClassName/
        // getElementsByName/querySelector/querySelectorAll, co-located in the DocumentQueryBinding
        // feature module (Phase 3).
        document.FastAddValue("getElementById", new JSFunction((in a) => Dom.Features.DocumentQueryBinding.GetElementById(this, in a), "getElementById", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("getElementsByTagName", new JSFunction((in a) => Dom.Features.DocumentQueryBinding.GetElementsByTagName(this, in a), "getElementsByTagName", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("getElementsByClassName", new JSFunction((in a) => Dom.Features.DocumentQueryBinding.GetElementsByClassName(this, in a), "getElementsByClassName", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("getElementsByName", new JSFunction((in a) => Dom.Features.DocumentQueryBinding.GetElementsByName(this, in a), "getElementsByName", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("querySelector", new JSFunction((in a) => Dom.Features.DocumentQueryBinding.QuerySelector(this, in a), "querySelector", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("querySelectorAll", new JSFunction((in a) => Dom.Features.DocumentQueryBinding.QuerySelectorAll(this, in a), "querySelectorAll", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        // document.elementFromPoint / elementsFromPoint (hit-testing), co-located in the HitTestBinding
        // feature module (Phase 3).
        document.FastAddValue("elementFromPoint", new JSFunction((in a) => Dom.Features.HitTestBinding.ElementFromPoint(this, in a), "elementFromPoint", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("elementsFromPoint", new JSFunction((in a) => Dom.Features.HitTestBinding.ElementsFromPoint(this, in a), "elementsFromPoint", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.getAnimations() — minimal Web Animations API support used by WPT.
        document.FastAddValue("getAnimations", new JSFunction((in _) => BuildAnimationList(null), "getAnimations", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // document node factories — createElement/createTextNode/createAttribute/createDocumentFragment,
        // co-located in the DocumentFactoryBinding feature module (Phase 3).
        document.FastAddValue("createElement", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.CreateElement(this, context, in a), "createElement", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("createTextNode", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.CreateTextNode(this, in a), "createTextNode", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("createAttribute", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.CreateAttribute(this, context, in a), "createAttribute", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("createDocumentFragment", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.CreateDocumentFragment(this, in a), "createDocumentFragment", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("importNode", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.ImportNode(this, context, in a), "importNode", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        // adoptNode moves the node itself rather than copying it, which is the half importNode
        // cannot do and the one a custom element hears as adoptedCallback.
        document.FastAddValue("adoptNode", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.AdoptNode(this, context, in a), "adoptNode", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.createEvent(type) — DOM Events Level 3 (Phase 3: co-located LegacyEventBinding module)
        document.FastAddValue("createEvent", new JSFunction(Dom.Features.LegacyEventBinding.Create, "createEvent", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.startViewTransition(updateCallback | { update, types }) — CSS View Transitions
        // (see DomBridge.ViewTransition.cs). Runs the callback and returns a resolved ViewTransition;
        // the pseudo tree is baked at serialize time.
        document.FastAddValue("startViewTransition", new JSFunction((in a) => StartViewTransition(in a), "startViewTransition", 1), JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private void RegisterDocumentWriting(JSObject document)
    {
        // document.write(html) — parse and insert at the current script position (Phase 3:
        // co-located DocumentWriteBinding feature module).
        document.FastAddValue("write", new JSFunction((in a) => Dom.Features.DocumentWriteBinding.Write(this, in a), "write", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.writeln(html) — same as write, with trailing newline
        var writeFn = (JSFunction)document[(KeyString)"write"];
        document.FastAddValue("writeln", new JSFunction((in a) => Dom.Features.DocumentWriteBinding.Writeln(writeFn, in a), "writeln", 1), JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private void RegisterDocumentNodeAndCollectionApis(JSContext context, JSObject document)
    {
        // Node interface constants on document (a Document IS a Node) — types and the
        // DOCUMENT_POSITION_* bits.
        Dom.Features.NodeConstantsBinding.Install(document);

        // document.nodeType = DOCUMENT_NODE (9)
        document.FastAddProperty("nodeType", new JSFunction((in _) => new JSNumber(9), "get nodeType"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.nodeName = "#document"
        document.FastAddProperty("nodeName", new JSFunction((in _) => new JSString("#document"), "get nodeName"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.firstChild (getter — returns first child of document: DOCTYPE if present, else documentElement)
        document.FastAddProperty("firstChild", new JSFunction((in _) => _document.ChildNodes.Count > 0 ? ToJSObject(ChildAt(_document, 0)) : JSNull.Value, "get firstChild"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.lastChild (getter — returns last child of document, typically documentElement)
        document.FastAddProperty("lastChild", new JSFunction((in _) => _document.ChildNodes.Count > 0 ? ToJSObject(ChildAt(_document, ^1)) : JSNull.Value, "get lastChild"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document-node mutation — childNodes/removeChild/appendChild/insertBefore, co-located in the
        // NodeMutationBinding feature module (Phase 3).
        document.FastAddProperty("childNodes", new JSFunction((in a) => Dom.Features.NodeMutationBinding.GetChildNodes(this, in a), "get childNodes"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        document.FastAddValue("removeChild", new JSFunction((in a) => Dom.Features.NodeMutationBinding.RemoveChild(this, in a), "removeChild", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("appendChild", new JSFunction((in a) => Dom.Features.NodeMutationBinding.AppendChild(this, in a), "appendChild", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("insertBefore", new JSFunction((in a) => Dom.Features.NodeMutationBinding.InsertBefore(this, in a), "insertBefore", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // Document includes the ParentNode mixin (DOM §4.2.6), so append/prepend/replaceChildren
        // exist on the document node just as they do on an element. Only the Node-level methods
        // above were bound, which made `document.append(x)` a TypeError mid-script.
        document.FastAddValue("append", new JSFunction((in a) => Dom.Features.NodeMutationBinding.Append(this, in a), "append", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("prepend", new JSFunction((in a) => Dom.Features.NodeMutationBinding.Prepend(this, in a), "prepend", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        document.FastAddValue("replaceChildren", new JSFunction((in a) => Dom.Features.NodeMutationBinding.ReplaceChildren(this, in a), "replaceChildren", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.forms/images/links/anchors/scripts/embeds/plugins/styleSheets — the live
        // collections, each built once and closed over so the identity a browser guarantees holds.
        RegisterDocumentCollections(context, document);

        // document.doctype/dir/designMode — the metadata accessors DOM §4.5 and HTML §3.2 name.
        RegisterDocumentMetadata(document);

        // document.createElementNS(namespace, tagName)  — DocumentFactoryBinding (Phase 3)
        document.FastAddValue("createElementNS", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.CreateElementNS(this, context, in a), "createElementNS", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.createAttributeNS(namespace, qualifiedName)  — DocumentFactoryBinding (Phase 3)
        document.FastAddValue("createAttributeNS", new JSFunction((in a) => Dom.Features.DocumentFactoryBinding.CreateAttributeNS(this, context, in a), "createAttributeNS", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.currentScript — the <script> element being executed, null when none is. The
        // element the bridge already tracks for document.write's insertion point, read from the
        // property a loader script uses to find its own <src>.
        document.FastAddProperty("currentScript", new JSFunction((in a) => Dom.Features.DocumentCollectionBinding.GetCurrentScript(this, in a), "get currentScript"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.adoptedStyleSheets — the live array of constructed stylesheets applied to the
        // document (CSSOM). Readable (supports .push) and assignable (= [sheet, …]).
        document.FastAddProperty("adoptedStyleSheets",
            new JSFunction((in _) => AdoptedStyleSheetsArray(), "get adoptedStyleSheets"),
            new JSFunction((in a) => { SetAdoptedStyleSheets(a.Length > 0 ? a[0] : JSUndefined.Value); return JSUndefined.Value; }, "set adoptedStyleSheets"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.open() — for main document
        document.FastAddValue("open", new JSFunction((in _) => document, "open", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.close() — for main document
        document.FastAddValue("close", UndefinedFunction("close", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.implementation — DOMImplementation
        var implementation = new JSObject();

        // implementation.hasFeature() — always returns true per spec
        implementation.FastAddValue("hasFeature", TrueFunction("hasFeature", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.implementation factories — createDocumentType/createDocument/createHTMLDocument,
        // co-located in the DocumentLevelFactoryBinding feature module (Phase 3).
        implementation.FastAddValue("createDocumentType", new JSFunction((in a) => Dom.Features.DocumentLevelFactoryBinding.CreateDocumentType(this, context, in a), "createDocumentType", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        // implementation.createDocument(namespace, qualifiedName, doctype)
        implementation.FastAddValue("createDocument", new JSFunction((in a) => Dom.Features.DocumentLevelFactoryBinding.CreateDocument(this, context, in a), "createDocument", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        // implementation.createHTMLDocument(title)
        implementation.FastAddValue("createHTMLDocument", new JSFunction((in a) => Dom.Features.DocumentLevelFactoryBinding.CreateHTMLDocument(this, in a), "createHTMLDocument", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        document.FastAddValue("implementation", implementation, JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private void RegisterDocumentEventTargetAndMetadata(JSObject document)
    {
        // document-level addEventListener / removeEventListener / dispatchEvent, co-located in the
        // DocumentEventTargetBinding feature module (Phase 3).
        // On EventTarget.prototype now, routed by receiver — the document's wrapper is registered
        // as its node's and its listener store is the same per-node one, so the routed method
        // reaches exactly what these did (DomBridge.EventTargetInterface.cs).
        if (!_eventTargetRoutingReady)
        {
            document.FastAddValue("addEventListener", new JSFunction((in a) => Dom.Features.DocumentEventTargetBinding.AddEventListener(this, in a), "addEventListener", 3), JSPropertyAttributes.EnumerableConfigurableValue);
            document.FastAddValue("removeEventListener", new JSFunction((in a) => Dom.Features.DocumentEventTargetBinding.RemoveEventListener(this, in a), "removeEventListener", 3), JSPropertyAttributes.EnumerableConfigurableValue);
            document.FastAddValue("dispatchEvent", new JSFunction((in a) => Dom.Features.DocumentEventTargetBinding.DispatchEvent(this, in a), "dispatchEvent", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // document.contentType — returns the MIME type of the document
        document.FastAddProperty("contentType", new JSFunction((in a) => Dom.Features.WindowDocumentMiscBinding.GetContentType(this, in a), "get contentType"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.URL — returns the document URL
        document.FastAddProperty("URL", new JSFunction((in _) => new JSString(_pageUrl), "get URL"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.documentURI — same as document.URL
        document.FastAddProperty("documentURI", new JSFunction((in _) => new JSString(_pageUrl), "get documentURI"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.compatMode — "CSS1Compat" for standards mode, "BackCompat" for quirks
        document.FastAddProperty("compatMode", new JSFunction((in _) => new JSString("CSS1Compat"), "get compatMode"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.characterSet — always UTF-8
        document.FastAddProperty("characterSet", new JSFunction((in _) => new JSString("UTF-8"), "get characterSet"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.inputEncoding — alias for characterSet
        document.FastAddProperty("inputEncoding", new JSFunction((in _) => new JSString("UTF-8"), "get inputEncoding"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.charset — the other historical alias of characterSet (DOM §4.5). It reads the same
        // value as the two above; it was simply not registered, so the oldest of the three spellings —
        // and the one legacy encoding-sniffing code reaches for first — was the one that answered
        // `undefined`.
        document.FastAddProperty("charset", new JSFunction((in _) => new JSString("UTF-8"), "get charset"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.referrer — the URL of the page that linked here (HTML §3.1.5). A capture navigates
        // to its URL directly, with no referring document, and the empty string is precisely what the
        // specification (and a browser following a typed URL or a bookmark) reports for that: "If the
        // document has no referrer, return the empty string." Analytics and same-site-entry checks read
        // it unguarded, where `undefined` stringifies into a bogus referrer rather than reading as none.
        document.FastAddProperty("referrer", new JSFunction((in _) => new JSString(string.Empty), "get referrer"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.domain — the origin's effective domain, i.e. this document's host.
        document.FastAddProperty("domain", new JSFunction((in a) => Dom.Features.WindowDocumentMiscBinding.GetDocumentDomain(this, in a), "get domain"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.lastModified — MM/DD/YYYY hh:mm:ss in local time; the current time when the source's
        // own modification date is unknown, which is the specification's stated fallback.
        document.FastAddProperty("lastModified", new JSFunction((in a) => Dom.Features.WindowDocumentMiscBinding.GetLastModified(in a), "get lastModified"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.activeElement — the focused element, or, when nothing is focused, the body element:
        // HTML's algorithm ends "if candidate is null, set candidate to the body element", so `body` is
        // the answer for an unfocused document rather than null. A capture focuses nothing, so it is
        // always body. Returning it as a getter (not a stored reference) keeps it correct across
        // document mutation. Scripts commonly walk up from it — `document.activeElement.tagName`,
        // `.blur()` — which threw outright while the property was missing.
        document.FastAddProperty("activeElement", new JSFunction((in a) => Dom.Features.DocumentStructureBinding.GetBody(this, in a), "get activeElement"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.hasFocus() — true; see the binding for why a capture's one document is always the
        // focused one, matching visibilityState below.
        document.FastAddValue("hasFocus", new JSFunction((in a) => Dom.Features.WindowDocumentMiscBinding.HasFocus(in a), "hasFocus", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // document.hidden / document.visibilityState (Page Visibility, HTML §6.6). A capture
        // renders one document in one viewport and never backgrounds it, so the answer is always
        // "visible" — but it has to be *an* answer. Absent, `document.hidden` reads `undefined`,
        // and the idiom scripts spell it with is a loose comparison: Google Search's bot-check VM
        // gates its "may I yield to the event loop?" predicate on `document.hidden == 0`, which is
        // true for `false` and false for `undefined`. A missing property does not read as
        // "not hidden"; it reads as a third state no page has a branch for. See
        // docs/google-search-post-consent-challenge.md.
        document.FastAddProperty("hidden", new JSFunction((in _) => JavaScript.BuiltIns.Boolean.JSBoolean.False, "get hidden"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        document.FastAddProperty("visibilityState", new JSFunction((in _) => new JSString("visible"), "get visibilityState"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // document.onvisibilitychange — the event handler IDL attribute that completes the pair above,
        // null until a page assigns one. Its event never fires here, and that is the accurate outcome
        // rather than a missing implementation: the capture's document is visible for its whole life,
        // so its visibility never *changes*. The slot has to exist all the same, because
        // `'onvisibilitychange' in document` is the feature test pages use to decide whether the Page
        // Visibility API is available at all — answering false sent them to legacy focus/blur polling
        // even though `visibilityState` above answers correctly.
        document.FastAddValue("onvisibilitychange", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
    }
}
