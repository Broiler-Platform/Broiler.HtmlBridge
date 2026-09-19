using Broiler.JSeal;
using Broiler.JavaScript.Engine;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The static helpers of <see cref="DomBridge"/> that cannot live in Broiler.HtmlBridge.DomBridgeUtils with
/// <see cref="DomBridgeUtils"/>: they take a bridge, reach a feature binding, name the JavaScript engine, or use a
/// Broiler.Layout internal that the package makes visible to this assembly only.
/// </summary>
internal static class DomBridgeHostUtils
{
    /// <summary>
    /// CSS Transforms 1 §8: the transform origin, as an offset from the box's own top-left corner.
    /// </summary>
    /// <remarks>
    /// Defers to <see cref="Layout.IR.CssTransformOrigin"/>, the grammar shared with the SVG
    /// renderer and the paint walker. Reading it here on its own got three things wrong that only
    /// the shared reading has ever handled: a lone <c>top</c> or <c>bottom</c> names the
    /// <em>vertical</em> axis and centres the other, so taking the first component as x put it on
    /// the wrong one; the keyword pair may be written <c>top left</c>, which has to be swapped back;
    /// and an invalid declaration such as <c>top 100%</c> — a length-percentage may not follow a
    /// vertical keyword — is dropped whole rather than half-read.
    /// </remarks>
    internal static (double X, double Y) ParseTransformOrigin(string? origin, double width, double height)
    {
        var point = Layout.IR.CssTransformOrigin.Resolve(
            origin,
            new System.Drawing.RectangleF(0, 0, (float)width, (float)height),
            initialIsBoxCorner: false);

        return (point.X, point.Y);
    }

    /// <summary>
    /// Wraps the context the host handed over as a JSEAL realm, by asking the registered provider
    /// whether it recognises it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every provider is asked, not just the default one, because the object came from whichever
    /// engine the host chose to build it with and that need not be the engine a page load would
    /// otherwise select — a run with <c>BROILER_JS_ENGINE</c> set is exactly that case. A provider
    /// that does not recognise the object says so; see <see cref="IJsRealmAdoption"/>.
    /// </para>
    /// <para>
    /// A failure here is thrown rather than tolerated. A bridge with no realm would build its
    /// bindings' objects nowhere and register a document missing every global they install — a page
    /// loading with no <c>console</c> and no error. (This said "whichever globals had already moved".)
    /// The diagnosis is short and worth stating in the message: nothing linked an engine provider.
    /// </para>
    /// <para>
    /// It is <see langword="internal"/> because <c>ScriptEngine</c>'s two document-free entry points,
    /// <c>Execute(scripts)</c> and <c>ExecuteDetailed(scripts)</c>, adopt the context they build through it
    /// too. So there is one loop and one place a context the host built is matched to a provider, and a
    /// missing provider fails here, with this message, before any of the caller's scripts runs on either
    /// kind of path.
    /// </para>
    /// </remarks>
    /// <param name="context">
    /// The context the host built and still owns. The realm wraps it and does not dispose it.
    /// </param>
    /// <param name="options">
    /// What scripts in the realm are allowed to do, mapped by the caller from the policy that governs them
    /// (<see cref="RealmOptionsFor"/>): the bridge's <c>Csp</c> on a document path, <c>ScriptEngine.Csp</c>
    /// on a document-free one. An adopted realm is bound by this exactly as a created one is; it used to be
    /// bound by nothing.
    /// </param>
    internal static IJsRealm AdoptRealm(JSContext context, JsRealmOptions options)
    {
        foreach (var provider in JsEngineRegistry.All)
        {
            if (provider is IJsRealmAdoption adoption &&
                adoption.TryAdopt(context, options, out var realm) &&
                realm is not null)
            {
                return realm;
            }
        }

        throw new InvalidOperationException(
            "No registered JavaScript engine provider recognised the script context the host supplied. " +
            $"{JsEngineRegistry.All.Count} provider(s) are registered. A host must reference an engine " +
            "provider assembly — Broiler.JSeal.BroilerJs for Broiler.JS — and that assembly " +
            "registers itself when it is loaded.");
    }

    /// <summary>
    /// Whether the element-<c>zoom</c> serialization bake (<see cref="DomBridge.ApplyZoomSerializationStyles"/>)
    /// runs. The bake and the engine used-value model (<c>Broiler.Layout.Engine.NativeZoom</c>,
    /// increments 1–5) are mutually exclusive: running both double-counts <c>zoom</c>, running neither
    /// drops it. The engine flag is <c>[ThreadStatic]</c> and set on the layout thread; the bake mutates
    /// the DOM thread-independently. So the bake is skipped exactly when the engine model is enabled on
    /// this thread — the increment-6 cutover switch. Default (flag off) the bake runs, byte-identical to
    /// before this gate.
    /// </summary>
    internal static bool ZoomBakeActive => !Broiler.Layout.Engine.NativeZoom.Enabled;

    /// <summary>
    /// Searches descendants of an element using a CSS selector.
    /// </summary>
    // Phase 4 item 1: root widened DomElement -> DomNode so querySelector/querySelectorAll work over a
    // canonical DomDocumentFragment. A fragment cannot itself match a selector, so the :scope-root
    // self-match is guarded to element roots and the descendant scope is null for a fragment root.
    internal static JsValue FindInDescendants(DomNode root, string selector, bool all, DomBridge bridge)
    {
        // DOM §4.2.6: an unparsable selector is a SyntaxError before the search runs. This is the
        // shared descendant search, so it covers the DocumentFragment forms as well as the Element
        // ones — a browser throws from `fragment.querySelector('[')` exactly as it does from the
        // document's.
        bridge.ValidateSelector(selector);

        var results = new List<JsValue>();

        // A pseudo-element selects no element, so the search is over before it starts — see
        // DomApiSyntax.CarriesPseudoElement. The empty list still has to be the right *kind* of
        // empty: a NodeList for querySelectorAll and null for querySelector.
        if (Dom.Features.DomApiSyntax.CarriesPseudoElement(selector))
        {
            return all
                ? Dom.Features.DomCollectionBinding.NodeList(bridge.Realm, () => results)
                : JsValue.Null;
        }

        var scope = root as DomElement;
        if (scope is not null && selector.Contains(":scope") &&
            bridge.MatchesSelector(scope, selector, scope))
        {
            results.Add(bridge.WrapNode(scope));
            if (!all)
                return results[0];
        }

        SearchDescendants(root, selector, results, bridge, all, scope);
        // querySelectorAll is a STATIC NodeList (DOM §4.2.6) — the one collection the specification
        // defines as a snapshot rather than live, so the list is handed the results it already has
        // rather than the search that produced them.
        if (all) return Dom.Features.DomCollectionBinding.NodeList(bridge.Realm, () => results);
        return results.Count > 0 ? results[0] : JsValue.Null;
    }

    private static void SearchDescendants(DomNode parent, string selector, List<JsValue> results, DomBridge bridge, bool all, DomElement? scope)
    {
        foreach (var child in ChildElements(parent))
        {
            if (!IsText(child) && bridge.MatchesSelector(child, selector, scope))
            {
                results.Add(bridge.WrapNode(child));
                if (!all) return;
            }
            SearchDescendants(child, selector, results, bridge, all, scope);
            if (!all && results.Count > 0) return;
        }
    }

    /// <summary>
    /// Collects descendant elements matching a tag name in tree order (depth-first).
    /// </summary>
    internal static void CollectDescendantsByTag(DomElement root, string tagName, List<JsValue> results, DomBridge bridge)
    {
        // Phase 4 item 4/5: reuse canonical Descendants() (public, document-order, level-snapshotted —
        // the bridge's own WPT #1143 defensive idiom promoted to canonical, operating on the real child
        // list so it also avoids the LegacyChildList projection overflow) instead of a hand-rolled
        // depth-first ChildElements recursion. Same element set + pre-order; mutation-safe where the old
        // live ChildElements iteration was not.
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (tagName == "*" || string.Equals(element.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                results.Add(bridge.WrapNode(element));
        }
    }

    /// <summary>
    /// Descendants of <paramref name="root"/> carrying every class in <paramref name="classNames"/>,
    /// in document order — the element half of <c>getElementsByClassName</c>.
    /// </summary>
    /// <remarks>
    /// The argument is a set of class names, not a selector, so it cannot be routed through the
    /// selector engine as <c>"." + classNames</c> — a class is a literal here and would need escaping
    /// to survive as a selector. The set rule itself lives in
    /// <see cref="Dom.Features.ClassNameSet"/>, shared with the document half of the same method.
    /// </remarks>
    internal static void CollectDescendantsByClass(DomElement root, string classNames, List<JsValue> results, DomBridge bridge)
    {
        var wanted = Dom.Features.ClassNameSet.Parse(classNames);
        if (wanted.Length == 0)
            return;

        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (Dom.Features.ClassNameSet.Matches(element, wanted))
                results.Add(bridge.WrapNode(element));
        }
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
    /// A bridge DOM object was a plain object whose prototype was <c>Object.prototype</c>, carrying
    /// its members directly, so the ordinary <c>instanceof</c> walk — follow the operand's prototype
    /// chain looking for the constructor's <c>prototype</c> — could never succeed for one, which is
    /// why the long-standing <c>Node</c> global (see <c>RegisterNodeConstructor</c>) reported
    /// <c>document.createElement('div') instanceof Node === false</c>. It is not that shape now: the
    /// script below chains these prototypes, and <see cref="DomBridge.ApplyInterfacePrototype"/> and
    /// <see cref="DomBridge.LinkToInterface"/> link each wrapper to its interface's.
    /// </para>
    /// <para>
    /// Each constructor was therefore given an <c>@@hasInstance</c> that answers from the object's
    /// own <c>nodeType</c>/<c>namespaceURI</c>/<c>tagName</c> instead of from a prototype chain.
    /// That is the spec's own extension point (ES §13.10.1 consults <c>@@hasInstance</c> before
    /// the prototype walk), so this is a real answer rather than a shim — and it is installed with
    /// <c>Object.defineProperty</c> because <c>Function.prototype[@@hasInstance]</c> is
    /// non-writable, so a plain assignment would silently do nothing in sloppy mode.
    /// </para>
    /// <para>
    /// The hooks are still installed, and still decide: a constructor carrying <c>@@hasInstance</c> is
    /// asked instead of walked, so <c>node instanceof Text</c> reads <c>nodeType</c> however the wrapper
    /// is linked. What they give that the link does not is an answer without one: for an object whose
    /// link was tried before its constructor existed (<see cref="DomBridge.LinkToInterface"/> is a no-op then, and
    /// the registration re-link revisits only the document and node wrappers), and for the
    /// <c>ImageData</c> readback, the view transition and the 2D context, plain objects no link reaches.
    /// <c>HTMLElement</c> is the exception: <c>RegisterCustomElements</c> replaces that global with a
    /// constructible one that keeps the prototype but not the hook, so <c>instanceof HTMLElement</c>
    /// walks the chain. (This said per-interface chains would subsume the hooks as a larger change.)
    /// </para>
    /// </remarks>
    internal static void RegisterDomInterfaceConstructors(IJsRealm realm)
    {
        realm.EvaluateHostScript(@"
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
        ", "interfaces:dom-constructors");

        RegisterHtmlElementInterfaces(realm);

        // The five collection interfaces DomCollectionBinding defines — NodeList, HTMLCollection,
        // StyleSheetList, NamedNodeMap and FileList — are the exception to everything above: they get
        // real prototypes with real methods, and their instances really are instances of them, rather
        // than an @@hasInstance hook over a foreign object. (This named NodeList and HTMLCollection
        // alone.) They are track 6 action 1's "establish real interface prototypes and Web IDL
        // collection behavior before adding more compatibility-only constructor globals", so adding
        // them in the shape this file otherwise uses would have been the thing that action rules out.
        // Handed to the custom-elements registration so its constructible HTMLElement can keep
        // this exact prototype object — every element wrapper is linked to it, so replacing it
        // with a fresh one would orphan them all.
        var htmlElement = realm.GetProperty(realm.Global, "HTMLElement");
        if (htmlElement.IsObject)
        {
            realm.SetProperty(
                realm.Global,
                "__broilerHTMLElementPrototype",
                realm.GetProperty(htmlElement, "prototype"));
        }

        Dom.Features.DomCollectionBinding.RegisterInterfaces(realm);
        // The six NamedNodeMap members that need the owning element are host functions, so they
        // are installed on the interface prototype after it exists.
        Dom.Features.DomCollectionBinding.RegisterNamedNodeMapOperations(realm);
    }

    /// <summary>
    /// Validates a <c>setAttribute</c>/<c>toggleAttribute</c> attribute name (DOM §4.9.1), throwing
    /// <c>InvalidCharacterError</c> when it does not match the XML <c>Name</c> production.
    /// </summary>
    /// <remarks>
    /// A separate rule from <see cref="ValidateElementName"/> rather than a reuse of it, because
    /// <c>Name</c> allows colons and the element-name pattern deliberately does not — see
    /// <see cref="Dom.Features.DomApiSyntax.IsValidAttributeName"/> for why that distinction is the
    /// load-bearing one. Every call site is a scripted DOM entry point: the canonical
    /// <c>DomElement.SetAttribute</c> stays permissive because the HTML parser goes through it.
    /// </remarks>
    internal static void ValidateAttributeName(string name, IJsRealm? realm)
    {
        if (realm is not null && !Dom.Features.DomApiSyntax.IsValidAttributeName(name))
        {
            ThrowDOMException(
                realm,
                $"Failed to execute 'setAttribute' on 'Element': '{name}' is not a valid attribute name.",
                "InvalidCharacterError");
        }
    }

    /// <summary>
    /// The property-indexed keyframe form: each animatable property maps to a list of values (or a
    /// single value), distributed evenly over the effect. Each property is turned into its own
    /// keyframes, which is exactly how <c>ResolveKeyframeProperties</c> reads them — it brackets
    /// each property against only the keyframes that define it, so properties with different list
    /// lengths need no common offset grid.
    /// </summary>
    internal static List<KeyframeEntry> ParsePropertyIndexedKeyframes(IJsRealm realm, JsValue keyframes)
    {
        var byPosition = new SortedDictionary<float, Dictionary<string, string>>();

        foreach (var (keyframeKey, cssName) in AnimatableProperties)
        {
            var value = realm.GetProperty(keyframes, keyframeKey);
            if (value.IsMissing || value.IsNullish)
                continue;

            var values = value.IsArray
                ? Dom.Features.WorkerTransfer.ArrayElements(realm, value).ToList()
                : [value];

            for (var i = 0; i < values.Count; i++)
            {
                if (values[i].IsMissing || values[i].IsNullish)
                    continue;
                var text = realm.ToJsString(values[i]);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // A list of one is a single keyframe at offset 1 (the spec's implicit-from case);
                // otherwise the values spread evenly from 0 to 1.
                var position = values.Count <= 1 ? 1f : (float)i / (values.Count - 1);
                if (!byPosition.TryGetValue(position, out var properties))
                {
                    properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    byPosition[position] = properties;
                }
                properties[cssName] = text;
            }
        }

        return byPosition.Select(entry => new KeyframeEntry(entry.Key, entry.Value)).ToList();
    }
}
