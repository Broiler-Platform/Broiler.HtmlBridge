using System.Text;

using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    /// <summary>
    /// The per-tag HTML element interfaces (HTML §4, "Element interfaces"), as the pairs
    /// <c>interface name → the tag names whose elements implement it</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>HTMLElement</c> answers for every HTML element and is registered separately; this table is
    /// only the subtypes below it. A tag absent from the table implements <c>HTMLElement</c> itself
    /// (<c>span</c> is the exception — it has a named interface that adds nothing — and the grouped
    /// entries are the spec's own: one interface serving several tags, as <c>HTMLQuoteElement</c>
    /// does for <c>blockquote</c> and <c>q</c>).
    /// </para>
    /// <para>
    /// <b>Every entry is single-valued: one tag names exactly one interface, its most derived one.</b>
    /// That is what lets the table answer <c>constructor.name</c> as well as <c>instanceof</c>. It
    /// used to carry an overlapping <c>("HTMLMediaElement", "audio video")</c> entry beside
    /// <c>HTMLAudioElement</c> and <c>HTMLVideoElement</c>, so <c>audio</c> named two interfaces and a
    /// reverse lookup had no answer — which is precisely why naming an element's interface was left
    /// undone. The abstract bases now come from <see cref="HtmlInterfaceBases"/> instead and are
    /// expanded into the <c>instanceof</c> sets at registration, so <c>audio instanceof
    /// HTMLMediaElement</c> still holds while <c>audio.constructor.name</c> can be
    /// <c>HTMLAudioElement</c>.
    /// </para>
    /// <para>
    /// The tag-to-interface assignments are Chromium's measured answers to
    /// <c>document.createElement(tag).constructor.name</c> over every HTML tag, not a reading of the
    /// specification — which is how the <c>plaintext</c> entry was found to be wrong. It sat with
    /// <c>listing pre xmp</c> under <c>HTMLPreElement</c>, and a browser gives it plain
    /// <c>HTMLElement</c>; the other three are right.
    /// </para>
    /// </remarks>
    private static readonly (string Interface, string Tags)[] HtmlElementInterfaces =
    [
        ("HTMLAnchorElement", "a"),
        ("HTMLAreaElement", "area"),
        ("HTMLAudioElement", "audio"),
        ("HTMLBaseElement", "base"),
        ("HTMLQuoteElement", "blockquote q"),
        ("HTMLBodyElement", "body"),
        ("HTMLBRElement", "br"),
        ("HTMLButtonElement", "button"),
        ("HTMLCanvasElement", "canvas"),
        ("HTMLTableCaptionElement", "caption"),
        ("HTMLTableColElement", "col colgroup"),
        ("HTMLDataElement", "data"),
        ("HTMLDataListElement", "datalist"),
        ("HTMLModElement", "del ins"),
        ("HTMLDetailsElement", "details"),
        ("HTMLDialogElement", "dialog"),
        ("HTMLDirectoryElement", "dir"),
        ("HTMLDivElement", "div"),
        ("HTMLDListElement", "dl"),
        ("HTMLEmbedElement", "embed"),
        ("HTMLFieldSetElement", "fieldset"),
        ("HTMLFontElement", "font"),
        ("HTMLFormElement", "form"),
        ("HTMLFrameElement", "frame"),
        ("HTMLFrameSetElement", "frameset"),
        ("HTMLHeadingElement", "h1 h2 h3 h4 h5 h6"),
        ("HTMLHeadElement", "head"),
        ("HTMLHRElement", "hr"),
        ("HTMLHtmlElement", "html"),
        ("HTMLIFrameElement", "iframe"),
        ("HTMLImageElement", "img"),
        ("HTMLInputElement", "input"),
        ("HTMLLabelElement", "label"),
        ("HTMLLegendElement", "legend"),
        ("HTMLLIElement", "li"),
        ("HTMLLinkElement", "link"),
        // `plaintext` is deliberately absent: a browser gives it plain HTMLElement, not this.
        ("HTMLPreElement", "listing pre xmp"),
        ("HTMLMapElement", "map"),
        ("HTMLMarqueeElement", "marquee"),
        ("HTMLMenuElement", "menu"),
        ("HTMLMetaElement", "meta"),
        ("HTMLMeterElement", "meter"),
        ("HTMLObjectElement", "object"),
        ("HTMLOListElement", "ol"),
        ("HTMLOptGroupElement", "optgroup"),
        ("HTMLOptionElement", "option"),
        ("HTMLOutputElement", "output"),
        ("HTMLParagraphElement", "p"),
        ("HTMLParamElement", "param"),
        ("HTMLPictureElement", "picture"),
        ("HTMLProgressElement", "progress"),
        ("HTMLScriptElement", "script"),
        ("HTMLSelectElement", "select"),
        ("HTMLSlotElement", "slot"),
        ("HTMLSourceElement", "source"),
        ("HTMLSpanElement", "span"),
        ("HTMLStyleElement", "style"),
        ("HTMLTableElement", "table"),
        ("HTMLTableSectionElement", "tbody tfoot thead"),
        ("HTMLTableCellElement", "td th"),
        ("HTMLTemplateElement", "template"),
        ("HTMLTextAreaElement", "textarea"),
        ("HTMLTimeElement", "time"),
        ("HTMLTitleElement", "title"),
        ("HTMLTableRowElement", "tr"),
        ("HTMLTrackElement", "track"),
        ("HTMLUListElement", "ul"),
        ("HTMLVideoElement", "video"),
    ];

    /// <summary>
    /// The Web IDL inheritance edges that are <em>not</em> the default. Every interface in
    /// <see cref="HtmlElementInterfaces"/> derives from <c>HTMLElement</c> unless named here.
    /// </summary>
    /// <remarks>
    /// This is what replaces the old overlapping table entry, and it carries more than that entry
    /// did: an interface's <c>instanceof</c> set is its own tags plus every descendant's, so
    /// <c>HTMLMediaElement</c> answers for <c>audio</c> and <c>video</c> without being either one's
    /// own interface — and the same edges are what the prototype chain is built from, so
    /// <c>Object.getPrototypeOf(HTMLAudioElement.prototype) === HTMLMediaElement.prototype</c> as it
    /// is in a browser. Measured from Chromium's own chains rather than transcribed.
    /// </remarks>
    private static readonly (string Interface, string Base)[] HtmlInterfaceBases =
    [
        ("HTMLAudioElement", "HTMLMediaElement"),
        ("HTMLVideoElement", "HTMLMediaElement"),
        ("HTMLMediaElement", "HTMLElement"),
        ("HTMLUnknownElement", "HTMLElement"),
    ];

    /// <summary>
    /// The HTML tags whose own interface <em>is</em> <c>HTMLElement</c> — known to the parser, but
    /// with no interface of their own.
    /// </summary>
    /// <remarks>
    /// The distinction this encodes is the one that made naming an element's interface look like
    /// guesswork: a tag absent from <see cref="HtmlElementInterfaces"/> is not automatically
    /// <c>HTMLElement</c>, because a browser splits those into <c>HTMLElement</c> for a tag it knows
    /// (<c>section</c>, <c>abbr</c>, <c>nav</c>) and <c>HTMLUnknownElement</c> for one it does not
    /// (<c>foo</c>, <c>blink</c>, and — since they were removed from HTML — <c>applet</c> and
    /// <c>keygen</c>). A name containing a hyphen is a valid custom element name and is
    /// <c>HTMLElement</c> whether or not anything defined it. All three cases are measured.
    /// </remarks>
    private const string PlainHtmlElementTags =
        "abbr acronym address article aside b basefont bdi bdo big center cite code dd dfn dt em " +
        "figcaption figure footer header hgroup i kbd main mark nav nobr noembed noframes noscript " +
        "plaintext rb rp rt rtc ruby s samp search section small strike strong sub summary sup tt u " +
        "var wbr";

    /// <summary>Tag name → its own interface, built once from <see cref="HtmlElementInterfaces"/>.</summary>
    private static readonly Dictionary<string, string> InterfaceByTag = BuildInterfaceByTag();

    private static readonly HashSet<string> PlainHtmlElementTagSet =
        new(PlainHtmlElementTags.Split(' '), StringComparer.Ordinal);

    private static Dictionary<string, string> BuildInterfaceByTag()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, tags) in HtmlElementInterfaces)
        {
            foreach (var tag in tags.Split(' '))
                map[tag] = name;
        }

        return map;
    }

    /// <summary>
    /// The interface an HTML element with <paramref name="tagName"/> implements — the name a browser
    /// answers from <c>constructor.name</c>.
    /// </summary>
    /// <remarks>
    /// The three-way split is the whole reason this could not be guessed: a named interface, plain
    /// <c>HTMLElement</c> for a known tag without one, and <c>HTMLUnknownElement</c> for a tag that is
    /// neither known nor a valid custom element name. Tag names are compared lower-case because an
    /// HTML document's are, and <c>createElement('DIV')</c> answers <c>HTMLDivElement</c>.
    /// </remarks>
    internal static string HtmlInterfaceForTag(string? tagName)
    {
        if (string.IsNullOrEmpty(tagName))
            return "HTMLUnknownElement";

        var tag = AsciiToLower(tagName);
        if (InterfaceByTag.TryGetValue(tag, out var named))
            return named;

        // A hyphen makes it a valid custom element name, which is an HTMLElement even undefined.
        return PlainHtmlElementTagSet.Contains(tag) || tag.Contains('-')
            ? "HTMLElement"
            : "HTMLUnknownElement";
    }

    /// <summary>
    /// Registers the DOM interface-constructor globals a page reaches for by bare name —
    /// <c>Element</c>, <c>HTMLElement</c>, <c>HTMLUnknownElement</c>, <c>Document</c>,
    /// <c>DocumentFragment</c>, <c>CharacterData</c>, <c>Text</c>, <c>Comment</c> and
    /// <c>Attr</c> — and teaches the pre-existing <c>Node</c> global to answer
    /// <c>instanceof</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bridge DOM object is a plain <c>JSObject</c> whose prototype is <c>Object.prototype</c>:
    /// it carries its members directly rather than inheriting them from an interface prototype.
    /// So the ordinary <c>instanceof</c> walk — follow the operand's prototype chain looking for
    /// the constructor's <c>prototype</c> — can never succeed for one, which is why the
    /// long-standing <c>Node</c> global (see <c>RegisterNodeConstructor</c>) reported
    /// <c>document.createElement('div') instanceof Node === false</c>.
    /// </para>
    /// <para>
    /// Each constructor therefore carries an <c>@@hasInstance</c> that answers from the object's
    /// own <c>nodeType</c>/<c>namespaceURI</c>/<c>tagName</c> instead of from a prototype chain.
    /// That is the spec's own extension point (ES §13.10.1 consults <c>@@hasInstance</c> before
    /// the prototype walk), so this is a real answer rather than a shim — and it is installed with
    /// <c>Object.defineProperty</c> because <c>Function.prototype[@@hasInstance]</c> is
    /// non-writable, so a plain assignment would silently do nothing in sloppy mode.
    /// </para>
    /// <para>
    /// Giving DOM objects genuine per-interface prototype chains would subsume this and is the
    /// better long-term shape; it is a far larger change to the object model than making
    /// <c>instanceof HTMLElement</c> — the single most common way a page asks "is this an
    /// element?" — stop throwing <c>ReferenceError</c>.
    /// </para>
    /// </remarks>
    private static void RegisterDomInterfaceConstructors(JSContext context)
    {
        context.Eval(@"
            // Calling one of these directly throws, as it does in a browser: these interfaces are
            // not constructible, and their objects come from document.createElement and friends.
            // Answering with a plain object instead would hand back something that looks like an
            // element to the caller and is not one — worse than the ReferenceError this replaces.
            function Element() { throw new TypeError('Illegal constructor'); }
            function HTMLElement() { throw new TypeError('Illegal constructor'); }
            function HTMLUnknownElement() { throw new TypeError('Illegal constructor'); }
            function Document() { throw new TypeError('Illegal constructor'); }
            // HTMLDocument is the interface an HTML document's own object implements, and it was the
            // one this file never registered: `document.constructor.name` answered 'Object' where a
            // browser answers 'HTMLDocument'.
            function HTMLDocument() { throw new TypeError('Illegal constructor'); }
            function DocumentFragment() { throw new TypeError('Illegal constructor'); }
            function CharacterData() { throw new TypeError('Illegal constructor'); }
            function Text() { throw new TypeError('Illegal constructor'); }
            function Comment() { throw new TypeError('Illegal constructor'); }
            function Attr() { throw new TypeError('Illegal constructor'); }
            function DocumentType() { throw new TypeError('Illegal constructor'); }
            function SVGElement() { throw new TypeError('Illegal constructor'); }
            function CanvasRenderingContext2D() { throw new TypeError('Illegal constructor'); }

            // The object document.startViewTransition() returns has always existed here; the
            // *interface* did not, and a page that probes it paid more than a missing feature
            // costs. css-view-transitions/view-transition-waituntil-animation-manipulation opens
            // with `failIfNot(ViewTransition.prototype.waitUntil, ...)`, so evaluating the
            // *argument* threw ReferenceError before failIfNot was ever entered: the whole inline
            // script aborted, including the onload assignment at the bottom of it, and
            // startViewTransition was never called at all. The failure then reads as a compositing
            // bug and is not one. With the interface present, `ViewTransition.prototype.waitUntil`
            // is a plain undefined, the precondition is *reached*, and the page reports the
            // precondition failure it was written to report.
            //
            // waitUntil is deliberately not defined. It is a proposal, not shipped here, and
            // faking one would let the test past its own guard and into Web Animations calls this
            // engine cannot answer either (there is no Animation constructor and no
            // Element.prototype.animate) — a worse answer than an honest precondition failure.
            function ViewTransition() { throw new TypeError('Illegal constructor'); }

            // ImageData, unlike the interfaces above, *is* constructible — HTML defines
            // new ImageData(w, h) and new ImageData(data, w, h) — so it gets a real body rather
            // than an illegal-constructor throw.
            function ImageData(a, b, c) {
                var data, width, height;
                if (typeof a === 'object' && a !== null) {
                    data = a;
                    width = b >>> 0;
                    height = arguments.length > 2 ? (c >>> 0) : (data.length / 4) / width;
                    if (data.length !== width * height * 4)
                        throw new TypeError(""ImageData: the source data length is not a multiple of the row length."");
                } else {
                    width = a >>> 0;
                    height = b >>> 0;
                    data = new Uint8ClampedArray(width * height * 4);
                }
                if (width === 0 || height === 0)
                    throw new TypeError(""ImageData: the source dimensions are zero."");
                this.width = width;
                this.height = height;
                this.data = data;
            }

            (function () {
                var HTML_NS = 'http://www.w3.org/1999/xhtml';

                // The HTML elements the parser knows. Anything else with no '-' in its name is an
                // HTMLUnknownElement; a name *with* a '-' is an (undefined) custom element, which
                // the spec makes an HTMLElement rather than an unknown one.
                var known = {};
                var names = ('a abbr acronym address applet area article aside audio b base ' +
                    'basefont bdi bdo big blockquote body br button canvas caption center cite ' +
                    'code col colgroup data datalist dd del details dfn dialog dir div dl dt em ' +
                    'embed fieldset figcaption figure font footer form frame frameset h1 h2 h3 ' +
                    'h4 h5 h6 head header hgroup hr html i iframe img input ins kbd keygen label ' +
                    'legend li link listing main map mark marquee menu meta meter nav nobr ' +
                    'noembed noframes noscript object ol optgroup option output p param picture ' +
                    'plaintext pre progress q rb rp rt rtc ruby s samp script search section ' +
                    'select slot small source span strike strong style sub summary sup table ' +
                    'tbody td template textarea tfoot th thead time title tr track tt u ul var ' +
                    'video wbr xmp').split(' ');
                for (var i = 0; i < names.length; i++) known[names[i]] = true;

                function define(ctor, test) {
                    Object.defineProperty(ctor, Symbol.hasInstance, {
                        value: test, writable: false, enumerable: false, configurable: true
                    });
                }

                function isNode(o) {
                    return !!o && typeof o === 'object' && typeof o.nodeType === 'number';
                }

                function isElement(o) {
                    return isNode(o) && o.nodeType === 1;
                }

                function isHtmlElement(o) {
                    if (!isElement(o)) return false;
                    var ns = o.namespaceURI;
                    // A bridge element created outside a namespace-aware path may report no
                    // namespace at all; treat that as HTML rather than as neither.
                    return ns === HTML_NS || ns === null || typeof ns === 'undefined';
                }

                define(Node, isNode);
                define(Element, isElement);
                define(HTMLElement, isHtmlElement);

                // HTMLUnknownElement is a *subtype* of HTMLElement, so an unknown element is an
                // instance of both. html5test's `x instanceof HTMLElement &&
                // !(x instanceof HTMLUnknownElement)` check relies on exactly that split.
                define(HTMLUnknownElement, function (o) {
                    if (!isHtmlElement(o)) return false;
                    var tag = typeof o.tagName === 'string' ? o.tagName.toLowerCase() : '';
                    if (tag === '' || known[tag] === true) return false;
                    return tag.indexOf('-') === -1;
                });

                define(Document, function (o) { return isNode(o) && (o.nodeType === 9 || o.nodeType === 10); });
                define(HTMLDocument, function (o) { return isNode(o) && o.nodeType === 9; });
                define(DocumentFragment, function (o) { return isNode(o) && o.nodeType === 11; });
                define(CharacterData, function (o) {
                    return isNode(o) && (o.nodeType === 3 || o.nodeType === 4 || o.nodeType === 8);
                });
                define(Text, function (o) { return isNode(o) && (o.nodeType === 3 || o.nodeType === 4); });
                define(Comment, function (o) { return isNode(o) && o.nodeType === 8; });
                define(Attr, function (o) { return isNode(o) && o.nodeType === 2; });

                // The canvas types are not nodes, so nodeType cannot discriminate them; they answer
                // from the members that define the interface instead. A 2D context is the only
                // object in the bridge carrying the drawing surface and the pixel readback together.
                define(CanvasRenderingContext2D, function (o) {
                    return !!o && typeof o === 'object'
                        && typeof o.getImageData === 'function'
                        && typeof o.fillRect === 'function'
                        && typeof o.fillStyle === 'string';
                });

                // A view transition is not a node either, and the bridge builds it as a plain
                // object, so it answers from the members the interface is defined by. `types` and
                // `skipTransition` together are what separate it from any other thenable-bearing
                // object a page might hold.
                define(ViewTransition, function (o) {
                    return !!o && typeof o === 'object'
                        && typeof o.skipTransition === 'function'
                        && !!o.ready && !!o.finished && !!o.updateCallbackDone;
                });

                // Accepts an ImageData this constructor produced and one getImageData returned,
                // which are different objects: the readback is built in C# and does not run through
                // the constructor, so a prototype walk would answer false for the commoner of the two.
                define(ImageData, function (o) {
                    return !!o && typeof o === 'object'
                        && typeof o.width === 'number' && typeof o.height === 'number'
                        && !!o.data && typeof o.data === 'object'
                        && typeof o.data.length === 'number'
                        && o.data.length === o.width * o.height * 4;
                });

                // SVGElement is the namespace's counterpart to HTMLElement, and the one interface
                // here that HTML_NS excludes rather than selects.
                define(SVGElement, function (o) {
                    return isElement(o) && o.namespaceURI === 'http://www.w3.org/2000/svg';
                });

                // The inheritance chain these interfaces are defined along, so a wrapper linked to
                // one of them inherits the whole chain — which is what makes the ordinary polyfill
                // idiom work: `Element.prototype.matches = ...` now reaches every element, where
                // before it assigned to an object nothing inherited from. setPrototypeOf rather than
                // Object.create keeps each prototype's identity and its non-enumerable
                // `constructor`. EventTarget is not registered by this file, so the edge to it is
                // taken only if the realm already carries it.
                var edges = [
                    [HTMLElement, Element], [SVGElement, Element], [Element, Node],
                    [HTMLDocument, Document], [Document, Node],
                    [CharacterData, Node], [Text, CharacterData], [Comment, CharacterData],
                    [Attr, Node], [DocumentFragment, Node], [DocumentType, Node]
                ];
                if (typeof EventTarget === 'function') edges.push([Node, EventTarget]);
                for (var e = 0; e < edges.length; e++) {
                    var child = edges[e][0], parent = edges[e][1];
                    if (child && parent && child.prototype && parent.prototype)
                        Object.setPrototypeOf(child.prototype, parent.prototype);
                }
            })();
        ");

        RegisterHtmlElementInterfaces(context);

        // NodeList and HTMLCollection are the exception to everything above: they get real
        // prototypes with real methods, and their instances really are instances of them, rather
        // than an @@hasInstance hook over a foreign object. See DomCollectionBinding — they are
        // track 6 action 1's "establish real interface prototypes and Web IDL collection behavior
        // before adding more compatibility-only constructor globals", so adding them in the shape
        // this file otherwise uses would have been the thing that action rules out.
        // Handed to the custom-elements registration so its constructible HTMLElement can keep
        // this exact prototype object — every element wrapper is linked to it, so replacing it
        // with a fresh one would orphan them all.
        if (context["HTMLElement"] is JSObject htmlElement)
            context["__broilerHTMLElementPrototype"] = htmlElement[(Broiler.JavaScript.Storage.KeyString)"prototype"];

        Dom.Features.DomCollectionBinding.RegisterInterfaces(context);
        // The five NamedNodeMap members that need the owning element are host functions, so they
        // are installed on the interface prototype after it exists.
        Dom.Features.DomCollectionBinding.RegisterNamedNodeMapOperations(context);
    }

    /// <summary>
    /// The per-tag HTML element interfaces from <see cref="HtmlElementInterfaces"/> —
    /// <c>HTMLFormElement</c>, <c>HTMLInputElement</c>, <c>HTMLAnchorElement</c> and the rest —
    /// as globals that answer <c>instanceof</c> from the element's tag name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These sit under the <c>HTMLElement</c> the method above registers, and they are what a page
    /// uses when it has an element in hand and wants to know <em>which</em> element it is. Only the
    /// bare name existing is not enough for that: it has to answer, so each one carries the same
    /// <c>@@hasInstance</c> the interfaces above do, reading <c>tagName</c> rather than walking a
    /// prototype chain a bridge DOM object does not have.
    /// </para>
    /// <para>
    /// Their absence is a whole-page failure rather than a missing feature, because a bare name that
    /// does not exist is a <c>ReferenceError</c> — which aborts the script at that statement, not
    /// merely the test that named it. <c>duckduckgo.com</c> is the case that prompted this: its SSG
    /// bootstrap sets each search form's method behind
    /// <c>form instanceof HTMLFormElement</c>, and the two statements it never reached afterwards
    /// were the <c>--vh</c> custom property and
    /// <c>documentElement.classList.add('partially-hydrated')</c>. The page ships
    /// <c>body { display: none }</c> with <c>html.partially-hydrated body { display: block }</c>, so
    /// losing that one class left the start page rendering as an empty white viewport.
    /// </para>
    /// <para>
    /// The declarations are generated rather than written out because the table is the interface
    /// list: pairing each name with its tags in one place keeps the name and the test it answers with
    /// from drifting apart.
    /// </para>
    /// <para>
    /// Each one is constructible, because a customized built-in extends it — see the comment in the
    /// method. Constructing is delegated to the custom-element registry through a hook this pass
    /// leaves unbound; until that hook arrives, and for any call with no <c>new.target</c>, they
    /// throw the <c>Illegal constructor</c> they always did.
    /// </para>
    /// </remarks>
    private static void RegisterHtmlElementInterfaces(JSContext context)
    {
        var script = new StringBuilder();

        // Each interface is a real constructor rather than an unconditional throw, because a
        // customized built-in extends one of them: `class Fancy extends HTMLButtonElement` reaches
        // HTMLButtonElement through super(), and the element it must produce is a <button> carrying
        // the class. The construction itself belongs to the custom-element registry, which registers
        // later — so the hook is a closure variable bound by the one-shot setter below, and the
        // interfaces throw the same "Illegal constructor" they always did until it is bound and
        // whenever there is no new.target. The name is passed along because HTML §4.13.3 checks it:
        // an autonomous definition may only be reached through HTMLElement, and a customized one only
        // through the interface of the tag it extends.
        //
        // HTMLMediaElement is in the list rather than declared beside HTMLElement because it belongs
        // to this table's world: it is abstract, so it owns no tag and appears only as a base.
        script.Append("(function () {\n");
        script.Append("    var construct = null;\n");
        script.Append("""
                globalThis.__broilerBindInterfaceConstructor = function (hook) {
                    construct = hook;
                    delete globalThis.__broilerBindInterfaceConstructor;
                };

                function define(name) {
                    var ctor = function () {
                        var target = new.target;
                        if (!target || !construct) throw new TypeError('Illegal constructor');
                        var element = construct(target, name);
                        // A string is the registry's refusal with the message it wants reported;
                        // null is the generic case. Throwing here rather than in the host is what
                        // makes either a real TypeError with a name.
                        if (typeof element === 'string') throw new TypeError(element);
                        if (!element) throw new TypeError('Illegal constructor');
                        Object.setPrototypeOf(element, target.prototype);
                        return element;
                    };
                    Object.defineProperty(ctor, 'name', { value: name, writable: false, enumerable: false, configurable: true });
                    globalThis[name] = ctor;
                    Object.defineProperty(ctor.prototype, 'constructor', {
                        value: ctor, writable: true, enumerable: false, configurable: true
                    });
                }

            """);
        script.Append("    var interfaceNames = ['HTMLMediaElement'");
        foreach (var (name, _) in HtmlElementInterfaces)
            script.Append(", '").Append(name).Append('\'');
        script.Append("];\n");
        script.Append("    for (var n = 0; n < interfaceNames.length; n++) define(interfaceNames[n]);\n");
        script.Append("})();\n");

        script.Append("(function () {\n");
        script.Append("    var ownTags = {\n");
        foreach (var (name, tags) in HtmlElementInterfaces)
            script.Append("        ").Append(name).Append(": '").Append(tags).Append("',\n");
        script.Append("    };\n");
        script.Append("    var bases = {\n");
        foreach (var (name, baseName) in HtmlInterfaceBases)
            script.Append("        ").Append(name).Append(": '").Append(baseName).Append("',\n");
        script.Append("    };\n");
        script.Append("""
                function ctorOf(name) {
                    var c = typeof globalThis !== 'undefined' ? globalThis[name] : undefined;
                    return typeof c === 'function' ? c : null;
                }

                function baseOf(name) {
                    // Everything in the table derives from HTMLElement unless an edge says otherwise.
                    return Object.prototype.hasOwnProperty.call(bases, name) ? bases[name] : 'HTMLElement';
                }

                // Each interface answers instanceof for its own tags *and* every descendant's, which
                // is what keeps an abstract base like HTMLMediaElement answering for audio and video
                // now that neither names it directly.
                var effective = {};
                for (var key in ownTags) {
                    if (!Object.prototype.hasOwnProperty.call(ownTags, key)) continue;
                    var tags = ownTags[key].split(' ');
                    for (var walk = key; walk && walk !== 'HTMLElement'; walk = baseOf(walk)) {
                        if (!effective[walk]) effective[walk] = {};
                        for (var i = 0; i < tags.length; i++) effective[walk][tags[i]] = true;
                    }
                }

                for (var name in effective) {
                    if (!Object.prototype.hasOwnProperty.call(effective, name)) continue;
                    var ctor = ctorOf(name);
                    if (!ctor) continue;
                    // The set and the constructor are captured per iteration through the factory
                    // rather than through the loop body, whose `var` bindings are one shared pair by
                    // the time a test runs.
                    //
                    // The `this !== owner` arm is what keeps a *subclass* honest. A class statically
                    // inherits @@hasInstance from the constructor it extends, so
                    // `class Fancy extends HTMLButtonElement` would otherwise answer the tag test —
                    // and report every <button> on the page as a Fancy, upgraded or not. A subclass
                    // has a genuine prototype chain to walk (its instances are elements whose
                    // prototype was re-pointed at it), so it gets the ordinary instanceof answer.
                    Object.defineProperty(ctor, Symbol.hasInstance, {
                        value: (function (tagSet, owner) {
                            return function (o) {
                                if (this !== owner) {
                                    if (!o || (typeof o !== 'object' && typeof o !== 'function')) return false;
                                    var target = this.prototype;
                                    for (var p = Object.getPrototypeOf(o); p; p = Object.getPrototypeOf(p)) {
                                        if (p === target) return true;
                                    }
                                    return false;
                                }
                                if (!o || typeof o !== 'object' || o.nodeType !== 1) return false;
                                var ns = o.namespaceURI;
                                if (ns !== 'http://www.w3.org/1999/xhtml' && ns !== null && typeof ns !== 'undefined')
                                    return false;
                                var tag = typeof o.tagName === 'string' ? o.tagName.toLowerCase() : '';
                                return tagSet[tag] === true;
                            };
                        })(effective[name], ctor),
                        writable: false, enumerable: false, configurable: true
                    });
                }

                // The prototype chain the interfaces inherit along. setPrototypeOf rather than a
                // fresh Object.create: it keeps each prototype object's identity and its
                // non-enumerable `constructor`, so a wrapper linked to it still reports the right
                // constructor.name and `for...in` over an element gains nothing.
                var chained = ['HTMLMediaElement', 'HTMLUnknownElement'];
                for (var t in ownTags) {
                    if (Object.prototype.hasOwnProperty.call(ownTags, t)) chained.push(t);
                }
                for (var j = 0; j < chained.length; j++) {
                    var child = ctorOf(chained[j]);
                    var parent = ctorOf(baseOf(chained[j]));
                    if (child && parent && child.prototype && parent.prototype)
                        Object.setPrototypeOf(child.prototype, parent.prototype);
                }
            })();
            """);

        context.Eval(script.ToString());
    }
}
