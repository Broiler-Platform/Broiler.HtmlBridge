using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.HtmlBridge.Net;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // The one MemoryInfo behind both console.memory and performance.memory. It is built with the
    // console in RegisterWindowBasics and read by RegisterPerformanceObject, which runs after it —
    // the two names report one object, as they do in Chrome. See PerformanceMemoryBinding.
    private JsValue _memoryInfo;

    private JsValue RegisterWindowBasics(JsValue document, JsValue window)
    {
        var realm = Realm;

        realm.DefineValue(window, "document", document);

        // window.localStorage / window.sessionStorage — the two Web Storage areas (HTML §12.2),
        // each an in-memory Storage object of its own. Built separately because they are separate
        // areas: a page that stashes per-tab state in one and durable state in the other must not
        // see the two answer each other's reads.
        //
        // The storage areas are exotic objects: WebStorageBinding mints all six members of each through
        // the realm onto an area whose handler completes its own lookup and, through IJsExoticDelete,
        // its own deletion.
        realm.DefineValue(window, "localStorage", Dom.Features.WebStorageBinding.BuildStorage(realm));
        realm.DefineValue(window, "sessionStorage", Dom.Features.WebStorageBinding.BuildStorage(realm));

        // window.matchMedia(query) — evaluates basic media queries. The realm mints the function
        // with the same name, arity and non-constructable shape it had, and the binding builds its
        // MediaQueryList through the realm.
        realm.DefineMethod(window, "matchMedia", 1, (in a) => Dom.Features.MatchMediaBinding.MatchMedia(this, in a));

        // window.location — the URL components here, and the navigation surface (`href`, `hash`,
        // assign, replace, reload, toString) from LocationBinding. The components alone made
        // `location.replace(url)` a TypeError ("undefined is not a function") which aborts the rest
        // of the calling function, so they are built together: see LocationBinding for what the
        // five do in a capture, why only a fragment navigation happens, and why the rest do not.
        //
        // `hash` is not among the components below — the binding owns it, because a fragment
        // navigation moves it and `href` together. `this` goes in as the hashchange target: it is
        // the top-level window, and the one whose listeners a page's hash routing registers on.
        var location = realm.NewObject();
        realm.DefineValue(location, "protocol", JsValue.String(_pageProtocol));
        realm.DefineValue(location, "host", JsValue.String(_pageHost));
        realm.DefineValue(location, "hostname", JsValue.String(_pageHostName));
        realm.DefineValue(location, "port", JsValue.String(_pagePort));
        realm.DefineValue(location, "pathname", JsValue.String(_pagePathName));
        realm.DefineValue(location, "search", JsValue.String(_pageSearch));
        realm.DefineValue(location, "origin", JsValue.String(_pageOrigin));
        Dom.Features.LocationBinding.AddNavigationSurface(realm, location, _pageUrl, this);

        realm.DefineValue(window, "location", location);

        // document.location is the *same* Location as window.location (HTML §3.1.5: the getter
        // returns this document's relevant global object's Location), so it is the one object
        // registered on both rather than a copy — pages compare the two, and `document.location`
        // is the spelling half of them reach for. Undefined here, it did not read as a missing
        // property but threw "Cannot get property protocol of undefined" out of the first script
        // to ask an origin of it, which aborts the whole <script>. That is the same One-Google-bar
        // bundle `top` above died in — `dF=function(){var a=document.location;return
        // a.protocol+"//"+a.host}` is how it builds its own origin — so it is the next thing that
        // failed once `top` resolved.
        realm.DefineValue(document, "location", location);

        // window timers / animation frames — thin adapters over the BrowserEventLoop, co-located
        // in the TimerBinding feature module. The realm mints all six with the names and
        // arities they had, and the binding reads its arguments off the call frame. The event loop
        // they queue into holds the handles the realm minted.
        realm.DefineMethod(window, "setTimeout", 2, (in a) => Dom.Features.TimerBinding.SetTimeout(_eventLoop, _windowContext, in a));
        realm.DefineMethod(window, "clearTimeout", 1, (in a) => Dom.Features.TimerBinding.ClearTimeout(_eventLoop, in a));
        realm.DefineMethod(window, "setInterval", 2, (in a) => Dom.Features.TimerBinding.SetInterval(_eventLoop, _windowContext, in a));
        realm.DefineMethod(window, "clearInterval", 1, (in a) => Dom.Features.TimerBinding.ClearInterval(_eventLoop, in a));
        realm.DefineMethod(window, "requestAnimationFrame", 1, (in a) => Dom.Features.TimerBinding.RequestAnimationFrame(_eventLoop, _windowContext, in a));
        realm.DefineMethod(window, "cancelAnimationFrame", 1, (in a) => Dom.Features.TimerBinding.CancelAnimationFrame(_eventLoop, in a));

        // window.alert(msg) — logs to debug output. The realm mints it with the name, arity and
        // non-constructable shape it had, and the binding coerces its message through the realm.
        realm.DefineMethod(window, "alert", 1, Dom.Features.WindowDocumentMiscBinding.Alert);

        // btoa / atob — the WindowOrWorkerGlobalScope base64 pair (HTML §8.3), co-located in the
        // Base64Binding feature module. The window IS the global object, so registering here is
        // what makes the unqualified `atob(…)` a page writes resolve as well. The binding raises its
        // InvalidCharacterError through the realm rather than being handed a context to raise it
        // against.
        realm.DefineMethod(window, "btoa", 1, Dom.Features.Base64Binding.Btoa);
        realm.DefineMethod(window, "atob", 1, Dom.Features.Base64Binding.Atob);

        // console object (shared between window.console and global console)
        var console = Dom.Features.ConsoleBinding.Build(realm);

        // console.memory — the same MemoryInfo shape performance.memory reports, and in Chrome the
        // same object. Kept as one object here too, so a page that samples both does not have to
        // reconcile two answers taken a moment apart. See PerformanceMemoryBinding.
        _memoryInfo = Dom.Features.PerformanceMemoryBinding.Build(realm);
        realm.DefineValue(console, "memory", _memoryInfo);

        realm.DefineValue(window, "console", console);

        return console;
    }

    /// <summary>Registers a window member and its unqualified spelling — the pair every global below
    /// is written as.</summary>
    /// <remarks>
    /// <c>window</c> and the global are one object under this engine
    /// (<see cref="JsCapabilities.GlobalIsVariableScope"/>), so the two writes land on the same object,
    /// exactly as they did when one went through the context and one through the window. Both are kept
    /// because a realm that separated them would still need both. <paramref name="window"/> stays a
    /// parameter rather than becoming <c>realm.Global</c>, because
    /// <c>DomBridgeUtils.MirrorWindowMembersOntoGlobal</c> branches on the two handles being distinct.
    /// </remarks>
    private void DefineWindowGlobal(JsValue window, string name, JsValue value)
    {
        var realm = Realm;
        realm.DefineValue(window, name, value);
        realm.SetProperty(realm.Global, name, value);
    }

    /// <summary>The event interface objects the window republishes from the global, in the order
    /// they are defined — define order fixes own-property enumeration order.</summary>
    private static readonly string[] EventConstructorNames =
        ["Event", "CustomEvent", "MouseEvent", "FocusEvent", "KeyboardEvent", "WheelEvent", "UIEvent", "InputEvent"];

    /// <summary>The timer functions exposed unqualified, mirroring their <c>window.*</c>
    /// counterparts, in the order they are set.</summary>
    private static readonly string[] TimerGlobalNames =
        ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "requestAnimationFrame", "cancelAnimationFrame"];

    private void RegisterWindowGlobals(JsValue document, JsValue window, JsValue console, JsValue fetchFn)
    {
        var realm = Realm;
        var global = realm.Global;

        realm.SetProperty(global, "window", window);
        foreach (var name in EventConstructorNames)
            realm.DefineValue(window, name, realm.GetProperty(global, name));

        // window.parent — uses the realm's global scope so that parent.X()
        // resolves user-defined globals (e.g. parent.notify() from sub-documents).
        var globalThis = realm.EvaluateHostScript("this", "probe:global-this");
        DefineWindowGlobal(window, "parent", globalThis);

        // window.self — refers to this window
        realm.DefineValue(window, "self", window);

        // window.top — the topmost browsing context. This document is the top-level one, so
        // `top`, `parent` and `self` are all this window; a sub-document's window instead gets
        // a `top` pointing back here (SubWindowBinding). It was the one member of that trio
        // never registered, and because `window` IS the global object that made the unqualified
        // `top` a ReferenceError rather than merely an undefined property — which aborts the
        // whole <script>, not just the statement that read it. Framing checks spell it
        // unqualified (`if (top != self)`), so this is the first thing a page's boilerplate
        // touches: google.com's One-Google-bar bundle died on it, taking with it every listener
        // the rest of that script would have registered.
        DefineWindowGlobal(window, "top", globalThis);

        // document.defaultView — returns the window object
        realm.DefineValue(document, "defaultView", window);
        realm.SetProperty(global, "console", console);
        realm.SetProperty(global, "fetch", fetchFn);

        // Expose timer functions as globals (matching window.* counterparts)
        foreach (var name in TimerGlobalNames)
            realm.SetProperty(global, name, realm.GetProperty(window, name));
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

    private void RegisterPerformanceObject(JsValue window)
    {
        var realm = Realm;

        // ---------------------------------------------------------------
        //  Google Search Compliance — critical polyfills
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
        var performanceObj = realm.NewObject();
        realm.DefineValue(performanceObj, "timeOrigin", JsValue.Number(performanceTimeOrigin));
        realm.DefineMethod(performanceObj, "now", 0, (in c) => Dom.Features.WindowDocumentMiscBinding.PerformanceNow(performanceMonotonicOrigin, in c));

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
            realm, performanceObj, _pageUrl, _pageProtocol, _navigationTiming, fetchTiming);

        // performance.memory — the same MemoryInfo console.memory reports (built with the console in
        // RegisterWindowBasics, which runs first).
        if (!_memoryInfo.IsMissing)
            realm.DefineValue(performanceObj, "memory", _memoryInfo);

        // performance.mark() / performance.measure() — no-op stubs
        realm.DefineValue(performanceObj, "mark", UndefinedMember("mark", 1));
        realm.DefineValue(performanceObj, "measure", UndefinedMember("measure", 3));

        // The clear/resize counterparts to mark, measure and the resource buffer. Nothing is recorded
        // for them to clear, but a page that marks commonly clears in the same breath, and the throw
        // would land on the clear rather than on the mark it pairs with.
        realm.DefineValue(performanceObj, "clearMarks", UndefinedMember("clearMarks", 1));
        realm.DefineValue(performanceObj, "clearMeasures", UndefinedMember("clearMeasures", 1));
        realm.DefineValue(performanceObj, "clearResourceTimings", UndefinedMember("clearResourceTimings", 0));
        realm.DefineValue(performanceObj, "setResourceTimingBufferSize", UndefinedMember("setResourceTimingBufferSize", 1));

        // toJSON is how the interface serialises, and telemetry that ships timings reaches it through
        // JSON.stringify(performance) as often as by name.
        realm.DefineMethod(
            performanceObj,
            "toJSON",
            0,
            (in _) =>
            {
                var json = realm.NewObject();
                realm.DefineValue(json, "timeOrigin", JsValue.Number(performanceTimeOrigin));
                return json;
            });

        DefineWindowGlobal(window, "performance", performanceObj);
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
    private void RegisterHistoryObject(JsValue window)
    {
        var realm = Realm;
        var history = realm.NewObject();

        realm.DefineValue(history, "length", JsValue.Number(1));
        realm.DefineValue(history, "state", JsValue.Null);
        realm.DefineValue(history, "scrollRestoration", JsValue.String("auto"));

        // pushState/replaceState record the state the page hands them, because a page that writes
        // one commonly reads it straight back; neither changes the document's URL, which a capture
        // has no way to honour.
        realm.DefineMethod(history, "pushState", 3, (in c) => StoreHistoryState(history, in c));
        realm.DefineMethod(history, "replaceState", 3, (in c) => StoreHistoryState(history, in c));

        realm.DefineValue(history, "back", UndefinedMember("back", 0));
        realm.DefineValue(history, "forward", UndefinedMember("forward", 0));
        realm.DefineValue(history, "go", UndefinedMember("go", 1));

        DefineWindowGlobal(window, "history", history);
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
    private void RegisterObservationStubs(JsValue window)
    {
        var realm = Realm;

        if (!realm.GetProperty(window, "PerformanceObserver").IsUndefined)
            return;

        var observerPrototype = realm.NewObject();
        realm.DefineValue(observerPrototype, "observe", UndefinedMember("observe", 1));
        realm.DefineValue(observerPrototype, "disconnect", UndefinedMember("disconnect", 0));
        realm.DefineMethod(observerPrototype, "takeRecords", 0, (in _) => realm.NewArray());

        var performanceObserver = realm.NewConstructor("PerformanceObserver", (in _) =>
        {
            var instance = realm.NewObject();
            realm.DefineValue(instance, "observe", UndefinedMember("observe", 1));
            realm.DefineValue(instance, "disconnect", UndefinedMember("disconnect", 0));
            realm.DefineMethod(instance, "takeRecords", 0, (in _) => realm.NewArray());
            return instance;
        }, 1);

        realm.DefineValue(performanceObserver, "prototype", observerPrototype, JsPropertyFlags.NonEnumerable);

        // Feature detection reads this before observing, and an observer that claims to support
        // nothing is the honest answer for a capture that reports no entries.
        realm.DefineValue(performanceObserver, "supportedEntryTypes", realm.NewArray());

        DefineWindowGlobal(window, "PerformanceObserver", performanceObserver);

        if (realm.GetProperty(window, "requestIdleCallback").IsUndefined)
        {
            var requestIdle = realm.NewMethod("requestIdleCallback", (in a) => Dom.Features.TimerBinding.RequestIdleCallback(_eventLoop, _windowContext, in a), 1);
            DefineWindowGlobal(window, "requestIdleCallback", requestIdle);

            var cancelIdle = realm.NewMethod("cancelIdleCallback", (in a) => Dom.Features.TimerBinding.CancelIdleCallback(_eventLoop, in a), 1);
            DefineWindowGlobal(window, "cancelIdleCallback", cancelIdle);
        }
    }

    private void RegisterNavigatorObject(JsValue window)
    {
        var realm = Realm;

        // TODO-G3: navigator object with sendBeacon, userAgent, language, etc.
        var navigatorObj = realm.NewObject();
        // The same string the network sees, rather than a second copy of it: a page that compares
        // what it was told with what its own fetches report is entitled to one answer.
        realm.DefineValue(navigatorObj, "userAgent", JsValue.String(Layout.Net.BroilerUserAgent.Value));
        realm.DefineValue(navigatorObj, "language", JsValue.String("en-US"));
        realm.DefineValue(navigatorObj, "languages", realm.NewArray([JsValue.String("en-US"), JsValue.String("en")]));
        realm.DefineValue(navigatorObj, "cookieEnabled", JsValue.True);
        realm.DefineValue(navigatorObj, "onLine", JsValue.True);
        realm.DefineValue(navigatorObj, "platform", JsValue.String("Win32"));
        // Conforming and truthful: §8.9 allows exactly "", "Apple Computer, Inc." or "Google Inc.",
        // and Broiler's user agent does not claim to be Chrome. See NavigatorIdentityBinding.
        realm.DefineValue(navigatorObj, "vendor", JsValue.String(""));

        // Who the browser is and what the machine underneath it has — the legacy identity constants
        // §8.9 mandates for every user agent, `webdriver`, and the measured hardware members. Takes
        // the same user-agent string registered above so `appVersion` cannot drift from `userAgent`.
        Dom.Features.NavigatorIdentityBinding.Install(realm, navigatorObj, Layout.Net.BroilerUserAgent.Value);

        // sendBeacon(url, data) — queues a fire-and-forget POST via fetch semantics
        realm.DefineMethod(navigatorObj, "sendBeacon", 2, (in a) => Dom.Features.BeaconBinding.Send(window, in a));

        // What the host machine can do — javaEnabled, plugins/mimeTypes, getGamepads, getBattery,
        // requestMediaKeySystemAccess — and the legacy storage-quota pair, each answering "no" in
        // its interface's own vocabulary rather than throwing. See NavigatorCapabilityBinding and
        // StorageQuotaBinding. Both take the realm: the DOMException the first rejects with is minted
        // through it, against the same global the script context reached.
        Dom.Features.NavigatorCapabilityBinding.Install(realm, navigatorObj);
        Dom.Features.StorageQuotaBinding.Install(realm, navigatorObj);

        // The object-valued surfaces that have a truthful answer: storage (zero usage, zero quota,
        // not persisted), permissions (denied, for every capability this engine gates) and
        // userAgentData (derived from the same user-agent string above). connection, mediaDevices
        // and mediaCapabilities stay absent — see NavigatorSurfacesBinding for each decision.
        Dom.Features.NavigatorSurfacesBinding.Install(realm, navigatorObj, Layout.Net.BroilerUserAgent.Value);

        DefineWindowGlobal(window, "navigator", navigatorObj);
        realm.SetProperty(realm.Global, "postMessage", realm.GetProperty(window, "postMessage"));
    }

    private void RegisterViewportObjects(JsValue window)
    {
        var realm = Realm;

        // TODO-G4: window.innerWidth / innerHeight
        var vpWidth = _viewportWidth;
        var vpHeight = _viewportHeight;

        realm.DefineAccessor(window, "innerWidth", (in _) => JsValue.Number(vpWidth), null);
        realm.DefineAccessor(window, "innerHeight", (in _) => JsValue.Number(vpHeight), null);
        realm.DefineAccessor(window, "outerWidth", (in _) => JsValue.Number(vpWidth), null);
        realm.DefineAccessor(window, "outerHeight", (in _) => JsValue.Number(vpHeight), null);
        realm.DefineAccessor(window, "scrollX", (in _) => JsValue.Number(GetElementScrollOffset(DocumentElement, vertical: false)), null);
        realm.DefineAccessor(window, "scrollY", (in _) => JsValue.Number(GetElementScrollOffset(DocumentElement, vertical: true)), null);
        realm.DefineAccessor(window, "pageXOffset", (in _) => JsValue.Number(GetElementScrollOffset(DocumentElement, vertical: false)), null);
        realm.DefineAccessor(window, "pageYOffset", (in _) => JsValue.Number(GetElementScrollOffset(DocumentElement, vertical: true)), null);

        // The window's position on the screen (CSSOM View §4). Zero, and not as a placeholder: the
        // capture's viewport IS its screen — `screen.width`/`height` below are the viewport's own
        // dimensions — so the window is flush with the screen origin. `screenLeft`/`screenTop` are the
        // older spelling of the same pair and must agree with it. Absent, all four read `undefined`,
        // and the popup-positioning arithmetic that reads them (`screenX + (outerWidth - w) / 2`, the
        // standard centre-on-parent idiom) produced NaN rather than a coordinate.
        realm.DefineAccessor(window, "screenX", (in _) => JsValue.Number(0), null);
        realm.DefineAccessor(window, "screenY", (in _) => JsValue.Number(0), null);
        realm.DefineAccessor(window, "screenLeft", (in _) => JsValue.Number(0), null);
        realm.DefineAccessor(window, "screenTop", (in _) => JsValue.Number(0), null);

        // window.devicePixelRatio — physical pixels per CSS pixel. One, because that is what this
        // renderer does: it has no device-scale or backing-store-scale concept at all, so a CSS pixel
        // is a rendered pixel. (Page zoom is a separate axis and is reported by
        // `visualViewport.scale` below, which is where a page should read it.) Absent, the near-universal
        // `devicePixelRatio || 1` fallback happened to survive, but the equally common
        // `canvas.width = rect.width * devicePixelRatio` produced NaN and collapsed the canvas.
        realm.DefineAccessor(window, "devicePixelRatio", (in _) => JsValue.Number(1), null);

        // The six BarProp objects. See WindowBarPropBinding for why every one reports not-visible.
        Dom.Features.WindowBarPropBinding.Install(realm, window);

        // window.offscreenBuffering — a legacy Netscape-era property that survives on the Window
        // interface and is still read by old feature-detection preambles. It has no standard
        // definition left to satisfy; `true` is the value the reference engine reports, and the point
        // of having it at all is that the read yields a boolean rather than `undefined`. Grouped with
        // the geometry above because it is the last member of that same audited block.
        realm.DefineAccessor(window, "offscreenBuffering", (in _) => JsValue.True, null);

        // window scroll / scrollTo / scrollBy, co-located in the WindowScrollBinding feature module.
        // The reading that tells scrollTo(x, y) from scrollTo({ left, top }) is the one
        // the sub-window contract already performs, shared rather than copied.
        realm.DefineMethod(window, "scroll", 2, (in c) => Dom.Features.WindowScrollBinding.Scroll(this, in c));
        realm.DefineMethod(window, "scrollTo", 2, (in c) => Dom.Features.WindowScrollBinding.ScrollTo(this, in c));
        realm.DefineMethod(window, "scrollBy", 2, (in c) => Dom.Features.WindowScrollBinding.ScrollBy(this, in c));
        // window addEventListener / removeEventListener / dispatchEvent, co-located in the
        // WindowEventTargetBinding feature module. These reach the global object — so
        // the idiomatic unqualified `addEventListener("load", …)` registers a window listener,
        // as it does in a browser — through MirrorWindowMembersOntoGlobal, which shares the
        // identical function objects so the two spellings address one listener store. It holds
        // handles, and the host contract converts nothing.
        realm.DefineMethod(window, "addEventListener", 3, (in c) => Dom.Features.WindowEventTargetBinding.AddEventListener(this, in c));
        realm.DefineMethod(window, "removeEventListener", 3, (in c) => Dom.Features.WindowEventTargetBinding.RemoveEventListener(this, in c));
        realm.DefineMethod(window, "dispatchEvent", 1, (in c) => Dom.Features.WindowEventTargetBinding.DispatchEvent(this, in c));

        _messaging.RegisterWindowMessaging(window);

        // `frames` is the one member registered twice with *different* shapes: a live getter on the
        // window and, historically, a static snapshot on the global for the unqualified spelling.
        // Now that the window IS the global the second write would simply overwrite the first,
        // freezing `frames` to whatever existed before any <iframe> was scripted. The accessor is
        // the correct one for both spellings, so it is the only registration.
        realm.DefineAccessor(
            window, "frames",
            (in _) => BuildWindowFramesArray(), null);

        // window.screen — basic stub for screen dimensions
        var screenObj = realm.NewObject();
        realm.DefineValue(screenObj, "width", JsValue.Number(vpWidth));
        realm.DefineValue(screenObj, "height", JsValue.Number(vpHeight));
        realm.DefineValue(screenObj, "availWidth", JsValue.Number(vpWidth));
        realm.DefineValue(screenObj, "availHeight", JsValue.Number(vpHeight));

        // The origin of the available area (CSSOM View §5). Zero for the same reason the avail sizes
        // above equal the full screen: nothing — no dock, no taskbar — is reserved out of a capture's
        // screen, so the available rectangle starts at the screen origin. They complete the pair the
        // avail sizes belong to; a page computing `availLeft + availWidth` was getting NaN.
        realm.DefineValue(screenObj, "availLeft", JsValue.Number(0));
        realm.DefineValue(screenObj, "availTop", JsValue.Number(0));
        realm.DefineValue(screenObj, "colorDepth", JsValue.Number(24));
        realm.DefineValue(screenObj, "pixelDepth", JsValue.Number(24));

        // screen.orientation — derived from the screen's own shape, so it stays consistent with the
        // width/height above rather than being a second, independent claim. See
        // ScreenOrientationBinding.
        realm.DefineValue(screenObj, "orientation", Dom.Features.ScreenOrientationBinding.Build(realm, vpWidth, vpHeight));

        DefineWindowGlobal(window, "screen", screenObj);

        var visualViewport = realm.NewObject();
        // The visualViewport root (DomBridge.cs) is this handle as minted — no conversion to an
        // engine object happens here.
        VisualViewportHandle = visualViewport;
        realm.DefineAccessor(visualViewport, "width", (in _) => JsValue.Number(GetVisualViewportWidth()), null);
        realm.DefineAccessor(visualViewport, "height", (in _) => JsValue.Number(GetVisualViewportHeight()), null);
        // `scale` is the one accessor of the five that has a setter; it coerces the assigned value
        // through the realm, because `visualViewport.scale = "2"` is a page passing a string.
        realm.DefineAccessor(
            visualViewport, "scale",
            (in _) => JsValue.Number(GetVisualViewportScale()),
            (in c) => Dom.Features.WindowDocumentMiscBinding.SetVisualViewportScale(this, in c));
        realm.DefineAccessor(visualViewport, "pageLeft", (in _) => JsValue.Number(GetVisualViewportPageOffset(vertical: false)), null);
        realm.DefineAccessor(visualViewport, "pageTop", (in _) => JsValue.Number(GetVisualViewportPageOffset(vertical: true)), null);

        // visualViewport addEventListener / removeEventListener (scroll), co-located in the
        // VisualViewportEventTargetBinding feature module.
        realm.DefineMethod(visualViewport, "addEventListener", 2, (in a) => Dom.Features.VisualViewportEventTargetBinding.AddEventListener(this, in a));
        realm.DefineMethod(visualViewport, "removeEventListener", 2, (in a) => Dom.Features.VisualViewportEventTargetBinding.RemoveEventListener(this, in a));

        DefineWindowGlobal(window, "visualViewport", visualViewport);
    }

}

public sealed partial class DomBridge
{
    // The per-element Web Animations timeline (currentTime) lives in a per-bridge instance table
    // owned by the session's bridge. It is an element-keyed ConditionalWeakTable, so it GCs with the
    // element and the cloneNode copy (see CloneDomElement) is preserved. The AnimationObjectBinding
    // currentTime get/set feature callbacks are threaded the resolved AnimationRuntimeState by
    // BuildAnimation.
    private readonly ConditionalWeakTable<DomElement, AnimationRuntimeState> _animationRuntimeStates = [];

    private AnimationRuntimeState AnimationStateFor(DomElement element) =>
        _animationRuntimeStates.GetValue(element, static _ => new AnimationRuntimeState());

    private JsValue BuildAnimationList(DomElement? target)
    {
        var animations = new List<JsValue>();
        foreach (var element in Elements)
        {
            if (IsText(element) || IsComment(element))
                continue;
            if (target != null && !ReferenceEquals(element, target))
                continue;

            if (!TryGetAnimationProperties(element, out var animationShorthand, out var animationDelay))
                continue;

            EnsureAnimationCurrentTime(element, animationShorthand, animationDelay);
            animations.Add(BuildAnimation(element));
        }

        // The list's own storage, not a copy of it: NewArray takes a span and materialises the array
        // from it, which is the same one pass the engine's list constructor made.
        return Realm.NewArray(CollectionsMarshal.AsSpan(animations));
    }

    private bool TryGetAnimationProperties(
        DomElement element,
        out string? animationShorthand,
        out string? animationDelay)
    {
        animationShorthand = null;
        animationDelay = null;

        if (InlineStyle(element).TryGetValue("animation", out animationShorthand))
        {
            InlineStyle(element).TryGetValue("animation-delay", out animationDelay);
            return true;
        }

        var stylesheetProps = CollectStylesheetAnimationProperties(element);
        if (stylesheetProps == null)
            return false;

        var hasAnimation = stylesheetProps.TryGetValue("animation", out animationShorthand);
        stylesheetProps.TryGetValue("animation-delay", out animationDelay);
        return hasAnimation || stylesheetProps.ContainsKey("animation-name");
    }

    private void EnsureAnimationCurrentTime(
        DomElement element,
        string? animationShorthand,
        string? animationDelay)
    {
        if (AnimationStateFor(element).CurrentTimeMilliseconds.IsSet)
            return;

        double delaySec = 0;
        if (!string.IsNullOrWhiteSpace(animationDelay) &&
            CssAnimation.TryParseTime(animationDelay, out var delayOverride))
        {
            delaySec = delayOverride;
        }
        else if (!string.IsNullOrWhiteSpace(animationShorthand))
        {
            var durations = new List<double>();
            foreach (var part in CssAnimation.TokenizeShorthand(animationShorthand))
            {
                if (CssAnimation.TryParseTime(part, out var seconds))
                    durations.Add(seconds);
            }

            if (durations.Count >= 2)
                delaySec = durations[1];
        }

        var currentTimeMs = delaySec > 0 ? (delaySec * 1000.0) + 1.0 : Math.Abs(delaySec) * 1000.0;
        AnimationStateFor(element).CurrentTimeMilliseconds.Set(currentTimeMs);
    }

    /// <summary>
    /// One <c>Animation</c> object for <paramref name="element"/>: its <c>currentTime</c> accessor
    /// pair and the <c>ready</c> thenable.
    /// </summary>
    /// <remarks>
    /// The surface is the co-located AnimationObjectBinding feature module, written against
    /// JSEAL — so the object, its accessor pair and the two ready-promise methods are minted by the
    /// realm, which names the accessors "get/set currentTime" and makes every function
    /// non-constructable exactly as the bridge's own native-callable type did. currentTime reads and writes
    /// the element's per-bridge animation timeline; it is resolved once here (a stable
    /// ConditionalWeakTable identity for this element and bridge) and handed to the callbacks.
    /// </remarks>
    private JsValue BuildAnimation(DomElement element)
    {
        var realm = Realm;
        var animation = realm.NewObject();
        var animationState = AnimationStateFor(element);
        realm.DefineAccessor(
            animation,
            "currentTime",
            (in c) => Dom.Features.AnimationObjectBinding.GetCurrentTime(animationState, in c),
            (in c) => Dom.Features.AnimationObjectBinding.SetCurrentTime(animationState, in c));

        var ready = realm.NewObject();
        realm.DefineMethod(ready, "then", 1, (in c) => Dom.Features.AnimationObjectBinding.Then(ready, in c));
        realm.DefineMethod(ready, "catch", 1, (in _) => ready);

        realm.DefineValue(animation, "ready", ready);
        return animation;
    }
}
