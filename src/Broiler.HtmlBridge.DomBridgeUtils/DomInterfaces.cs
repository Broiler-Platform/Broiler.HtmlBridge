using System.Runtime.ExceptionServices;
using System.Text;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
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
    /// The per-tag HTML element interfaces from <see cref="HtmlElementInterfaces"/> —
    /// <c>HTMLFormElement</c>, <c>HTMLInputElement</c>, <c>HTMLAnchorElement</c> and the rest —
    /// as globals that answer <c>instanceof</c> from the element's tag name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These sit under the <c>HTMLElement</c> the method above registers, and they are what a page
    /// uses when it has an element in hand and wants to know <em>which</em> element it is. Only the
    /// bare name existing is not enough for that: it has to answer, so each one carries the same
    /// <c>@@hasInstance</c> the interfaces above do, reading <c>tagName</c> — the test they were
    /// written with while a bridge DOM object had no prototype chain to walk. An element wrapper is
    /// linked to its per-tag prototype now (<c>DomBridge.ApplyInterfacePrototype</c>), chosen from this
    /// same table and chained along the same <see cref="HtmlInterfaceBases"/> edges, so for an element
    /// whose chain still runs through that prototype the tag test and a walk give one answer. The tag
    /// test is what <c>instanceof</c> still asks, because a constructor carrying the hook is not
    /// walked; only for a subclass does the hook walk the chain itself — see the
    /// <c>this !== owner</c> arm in the method.
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
    internal static void RegisterHtmlElementInterfaces(IJsRealm realm)
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

        realm.EvaluateHostScript(script.ToString(), "interfaces:html-elements");
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or the empty string when nothing was passed.
    /// </summary>
    /// <remarks>
    /// The selector and collection members read their argument here rather than inside
    /// <c>Dom.Features.SelectorsBinding</c>, because the module's entry points take the string
    /// their caller has already produced and this file is their only caller; the sub-document and
    /// <c>DocumentFragment</c> forms read their own (this said they shared them). It is the realm's
    /// <c>ToString</c> and not the handle's rendering, the same read the engine frame performed.
    /// </remarks>
    internal static string StringArgument(in JsCall call) =>
        call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;

    /// <summary>
    /// <c>tagName</c>'s value: upper-cased for an HTML element, verbatim otherwise, which is the rule
    /// the wrapper applied once when it minted the string.
    /// </summary>
    internal static string TagNameForScript(DomElement element) =>
        string.IsNullOrEmpty(element.NamespaceUri) ||
        string.Equals(element.NamespaceUri, "http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase)
            ? element.TagName.ToUpperInvariant()
            : element.TagName;
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// HTMLLinkElement's plain reflected DOMString IDL attributes (HTML §4.2.4), as
    /// IDL name → content-attribute name. Deliberately partial: <c>type</c> and <c>name</c> are
    /// already present because the form-control reflectors install on every element; <c>href</c> is
    /// URL-typed and wired separately; and <c>crossOrigin</c> (nullable + enumerated) and
    /// <c>disabled</c> (which toggles the sheet rather than the attribute — see
    /// <c>DomBridge/StyleSheets.cs</c>) are left out rather than approximated as plain strings.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] LinkReflectedAttributes =
    [
        ("rel", "rel"),
        ("as", "as"),
        ("media", "media"),
        ("hreflang", "hreflang"),
        ("integrity", "integrity"),
        ("referrerPolicy", "referrerpolicy"),
    ];

    /// <summary>
    /// HTMLScriptElement's plain reflected DOMString IDL attributes (HTML §4.12.1), as IDL name →
    /// content-attribute name. <c>src</c> is URL-typed and wired separately, and the boolean ones are
    /// in <see cref="ScriptReflectedBooleans"/>.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] ScriptReflectedAttributes =
    [
        ("type", "type"),
        ("charset", "charset"),
        ("integrity", "integrity"),
        ("crossOrigin", "crossorigin"),
        ("referrerPolicy", "referrerpolicy"),
        ("fetchPriority", "fetchpriority"),
        // Reflected plainly rather than with the spec's nonce-hiding (the content attribute is
        // cleared once the element is inserted, the IDL value kept): what matters here is that a
        // page setting s.nonce reaches the CSP check that authorises the script it is injecting.
        ("nonce", "nonce"),
    ];

    /// <summary>
    /// HTMLScriptElement's boolean reflected IDL attributes: present/absent, never the string
    /// "false". A loader that sets <c>s.async = false</c> to keep injected scripts in order relies on
    /// the removal half.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] ScriptReflectedBooleans =
    [
        ("async", "async"),
        ("defer", "defer"),
        ("noModule", "nomodule"),
    ];

    /// <summary>
    /// HTMLImageElement's plain reflected DOMString IDL attributes (HTML §4.8.3), as IDL name →
    /// content-attribute name. <c>src</c> is URL-typed and wired separately, <c>isMap</c> is boolean,
    /// and <c>width</c>/<c>height</c> report the used dimension rather than the raw attribute.
    /// <c>crossOrigin</c>, <c>decoding</c>, <c>loading</c>, <c>fetchPriority</c> and
    /// <c>referrerPolicy</c> are enumerated in the IDL and so read back a limited value in a browser;
    /// they reflect plainly here, which is what a page setting them expects and what the content
    /// attribute they write carries.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] ImageReflectedAttributes =
    [
        ("alt", "alt"),
        ("srcset", "srcset"),
        ("sizes", "sizes"),
        ("useMap", "usemap"),
        ("crossOrigin", "crossorigin"),
        ("referrerPolicy", "referrerpolicy"),
        ("decoding", "decoding"),
        ("loading", "loading"),
        ("fetchPriority", "fetchpriority"),
    ];

    /// <summary>
    /// A plainly reflected content attribute as its IDL getter answers it: the attribute's value, or
    /// the empty string when it is absent.
    /// </summary>
    /// <remarks>
    /// The shape these per-tag getters were each written out in, once, now that the realm mints them —
    /// <see cref="TryGetAttribute"/> is the bridge's engine-neutral scan and the answer was always the
    /// same two cases. Never JavaScript <c>null</c>: a missing reflected DOMString is <c>""</c>, which
    /// is why the empty string is spelled out rather than left to <see cref="JsValue.String(string?)"/>.
    /// </remarks>
    internal static JsValue ReflectedAttribute(Broiler.Dom.DomElement element, string attribute) =>
        JsValue.String(TryGetAttribute(element, attribute, out var value) ? value : string.Empty);
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Hands the call back to the function the engine installed, for a receiver this bridge does not
    /// own — <c>new EventTarget()</c>, an <c>AbortSignal</c>, anything else engine-side. Its own
    /// receiver check is what still rejects a receiver that is neither.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receiver and every supplied argument go through unchanged: an argument the page did not
    /// pass is not invented, so the engine's own arity checks see the call the page actually made.
    /// </para>
    /// <para>
    /// <b>The rethrow is not tidiness.</b> A realm's <c>Invoke</c> reports what the callee threw as a
    /// JSEAL exception carrying the thrown value, and JSEAL has no "throw this value again" operation
    /// — only "throw a new error of this kind". Letting that wrapper escape into the engine would
    /// hand the page a freshly synthesised <c>Error</c> built from a CLR exception in place of the
    /// engine's own, so <c>EventTarget.prototype.addEventListener.call({}, …)</c> would stop being
    /// catchable as a <c>TypeError</c>. Rethrowing the inner exception with its stack intact keeps
    /// the object the page catches the object the engine threw.
    /// </para>
    /// </remarks>
    internal static JsValue InvokeEngineEventTargetMethod(JsValue engineMethod, string name, in JsCall call)
    {
        if (!engineMethod.IsFunction)
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                $"Failed to execute '{name}' on 'EventTarget': Illegal invocation");

        try
        {
            return call.Realm.Invoke(engineMethod, call.This, call.Arguments);
        }
        catch (JsEngineException wrapped) when (wrapped.InnerException is { } thrownByEngine)
        {
            ExceptionDispatchInfo.Throw(thrownByEngine);
            throw; // Unreachable: ExceptionDispatchInfo.Throw never returns.
        }
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// The interface a node implements, or <see langword="null"/> for a kind this does not reach.
    /// </summary>
    /// <remarks>
    /// The element arm is a tag lookup rather than a type test, because that is what an element's
    /// interface is; <see cref="HtmlInterfaceForTag"/> owns the rule. A non-HTML element — an SVG one
    /// — is deliberately left at <c>SVGElement</c> rather than given a per-tag name: a browser does
    /// have <c>SVGRectElement</c> and the rest, but this engine registers no SVG element interfaces
    /// to point at, and inventing the globals to satisfy a name is what the collection work already
    /// ruled out.
    /// <para>
    /// The order matters: <see cref="DomDocument"/> is checked before the element arm because a
    /// document is not an element, and <c>HTMLDocument</c> is its interface.
    /// </para>
    /// </remarks>
    internal static string? InterfaceNameFor(DomNode node) => node switch
    {
        DomDocumentType => "DocumentType",
        DomDocumentFragment => "DocumentFragment",
        DomComment => "Comment",
        DomText => "Text",
        DomDocument => "HTMLDocument",
        DomElement element => IsHtmlNamespace(element) ? HtmlInterfaceForTag(element.TagName) : "SVGElement",
        _ => null,
    };

    /// <summary>Whether the element is in the HTML namespace — including the no-namespace case, which
    /// a bridge element created outside a namespace-aware path reports and which the
    /// <c>instanceof</c> hooks already treat as HTML.</summary>
    internal static bool IsHtmlNamespace(DomElement element) =>
        element.NamespaceUri is null or "" or "http://www.w3.org/1999/xhtml";
}

public static partial class DomBridgeUtils
{
    private const double DefaultBodyMarginPixels = 8;
    internal const int MaxScrollContinuationDepth = 16;
}

public static partial class DomBridgeUtils
{
    // Phase 4 item 1 (P4.4a): docRoot may be a legacy #subdoc-root element OR a canonical
    // DomDocument browsing-context root; ChildElements works over both. Returns the documentElement,
    // or — for an element root with none — the root itself (the prior `?? docRoot` fallback). A
    // canonical DomDocument with no documentElement yields null (per DOM; e.g. createDocument with an
    // empty qualifiedName), so callers must null-check.
    internal static DomElement? GetDocumentElement(DomNode docRoot) => docRoot switch
    {
        DomDocument doc => doc.DocumentElement,
        _ => docRoot.ChildElements.FirstOrDefault(c => !c.TagName.StartsWith('#')) ?? docRoot as DomElement
    };
}
