using Broiler.HtmlBridge.Jseal;
// The two engine namespaces left: the property-attribute enum the unmigrated installations below
// pass, and the wrapper type this file is handed.
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    /// <summary>
    /// HTMLLinkElement's plain reflected DOMString IDL attributes (HTML §4.2.4), as
    /// IDL name → content-attribute name. Deliberately partial: <c>type</c> and <c>name</c> are
    /// already present because the form-control reflectors install on every element; <c>href</c> is
    /// URL-typed and wired separately; and <c>crossOrigin</c> (nullable + enumerated) and
    /// <c>disabled</c> (which toggles the sheet rather than the attribute — see
    /// <c>DomBridge/StyleSheets.cs</c>) are left out rather than approximated as plain strings.
    /// </summary>
    private static readonly (string IdlName, string AttributeName)[] LinkReflectedAttributes =
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
    private static readonly (string IdlName, string AttributeName)[] ScriptReflectedAttributes =
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
    private static readonly (string IdlName, string AttributeName)[] ScriptReflectedBooleans =
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
    private static readonly (string IdlName, string AttributeName)[] ImageReflectedAttributes =
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

    private void AddElementSpecificMembers(JSObject obj, Broiler.Dom.DomElement element)
    {
        // -- Phase 5: HTML DOM Interfaces --

        var tag = element.TagName.ToLowerInvariant();

        // A JSEAL handle over the same wrapper, for the members whose module has migrated. The seam is
        // a cast rather than a conversion (Runtime/JsInterop.cs), so a member installed through the
        // realm lands on this object in the position it is installed in — which is what keeps
        // Object.getOwnPropertyNames(el) reporting the order it always has, with the migrated and
        // unmigrated members interleaved exactly as they are written below.
        var handle = Dom.Runtime.JsInterop.FromEngineObject(obj);

        // HTMLTableElement / HTMLTableSectionElement / HTMLTableRowElement interfaces (Phase 3 P3.5:
        // extracted into the co-located TableBinding feature module).
        _tables.Install(obj, element, tag);

        // HTMLFormElement interface (Phase 3 P3.9: extracted into the co-located FormBinding module).
        _forms.Install(obj, element, tag);

        // HTMLDetailsElement.open, HTMLDialogElement (showModal/show/close/open/returnValue) and the
        // popover API (Phase 3 P3.7: extracted into the co-located DialogBinding feature module).
        _dialogs.Install(obj, element, tag, HasAttr(element, "popover"));

        // HTMLSelectElement / HTMLOptionElement (Phase 3 P3.8: extracted into the co-located
        // SelectBinding feature module).
        _select.Install(obj, element, tag);

        // HTMLMediaElement.canPlayType() on <video>/<audio> — the capability question a media player
        // asks before it commits to a source (Phase 3 co-located MediaCapabilityBinding module,
        // shared with the MediaSource.isTypeSupported that answers it statically).
        Dom.Features.MediaCapabilityBinding.Install(Realm, handle, tag);

        // Form association (HTML §4.10.2, §4.10.4): a control's `form` owner and `labels`, and a
        // label's `control`. Installed per tag rather than on every wrapper, because their absence
        // on a non-form element is observable — see FormAssociationBinding.
        Dom.Features.FormAssociationBinding.Install(this, obj, element, tag);

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
            // The getter's module has migrated and the setter's has not, so the pair is mixed: the
            // realm mints the half that is ready and the seam unwraps it for the engine-typed
            // installation the other half still needs.
            obj.FastAddProperty("data",
                Dom.Runtime.JsInterop.ToEngineObject(Realm.NewMethod("get data",
                    (in _) => Dom.Features.ElementReflectionBinding.GetData(this, element))),
                new DomFunction((in a) => Dom.Features.ObjectElementBinding.SetData(this, element, in a), "set data"),
                JSPropertyAttributes.EnumerableConfigurableProperty);

            // type property (MIME type of the resource)
            Realm.DefineAccessor(handle, "type",
                (in _) => ReflectedAttribute(element, "type"),
                (in call) => Dom.Features.ElementReflectionBinding.SetType(element, in call));

            // contentDocument for <object> element (with same-origin check)
            // Returns null when the resource fails to load (HTTP 404, file not found, etc.)
            // which signals that the fallback content (child nodes) should be visible.
            obj.FastAddProperty("contentDocument",
                new DomFunction((in _) => Dom.Features.ObjectElementBinding.GetContentDocument(this, element, in _), "get contentDocument"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);

            // getSVGDocument() for <object> element
            obj.FastAddValue("getSVGDocument",
                new DomFunction((in _) => Dom.Features.ObjectElementBinding.GetSvgDocument(this, element, in _), "getSVGDocument", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);
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
                (in _) => JsValue.String(((Dom.Features.IElementContentHost)this).NodeTextValue(element)),
                (in call) =>
                {
                    // ToJsString, not the handle's own rendering: `s.text = templateObject` runs the
                    // object's toString, which is what the engine was doing here before.
                    SetElementTextContent(element, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
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
                // Mixed, like <object>.data: the used-dimension getter's module has not migrated and
                // the reflected-dimension setter's has, so the realm mints the half that is ready.
                obj.FastAddProperty(dimName,
                    new DomFunction((in _) => Dom.Features.ComputedStyleBinding.GetUsedDimension(this, dimName, element, in _), "get " + dimName),
                    Dom.Runtime.JsInterop.ToEngineObject(Realm.NewMethod("set " + dimName,
                        (in call) => Dom.Features.ElementReflectionBinding.SetReflectedDimension(dimName, element, in call), 1)),
                    JSPropertyAttributes.EnumerableConfigurableProperty);
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
        // bridge through the wide IElementGeometryHost contract (DomBridge.ElementGeometryHost.cs).
        // The two interface halves of that module are on their prototypes: Element's client*/scroll*
        // metrics, getBoundingClientRect/getClientRects and the imperative scrolling API
        // (DomBridge.ElementInterface.cs, with animate()), and HTMLElement's offset* family
        // (DomBridge.HtmlElementInterface.cs). scrollParent is on neither, because it is on no
        // browser's prototype.
        Dom.Features.ElementGeometryBinding.InstallBridgeMembers(this, Realm, handle, element);

        // SVG DOM interfaces — SVGAnimatedLength/Rect stubs, SVGTextContentElement text metrics, the
        // SVGSVGElement animation timeline and the SMIL animation-element no-ops (Phase 3 P3.50:
        // extracted into the co-located SvgElementBinding feature module). The module is migrated to
        // JSEAL, so it takes the realm and a handle over this still-engine-typed wrapper —
        // JsInterop.FromEngineObject is the half-migrated seam, not a conversion.
        Dom.Features.SvgElementBinding.Install(Realm, handle, element, tag);
    }

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
    private static JsValue ReflectedAttribute(Broiler.Dom.DomElement element, string attribute) =>
        JsValue.String(TryGetAttribute(element, attribute, out var value) ? value : string.Empty);
}
