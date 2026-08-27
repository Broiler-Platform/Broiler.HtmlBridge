using System;
using System.Linq;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// The state authority (JS-object identity, location/base-URL caches) is the P3.16
/// <see cref="BrowsingContextManager"/>, which the module holds a reference to (as it does the shared
/// <see cref="EventTargetRegistry"/> and <see cref="MessagingBinding"/> it installs on the sub-window).
/// Everything else — the sub-document builder it wraps, sub-resource URL resolution, scroll geometry,
/// computed style, the global constructors — is reached through the narrow <see cref="ISubWindowHost"/>
/// contract, so no callback touches an arbitrary bridge private field.
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
    /// Names the context does not define are skipped, so this list may name more than Broiler has.
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

    /// <summary>Gets or builds the sub-window JS object for a nested-browsing-context container.</summary>
    public JSObject GetOrCreate(DomElement containerElement)
    {
        if (_browsingContexts.TryGetSubWindow(containerElement, out var cached))
            return cached;

        var subDocument = _host.GetOrCreateSubDocument(containerElement);
        var subWindow = new JSObject();
        _browsingContexts.SetSubWindow(containerElement, subWindow);
        _eventTargets.SetOwnerWindow(subWindow, subWindow);
        _messaging.InstallEventTargetApi(subWindow, "DomBridge.subWindow.dispatchEvent");
        _messaging.RegisterWindowMessaging(subWindow);

        subWindow.FastAddProperty("document",
            new DomFunction((in _) => _host.GetOrCreateSubDocument(containerElement), "get document"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // The frame's Location is built the same way the top-level one is — components and the
        // navigation methods together, because a framed page calls location.replace() as readily
        // as a top-level one and a missing method is a TypeError that takes its caller with it.
        var locationHref = GetSubWindowLocationHref(containerElement);
        var iframeLocation = LocationBinding.Build(locationHref);
        subWindow.FastAddValue("location", iframeLocation, JSPropertyAttributes.EnumerableConfigurableValue);

        subWindow.FastAddProperty("scrollX", new DomFunction((in _) => new JSNumber(GetSubWindowScrollOffset(containerElement, vertical: false)), "get scrollX"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        subWindow.FastAddProperty("scrollY", new DomFunction((in _) => new JSNumber(GetSubWindowScrollOffset(containerElement, vertical: true)), "get scrollY"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        subWindow.FastAddProperty("pageXOffset", new DomFunction((in _) => new JSNumber(GetSubWindowScrollOffset(containerElement, vertical: false)), "get pageXOffset"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        subWindow.FastAddProperty("pageYOffset", new DomFunction((in _) => new JSNumber(GetSubWindowScrollOffset(containerElement, vertical: true)), "get pageYOffset"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        subWindow.FastAddValue("scroll", new DomFunction((in a) => Scroll(containerElement, in a), "scroll", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        subWindow.FastAddValue("scrollTo", new DomFunction((in a) => ScrollTo(containerElement, in a), "scrollTo", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        subWindow.FastAddValue("scrollBy", new DomFunction((in a) => ScrollBy(containerElement, in a), "scrollBy", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        subWindow.FastAddValue("self", subWindow, JSPropertyAttributes.EnumerableConfigurableValue);
        subWindow.FastAddValue("window", subWindow, JSPropertyAttributes.EnumerableConfigurableValue);

        subWindow.FastAddValue("globalThis", subWindow, JSPropertyAttributes.EnumerableConfigurableValue);

        foreach (var ctorName in MirroredGlobals)
        {
            if (_host.GetGlobal(ctorName) is { } ctor)
                subWindow.FastAddValue(ctorName, ctor, JSPropertyAttributes.EnumerableConfigurableValue);
        }

        var parentWindow = GetParentWindowForSubDocument(containerElement);
        if (parentWindow != null)
        {
            subWindow.FastAddValue("parent", parentWindow, JSPropertyAttributes.EnumerableConfigurableValue);
        }

        subWindow.FastAddValue("top", _host.WindowJSObject ?? subWindow, JSPropertyAttributes.EnumerableConfigurableValue);

        subDocument.FastAddValue("defaultView", subWindow, JSPropertyAttributes.EnumerableConfigurableValue);

        // window.getSelection — the frame's own selection, distinct from the containing page's. It is
        // literally the document's function rather than a second one over the same root, so
        // `w.getSelection() === w.document.getSelection()` holds for a frame as it does for the page.
        if (subDocument[(KeyString)"getSelection"] is { } documentGetSelection)
        {
            subWindow.FastAddValue(
                "getSelection", documentGetSelection, JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // The frame's document shares its window's Location, as the main document shares the main
        // window's. A framed page reads `document.location` for its origin exactly as a top-level
        // one does, and undefined there throws rather than reading as absent.
        subDocument.FastAddValue("location", iframeLocation, JSPropertyAttributes.EnumerableConfigurableValue);

        // window.getComputedStyle — sub-window needs its own copy so that
        // doc.defaultView.getComputedStyle(node, "") resolves CSS rules from
        // the sub-document's <style> elements rather than the main document.
        subWindow.FastAddValue("getComputedStyle", new DomFunction((in a) => GetComputedStyle(in a), "getComputedStyle", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Last, so the bridge's own members are already in place and win over a same-named
        // declaration: whatever the frame's scripts declared while the sub-document above was being
        // built now becomes reachable as frames[0].window.foo.
        _host.PublishPendingSubDocumentGlobals(containerElement, subWindow);

        return subWindow;
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

    private JSObject? GetParentWindowForSubDocument(DomElement containerElement)
    {
        // The container's owning document is a severed sub-document DomDocument when the container is
        // itself nested in another frame; recover that frame via the reverse map (P4.4c: the owning
        // document comes from the canonical tree, was OwnerDocRoot / ParentEl(#subdoc-root)).
        var parentFrame = _host.GetFrameForContentDocument(DomBridge.GetOwningDocument(containerElement));
        if (parentFrame != null)
            return GetOrCreate(parentFrame);

        return _host.WindowJSObject;
    }

    // ── Scroll / getComputedStyle callbacks (were JsSubDocumentsScroll006Core … 009Core) ──
    private JSValue Scroll(DomElement containerElement, in Arguments a)
    {
        var (left, top, behavior) = _host.GetScrollArguments(a);
        SetSubWindowScrollOffsets(containerElement, left, top, behavior: behavior);
        return JSUndefined.Value;
    }

    private JSValue ScrollTo(DomElement containerElement, in Arguments a)
    {
        var (left, top, behavior) = _host.GetScrollArguments(a);
        SetSubWindowScrollOffsets(containerElement, left, top, behavior: behavior);
        return JSUndefined.Value;
    }

    private JSValue ScrollBy(DomElement containerElement, in Arguments a)
    {
        var (left, top, behavior) = _host.GetScrollArguments(a);
        SetSubWindowScrollOffsets(containerElement, left, top, relative: true, behavior: behavior);
        return JSUndefined.Value;
    }

    private JSValue GetComputedStyle(in Arguments a)
    {
        if (a.Length == 0)
            return new JSObject();
        var targetObj = a[0] as JSObject;
        var el = targetObj != null ? _host.FindDomElementByJSObject(targetObj) : null;
        var pseudoElement = a.Length > 1 ? a[1]?.ToString() : null;
        return _host.BuildComputedStyleObject(el, pseudoElement);
    }
}
