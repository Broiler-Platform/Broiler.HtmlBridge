using System;
using System.Linq;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The co-located nested-browsing-context <c>window</c> (sub-window) feature (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.17 — the residual Frames surface P3.13 deferred). Owns the
/// sub-window JS object built for an <c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c> —
/// its <c>document</c>/<c>location</c>/<c>self</c>/<c>window</c>/<c>parent</c>/<c>top</c> wiring, the
/// scroll surface (<c>scrollX</c>/<c>scrollY</c>/<c>pageXOffset</c>/<c>pageYOffset</c> +
/// <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c>), the mirrored event constructors, and its own
/// <c>getComputedStyle</c> — plus the sub-window-scoped helpers (location href, scroll offset read/write,
/// scrolling-element and parent-window resolution).
/// </summary>
/// <remarks>
/// <para>
/// The state authority (JS-object identity, location/base-URL caches) is the P3.16
/// <see cref="BrowsingContextManager"/>, which the module holds a reference to (as it does the shared
/// <see cref="EventTargetRegistry"/> and <see cref="MessagingBinding"/> it installs on the sub-window).
/// Everything else — the sub-document builder it wraps, sub-resource URL resolution, scroll geometry,
/// computed style, the global constructors — is reached through the narrow <see cref="ISubWindowHost"/>
/// contract, so no callback touches an arbitrary bridge private field.
/// </para>
/// <para>
/// The window object is minted and populated through the realm, so nothing here spells a member
/// installation in engine terms. Four collaborators still hold JS objects as engine values — the
/// browsing-context cache, the event-target registry, the messaging module and
/// <see cref="LocationBinding"/> — so the object is unwrapped once, through <see cref="JsInterop"/>,
/// and handed to them. That is a cast rather than a conversion: <c>frames[0].window</c> is the same
/// object it always was, and the caches stay keyed on it.
/// </para>
/// </remarks>
internal sealed class SubWindowBinding(
    ISubWindowHost host,
    BrowsingContextManager browsingContexts,
    EventTargetRegistry eventTargets,
    MessagingBinding messaging)
{
    private readonly ISubWindowHost _host = host;
    private readonly BrowsingContextManager _browsingContexts = browsingContexts;
    private readonly EventTargetRegistry _eventTargets = eventTargets;
    private readonly MessagingBinding _messaging = messaging;

    /// <summary>
    /// The globals published on a sub-window: the event constructors it has always mirrored, plus the
    /// standard JavaScript ones.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sub-window used to carry <c>document</c>, <c>location</c> and the event constructors and
    /// nothing else, so <c>iframe.contentWindow.String</c> — and <c>Object</c>, <c>Array</c>,
    /// <c>Function</c>, <c>JSON</c>, <c>Math</c>, every one of them — read as <c>undefined</c>. In a
    /// browser a nested browsing context is a full global object, and script reaches for exactly
    /// these: taking a reference to a built-in from a fresh frame, rather than from the current
    /// global, is the standard way to get one that page script has not patched. Google Search's
    /// anti-abuse bundle does it on the first run of its interpreter — <c>contentWindow.String</c>,
    /// then <c>.prototype</c> — and reading <c>prototype</c> off <c>undefined</c> threw, which
    /// derailed the interpreter into decoding its own bytecode wrongly for the rest of the page.
    /// </para>
    /// <para>
    /// These are the PARENT realm's objects, not a realm of the frame's own: <c>contentWindow.String
    /// === String</c> here, where a browser gives two distinct functions. Broiler runs every document
    /// in one JavaScript context, so a per-frame realm is not something this binding can conjure —
    /// and identity is the lesser problem. Code that harvests a built-in gets a working built-in;
    /// only code that compares the two for identity can tell, and it gets a defensible answer either
    /// way, where <c>undefined</c> was defensible to nobody.
    /// </para>
    /// <para>
    /// The list may name more than Broiler has: a name the realm does not define reads as
    /// <c>undefined</c> and is published as such, which is what a global read answers and what this
    /// has always installed.
    /// </para>
    /// </remarks>
    private static readonly string[] MirroredGlobals =
    [
        // Event constructors — mirrored since P3.17.
        "Event", "CustomEvent", "MouseEvent", "FocusEvent", "KeyboardEvent",
        "WheelEvent", "UIEvent", "MessageChannel",

        // Fundamental objects and their namespaces.
        "Object", "Function", "Boolean", "Symbol", "Math", "JSON", "Reflect",

        // Numbers, text and time.
        "Number", "BigInt", "String", "Date", "RegExp",

        // Collections.
        "Array", "Map", "Set", "WeakMap", "WeakSet", "WeakRef",

        // Errors.
        "Error", "TypeError", "RangeError", "SyntaxError", "ReferenceError",
        "EvalError", "URIError", "AggregateError",

        // Binary data.
        "ArrayBuffer", "SharedArrayBuffer", "DataView", "Int8Array", "Uint8Array",
        "Uint8ClampedArray", "Int16Array", "Uint16Array", "Int32Array", "Uint32Array",
        "Float32Array", "Float64Array", "BigInt64Array", "BigUint64Array",

        // Control flow and metaprogramming.
        "Promise", "Proxy", "Intl",

        // Global functions and value properties.
        "eval", "parseInt", "parseFloat", "isNaN", "isFinite",
        "encodeURI", "encodeURIComponent", "decodeURI", "decodeURIComponent",
        "NaN", "Infinity", "undefined",

        // Web globals a frame's script reaches for as readily as the language ones.
        "console", "navigator", "fetch", "XMLHttpRequest", "URL", "URLSearchParams",
        "TextEncoder", "TextDecoder", "Blob", "AbortController", "Headers",
        "setTimeout", "clearTimeout", "setInterval", "clearInterval",
        "requestAnimationFrame", "cancelAnimationFrame", "queueMicrotask",
        // The idle pair belongs with the animation-frame pair it sits beside on the main window; it
        // was the only scheduling API missing here. The bare name always resolved (it is a context
        // global, and every document shares the one context), so what was undefined is the qualified
        // read — `contentWindow.requestIdleCallback` and `frames[0].requestIdleCallback` from the
        // parent, and `window.requestIdleCallback` from inside the frame, where `window` IS the
        // sub-window. That last one is the idiomatic spelling: the standard feature test is
        // `window.requestIdleCallback ? … : fallback` (MediaWiki writes exactly that), so a framed
        // page took its no-native path, and one that called `window.requestIdleCallback(cb)`
        // unguarded got a TypeError. They schedule on the bridge's one event loop, as the mirrored
        // timers already do.
        "requestIdleCallback", "cancelIdleCallback",
        "atob", "btoa", "structuredClone", "performance", "crypto",

        // Interface objects a framed page feature-tests before it uses the capability behind them.
        // Both answer "not available here" rather than throwing (NotificationBinding,
        // MediaCapabilityBinding), and that answer is worth as much inside a frame as outside one —
        // an embedded player is exactly the kind of document that probes MediaSource first.
        "Notification", "MediaSource",

        // The two storage areas. A frame gets the parent's objects rather than fresh ones, which
        // is what a same-origin frame sees in a browser: the Storage object differs there, the
        // *area* behind it does not, and a frame that cannot read what its opener wrote is the
        // more visible wrong answer.
        "localStorage", "sessionStorage",
    ];

    /// <summary>
    /// <see cref="Build"/> as an engine object, for the callers that still hold one: the
    /// browsing-context cache the window is stored in, and the bridge's iframe/window-load surfaces
    /// (<c>DomBridge.IframeElementHost.cs</c>, <c>DomBridge.WindowLoad.cs</c>) — other groups' files
    /// this round. A cast, not a conversion, so it is the same window object either way.
    /// </summary>
    public JavaScript.Runtime.JSObject GetOrCreate(DomElement containerElement) =>
        JsInterop.ToEngineObject(Build(containerElement));

    /// <summary>Gets or builds the sub-window JS object for a nested-browsing-context container.</summary>
    private JsValue Build(DomElement containerElement)
    {
        if (_browsingContexts.TryGetSubWindow(containerElement, out var cached))
            return JsInterop.FromEngineObject(cached);

        var realm = _host.Realm;
        var subDocument = _host.GetOrCreateSubDocument(containerElement);
        var window = realm.NewObject();

        // The event-target registry and the generic EventTarget installation take handles now, so the
        // window goes to them as it stands. The other two still hold JS objects as engine values —
        // the browsing-context cache stores one, and window.postMessage is installed by a member
        // whose other caller (DomBridge/Registration/Window.cs) hands over an engine object — so the
        // window is unwrapped once here rather than at each of them. That is a cast, so all four are
        // given the same object.
        var engineWindow = JsInterop.ToEngineObject(window);
        _browsingContexts.SetSubWindow(containerElement, engineWindow);
        _eventTargets.SetOwnerWindow(window, window);
        _messaging.InstallEventTargetApi(window, "DomBridge.subWindow.dispatchEvent");
        _messaging.RegisterWindowMessaging(engineWindow);

        realm.DefineAccessor(window, "document",
            (in _) => _host.GetOrCreateSubDocument(containerElement), null);

        // The frame's Location is built the same way the top-level one is — components and the
        // navigation methods together, because a framed page calls location.replace() as readily
        // as a top-level one and a missing method is a TypeError that takes its caller with it.
        //
        // LocationBinding still mints that object with the engine's own types and its Build takes no
        // realm, so the result crosses back through the seam here. `realm` is already in hand three
        // lines above, so the day Build takes one this becomes
        // `realm.DefineValue(window, "location", LocationBinding.Build(realm, locationHref))` and the
        // unwrapping goes — nothing else on this side has to move.
        var locationHref = GetSubWindowLocationHref(containerElement);
        var iframeLocation = JsInterop.FromEngineObject(LocationBinding.Build(locationHref));
        realm.DefineValue(window, "location", iframeLocation);

        realm.DefineAccessor(window, "scrollX",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: false)), null);
        realm.DefineAccessor(window, "scrollY",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: true)), null);
        realm.DefineAccessor(window, "pageXOffset",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: false)), null);
        realm.DefineAccessor(window, "pageYOffset",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: true)), null);

        realm.DefineValue(window, "scroll",
            realm.NewMethod("scroll", (in call) => Scroll(containerElement, in call), 2));
        realm.DefineValue(window, "scrollTo",
            realm.NewMethod("scrollTo", (in call) => ScrollTo(containerElement, in call), 2));
        realm.DefineValue(window, "scrollBy",
            realm.NewMethod("scrollBy", (in call) => ScrollBy(containerElement, in call), 2));

        realm.DefineValue(window, "self", window);
        realm.DefineValue(window, "window", window);

        realm.DefineValue(window, "globalThis", window);

        foreach (var ctorName in MirroredGlobals)
        {
            if (_host.TryGetGlobal(ctorName, out var ctor))
                realm.DefineValue(window, ctorName, ctor);
        }

        var parentWindow = GetParentWindowForSubDocument(containerElement);
        if (parentWindow.IsObject)
        {
            realm.DefineValue(window, "parent", parentWindow);
        }

        realm.DefineValue(window, "top", _host.MainWindow is { IsObject: true } top ? top : window);

        realm.DefineValue(subDocument, "defaultView", window);

        // window.getSelection — the frame's own selection, distinct from the containing page's. It is
        // literally the document's function rather than a second one over the same root, so
        // `w.getSelection() === w.document.getSelection()` holds for a frame as it does for the page.
        // Read unconditionally: the sub-document built above always carries the method, and the null
        // test this replaces was asking whether the engine's property read returned a CLR null, which
        // a missing property never did — it answered undefined, and a handle is never null at all.
        realm.DefineValue(window, "getSelection", realm.GetProperty(subDocument, "getSelection"));

        // The frame's document shares its window's Location, as the main document shares the main
        // window's. A framed page reads `document.location` for its origin exactly as a top-level
        // one does, and undefined there throws rather than reading as absent.
        realm.DefineValue(subDocument, "location", iframeLocation);

        // window.getComputedStyle — sub-window needs its own copy so that
        // doc.defaultView.getComputedStyle(node, "") resolves CSS rules from
        // the sub-document's <style> elements rather than the main document.
        realm.DefineValue(window, "getComputedStyle",
            realm.NewMethod("getComputedStyle", (in call) => GetComputedStyle(in call), 2));

        // Last, so the bridge's own members are already in place and win over a same-named
        // declaration: whatever the frame's scripts declared while the sub-document above was being
        // built now becomes reachable as frames[0].window.foo.
        _host.PublishPendingSubDocumentGlobals(containerElement, window);

        return window;
    }

    private string GetSubWindowLocationHref(DomElement containerElement)
    {
        if (_browsingContexts.TryGetLocation(containerElement, out var cachedLocation) &&
            !string.IsNullOrWhiteSpace(cachedLocation))
        {
            return cachedLocation;
        }

        if (string.Equals(containerElement.TagName, "iframe", StringComparison.OrdinalIgnoreCase) &&
            DomBridge.HasAttr(containerElement, "srcdoc"))
            return "about:srcdoc";

        var resolvedUrl = _host.ResolveSubResourceUrl(DomBridge.GetSubResourceUrl(containerElement), _host.GetInheritedSubDocumentBaseUrl(containerElement));
        return !string.IsNullOrWhiteSpace(resolvedUrl) ? resolvedUrl : "about:blank";
    }

    private double GetSubWindowScrollOffset(DomElement containerElement, bool vertical)
    {
        var scrollingElement = GetSubDocumentScrollingElement(containerElement);
        return scrollingElement == null ? 0 : _host.GetElementScrollOffset(scrollingElement, vertical);
    }

    private void SetSubWindowScrollOffsets(DomElement containerElement, double? left = null, double? top = null, bool relative = false, string? behavior = null)
    {
        var scrollingElement = GetSubDocumentScrollingElement(containerElement);
        if (scrollingElement == null)
            return;

        _host.SetElementScroll(scrollingElement, left, top, relative, behavior);
    }

    private DomElement? GetSubDocumentScrollingElement(DomElement containerElement)
    {
        var document = _host.GetContentDocument(containerElement);
        return document == null ? null : DomBridge.GetDocumentElement(document);
    }

    /// <summary>
    /// The window a frame's <c>parent</c> names, or a non-object when there is none — the frame's own
    /// containing frame when it is itself nested, else the top-level window.
    /// </summary>
    private JsValue GetParentWindowForSubDocument(DomElement containerElement)
    {
        // The container's owning document is a severed sub-document DomDocument when the container is
        // itself nested in another frame; recover that frame via the reverse map (P4.4c: the owning
        // document comes from the canonical tree, was OwnerDocRoot / ParentEl(#subdoc-root)).
        var parentFrame = _host.GetFrameForContentDocument(DomBridge.GetOwningDocument(containerElement));
        if (parentFrame != null)
            return Build(parentFrame);

        return _host.MainWindow;
    }

    // ── Scroll / getComputedStyle callbacks (were JsSubDocumentsScroll006Core … 009Core) ──
    private JsValue Scroll(DomElement containerElement, in JsCall call)
    {
        var (left, top, behavior) = _host.GetScrollArguments(call.Arguments);
        SetSubWindowScrollOffsets(containerElement, left, top, behavior: behavior);
        return JsValue.Undefined;
    }

    private JsValue ScrollTo(DomElement containerElement, in JsCall call)
    {
        var (left, top, behavior) = _host.GetScrollArguments(call.Arguments);
        SetSubWindowScrollOffsets(containerElement, left, top, behavior: behavior);
        return JsValue.Undefined;
    }

    private JsValue ScrollBy(DomElement containerElement, in JsCall call)
    {
        var (left, top, behavior) = _host.GetScrollArguments(call.Arguments);
        SetSubWindowScrollOffsets(containerElement, left, top, relative: true, behavior: behavior);
        return JsValue.Undefined;
    }

    private JsValue GetComputedStyle(in JsCall call)
    {
        if (call.Length == 0)
            return call.Realm.NewObject();
        var el = call[0].IsObject ? _host.FindElement(call[0]) : null;
        // The realm's ToString, not the handle's: a page passing an object as the pseudo-element runs
        // its own toString here, which is the coercion the engine performed.
        var pseudoElement = call.Length > 1 ? call.Realm.ToJsString(call[1]) : null;
        return _host.BuildComputedStyleObject(el, pseudoElement);
    }
}
