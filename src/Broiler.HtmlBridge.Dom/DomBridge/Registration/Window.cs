using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Net;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // The one MemoryInfo behind both console.memory and performance.memory. It is built with the
    // console in RegisterWindowBasics and read by RegisterPerformanceObject, which runs after it —
    // the two names report one object, as they do in Chrome. See PerformanceMemoryBinding.
    private JSObject? _memoryInfo;

    private JSObject RegisterWindowBasics(JSObject document, JSObject window)
    {
        window.FastAddValue("document", document, JSPropertyAttributes.EnumerableConfigurableValue);

        // window.localStorage / window.sessionStorage — the two Web Storage areas (HTML §12.2),
        // each an in-memory Storage object of its own. Built separately because they are separate
        // areas: a page that stashes per-tab state in one and durable state in the other must not
        // see the two answer each other's reads.
        window.FastAddValue("localStorage", Dom.Features.WebStorageBinding.BuildStorage(), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("sessionStorage", Dom.Features.WebStorageBinding.BuildStorage(), JSPropertyAttributes.EnumerableConfigurableValue);

        // window.matchMedia(query) — evaluates basic media queries
        window.FastAddValue("matchMedia", new DomFunction((in a) => Dom.Features.MatchMediaBinding.MatchMedia(this, in a), "matchMedia", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // window.location — the URL components here, and the navigation surface (`href`, assign,
        // replace, reload, toString) from LocationBinding. The components alone made
        // `location.replace(url)` a TypeError ("undefined is not a function") which aborts the rest
        // of the calling function, so they are built together: see LocationBinding for what the
        // four do in a capture, and why they do not navigate.
        var location = new JSObject();
        location.FastAddValue("protocol", new JSString(_pageProtocol), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("host", new JSString(_pageHost), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("hostname", new JSString(_pageHostName), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("port", new JSString(_pagePort), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("pathname", new JSString(_pagePathName), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("search", new JSString(_pageSearch), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("hash", new JSString(_pageHash), JSPropertyAttributes.EnumerableConfigurableValue);
        location.FastAddValue("origin", new JSString(_pageOrigin), JSPropertyAttributes.EnumerableConfigurableValue);
        Dom.Features.LocationBinding.AddNavigationSurface(location, _pageUrl);

        window.FastAddValue("location", location, JSPropertyAttributes.EnumerableConfigurableValue);

        // document.location is the *same* Location as window.location (HTML §3.1.5: the getter
        // returns this document's relevant global object's Location), so it is the one object
        // registered on both rather than a copy — pages compare the two, and `document.location`
        // is the spelling half of them reach for. Undefined here, it did not read as a missing
        // property but threw "Cannot get property protocol of undefined" out of the first script
        // to ask an origin of it, which aborts the whole <script>. That is the same One-Google-bar
        // bundle `top` above died in — `dF=function(){var a=document.location;return
        // a.protocol+"//"+a.host}` is how it builds its own origin — so it is the next thing that
        // failed once `top` resolved.
        document.FastAddValue("location", location, JSPropertyAttributes.EnumerableConfigurableValue);

        // window timers / animation frames — thin adapters over the P2.4 BrowserEventLoop, co-located
        // in the TimerBinding feature module (Phase 3).
        window.FastAddValue("setTimeout", new DomFunction((in a) => Dom.Features.TimerBinding.SetTimeout(_eventLoop, _windowContext, in a), "setTimeout", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("clearTimeout", new DomFunction((in a) => Dom.Features.TimerBinding.ClearTimeout(_eventLoop, in a), "clearTimeout", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("setInterval", new DomFunction((in a) => Dom.Features.TimerBinding.SetInterval(_eventLoop, _windowContext, in a), "setInterval", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("clearInterval", new DomFunction((in a) => Dom.Features.TimerBinding.ClearInterval(_eventLoop, in a), "clearInterval", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("requestAnimationFrame", new DomFunction((in a) => Dom.Features.TimerBinding.RequestAnimationFrame(_eventLoop, _windowContext, in a), "requestAnimationFrame", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("cancelAnimationFrame", new DomFunction((in a) => Dom.Features.TimerBinding.CancelAnimationFrame(_eventLoop, in a), "cancelAnimationFrame", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // window.alert(msg) — logs to debug output
        window.FastAddValue("alert", new DomFunction(Dom.Features.WindowDocumentMiscBinding.Alert, "alert", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // btoa / atob — the WindowOrWorkerGlobalScope base64 pair (HTML §8.3), co-located in the
        // Base64Binding feature module. The window IS the global object, so registering here is
        // what makes the unqualified `atob(…)` a page writes resolve as well.
        window.FastAddValue("btoa", new DomFunction((in a) => Dom.Features.Base64Binding.Btoa(_jsContext!, in a), "btoa", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("atob", new DomFunction((in a) => Dom.Features.Base64Binding.Atob(_jsContext!, in a), "atob", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // console object (shared between window.console and global console)
        var console = Dom.Features.ConsoleBinding.Build();

        // console.memory — the same MemoryInfo shape performance.memory reports, and in Chrome the
        // same object. Kept as one object here too, so a page that samples both does not have to
        // reconcile two answers taken a moment apart. See PerformanceMemoryBinding.
        _memoryInfo = Dom.Features.PerformanceMemoryBinding.Build();
        console.FastAddValue("memory", _memoryInfo, JSPropertyAttributes.EnumerableConfigurableValue);

        window.FastAddValue("console", console, JSPropertyAttributes.EnumerableConfigurableValue);

        return console;
    }

    private void RegisterWindowGlobals(JSContext context, JSObject document, JSObject window, JSObject console, JSFunction fetchFn)
    {
        context["window"] = window;
        window.FastAddValue("Event", context["Event"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("CustomEvent", context["CustomEvent"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("MouseEvent", context["MouseEvent"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("FocusEvent", context["FocusEvent"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("KeyboardEvent", context["KeyboardEvent"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("WheelEvent", context["WheelEvent"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("UIEvent", context["UIEvent"], JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("InputEvent", context["InputEvent"], JSPropertyAttributes.EnumerableConfigurableValue);

        // window.parent — uses the JSContext global scope so that parent.X()
        // resolves user-defined globals (e.g. parent.notify() from sub-documents).
        var globalThis = context.Eval("this");
        window.FastAddValue("parent", globalThis, JSPropertyAttributes.EnumerableConfigurableValue);
        context["parent"] = globalThis;

        // window.self — refers to this window
        window.FastAddValue("self", window, JSPropertyAttributes.EnumerableConfigurableValue);

        // window.top — the topmost browsing context. This document is the top-level one, so
        // `top`, `parent` and `self` are all this window; a sub-document's window instead gets
        // a `top` pointing back here (SubWindowBinding). It was the one member of that trio
        // never registered, and because `window` IS the global object that made the unqualified
        // `top` a ReferenceError rather than merely an undefined property — which aborts the
        // whole <script>, not just the statement that read it. Framing checks spell it
        // unqualified (`if (top != self)`), so this is the first thing a page's boilerplate
        // touches: google.com's One-Google-bar bundle died on it, taking with it every listener
        // the rest of that script would have registered.
        window.FastAddValue("top", globalThis, JSPropertyAttributes.EnumerableConfigurableValue);
        context["top"] = globalThis;

        // document.defaultView — returns the window object
        document.FastAddValue("defaultView", window, JSPropertyAttributes.EnumerableConfigurableValue);
        context["console"] = console;
        context["fetch"] = fetchFn;

        // Expose timer functions as globals (matching window.* counterparts)
        context["setTimeout"] = window[(KeyString)"setTimeout"];
        context["clearTimeout"] = window[(KeyString)"clearTimeout"];
        context["setInterval"] = window[(KeyString)"setInterval"];
        context["clearInterval"] = window[(KeyString)"clearInterval"];
        context["requestAnimationFrame"] = window[(KeyString)"requestAnimationFrame"];
        context["cancelAnimationFrame"] = window[(KeyString)"cancelAnimationFrame"];
    }

    /// <summary>
    /// Optional measurements of the top-level document's fetch, taken by the host that performed it.
    /// Set before <c>Attach</c>: the window registration reads it once, to fix the document's time
    /// origin at the navigation's start and to give the <c>PerformanceNavigationTiming</c> entry its
    /// network phases and body sizes.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary case, not an error: HTML handed to the bridge as a string never had a
    /// fetch to measure, and neither the conformance runner nor a test performs one. The entry then
    /// reports the specification's "not observed" <c>0</c> for each network mark, and the time origin
    /// is this bridge's own start. See <see cref="DocumentFetchTiming"/> for why the origin is the
    /// part that matters.
    /// </remarks>
    public DocumentFetchTiming? DocumentFetchTiming { get; set; }

    private void RegisterPerformanceObject(JSContext context, JSObject window)
    {
        // ---------------------------------------------------------------
        //  Google Search Compliance: Phase 1 (P0) — Critical polyfills
        // ---------------------------------------------------------------

        // TODO-G2: performance object with performance.now() and timeOrigin.
        // timeOrigin is a wall-clock estimate of this context's origin instant (HR-Time §5), while
        // now() must be MONOTONIC and sub-millisecond (HR-Time §3). Capture the two together: the
        // wall-clock unix-ms for timeOrigin, and a Stopwatch timestamp at the same instant that now()
        // measures its monotonic elapsed time from.
        //
        // The origin belongs to the NAVIGATION, not to this call (HR-Time §5). When the host measured
        // the document's fetch it took that instant before the fetch began and hands it across here,
        // and everything on this timeline — now(), the lifecycle marks, and the entry's network
        // phases — is then measured from the same point, as a browser measures them. Without one this
        // call is the earliest instant the bridge knows of, which is already after the fetch; that is
        // exactly why an unmeasured network phase can only report 0 rather than a negative number.
        var fetchTiming = DocumentFetchTiming;
        var performanceTimeOrigin = fetchTiming?.UnixTimeOriginMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var performanceMonotonicOrigin = fetchTiming?.MonotonicOrigin ?? System.Diagnostics.Stopwatch.GetTimestamp();
        var performanceObj = new JSObject();
        performanceObj.FastAddValue("timeOrigin", new JSNumber(performanceTimeOrigin), JSPropertyAttributes.EnumerableConfigurableValue);
        performanceObj.FastAddValue("now", new DomFunction((in _) => Dom.Features.WindowDocumentMiscBinding.PerformanceNow(performanceMonotonicOrigin, in _), "now", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // The Performance Timeline getters (Performance Timeline §3), all three of which answer from
        // the one entry a document that has navigated once and loaded no instrumented resources has:
        // its own PerformanceNavigationTiming. Everything else a browser would have recorded — paint
        // timings, resource entries, marks — a capture does not, so those searches still come back
        // with an empty list, which is a buffer holding nothing rather than a missing method. See
        // NavigationTimingBinding for what the navigation entry does and does not carry.
        //
        // getEntriesByName was the one of the three missing, and a page does not reach it behind a
        // feature test: performance and PerformanceObserver both existing is the guard it writes, and
        // this call comes after it. duckduckgo.com's first-contentful-paint pixel opens with
        // `performance.getEntriesByName('first-contentful-paint')` on exactly that guard, which threw
        // "undefined is not a function" — taking the PerformanceObserver fallback in the same try
        // block with it, so the page never observed the paint it was asking about either. That name
        // is not the document's, so the pixel still finds nothing and still installs its observer.
        // The navigation entry's document-lifecycle marks are stamped by the load sequence, which
        // runs after this, so the entry reads them through this holder rather than holding values.
        // It shares the monotonic origin with performance.now() above, so a mark and a now() reading
        // are two points on one timeline.
        _navigationTiming = new Dom.Features.NavigationTimingState(performanceMonotonicOrigin);
        Dom.Features.NavigationTimingBinding.Install(
            performanceObj, _pageUrl, _pageProtocol, _navigationTiming, fetchTiming);

        // performance.memory — the same MemoryInfo console.memory reports (built with the console in
        // RegisterWindowBasics, which runs first).
        if (_memoryInfo is { } memory)
            performanceObj.FastAddValue("memory", memory, JSPropertyAttributes.EnumerableConfigurableValue);

        // performance.mark() / performance.measure() — no-op stubs
        performanceObj.FastAddValue("mark", UndefinedFunction("mark", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        performanceObj.FastAddValue("measure", UndefinedFunction("measure", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        // The clear/resize counterparts to mark, measure and the resource buffer. Nothing is recorded
        // for them to clear, but a page that marks commonly clears in the same breath, and the throw
        // would land on the clear rather than on the mark it pairs with.
        performanceObj.FastAddValue("clearMarks", UndefinedFunction("clearMarks", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        performanceObj.FastAddValue("clearMeasures", UndefinedFunction("clearMeasures", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        performanceObj.FastAddValue("clearResourceTimings", UndefinedFunction("clearResourceTimings", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        performanceObj.FastAddValue("setResourceTimingBufferSize", UndefinedFunction("setResourceTimingBufferSize", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // toJSON is how the interface serialises, and telemetry that ships timings reaches it through
        // JSON.stringify(performance) as often as by name.
        performanceObj.FastAddValue(
            "toJSON",
            new DomFunction(
                (in _) =>
                {
                    var json = new JSObject();
                    json.FastAddValue("timeOrigin", new JSNumber(performanceTimeOrigin), JSPropertyAttributes.EnumerableConfigurableValue);
                    return json;
                },
                "toJSON",
                0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        window.FastAddValue("performance", performanceObj, JSPropertyAttributes.EnumerableConfigurableValue);
        context["performance"] = performanceObj;
    }

    /// <summary>
    /// <c>window.history</c> (HTML §7.2.3). A capture never leaves the page it was given, so the
    /// session is one entry long and the traversal methods do nothing; what matters is that the
    /// object and its members <em>exist</em>.
    /// </summary>
    /// <remarks>
    /// Absent, it did not read as a missing feature — reading through it threw "Cannot get
    /// property replaceState of undefined", which aborts the function that asked and, with it,
    /// whatever that function was in the middle of setting up. It is boilerplate in every router
    /// and analytics bundle, so the abort lands early: on <c>www.mediawiki.org</c> it took out
    /// <c>skins.vector.js</c>, and the skin then never applied the preferences that decide which
    /// of its two appearance panels is shown — leaving both in the page and the article a
    /// panel's height too far down.
    /// </remarks>
    private void RegisterHistoryObject(JSContext context, JSObject window)
    {
        var history = new JSObject();

        history.FastAddValue("length", new JSNumber(1), JSPropertyAttributes.EnumerableConfigurableValue);
        history.FastAddValue("state", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        history.FastAddValue("scrollRestoration", new JSString("auto"), JSPropertyAttributes.EnumerableConfigurableValue);

        // pushState/replaceState record the state the page hands them, because a page that writes
        // one commonly reads it straight back; neither changes the document's URL, which a capture
        // has no way to honour.
        history.FastAddValue("pushState", new DomFunction((in a) => StoreHistoryState(history, in a), "pushState", 3), JSPropertyAttributes.EnumerableConfigurableValue);
        history.FastAddValue("replaceState", new DomFunction((in a) => StoreHistoryState(history, in a), "replaceState", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        history.FastAddValue("back", UndefinedFunction("back", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        history.FastAddValue("forward", UndefinedFunction("forward", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        history.FastAddValue("go", UndefinedFunction("go", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        window.FastAddValue("history", history, JSPropertyAttributes.EnumerableConfigurableValue);
        context["history"] = history;
    }

    private static JSValue StoreHistoryState(JSObject history, in Arguments arguments)
    {
        history.FastAddValue(
            "state",
            arguments.Length > 0 ? arguments[0] : JSNull.Value,
            JSPropertyAttributes.EnumerableConfigurableValue);

        return JSUndefined.Value;
    }

    /// <summary>
    /// <c>PerformanceObserver</c> (Performance Timeline §2) and <c>requestIdleCallback</c>
    /// (Background Tasks §2), as the shapes a page needs them to have rather than as working
    /// instrumentation: a headless capture produces no performance entries to deliver, and its
    /// event loop has no idle period to wait for.
    /// </summary>
    /// <remarks>
    /// A missing constructor is worse than an inert one here. Telemetry bundles construct one at
    /// module scope, so <c>new PerformanceObserver(…)</c> threw a ReferenceError that rejected the
    /// promise the module was resolving, and every module waiting on that one stayed unresolved —
    /// which is how a page can lose behaviour that has nothing to do with performance timing.
    /// <c>requestIdleCallback</c> runs its callback on a timer instead of dropping it, because
    /// what pages defer to idle is often the work that produces visible content — and it hands that
    /// callback a real <c>IdleDeadline</c> (see <c>TimerBinding.RequestIdleCallback</c>), because a
    /// callback that receives no deadline throws on the first thing it does with the parameter.
    /// </remarks>
    private void RegisterObservationStubs(JSContext context, JSObject window)
    {
        if (window[(KeyString)"PerformanceObserver"] is not JSUndefined)
            return;

        var observerPrototype = new JSObject();
        observerPrototype.FastAddValue("observe", UndefinedFunction("observe", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        observerPrototype.FastAddValue("disconnect", UndefinedFunction("disconnect", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        observerPrototype.FastAddValue("takeRecords", new DomFunction((in _) => new JSArray(), "takeRecords", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        var performanceObserver = new JSFunction((in _) =>
        {
            var instance = new JSObject();
            instance.FastAddValue("observe", UndefinedFunction("observe", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            instance.FastAddValue("disconnect", UndefinedFunction("disconnect", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            instance.FastAddValue("takeRecords", new DomFunction((in _) => new JSArray(), "takeRecords", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            return instance;
        }, "PerformanceObserver", 1);

        performanceObserver.FastAddValue("prototype", observerPrototype, JSPropertyAttributes.ConfigurableValue);

        // Feature detection reads this before observing, and an observer that claims to support
        // nothing is the honest answer for a capture that reports no entries.
        performanceObserver.FastAddValue("supportedEntryTypes", new JSArray(), JSPropertyAttributes.EnumerableConfigurableValue);

        window.FastAddValue("PerformanceObserver", performanceObserver, JSPropertyAttributes.EnumerableConfigurableValue);
        context["PerformanceObserver"] = performanceObserver;

        if (window[(KeyString)"requestIdleCallback"] is JSUndefined)
        {
            var requestIdle = new DomFunction((in a) => Dom.Features.TimerBinding.RequestIdleCallback(_eventLoop, _windowContext, in a), "requestIdleCallback", 1);
            window.FastAddValue("requestIdleCallback", requestIdle, JSPropertyAttributes.EnumerableConfigurableValue);
            context["requestIdleCallback"] = requestIdle;

            var cancelIdle = new DomFunction((in a) => Dom.Features.TimerBinding.CancelIdleCallback(_eventLoop, in a), "cancelIdleCallback", 1);
            window.FastAddValue("cancelIdleCallback", cancelIdle, JSPropertyAttributes.EnumerableConfigurableValue);
            context["cancelIdleCallback"] = cancelIdle;
        }
    }

    private void RegisterNavigatorObject(JSContext context, JSObject window)
    {
        // TODO-G3: navigator object with sendBeacon, userAgent, language, etc.
        var navigatorObj = new JSObject();
        // The same string the network sees, rather than a second copy of it: a page that compares
        // what it was told with what its own fetches report is entitled to one answer.
        navigatorObj.FastAddValue("userAgent", new JSString(Layout.Net.BroilerUserAgent.Value), JSPropertyAttributes.EnumerableConfigurableValue);
        navigatorObj.FastAddValue("language", new JSString("en-US"), JSPropertyAttributes.EnumerableConfigurableValue);
        navigatorObj.FastAddValue("languages", new JSArray([new JSString("en-US"), new JSString("en")]), JSPropertyAttributes.EnumerableConfigurableValue);
        navigatorObj.FastAddValue("cookieEnabled", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        navigatorObj.FastAddValue("onLine", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        navigatorObj.FastAddValue("platform", new JSString("Win32"), JSPropertyAttributes.EnumerableConfigurableValue);
        // Conforming and truthful: §8.9 allows exactly "", "Apple Computer, Inc." or "Google Inc.",
        // and Broiler's user agent does not claim to be Chrome. See NavigatorIdentityBinding.
        navigatorObj.FastAddValue("vendor", new JSString(""), JSPropertyAttributes.EnumerableConfigurableValue);

        // Who the browser is and what the machine underneath it has — the legacy identity constants
        // §8.9 mandates for every user agent, `webdriver`, and the measured hardware members. Takes
        // the same user-agent string registered above so `appVersion` cannot drift from `userAgent`.
        Dom.Features.NavigatorIdentityBinding.Install(navigatorObj, Layout.Net.BroilerUserAgent.Value);

        // sendBeacon(url, data) — queues a fire-and-forget POST via fetch semantics
        navigatorObj.FastAddValue("sendBeacon", new DomFunction((in a) => Dom.Features.BeaconBinding.Send(window, in a), "sendBeacon", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // What the host machine can do — javaEnabled, plugins/mimeTypes, getGamepads, getBattery,
        // requestMediaKeySystemAccess — and the legacy storage-quota pair, each answering "no" in
        // its interface's own vocabulary rather than throwing. See NavigatorCapabilityBinding and
        // StorageQuotaBinding.
        Dom.Features.NavigatorCapabilityBinding.Install(navigatorObj, context);
        Dom.Features.StorageQuotaBinding.Install(navigatorObj);

        // The object-valued surfaces that have a truthful answer: storage (zero usage, zero quota,
        // not persisted), permissions (denied, for every capability this engine gates) and
        // userAgentData (derived from the same user-agent string above). connection, mediaDevices
        // and mediaCapabilities stay absent — see NavigatorSurfacesBinding for each decision.
        Dom.Features.NavigatorSurfacesBinding.Install(navigatorObj, context, Layout.Net.BroilerUserAgent.Value);

        window.FastAddValue("navigator", navigatorObj, JSPropertyAttributes.EnumerableConfigurableValue);

        context["navigator"] = navigatorObj;
        context["postMessage"] = window[(KeyString)"postMessage"];
    }

    private void RegisterViewportObjects(JSContext context, JSObject window)
    {
        // TODO-G4: window.innerWidth / innerHeight
        var vpWidth = _viewportWidth;
        var vpHeight = _viewportHeight;

        window.FastAddProperty("innerWidth", new DomFunction((in _) => new JSNumber(vpWidth), "get innerWidth"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("innerHeight", new DomFunction((in _) => new JSNumber(vpHeight), "get innerHeight"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("outerWidth", new DomFunction((in _) => new JSNumber(vpWidth), "get outerWidth"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("outerHeight", new DomFunction((in _) => new JSNumber(vpHeight), "get outerHeight"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("scrollX", new DomFunction((in _) => new JSNumber(GetElementScrollOffset(DocumentElement, vertical: false)), "get scrollX"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("scrollY", new DomFunction((in _) => new JSNumber(GetElementScrollOffset(DocumentElement, vertical: true)), "get scrollY"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("pageXOffset", new DomFunction((in _) => new JSNumber(GetElementScrollOffset(DocumentElement, vertical: false)), "get pageXOffset"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("pageYOffset", new DomFunction((in _) => new JSNumber(GetElementScrollOffset(DocumentElement, vertical: true)), "get pageYOffset"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // The window's position on the screen (CSSOM View §4). Zero, and not as a placeholder: the
        // capture's viewport IS its screen — `screen.width`/`height` below are the viewport's own
        // dimensions — so the window is flush with the screen origin. `screenLeft`/`screenTop` are the
        // older spelling of the same pair and must agree with it. Absent, all four read `undefined`,
        // and the popup-positioning arithmetic that reads them (`screenX + (outerWidth - w) / 2`, the
        // standard centre-on-parent idiom) produced NaN rather than a coordinate.
        window.FastAddProperty("screenX", new DomFunction((in _) => new JSNumber(0), "get screenX"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("screenY", new DomFunction((in _) => new JSNumber(0), "get screenY"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("screenLeft", new DomFunction((in _) => new JSNumber(0), "get screenLeft"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        window.FastAddProperty("screenTop", new DomFunction((in _) => new JSNumber(0), "get screenTop"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // window.devicePixelRatio — physical pixels per CSS pixel. One, because that is what this
        // renderer does: it has no device-scale or backing-store-scale concept at all, so a CSS pixel
        // is a rendered pixel. (Page zoom is a separate axis and is reported by
        // `visualViewport.scale` below, which is where a page should read it.) Absent, the near-universal
        // `devicePixelRatio || 1` fallback happened to survive, but the equally common
        // `canvas.width = rect.width * devicePixelRatio` produced NaN and collapsed the canvas.
        window.FastAddProperty("devicePixelRatio", new DomFunction((in _) => new JSNumber(1), "get devicePixelRatio"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // The six BarProp objects. See WindowBarPropBinding for why every one reports not-visible.
        Dom.Features.WindowBarPropBinding.Install(window);

        // window.offscreenBuffering — a legacy Netscape-era property that survives on the Window
        // interface and is still read by old feature-detection preambles. It has no standard
        // definition left to satisfy; `true` is the value the reference engine reports, and the point
        // of having it at all is that the read yields a boolean rather than `undefined`. Grouped with
        // the geometry above because it is the last member of that same audited block.
        window.FastAddProperty("offscreenBuffering", new DomFunction((in _) => JSBoolean.True, "get offscreenBuffering"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // window scroll / scrollTo / scrollBy, co-located in the WindowScrollBinding feature module (Phase 3).
        window.FastAddValue("scroll", new DomFunction((in a) => Dom.Features.WindowScrollBinding.Scroll(this, in a), "scroll", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("scrollTo", new DomFunction((in a) => Dom.Features.WindowScrollBinding.ScrollTo(this, in a), "scrollTo", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("scrollBy", new DomFunction((in a) => Dom.Features.WindowScrollBinding.ScrollBy(this, in a), "scrollBy", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        // window addEventListener / removeEventListener / dispatchEvent, co-located in the
        // WindowEventTargetBinding feature module (Phase 3). These reach the global object — so
        // the idiomatic unqualified `addEventListener("load", …)` registers a window listener,
        // as it does in a browser — through MirrorWindowMembersOntoGlobal, which shares the
        // identical function objects so the two spellings address one listener store.
        window.FastAddValue("addEventListener", new DomFunction((in a) => Dom.Features.WindowEventTargetBinding.AddEventListener(this, in a), "addEventListener", 3), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("removeEventListener", new DomFunction((in a) => Dom.Features.WindowEventTargetBinding.RemoveEventListener(this, in a), "removeEventListener", 3), JSPropertyAttributes.EnumerableConfigurableValue);
        window.FastAddValue("dispatchEvent", new DomFunction((in a) => Dom.Features.WindowEventTargetBinding.DispatchEvent(this, in a), "dispatchEvent", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        _messaging.RegisterWindowMessaging(window);

        // `frames` is the one member registered twice with *different* shapes: a live getter on the
        // window and, historically, a static snapshot on the global for the unqualified spelling.
        // Now that the window IS the global the second write would simply overwrite the first,
        // freezing `frames` to whatever existed before any <iframe> was scripted. The accessor is
        // the correct one for both spellings, so it is the only registration.
        window.FastAddProperty("frames", new DomFunction((in _) => BuildWindowFramesArray(), "get frames"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // window.screen — basic stub for screen dimensions
        var screenObj = new JSObject();
        screenObj.FastAddValue("width", new JSNumber(vpWidth), JSPropertyAttributes.EnumerableConfigurableValue);
        screenObj.FastAddValue("height", new JSNumber(vpHeight), JSPropertyAttributes.EnumerableConfigurableValue);
        screenObj.FastAddValue("availWidth", new JSNumber(vpWidth), JSPropertyAttributes.EnumerableConfigurableValue);
        screenObj.FastAddValue("availHeight", new JSNumber(vpHeight), JSPropertyAttributes.EnumerableConfigurableValue);

        // The origin of the available area (CSSOM View §5). Zero for the same reason the avail sizes
        // above equal the full screen: nothing — no dock, no taskbar — is reserved out of a capture's
        // screen, so the available rectangle starts at the screen origin. They complete the pair the
        // avail sizes belong to; a page computing `availLeft + availWidth` was getting NaN.
        screenObj.FastAddValue("availLeft", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        screenObj.FastAddValue("availTop", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        screenObj.FastAddValue("colorDepth", new JSNumber(24), JSPropertyAttributes.EnumerableConfigurableValue);
        screenObj.FastAddValue("pixelDepth", new JSNumber(24), JSPropertyAttributes.EnumerableConfigurableValue);

        // screen.orientation — derived from the screen's own shape, so it stays consistent with the
        // width/height above rather than being a second, independent claim. See
        // ScreenOrientationBinding.
        screenObj.FastAddValue("orientation", Dom.Features.ScreenOrientationBinding.Build(vpWidth, vpHeight), JSPropertyAttributes.EnumerableConfigurableValue);

        window.FastAddValue("screen", screenObj, JSPropertyAttributes.EnumerableConfigurableValue);
        context["screen"] = screenObj;

        var visualViewport = new JSObject();
        _visualViewportJSObject = visualViewport;
        visualViewport.FastAddProperty("width", new DomFunction((in _) => new JSNumber(GetVisualViewportWidth()), "get width"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        visualViewport.FastAddProperty("height", new DomFunction((in _) => new JSNumber(GetVisualViewportHeight()), "get height"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        visualViewport.FastAddProperty("scale", new DomFunction((in _) => new JSNumber(GetVisualViewportScale()), "get scale"), new DomFunction((in a) => Dom.Features.WindowDocumentMiscBinding.SetVisualViewportScale(this, in a), "set scale"), JSPropertyAttributes.EnumerableConfigurableProperty);
        visualViewport.FastAddProperty("pageLeft", new DomFunction((in _) => new JSNumber(GetVisualViewportPageOffset(vertical: false)), "get pageLeft"), null, JSPropertyAttributes.EnumerableConfigurableProperty);
        visualViewport.FastAddProperty("pageTop", new DomFunction((in _) => new JSNumber(GetVisualViewportPageOffset(vertical: true)), "get pageTop"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // visualViewport addEventListener / removeEventListener (scroll), co-located in the
        // VisualViewportEventTargetBinding feature module (Phase 3).
        visualViewport.FastAddValue("addEventListener", new DomFunction((in a) => Dom.Features.VisualViewportEventTargetBinding.AddEventListener(this, in a), "addEventListener", 2), JSPropertyAttributes.EnumerableConfigurableValue);
        visualViewport.FastAddValue("removeEventListener", new DomFunction((in a) => Dom.Features.VisualViewportEventTargetBinding.RemoveEventListener(this, in a), "removeEventListener", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        window.FastAddValue("visualViewport", visualViewport, JSPropertyAttributes.EnumerableConfigurableValue);
        context["visualViewport"] = visualViewport;
    }

}
