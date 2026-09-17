using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// No engine namespace, and no engine object either: the wrapper arrives as a handle and every
// installation below takes it as one. What stood here said "the two engine
// namespaces left, and one member decides both", naming <img>.width/height's used-dimension getter
// as the member and _tables and FormAssociationBinding as passengers on it. There were no engine
// namespaces: Features/ComputedStyleBinding.cs migrated, the getter has been the realm's since, and
// the only thing keeping the claim half-true was a ToEngineObject feeding two installers that
// undid it.

public sealed partial class DomBridge
{
    /// <summary>
    /// The per-tag member pass: everything an element gets because of what tag it is, rather than
    /// because it is an <c>Element</c> or an <c>HTMLElement</c>.
    /// </summary>
    /// <remarks>
    /// The wrapper arrives as a handle and stays one: nothing here derives an engine object from it
    /// any more. Property order is unaffected by that. A handle and the engine object it carries name
    /// the same object (<c>Runtime/JsInterop.cs</c> is a cast, not a conversion), so every member still
    /// lands in the position it is installed in and <c>Object.getOwnPropertyNames(el)</c> reports the
    /// order it always has.
    /// </remarks>
    private void AddElementSpecificMembers(JsValue handle, Broiler.Dom.DomElement element)
    {
        // -- Phase 5: HTML DOM Interfaces --

        var tag = element.TagName.ToLowerInvariant();

        // HTMLTableElement / HTMLTableSectionElement / HTMLTableRowElement interfaces (Phase 3 P3.5:
        // extracted into the co-located TableBinding feature module).
        _tables.Install(handle, element, tag);

        // HTMLFormElement interface (Phase 3 P3.9: extracted into the co-located FormBinding module).
        _forms.Install(handle, element, tag);

        // HTMLDetailsElement.open, HTMLDialogElement (showModal/show/close/open/returnValue) and the
        // popover API (Phase 3 P3.7: extracted into the co-located DialogBinding feature module).
        _dialogs.Install(handle, element, tag, HasAttr(element, "popover"));

        // HTMLSelectElement / HTMLOptionElement (Phase 3 P3.8: extracted into the co-located
        // SelectBinding feature module).
        _select.Install(handle, element, tag);

        // HTMLMediaElement.canPlayType() on <video>/<audio> — the capability question a media player
        // asks before it commits to a source (Phase 3 co-located MediaCapabilityBinding module,
        // shared with the MediaSource.isTypeSupported that answers it statically).
        Dom.Features.MediaCapabilityBinding.Install(Realm, handle, tag);

        // Form association (HTML §4.10.2, §4.10.4): a control's `form` owner and `labels`, and a
        // label's `control`. Installed per tag rather than on every wrapper, because their absence
        // on a non-form element is observable — see FormAssociationBinding.
        Dom.Features.FormAssociationBinding.Install(this, handle, element, tag);

        // HTMLLabelElement — htmlFor property (maps to 'for' content attribute)
        if (tag == "label")
        {
            Realm.DefineAccessor(handle, "htmlFor",
                (in _) => ReflectedAttribute(element, "for"),
                (in call) => Dom.Features.ElementReflectionBinding.SetHtmlFor(element, in call));
        }

        // HTMLMetaElement — httpEquiv property (maps to 'http-equiv' content attribute)
        if (tag == "meta")
        {
            Realm.DefineAccessor(handle, "httpEquiv",
                (in _) => ReflectedAttribute(element, "http-equiv"),
                (in call) => Dom.Features.ElementReflectionBinding.SetHttpEquiv(element, in call));
        }

        // HTMLObjectElement — data property with URI resolution + contentDocument + getSVGDocument + type
        if (tag == "object")
        {
            // data get (reflected URL) + type get/set are in ElementReflectionBinding (P3.49); the data
            // setter, contentDocument getter and getSVGDocument() are sub-document-coupled and live in the
            // ObjectElementBinding feature module (Phase 3 P3.52).
            // Both modules are migrated now, so the pair is the realm's: it names the two functions
            // "get data"/"set data" as the engine-typed installation spelled out, and the setter's
            // ToString is the realm's — the same ECMAScript coercion on the same value.
            Realm.DefineAccessor(handle, "data",
                (in _) => Dom.Features.ElementReflectionBinding.GetData(this, element),
                (in call) => Dom.Features.ObjectElementBinding.SetData(this, element, in call));

            // type property (MIME type of the resource)
            Realm.DefineAccessor(handle, "type",
                (in _) => ReflectedAttribute(element, "type"),
                (in call) => Dom.Features.ElementReflectionBinding.SetType(element, in call));

            // contentDocument for <object> element (with same-origin check)
            // Returns null when the resource fails to load (HTTP 404, file not found, etc.)
            // which signals that the fallback content (child nodes) should be visible.
            Realm.DefineAccessor(handle, "contentDocument",
                (in _) => Dom.Features.ObjectElementBinding.ContentDocument(this, element), null);

            // getSVGDocument() for <object> element
            Realm.DefineValue(handle, "getSVGDocument",
                Realm.NewMethod("getSVGDocument",
                    (in _) => Dom.Features.ObjectElementBinding.SvgDocument(this, element), 0));
        }

        // HTMLAnchorElement — href property with URI resolution
        if (tag == "a")
        {
            Realm.DefineAccessor(handle, "href",
                (in _) => Dom.Features.ElementReflectionBinding.GetHref(this, element),
                (in call) => Dom.Features.ElementReflectionBinding.SetHref(element, in call));
        }

        // -- Phase 7: HTMLAreaElement properties --
        if (tag == "area")
        {
            // shape, coords, alt, target — simple reflected attributes
            foreach (var attrName in new[] { "shape", "coords", "alt", "target" })
            {
                var captured = attrName; // capture for closure
                Realm.DefineAccessor(handle, captured,
                    (in _) => ReflectedAttribute(element, captured),
                    (in call) => Dom.Features.ElementReflectionBinding.SetReflectedAttribute(captured, element, in call));
            }

            // href — with URI resolution like <a>
            Realm.DefineAccessor(handle, "href",
                (in _) => Dom.Features.ElementReflectionBinding.GetHref(this, element),
                (in call) => Dom.Features.ElementReflectionBinding.SetHref(element, in call));
        }

        // HTMLLinkElement / HTMLBaseElement — href is a reflected URL, exactly as on <a>/<area>.
        // Neither had it, so `link.href = "…"` wrote nothing at all: a stylesheet injected the
        // ordinary way — createElement("link"), set .rel and .href, append — serialized as a bare
        // <link> with no attributes and never reached the cascade, and the page rendered unstyled
        // (WPT issue #1497 problem 24, dom/nodes/moveBefore/preserve-render-blocking-style).
        if (tag is "link" or "base")
        {
            // Writing a live <link>'s href points it at a new sheet, which is a fresh fetch and so a
            // fresh load event (HTML §4.2.4) — the shape UIEvent.load.stylesheet waits on.
            var isLink = tag == "link";
            Realm.DefineAccessor(handle, "href",
                (in _) => Dom.Features.ElementReflectionBinding.GetHref(this, element),
                (in call) =>
                {
                    var result = Dom.Features.ElementReflectionBinding.SetHref(element, in call);
                    if (isLink)
                        FireStylesheetLinkLoad(element);
                    return result;
                });
        }

        // The rest of HTMLLinkElement's plain reflected DOMStrings. `rel` also fires the load event:
        // a link only becomes a stylesheet once rel says so, so `link.href = …; link.rel = …` has to
        // work as well as the other order.
        if (tag == "link")
        {
            foreach (var (idlName, attrName) in LinkReflectedAttributes)
            {
                var captured = attrName; // capture for closure
                var firesLoad = captured == "rel";
                Realm.DefineAccessor(handle, idlName,
                    (in _) => ReflectedAttribute(element, captured),
                    (in call) =>
                    {
                        var result = Dom.Features.ElementReflectionBinding.SetReflectedAttribute(captured, element, in call);
                        if (firesLoad)
                            FireStylesheetLinkLoad(element);
                        return result;
                    });
            }
        }

        // HTMLScriptElement — the reflected IDL attributes. None of them existed, so the one line
        // every dynamic script loader on the web is built out of — `s = createElement("script");
        // s.src = url; head.appendChild(s)` — set a plain JS property on the wrapper and wrote
        // nothing to the DOM. The element serialized as a bare <script> with no attributes, and with
        // no src there was nothing for ScriptInsertionRunner to fetch: the injected script never ran,
        // and a page waiting on the global it defines waited forever (html5test.com's
        // waitForWhichBrowser poll). Exactly the shape of the <link>.href gap fixed above.
        if (tag == "script")
        {
            Realm.DefineAccessor(handle, "src",
                (in _) => Dom.Features.ElementReflectionBinding.GetSrc(this, element),
                (in call) => Dom.Features.ElementReflectionBinding.SetSrc(element, in call));

            foreach (var (idlName, attrName) in ScriptReflectedAttributes)
            {
                var captured = attrName; // capture for closure
                Realm.DefineAccessor(handle, idlName,
                    (in _) => ReflectedAttribute(element, captured),
                    (in call) => Dom.Features.ElementReflectionBinding.SetReflectedAttribute(captured, element, in call));
            }

            foreach (var (idlName, attrName) in ScriptReflectedBooleans)
            {
                var captured = attrName; // capture for closure
                Realm.DefineAccessor(handle, idlName,
                    (in _) => JsValue.Boolean(HasAttr(element, captured)),
                    (in call) => Dom.Features.ElementReflectionBinding.SetReflectedBoolean(captured, element, in call));
            }

            // .text is HTMLScriptElement's own name for its child text — the other half of the
            // loader idiom, for an inline script built in JS rather than fetched. textContent is
            // already installed on every element and does the same thing; this aliases it so
            // `s.text = code` is not silently a plain JS property either.
            Realm.DefineAccessor(handle, "text",
                (in _) => JsValue.String(element.TextContent),
                (in call) =>
                {
                    // ToJsString, not the handle's own rendering: `s.text = templateObject` runs the
                    // object's toString, which is what the engine was doing here before. Unlike
                    // textContent, `text` is a plain DOMString, so `s.text = null` writes "null".
                    element.TextContent = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
                    return JsValue.Undefined;
                });
        }

        // HTMLImageElement — height/width return computed CSS value or HTML attribute (Phase 3 P3.53:
        // the used-dimension getter moved to the ComputedStyleBinding feature module alongside
        // getComputedStyle; the reflected-dimension setter is in ElementReflectionBinding, P3.49).
        if (tag == "img")
        {
            foreach (var dim in new[] { "height", "width" })
            {
                var dimName = dim;
                // Both halves are the realm's now. This was mixed -- an engine getter beside a
                // realm-minted setter converted back out with ToEngineObject -- because the used
                // dimension's module had not migrated. It has, so the pair is one DefineAccessor
                // over the handle the wrapper arrived as, and `obj` is not involved.
                Realm.DefineAccessor(handle, dimName,
                    (in call) => Dom.Features.ComputedStyleBinding.GetUsedDimension(this, dimName, element),
                    (in call) => Dom.Features.ElementReflectionBinding.SetReflectedDimension(dimName, element, in call));
            }

            // .src — a reflected URL, resolved against the page URL exactly as on <script>/<a>/<link>.
            // It was missing entirely, so reading an image's URL the ordinary way produced *undefined*
            // rather than a string, and every caller that went straight on to a string method died on
            // it: mediawiki.org's start page reached mw.Title.newFromImg(), which hands img.src to
            // mw.util.parseImageUrl(), whose first act is url.match(...) — "Cannot get property match
            // of undefined". That throw took MultimediaViewerBootstrap.processThumbs down and, with
            // it, the rest of the single load.php bundle queued behind it.
            Realm.DefineAccessor(handle, "src",
                (in _) => Dom.Features.ElementReflectionBinding.GetSrc(this, element),
                (in call) => Dom.Features.ElementReflectionBinding.SetSrc(element, in call));

            // .currentSrc — read-only, the URL of the image the element actually settled on. Srcset
            // candidate selection happens down in layout and is not visible from here, so this reports
            // the resolved src, and the empty string when there is no src at all. That is the pair of
            // answers the `img.currentSrc || img.src` idiom is written against (mmv.bootstrap's
            // processThumb reads exactly that, then calls .includes() on the result).
            Realm.DefineAccessor(handle, "currentSrc",
                (in _) => Dom.Features.ElementReflectionBinding.GetCurrentSrc(this, element), null);

            // .isMap — the one boolean in the interface: present or absent, never the string "false".
            Realm.DefineAccessor(handle, "isMap",
                (in _) => JsValue.Boolean(HasAttr(element, "ismap")),
                (in call) => Dom.Features.ElementReflectionBinding.SetReflectedBoolean("ismap", element, in call));

            // The rest of HTMLImageElement's plain reflected DOMStrings — alt, srcset, sizes, useMap
            // and the enumerated fetch hints. Same gap as .src: reading any of them returned undefined
            // and writing one set a plain JS property on the wrapper that no content attribute, and so
            // no layout or serialization, ever saw.
            foreach (var (idlName, attrName) in ImageReflectedAttributes)
            {
                var captured = attrName; // capture for closure
                Realm.DefineAccessor(handle, idlName,
                    (in _) => ReflectedAttribute(element, captured),
                    (in call) => Dom.Features.ElementReflectionBinding.SetReflectedAttribute(captured, element, in call));
            }
        }

        // HTMLIFrameElement.width/height are plain reflected DOMString content attributes (unlike
        // HTMLImageElement's, which are unsigned longs returning the used dimension). Without the
        // reflection `iframe.width = 100` set nothing at all, so the box fell back to the replaced
        // element's 300x150 default object size instead of the size the page asked for
        // (WPT html/semantics/interactive-elements/the-dialog-element/centering).
        if (tag == "iframe")
        {
            foreach (var dim in new[] { "height", "width" })
            {
                var dimName = dim;
                Realm.DefineAccessor(handle, dimName,
                    (in _) => ReflectedAttribute(element, dimName),
                    (in call) => Dom.Features.ElementReflectionBinding.SetReflectedDimension(dimName, element, in call));
            }
        }

        // The bridge's own scrollParent — Phase 3 P3.51: extracted into the co-located
        // ElementGeometryBinding feature module. It reads the live layout, so the module reaches the
        // bridge through the wide IElementGeometryHost contract (DomBridge/Hosts.Elements.cs).
        // The two interface halves of that module are on their prototypes: Element's client*/scroll*
        // metrics, getBoundingClientRect/getClientRects and the imperative scrolling API
        // (DomBridge/ElementInterface.cs, with animate()), and HTMLElement's offset* family
        // (DomBridge/ElementInterface.cs). scrollParent is on neither, because it is on no
        // browser's prototype.
        Dom.Features.ElementGeometryBinding.InstallBridgeMembers(this, Realm, handle, element);

        // SVG DOM interfaces — SVGAnimatedLength/Rect stubs, SVGTextContentElement text metrics, the
        // SVGSVGElement animation timeline and the SMIL animation-element no-ops (Phase 3 P3.50:
        // extracted into the co-located SvgElementBinding feature module). The module is migrated to
        // JSEAL and the wrapper is a handle, so it is passed straight through; the note that used to
        // stand here called this call a JsInterop.FromEngineObject seam over a "still-engine-typed
        // wrapper", and it has been neither for some time.
        Dom.Features.SvgElementBinding.Install(Realm, handle, element, tag);
    }
}

/// <summary>
/// <c>Node</c>, <c>CharacterData</c> and <c>Text</c> as real interfaces for a character-data node:
/// their members on the interface prototypes rather than copied onto every text and comment wrapper.
/// </summary>
/// <remarks>
/// <para>
/// Every DOM wrapper in this bridge installed its interface as own properties of each object, so
/// <c>Object.getOwnPropertyNames(node)</c> listed the whole interface and
/// <c>Text.prototype.splitText</c> was <see langword="undefined"/> — track 6's wrapper item. The
/// prototype <em>chain</em> was already real through <see cref="ApplyInterfacePrototype"/>
/// (<c>Text → CharacterData → Node → EventTarget → Object</c>), and the interface objects existed;
/// what had not happened was the engine putting its members on them. A text node carried 57 own
/// properties where a browser gives it none.
/// </para>
/// <para>
/// This is the first node interface to move, and the mechanism it needs is the general one:
/// a member on a prototype has no node captured in a closure, so it finds one from its receiver
/// (<see cref="RequireNode"/>, over the registry's constant-time reverse map). That is also what
/// makes an illegal invocation — <c>Text.prototype.splitText.call({}, 1)</c> — a <c>TypeError</c>
/// rather than a crash or a silent wrong answer. <c>Range</c>, <c>Selection</c> and <c>Blob</c> are
/// the same shape with their state in a weak table; a node's state is the node, so the registry that
/// already owns wrapper identity is the table.
/// </para>
/// <para>
/// <b>The split across the three prototypes is Web IDL's, not a convenience.</b> The tree accessors,
/// the node methods and the <c>ChildNode</c> mixin members go on <c>Node.prototype</c> and
/// <c>CharacterData.prototype</c> where the specification puts them, so a page walking a prototype's
/// own property names reads the shape a browser has. <c>splitText</c> is <c>Text</c>'s alone, which
/// is why the old wrapper installed it behind an <c>IsText</c> test and why a <c>Comment</c> must not
/// inherit it.
/// </para>
/// <para>
/// <b>An element inherits the <c>Node.prototype</c> members installed here too.</b> It shadowed
/// every one with a byte-identical copy of its own; those copies are gone, so the prototype is where
/// they live for an element as well — see <c>PopulateElementNodeMembersOnInstance</c>, which is now
/// only the pre-realm fallback. <c>textContent</c> is the exception and stays the element's own. It
/// was one because an element's was a different operation from a character-data node's; both are
/// the canonical <c>DomNode.TextContent</c> now, so an element answers the same through either.
/// </para>
/// <para>
/// The document kept its own — separate implementations, not copies: <c>nodeType</c> a literal
/// <c>9</c>, <c>childNodes</c> a different binding — so each was checked against the prototype's
/// answer before <c>DropDocumentNodeMemberCopies</c> deleted it (a sub-document object still installs
/// its own), and <c>Element</c>'s surface has moved to its prototypes too. (This left both open.)
/// </para>
/// <para>
/// <b>The three <c>EventTarget</c> members are not here, and not on the instance either.</b> They
/// stayed on the wrapper when this moved, because the realm's own <c>EventTarget.prototype</c> keeps
/// its listeners engine-side where the bridge's dispatch would never find them — so a node could not
/// simply inherit them, and shadowing them on <c>Node.prototype</c> would have put three members on a
/// prototype no browser carries them on. That is resolved where it belongs, on
/// <c>EventTarget.prototype</c> itself: see <c>DomBridge/Events.cs</c>, which routes
/// those three by receiver. A text or comment node consequently carries no own properties at all.
/// </para>
/// <para>
/// <b>One vocabulary for installing a prototype member.</b> <see cref="DefinePrototypeMethod"/> and
/// <see cref="DefinePrototypeAccessor"/> mint through the realm, and every member below uses them —
/// the <c>ChildNode</c> mixin four included — because every body below calls a binding that reads a
/// <see cref="JsCall"/> frame. An engine-typed pair stood beside them for
/// <c>DomBridge/ElementInterface.cs</c> and <c>DomBridge/ElementInterface.cs</c>, whose bodies
/// took the engine's argument frame; those two files install every member through the realm now, and
/// the engine-framed helper this said they still kept, for <c>animate</c> and
/// <c>click</c>/<c>focus</c>/<c>blur</c>, is gone with the frame those bodies read. Nothing here
/// names an engine type.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether the node interface prototypes carry their members yet, which is what lets a wrapper
    /// stop installing them — a character-data wrapper its whole interface, an element the <c>Node</c>
    /// members it used to duplicate.
    /// </summary>
    /// <remarks>
    /// A wrapper minted before the realm is up has no prototype to inherit from —
    /// <see cref="ApplyInterfacePrototype"/> is a no-op then — so it still installs its own members,
    /// exactly as before. Without that fallback such a node would have neither, and the shape it gets
    /// is the old one rather than a broken one.
    /// </remarks>
    private bool _nodeInterfacePrototypesReady;

    /// <summary>
    /// Installs the <c>Node</c>, <c>CharacterData</c> and <c>Text</c> members a character-data node
    /// exposes onto their interface prototypes. A no-op when the realm does not carry the interfaces.
    /// </summary>
    internal void RegisterCharacterDataInterface()
    {
        // Asked for one at a time, so that a realm missing `Node` never looks the other two up — the
        // short-circuit the `is not { } … || …` chain this replaces performed.
        var nodeProto = PrototypeHandleOfInterface("Node");
        if (!nodeProto.IsObject)
            return;

        var characterDataProto = PrototypeHandleOfInterface("CharacterData");
        if (!characterDataProto.IsObject)
            return;

        var textProto = PrototypeHandleOfInterface("Text");
        if (!textProto.IsObject)
            return;

        InstallNodePrototypeMembers(nodeProto);
        InstallCharacterDataPrototypeMembers(characterDataProto);
        InstallElementNamePrototypeMembers();

        // Text's alone: a Comment inherits CharacterData and must not answer splitText.
        DefinePrototypeMethod(textProto, "splitText", 1,
            (in call) => Dom.Features.CharacterDataBinding.SplitText(
                this, RequireNode(in call, "Text", "splitText"), in call));

        _nodeInterfacePrototypesReady = true;

        DropDocumentNodeMemberCopies();
    }

    /// <summary>
    /// The <c>Node</c> members and constants the <c>document</c> wrapper installed for itself, dropped
    /// now that <c>Node.prototype</c> carries them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other wrapper is minted lazily and simply skips installing what it can inherit. The
    /// document's is not: it is built during document registration, which runs before the interface
    /// constructors this pass needs exist, so by the time there is a prototype to inherit from it has
    /// already made its own copies. Removing them afterwards is what makes the ordering irrelevant,
    /// short of reordering registration itself.
    /// </para>
    /// <para>
    /// Only the five members it actually had, and only after checking that the prototype answers the
    /// same for a document receiver: <c>nodeType</c> is 9, <c>nodeName</c> is <c>#document</c>,
    /// <c>childNodes</c>/<c>firstChild</c>/<c>lastChild</c> report the same nodes. They were separate
    /// implementations rather than copies of the prototype's — a literal <c>9</c>, a different
    /// <c>childNodes</c> binding — so agreeing was a thing to verify rather than assume.
    /// </para>
    /// <para>
    /// The eighteen constants beside them need no such check: they are plain numbers, and
    /// <c>RegisterNodeConstructor</c> puts the same eighteen values on <c>Node.prototype</c> — the
    /// copies were duplication rather than a second implementation.
    /// </para>
    /// </remarks>
    private void DropDocumentNodeMemberCopies()
    {
        // DocumentHandle is the bridge's document root (DomBridge.cs), the handle the page's `document`
        // is, so deleting through the realm deletes from what the page holds. Missing — no document
        // registered yet — is not an object, which is the same escape the null test on the old
        // engine-typed field made.
        var handle = DocumentHandle;
        if (!handle.IsObject)
            return;

        foreach (var member in new[] { "nodeType", "nodeName", "childNodes", "firstChild", "lastChild" })
            Realm.DeleteProperty(handle, member);

        foreach (var constant in Dom.Features.NodeConstantsBinding.Names)
            Realm.DeleteProperty(handle, constant);
    }

    /// <summary>
    /// The <c>Node</c> constants for a wrapper that cannot inherit them — one minted before the realm
    /// carried the interfaces. Every other wrapper's chain reaches <c>Node.prototype</c>, which has
    /// all eighteen.
    /// </summary>
    private void InstallNodeConstantsIfNotInherited(JsValue handle)
    {
        if (!_nodeInterfacePrototypesReady)
            Dom.Features.NodeConstantsBinding.Install(Realm, handle);
    }

    /// <summary>
    /// The prototype object of a registered interface global as a JSEAL handle, or
    /// <see cref="JsValue.Undefined"/> when the realm carries no such interface.
    /// </summary>
    /// <remarks>
    /// Two property reads through the realm: the global's <paramref name="interfaceName"/>, then that
    /// constructor's <c>prototype</c>. They are the reads the engine-typed <c>PrototypeOfInterface</c>
    /// (retired in 5282d02) made through the context, which <c>Realm.Global</c> <em>is</em> under Broiler.JS.
    /// </remarks>
    private JsValue PrototypeHandleOfInterface(string interfaceName)
    {
        var constructor = Realm.GetProperty(Realm.Global, interfaceName);
        if (!constructor.IsObject)
            return JsValue.Undefined;

        var prototype = Realm.GetProperty(constructor, "prototype");
        return prototype.IsObject ? prototype : JsValue.Undefined;
    }

    /// <summary>
    /// <c>Node.prototype</c>: the tree accessors and node operations. Character data, elements (bar
    /// <c>textContent</c>) and the document inherit them; a doctype, fragment or sub-document object
    /// still shadows those it installs itself. (This said an element or document shadowed each one.)
    /// </summary>
    private void InstallNodePrototypeMembers(JsValue proto)
    {
        DefinePrototypeAccessor(proto, "nodeType",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeType(RequireNode(in call, "Node", "nodeType"), in call));
        DefinePrototypeAccessor(proto, "nodeName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeName(RequireNode(in call, "Node", "nodeName"), in call));

        DefinePrototypeAccessor(proto, "nodeValue",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeValue(RequireNode(in call, "Node", "nodeValue"), in call),
            (in call) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, RequireNode(in call, "Node", "nodeValue"), in call));
        DefinePrototypeAccessor(proto, "textContent",
            // JsValue.String turns the "no text at all" null into JavaScript null, which is the
            // distinction DOM §4.4 draws for a document and a doctype. (This also named an engine-typed
            // GetNodeTextValue adapter as producing the same value elsewhere; that adapter is gone.)
            (in call) => JsValue.String(NodeTextOrNull(RequireNode(in call, "Node", "textContent"))),
            // The canonical setter, the one every node kind's textContent uses. This was the nodeValue
            // setter, which coerced null to "null" and wrote nothing but a text or comment node's data.
            (in call) => Dom.Features.NodeAccessorsBinding.SetTextContent(RequireNode(in call, "Node", "textContent"), in call));

        DefinePrototypeAccessor(proto, "parentNode", (in call) =>
        {
            var node = RequireNode(in call, "Node", "parentNode");
            return node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null;
        });
        DefinePrototypeAccessor(proto, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, RequireNode(in call, "Node", "parentElement"), in call));
        DefinePrototypeAccessor(proto, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(RequireNode(in call, "Node", "isConnected"), in call));
        DefinePrototypeAccessor(proto, "childNodes",
            (in call) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, RequireNode(in call, "Node", "childNodes"), in call));
        DefinePrototypeAccessor(proto, "firstChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, RequireNode(in call, "Node", "firstChild"), in call));
        DefinePrototypeAccessor(proto, "lastChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, RequireNode(in call, "Node", "lastChild"), in call));
        DefinePrototypeAccessor(proto, "nextSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, RequireNode(in call, "Node", "nextSibling"), in call));
        DefinePrototypeAccessor(proto, "previousSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, RequireNode(in call, "Node", "previousSibling"), in call));
        DefinePrototypeAccessor(proto, "ownerDocument",
            (in call) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, RequireNode(in call, "Node", "ownerDocument"), in call));

        DefinePrototypeMethod(proto, "hasChildNodes", 0, (in call) =>
            JsValue.Boolean(RequireNode(in call, "Node", "hasChildNodes").ChildNodes.Count > 0));
        DefinePrototypeMethod(proto, "cloneNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, RequireNode(in call, "Node", "cloneNode"), in call));
        DefinePrototypeMethod(proto, "contains", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, RequireNode(in call, "Node", "contains"), in call));
        DefinePrototypeMethod(proto, "compareDocumentPosition", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, RequireNode(in call, "Node", "compareDocumentPosition"), in call));
        DefinePrototypeMethod(proto, "isSameNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, RequireNode(in call, "Node", "isSameNode"), in call));
        DefinePrototypeMethod(proto, "isEqualNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, RequireNode(in call, "Node", "isEqualNode"), in call));
        DefinePrototypeMethod(proto, "getRootNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, RequireNode(in call, "Node", "getRootNode"), in call));
        DefinePrototypeMethod(proto, "normalize", 0,
            (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, RequireNode(in call, "Node", "normalize"), in call));
    }

    /// <summary>
    /// <c>localName</c>, <c>prefix</c> and <c>namespaceURI</c> on <c>Element.prototype</c>, which is
    /// where the DOM puts them.
    /// </summary>
    /// <remarks>
    /// They are not <c>Node</c> members, though this pass first installed them there: the
    /// character-data wrapper carried all three as own properties, and moving that wrapper's members
    /// wholesale took them along. On <c>Node.prototype</c> they reach every node, so a text node
    /// answered <c>null</c> and — once an element stopped shadowing them — so did the document, where
    /// a browser answers <c>undefined</c> for both because neither interface declares them. DOM §4.9
    /// gives them to <c>Element</c>, and <c>Attr</c> separately; measured in Chromium,
    /// <c>'localName' in Node.prototype</c> is <see langword="false"/> and
    /// <c>Element.prototype</c> owns all three.
    /// </remarks>
    private void InstallElementNamePrototypeMembers()
    {
        var proto = PrototypeHandleOfInterface("Element");
        if (!proto.IsObject)
            return;

        DefinePrototypeAccessor(proto, "localName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLocalName(RequireNode(in call, "Element", "localName"), in call));
        DefinePrototypeAccessor(proto, "prefix",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPrefix(RequireNode(in call, "Element", "prefix"), in call));
        DefinePrototypeAccessor(proto, "namespaceURI",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNamespaceURI(RequireNode(in call, "Element", "namespaceURI"), in call));
    }

    /// <summary>
    /// <c>CharacterData.prototype</c>: the data operations, plus the <c>ChildNode</c> mixin members —
    /// which the mixin gives to <c>CharacterData</c>, <c>Element</c> and <c>DocumentType</c>
    /// separately, so they belong here rather than on <c>Node.prototype</c>.
    /// </summary>
    /// <remarks>
    /// The four <c>ChildNode</c> members are minted by the realm like the rest: <c>ChildNodeBinding</c>
    /// reads a <see cref="JsCall"/> at the entry point this prototype reaches it through. Their
    /// position in the install order is unchanged, which is what
    /// <c>Object.getOwnPropertyNames(CharacterData.prototype)</c> can see.
    /// </remarks>
    private void InstallCharacterDataPrototypeMembers(JsValue proto)
    {
        DefinePrototypeAccessor(proto, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(RequireNode(in call, "CharacterData", "data"), in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, RequireNode(in call, "CharacterData", "data"), in call));
        DefinePrototypeAccessor(proto, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(RequireNode(in call, "CharacterData", "length"), in call));

        DefinePrototypeMethod(proto, "substringData", 2,
            (in call) => Dom.Features.CharacterDataBinding.SubstringData(this, RequireNode(in call, "CharacterData", "substringData"), in call));
        DefinePrototypeMethod(proto, "appendData", 1,
            (in call) => Dom.Features.CharacterDataBinding.AppendData(this, RequireNode(in call, "CharacterData", "appendData"), in call));
        DefinePrototypeMethod(proto, "deleteData", 2,
            (in call) => Dom.Features.CharacterDataBinding.DeleteData(this, RequireNode(in call, "CharacterData", "deleteData"), in call));
        DefinePrototypeMethod(proto, "insertData", 2,
            (in call) => Dom.Features.CharacterDataBinding.InsertData(this, RequireNode(in call, "CharacterData", "insertData"), in call));
        DefinePrototypeMethod(proto, "replaceData", 3,
            (in call) => Dom.Features.CharacterDataBinding.ReplaceData(this, RequireNode(in call, "CharacterData", "replaceData"), in call));

        DefinePrototypeMethod(proto, "remove", 0,
            (in call) => Dom.Features.ChildNodeBinding.Remove(this, RequireNode(in call, "CharacterData", "remove"), in call));
        DefinePrototypeMethod(proto, "before", 0,
            (in call) => Dom.Features.ChildNodeBinding.Before(this, RequireNode(in call, "CharacterData", "before"), in call));
        DefinePrototypeMethod(proto, "after", 0,
            (in call) => Dom.Features.ChildNodeBinding.After(this, RequireNode(in call, "CharacterData", "after"), in call));
        DefinePrototypeMethod(proto, "replaceWith", 0,
            (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, RequireNode(in call, "CharacterData", "replaceWith"), in call));
    }

    /// <summary>
    /// The node a prototype member was called on, or a <c>TypeError</c> naming the interface and the
    /// member when the receiver is not a node wrapper — which is what a browser answers for
    /// <c>Text.prototype.splitText.call({}, 1)</c>.
    /// </summary>
    private DomNode RequireNode(in JsCall call, string interfaceName, string member)
    {
        // The reverse map keys on JsValue.ObjectIdentity, so the receiver is looked up as it stands. A
        // non-object receiver answers no node without asking, the branch the engine-object test took.
        if (call.This.IsObject &&
            _jsObjects.TryGetNode(call.This, out var node))
        {
            return node;
        }

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on '{interfaceName}': Illegal invocation");
    }

    /// <summary>Adds a WebIDL operation to an interface prototype, through the realm.</summary>
    /// <remarks>
    /// <see cref="JsPropertyFlags.Default"/> is enumerable, configurable and writable — what the
    /// instance properties were and what Web IDL asks for on a prototype; keeping the same attributes
    /// means only the *location* of the member changes.
    /// </remarks>
    private void DefinePrototypeMethod(JsValue proto, string name, int length, JsNativeFunction body) =>
        Realm.DefineValue(proto, name, Realm.NewMethod(name, body, length));

    /// <summary>Adds a WebIDL attribute to an interface prototype, read-only unless a setter is given.</summary>
    /// <remarks>
    /// A null <paramref name="setter"/> is how a read-only IDL attribute is spelled, and the realm
    /// names the pair <c>get name</c>/<c>set name</c> — the names its engine-typed sibling,
    /// <c>AddPrototypeAccessor</c>, gave them explicitly until 5282d02 removed it.
    /// </remarks>
    private void DefinePrototypeAccessor(JsValue proto, string name,
        JsNativeFunction getter, JsNativeFunction? setter = null) =>
        Realm.DefineAccessor(proto, name, getter, setter);
}
