using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    private void RegisterContentRenderingPolyfills(JSContext context, JSObject document)
    {
        // Google Search Compliance content-rendering / fidelity polyfills — Image, IntersectionObserver,
        // ResizeObserver, TextEncoder/TextDecoder, URL/URLSearchParams and AbortController — are a versioned
        // embedded .js asset (Phase 3 work item 6, externalized from inline C# string literals) evaluated
        // once here. See Polyfills/content-rendering-polyfills.js.
        context.Eval(PolyfillAssets.ContentRendering);

        // document.cookie — get/set stub (in-memory, non-persistent). Host-driven (not pure JS), so it stays
        // here rather than in the JS asset. Order-independent of the pure-JS polyfills above.
        var cookieStore = "";
        document.FastAddProperty(
            "cookie",
            new DomFunction((in _) => new JSString(cookieStore), "get cookie"),
            new DomFunction((in a) => Dom.Features.WindowDocumentMiscBinding.SetCookie(ref cookieStore, in a), "set cookie"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    private void RegisterSecurityAndConstructorPolyfills(JSContext context, JSObject window)
    {
        // window.crypto — the getRandomValues/randomUUID subset (Phase 3: co-located CryptoBinding module)
        var cryptoObj = Dom.Features.CryptoBinding.Build();
        window.FastAddValue("crypto", cryptoObj, JSPropertyAttributes.EnumerableConfigurableValue);
        context["crypto"] = cryptoObj;

        // window.CSS — the CSSOM namespace object (supports/escape). Host-driven rather than a
        // pure-JS polyfill because supports() has to answer from the CSS engine's own @supports
        // evaluator; answering from the CSSOM instead would claim support for everything, since
        // Broiler's CSSOM stores declarations without validating them.
        var cssObj = Dom.Features.CssBinding.Build();
        window.FastAddValue("CSS", cssObj, JSPropertyAttributes.EnumerableConfigurableValue);
        context["CSS"] = cssObj;

        // DOMException constructor
        RegisterDOMException(context);

        // Node constructor with type constants
        RegisterNodeConstructor(context);

        // Element/HTMLElement/HTMLUnknownElement/… interface globals. After Node, whose
        // @@hasInstance it installs.
        RegisterDomInterfaceConstructors(context);

        // The Node/CharacterData/Text members a text or comment node exposes, onto those interface
        // prototypes. After the constructors above, which is what there is a prototype to install on,
        // and before the first wrapper is minted, which is what lets one inherit them instead of
        // carrying its own copies.
        RegisterCharacterDataInterface();

        // Element's own interface, onto Element.prototype. After the constructors for the same reason,
        // and after the character-data pass because that one puts Element's three name accessors
        // (localName/prefix/namespaceURI) on the same prototype.
        RegisterElementInterface();

        // HTMLElement's, onto HTMLElement.prototype. The custom-elements pass replaces the
        // HTMLElement *constructor* later with a constructible one, deliberately keeping this same
        // prototype object — so installing here rather than after it is what every element wrapper
        // already linked to that object needs.
        RegisterHtmlElementInterface();

        // SVGLength interface constants
        RegisterSVGLength(context);

        // AbstractRange/Range — the one DOM interface here whose members really live on its
        // prototype, so it has to be registered before the first document.createRange() can link a
        // range to it.
        _traversal.RegisterRangeInterface(context);

        // Blob/File, and the URL.createObjectURL pair they need. After the content-rendering
        // polyfills, which is where the URL constructor these attach to comes from.
        _blobs.RegisterInterfaces(context);

        // ReadableStream (with its default reader and controller), ProgressEvent and FileReader,
        // plus blob.stream(). After blobs, because the stream reads a blob's bytes and the stream
        // member goes onto Blob.prototype.
        _streams.Register(context, _blobs);

        // ElementInternals/ValidityState/CustomStateSet — the objects a form-associated custom
        // element's attachInternals() hands back. Registered here rather than with the custom-element
        // pass because they are ordinary interface globals a page can name and feature-detect.
        ElementInternals.RegisterInterfaces(context);

        // Storage interface global — the name a page tests before it touches an area.
        RegisterStorageConstructor(context);

        // Notification — the interface, with its permission already settled at "denied" because
        // there is no surface to show one on (NotificationBinding).
        var notification = Dom.Features.NotificationBinding.Build();
        window.FastAddValue("Notification", notification, JSPropertyAttributes.EnumerableConfigurableValue);

        // MediaSource — the Media Source Extensions entry point, whose isTypeSupported answers for
        // the playback pipeline the HTML layer does not yet have (MediaCapabilityBinding, which also
        // installs canPlayType on the media elements themselves).
        var mediaSource = Dom.Features.MediaCapabilityBinding.BuildMediaSource(context);
        window.FastAddValue("MediaSource", mediaSource, JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// The <c>Storage</c> interface global (HTML §12.2.2), as the name a feature test asks for
    /// rather than as a constructible class: the two areas come from <c>localStorage</c> and
    /// <c>sessionStorage</c>, never from <c>new Storage()</c>.
    /// </summary>
    /// <remarks>
    /// <c>typeof Storage !== 'undefined'</c> is the canonical Web Storage feature test — MDN
    /// documents it as such — so an absent global makes a page conclude it has no storage at all
    /// and take its no-storage path, however complete the two area objects are. Like the DOM
    /// interface globals (see <c>RegisterDomInterfaceConstructors</c>) it answers
    /// <c>instanceof</c> from the object's shape, because a bridge storage object carries its
    /// members directly instead of inheriting them from this constructor's prototype.
    /// </remarks>
    private static void RegisterStorageConstructor(JSContext context)
    {
        context.Eval(@"
            function Storage() { throw new TypeError('Illegal constructor'); }

            Object.defineProperty(Storage, Symbol.hasInstance, {
                value: function (o) {
                    return !!o && typeof o === 'object'
                        && typeof o.getItem === 'function'
                        && typeof o.setItem === 'function'
                        && typeof o.removeItem === 'function'
                        && typeof o.key === 'function'
                        && typeof o.length === 'number';
                },
                writable: false, enumerable: false, configurable: true
            });
        ");
    }

}
