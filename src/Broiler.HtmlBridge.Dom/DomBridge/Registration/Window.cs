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
        // its own deletion. The global's two members answer the areas of the document whose script is
        // running, because a frame's script reads them here as well (DomBridge/WebStorage.cs).
        InstallWebStorage(window);

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
        realm.DefineValue(location, "origin", JsValue.String(_pageOrigin));

        // The URL the page's Location and History share: a fragment navigation, pushState and a history
        // traversal move it, and with it the path and the query (DomBridge/SessionHistory.cs).
        _pageDocumentUrl = new Dom.Features.DocumentUrl(_pageUrl);
        var pageUrl = _pageDocumentUrl;
        realm.DefineAccessor(location, "pathname", (in _) => JsValue.String(pageUrl.IsAbsolute ? pageUrl.PathName : _pagePathName), null);
        realm.DefineAccessor(location, "search", (in _) => JsValue.String(pageUrl.IsAbsolute ? pageUrl.Search : _pageSearch), null);
        Dom.Features.LocationBinding.AddNavigationSurface(realm, location, _pageDocumentUrl, this);

        // The global's `location` is the Location of the document whose script is running -- this
        // one, or a frame's, since every document shares the global -- and it is [PutForwards=href]:
        // `location = url` and `window.location = url`, the commonest way a page sends itself on,
        // assign that Location's href and navigate. A data property, the assignment replaced the
        // Location with a string and nothing navigated.
        _topLocation = location;
        realm.DefineAccessor(window, "location",
            (in _) => CurrentLocation(),
            (in call) =>
            {
                call.Realm.SetProperty(CurrentLocation(), "href", call.Length > 0 ? call[0] : JsValue.Undefined);
                return JsValue.Undefined;
            });

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
        // in the TimerBinding feature module. The realm mints all six with their WebIDL arities --
        // setTimeout and setInterval take one required argument, the handler, as everything after
        // it is optional -- and the binding reads its arguments off the call frame. The event loop
        // they queue into holds the handles the realm minted.
        realm.DefineMethod(window, "setTimeout", 1, (in a) => Dom.Features.TimerBinding.SetTimeout(_eventLoop, _windowContext, in a));
        realm.DefineMethod(window, "clearTimeout", 1, (in a) => Dom.Features.TimerBinding.ClearTimeout(_eventLoop, in a));
        realm.DefineMethod(window, "setInterval", 1, (in a) => Dom.Features.TimerBinding.SetInterval(_eventLoop, _windowContext, in a));
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

    /// <summary>The timer functions exposed unqualified, mirroring their <c>window.*</c>
    /// counterparts, in the order they are set.</summary>
    private static readonly string[] TimerGlobalNames =
        ["setTimeout", "clearTimeout", "setInterval", "clearInterval", "requestAnimationFrame", "cancelAnimationFrame"];

    private void RegisterWindowGlobals(JsValue document, JsValue window, JsValue console, JsValue fetchFn)
    {
        var realm = Realm;
        var global = realm.Global;

        // The event interface objects (Event, CustomEvent, …) need no republishing: `window` IS the
        // global here, and the engine already defines them on it as non-configurable. The loop that
        // once redefined each as itself was always refused; JSeal 0.1.0-preview.2 reports a refused
        // define instead of ignoring it.
        realm.SetProperty(global, "window", window);

        // window.parent — uses the realm's global scope so that parent.X()
        // resolves user-defined globals (e.g. parent.notify() from sub-documents).
        var globalThis = realm.EvaluateHostScript("this", "probe:global-this");
        DefineWindowGlobal(window, "parent", globalThis);

        // window.self — refers to this window
        realm.DefineValue(window, "self", window);

        // window.opener -- null: no window here was opened by another's window.open. It is an accessor
        // an assignment replaces with what was assigned, as on a browser's window. It read undefined,
        // which a script comparing it with null tells apart.
        DefineOpener(realm, window);

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

        // document.defaultView — the window object, as the script that asks has it: a frame's script
        // reaching the page's document through `parent.document` gets the view of the top window it
        // has as `parent`, not the global object, which in its script answers for the frame.
        realm.DefineAccessor(document, "defaultView", (in _) => _subWindows.TopWindowAsSeen(), null);
        realm.SetProperty(global, "console", console);
        realm.SetProperty(global, "fetch", fetchFn);

        // Expose timer functions as globals (matching window.* counterparts)
        foreach (var name in TimerGlobalNames)
            realm.SetProperty(global, name, realm.GetProperty(window, name));

        // setImmediate is the engine's, the one Node has; Internet Explorer had one too, and no
        // browser does. A scheduler that finds it uses it -- React's looks for it before a
        // MessageChannel and takes it for every render -- so leaving it here decided how a page
        // schedules its work. And the engine's is no timer of the window's: it posts to the calling
        // thread's SynchronizationContext, not to the event loop above, and fails on a thread that
        // has none. The thread that settles the load window has none when it runs a script the page
        // inserted, and duckduckgo.com's React root scheduled its first render from there: a
        // ReferenceError ("Object reference not set to an instance of an object"), and a page with
        // nothing on it.
        realm.DeleteProperty(global, "setImmediate");

        // onload, onmessage and the rest answer for the window whose script is running
        // (DomBridge/WindowEventHandlers.cs).
        RegisterWindowEventHandlers(window);
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

        // Performance is an EventTarget: resourcetimingbufferfull is fired at it, and a page listens with
        // addEventListener as often as with the handler (DomBridge/ResourceTimings.cs).
        var eventTarget = realm.GetProperty(realm.Global, "EventTarget");
        var performanceObj = eventTarget.IsFunction ? realm.Construct(eventTarget) : realm.NewObject();
        realm.DefineValue(performanceObj, "timeOrigin", JsValue.Number(performanceTimeOrigin));
        realm.DefineMethod(performanceObj, "now", 0, (in c) => Dom.Features.WindowDocumentMiscBinding.PerformanceNow(performanceMonotonicOrigin, in c));

        // The document's PerformanceNavigationTiming entry. Its document-lifecycle marks are stamped by
        // the load sequence, which runs after this, so the entry reads them through this holder rather
        // than holding values. It shares the monotonic origin with performance.now() above, so a mark
        // and a now() reading are two points on one timeline. See NavigationTimingBinding for what the
        // entry does and does not carry.
        _navigationTiming = new Dom.Features.NavigationTimingState(performanceMonotonicOrigin);
        var navigationEntry = Dom.Features.NavigationTimingBinding.BuildEntry(
            realm, _pageUrl, _pageProtocol, _navigationTiming, fetchTiming);

        // performance.memory — the same MemoryInfo console.memory reports (built with the console in
        // RegisterWindowBasics, which runs first).
        if (!_memoryInfo.IsMissing)
            realm.DefineValue(performanceObj, "memory", _memoryInfo);

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

        // The Performance Timeline over this document's navigation entry and the fetches it makes:
        // performance.mark, measure, clearMarks, clearMeasures, the three entry getters, the resource
        // buffer's clearResourceTimings and setResourceTimingBufferSize, PerformanceObserver, and the
        // entry interfaces (DomBridge/PerformanceTimeline.cs, DomBridge/ResourceTimings.cs).
        //
        // getEntriesByName is one a page does not reach behind a feature test: performance and
        // PerformanceObserver both existing is the guard it writes. duckduckgo.com's
        // first-contentful-paint pixel opens with `performance.getEntriesByName('first-contentful-paint')`
        // on exactly that guard; no entry has that name here, so it still finds nothing and installs
        // its observer.
        InstallPerformanceTimeline(performanceObj, navigationEntry, performanceMonotonicOrigin);
    }

    /// <summary>
    /// <c>window.history</c> (HTML §7.2.3): the page's session history, over the URL its Location shows
    /// (DomBridge/SessionHistory.cs).
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
        BuildPageHistory(_pageDocumentUrl ??= new Dom.Features.DocumentUrl(_pageUrl));

        // The global's `history` is the History of the document whose script is running -- the page's, or
        // a frame's, since every document shares the global -- as its `location` is. A frame's pushState
        // must move the frame's URL, not the page's.
        realm.DefineAccessor(window, "history", (in _) => CurrentHistory(), null);
        realm.DefineAccessor(realm.Global, "history", (in _) => CurrentHistory(), null);
    }

    /// <summary>
    /// <c>requestIdleCallback</c> (Background Tasks §2), as the shape a page needs it to have rather
    /// than as a real idle period, which a headless event loop does not have to wait for.
    /// </summary>
    /// <remarks>
    /// <c>requestIdleCallback</c> runs its callback on a timer instead of dropping it, because
    /// what pages defer to idle is often the work that produces visible content — and it hands that
    /// callback a real <c>IdleDeadline</c> (see <c>TimerBinding.RequestIdleCallback</c>), because a
    /// callback that receives no deadline throws on the first thing it does with the parameter.
    /// <c>PerformanceObserver</c> is the Performance Timeline's (<see cref="InstallPerformanceTimeline"/>).
    /// </remarks>
    private void RegisterObservationStubs(JsValue window)
    {
        var realm = Realm;

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

        var navigatorObj = Dom.Features.NavigatorBinding.Install(
            realm,
            window,
            this,
            global::Broiler.Net.Http.BroilerUserAgent.Value,
            CookieAccess.CookiesEnabled);

        // navigator.userActivation, for the document whose script reads it (DomBridge/UserActivation.cs).
        InstallUserActivation(navigatorObj);

        DefineWindowGlobal(window, "navigator", navigatorObj);
        realm.SetProperty(realm.Global, "postMessage", realm.GetProperty(window, "postMessage"));
    }

    private void RegisterViewportObjects(JsValue window)
    {
        var realm = Realm;

        // Read when asked rather than when registered: a host whose window is resized sets the
        // viewport again (ViewportWidth), and the page's scripts measure the window it now shows.
        realm.DefineAccessor(window, "innerWidth", (in _) => JsValue.Number(_viewportWidth), null);
        realm.DefineAccessor(window, "innerHeight", (in _) => JsValue.Number(_viewportHeight), null);
        realm.DefineAccessor(window, "outerWidth", (in _) => JsValue.Number(_viewportWidth), null);
        realm.DefineAccessor(window, "outerHeight", (in _) => JsValue.Number(_viewportHeight), null);
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
        // handles, and the host contract converts nothing. Called bare in a frame's script, they are
        // the frame window's (TryGetFrameWindowForGlobalCall).
        realm.DefineMethod(window, "addEventListener", 3, (in c) => TryGetFrameWindowForGlobalCall(in c, out var frame)
            ? CallOnFrameWindow(frame, "addEventListener", in c)
            : Dom.Features.WindowEventTargetBinding.AddEventListener(this, in c));
        realm.DefineMethod(window, "removeEventListener", 3, (in c) => TryGetFrameWindowForGlobalCall(in c, out var frame)
            ? CallOnFrameWindow(frame, "removeEventListener", in c)
            : Dom.Features.WindowEventTargetBinding.RemoveEventListener(this, in c));
        realm.DefineMethod(window, "dispatchEvent", 1, (in c) => TryGetFrameWindowForGlobalCall(in c, out var frame)
            ? CallOnFrameWindow(frame, "dispatchEvent", in c)
            : Dom.Features.WindowEventTargetBinding.DispatchEvent(this, in c));

        _messaging.RegisterWindowMessaging(window);

        // Kept, because a frame's script has its own swapped onto the global, and a frame that posts to
        // `top` or `parent` posts through the top window's (SubWindowBinding.TopWindowAsSeen).
        _topPostMessage = realm.GetProperty(window, "postMessage");

        // `frames` (the page's frames, live, by index and by name), `length` and `name`. `frames` was
        // once registered twice with different shapes, a live getter on the window and a snapshot on
        // the global; the window IS the global, so one registration serves both spellings.
        _subWindows.InstallTopWindowMembers(window, () => _document);

        // window.screen and the Screen interface (CSSOM View §4). See ScreenBinding.
        var screenObj = Dom.Features.ScreenBinding.Install(realm, window, this);
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
