using System;
using System.Linq;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The co-located nested-browsing-context <c>window</c> (sub-window) feature. Owns the
/// sub-window JS object built for an <c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c>/<c>&lt;frame&gt;</c> —
/// its <c>document</c>/<c>location</c>/<c>self</c>/<c>window</c>/<c>parent</c>/<c>top</c> wiring, the
/// scroll surface (<c>scrollX</c>/<c>scrollY</c>/<c>pageXOffset</c>/<c>pageYOffset</c> +
/// <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c>), the mirrored event constructors, and its own
/// <c>getComputedStyle</c> — plus the sub-window-scoped helpers (location href, scroll offset read/write,
/// scrolling-element and parent-window resolution).
/// </summary>
/// <remarks>
/// <para>
/// The state authority (JS-object identity, location/base-URL caches) is
/// <see cref="BrowsingContextManager"/>, which the module holds a reference to (as it does the shared
/// <see cref="EventTargetRegistry"/> and <see cref="MessagingBinding"/> it installs on the sub-window).
/// Everything else — the sub-document builder it wraps, sub-resource URL resolution, scroll geometry,
/// computed style, the global constructors — is reached through the narrow <see cref="ISubWindowHost"/>
/// contract, so no callback touches an arbitrary bridge private field.
/// </para>
/// <para>
/// The window object is minted and populated through the realm, so nothing here spells a member
/// installation in engine terms, and NO collaborator holds it as an engine value. The four
/// collaborators the window is handed to below (the browsing-context cache, whose sub-window maps
/// key on <c>JsValue</c>, the event-target registry, and the messaging module twice) all file the
/// one handle the realm minted, so <c>frames[0].window</c> is one object. Nothing in this file
/// converts anything.
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
    /// A sub-window carrying <c>document</c>, <c>location</c> and the event constructors and
    /// nothing else would answer <c>undefined</c> for <c>iframe.contentWindow.String</c> — and for
    /// <c>Object</c>, <c>Array</c>, <c>Function</c>, <c>JSON</c>, <c>Math</c>, every one of them. In a
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
        // Event constructors.
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

    /// <summary>Gets or builds the sub-window for a nested-browsing-context container.</summary>
    /// <remarks>
    /// It answered an engine object until its callers stopped wanting one, and the last of the
    /// three was <c>DomBridge/Lifecycle.cs</c>: it collected windows into the list
    /// <c>window.frames</c> was built from, and that list held engine values for exactly as long as
    /// the array did. The array is minted through the realm now, so all three callers take the
    /// handle as it stands and nothing converts what this returns.
    /// </remarks>
    public JsValue GetOrCreate(DomElement containerElement) => Build(containerElement);

    /// <summary>Gets or builds the sub-window JS object for a nested-browsing-context container.</summary>
    private JsValue Build(DomElement containerElement)
    {
        if (_browsingContexts.TryGetSubWindow(containerElement, out var cached))
            return cached;

        var realm = _host.Realm;
        var subDocument = _host.GetOrCreateSubDocument(containerElement);

        // Building the sub-document runs the frame's scripts, and they reach back for this window
        // before it exists (DomBridge.ExecuteSubDocumentScripts): that re-entrant call has built and
        // cached it by now. It is the window the scripts ran against -- their `message` listeners,
        // the context their timers run in and the `source` of every message they post are all
        // filed under it -- so it is the one that stays. A second one minted here replaced it in the
        // cache, and `contentWindow` answered a window the frame had never seen: a page's
        // `frame.contentWindow.postMessage(...)` reached no listener, a message from the frame
        // carried a `source` that was not the frame's `contentWindow`, and what its script set on
        // `window` could not be read through it. reCAPTCHA's frame waits for the page to hand it a
        // MessagePort that way, and gave up after five seconds.
        if (_browsingContexts.TryGetSubWindow(containerElement, out var builtByTheFramesScripts))
        {
            // What the scripts declared was recorded after that window was built.
            _host.PublishPendingSubDocumentGlobals(containerElement, builtByTheFramesScripts);
            return builtByTheFramesScripts;
        }

        // A frame that navigated shows its new document in the window it had, emptied when the old
        // document went (DomBridge.InvalidateCachedSubDocument): a browsing context keeps its
        // WindowProxy, so a `contentWindow` the page kept is still the frame's window, and
        // `frame.contentWindow === kept` holds across the navigation. A new object here answered
        // `false`, and the kept one stayed on the old document.
        var window = _browsingContexts.TryTakeRetiredSubWindow(containerElement, out var retired)
            ? retired
            : realm.NewObject();

        // All four take the handle, so the one object the realm minted above is what the sub-window
        // identity cache, the owner-window map and both messaging installations file, which is what
        // `frame.contentWindow === frame.contentWindow` and the owner-window lookup both depend on.
        // The cache keys on the handle (Runtime/BrowsingContextManager.cs) and refuses one that is
        // not an object.
        _browsingContexts.SetSubWindow(containerElement, window);
        _eventTargets.SetOwnerWindow(window, window);
        _messaging.InstallEventTargetApi(window, "DomBridge.subWindow.dispatchEvent");
        _messaging.RegisterWindowMessaging(window);

        // The document is the frame's own to hand out: a script whose document is cross-origin to it
        // gets a SecurityError, as reading `document` off a cross-origin WindowProxy does. Such a script
        // is handed the frame's cross-origin window rather than this one by contentWindow, frames and a
        // message's `source`; this is the same answer for this window reached another way -- a reference
        // kept from before the frame navigated. Loaded first, so what is judged is what it shows.
        realm.DefineAccessor(window, "document",
            (in call) =>
            {
                var document = _host.GetOrCreateSubDocument(containerElement);
                if (_host.IsWindowCrossOriginToCurrentScript(window))
                    throw call.Realm.DomError("SecurityError", "Blocked a frame from accessing a cross-origin frame.");
                return document;
            },
            null);

        // The frame's Location is built the same way the top-level one is — components and the
        // navigation methods together, because a framed page calls location.replace() as readily
        // as a top-level one and a missing method is a TypeError that takes its caller with it.
        //
        // The realm builder, which is now the only one. Its predecessor took no realm and answered
        // an engine object this line converted back; both are gone, and with them the second
        // navigation surface that existed only to install the same six members in engine terms.
        // Held in a local because the frame's DOCUMENT shares this exact object with its window,
        // below -- two DefineValue calls over one Location, not two Locations.
        //
        // It has a host now: a navigation loads another document into the frame (DomBridge's
        // FrameNavigation). And `location` itself is [PutForwards=href]: `frames[0].location = url`, the
        // commonest way a page drives a frame, assigns the frame's href rather than overwriting the
        // window's property.
        var locationHref = GetSubWindowLocationHref(containerElement);
        var iframeLocation = LocationBinding.Build(realm, locationHref, _host.FrameLocationHost(containerElement));
        DefineForwardedLocation(realm, window, iframeLocation);

        realm.DefineAccessor(window, "scrollX",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: false)), null);
        realm.DefineAccessor(window, "scrollY",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: true)), null);
        realm.DefineAccessor(window, "pageXOffset",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: false)), null);
        realm.DefineAccessor(window, "pageYOffset",
            (in _) => JsValue.Number(GetSubWindowScrollOffset(containerElement, vertical: true)), null);

        realm.DefineMethod(window, "scroll", 2, (in call) => Scroll(containerElement, in call));
        realm.DefineMethod(window, "scrollTo", 2, (in call) => ScrollTo(containerElement, in call));
        realm.DefineMethod(window, "scrollBy", 2, (in call) => ScrollBy(containerElement, in call));

        realm.DefineValue(window, "self", window);
        realm.DefineValue(window, "window", window);

        realm.DefineValue(window, "globalThis", window);

        // The frame's browsing-context name, which it was given by its container's `name` attribute
        // when its window was first made and may change itself. It survives the frame navigating,
        // as a browsing context's name does, so it is kept per container rather than on this object.
        _browsingContexts.NameAtCreation(containerElement);
        realm.DefineAccessor(window, "name",
            (in _) => JsValue.String(_browsingContexts.NameOf(containerElement)),
            (in call) => SetName(containerElement, in call));

        // The frame's own frames, by index and by name, and how many there are.
        var frames = NewFrameList(() => _host.GetContentDocument(containerElement));
        DefineReplaceable(realm, window, "frames", (in _) => frames);
        DefineReplaceable(realm, window, "length",
            (in _) => JsValue.Number(ChildFrameContainers(_host.GetContentDocument(containerElement)).Count));

        foreach (var ctorName in MirroredGlobals)
        {
            if (_host.TryGetGlobal(ctorName, out var ctor))
                realm.DefineValue(window, ctorName, ctor);
        }

        // The frame's parent and the top window, as the script that asks may have them: the page's own
        // script gets the global object, which is the top window, and a frame's script a view of it
        // (TopWindowAsSeen). `parent` is [Replaceable], as on any window.
        DefineReplaceable(realm, window, "parent", (in _) => ParentAsSeen(containerElement));
        realm.DefineAccessor(window, "top", (in _) => TopWindowAsSeen(), null);

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
        DefineForwardedLocation(realm, subDocument, iframeLocation);

        // window.getComputedStyle — sub-window needs its own copy so that
        // doc.defaultView.getComputedStyle(node, "") resolves CSS rules from
        // the sub-document's <style> elements rather than the main document.
        realm.DefineMethod(window, "getComputedStyle", 2, (in call) => GetComputedStyle(in call));

        // Last, so the bridge's own members are already in place and win over a same-named
        // declaration: whatever the frame's scripts declared while the sub-document above was being
        // built now becomes reachable as frames[0].window.foo.
        _host.PublishPendingSubDocumentGlobals(containerElement, window);

        return window;
    }

    /// <summary>
    /// <c>location</c> on a window or a document: the Location, and assigning it assigns the Location's
    /// <c>href</c> (HTML's [PutForwards=href]), which navigates.
    /// </summary>
    internal static void DefineForwardedLocation(IJsRealm realm, JsValue target, JsValue location) =>
        realm.DefineAccessor(target, "location",
            (in _) => location,
            (in call) =>
            {
                call.Realm.SetProperty(location, "href", call.Length > 0 ? call[0] : JsValue.Undefined);
                return JsValue.Undefined;
            });

    // ── Frame names and frame lists ─────────────────────────────────────────

    /// <summary>
    /// Installs the top window's <c>frames</c>, <c>length</c> and <c>name</c> on
    /// <paramref name="window"/>, the realm's global object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>name</c> answers for the document whose script is running.</b> Every document here
    /// shares the one global object, so a frame's script that reads a bare <c>name</c>, or
    /// <c>this.name</c> at its top level -- Closure's <c>goog.global.name</c> -- reads it here, and
    /// what it means is its own window's name. <c>window.name</c> and <c>self.name</c> already reach
    /// the frame's window, and <c>top.name</c> and <c>parent.name</c> the page's, through the view of
    /// the top window a frame is handed (<see cref="TopWindowAsSeen"/>).
    /// </para>
    /// <para>
    /// <b><c>frames</c> and <c>length</c> do not.</b> A frame reaches its parent's frames as
    /// <c>parent.frames</c>, which is this object, and it has to find the parent's frames there.
    /// </para>
    /// </remarks>
    internal void InstallTopWindowMembers(JsValue window, Func<DomNode?> topDocument)
    {
        _topDocument = topDocument;
        var realm = _host.Realm;
        var frames = NewFrameList(topDocument);
        DefineReplaceable(realm, window, "frames", (in _) => frames);
        DefineReplaceable(realm, window, "length",
            (in _) => JsValue.Number(ChildFrameContainers(topDocument()).Count));

        realm.DefineAccessor(window, "name",
            (in _) => JsValue.String(CurrentFrame() is { } frame
                ? _browsingContexts.NameOf(frame)
                : _browsingContexts.TopName),
            (in call) =>
            {
                var name = NameArgument(in call);
                if (CurrentFrame() is { } frame)
                    _browsingContexts.SetName(frame, name);
                else
                    _browsingContexts.TopName = name;
                return JsValue.Undefined;
            });
    }

    /// <summary>
    /// Builds the window of every same-origin frame of <paramref name="document"/>, which runs each
    /// one's scripts: what <c>load</c> does before it fires, so no frame waits for a script to touch it.
    /// </summary>
    internal void BuildSameOriginFrameWindows(DomNode? document)
    {
        foreach (var container in ChildFrameContainers(document))
        {
            if (!_host.IsCurrentIframeCrossOrigin(container))
                Build(container);
        }
    }

    /// <summary>
    /// The child frames of <paramref name="document"/> -- its <c>&lt;iframe&gt;</c>s and
    /// <c>&lt;frame&gt;</c>s, in tree order. A frame's own frames are in its document, which is not
    /// part of this one's tree, so the walk never crosses into them.
    /// </summary>
    internal static List<DomElement> ChildFrameContainers(DomNode? document)
    {
        var containers = new List<DomElement>();
        if (document is null)
            return containers;

        foreach (var element in document.Descendants().OfType<DomElement>())
        {
            if (string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.TagName, "frame", StringComparison.OrdinalIgnoreCase))
            {
                containers.Add(element);
            }
        }

        return containers;
    }

    private JsValue NewFrameList(Func<DomNode?> document) =>
        _host.Realm.NewExotic(new WindowFrames(
            () => ChildFrameContainers(document()),
            WindowAsSeen,
            _browsingContexts.NameOf));

    /// <summary>The frame whose script is running, or <see langword="null"/> for the top document's.</summary>
    private DomElement? CurrentFrame() =>
        _host.CurrentSubWindow is { } window && _browsingContexts.TryGetSubWindowContainer(window, out var container)
            ? container
            : null;

    private JsValue SetName(DomElement container, in JsCall call)
    {
        _browsingContexts.SetName(container, NameArgument(in call));
        return JsValue.Undefined;
    }

    // The realm's ToString, as an assignment of any value to `name` stores its string.
    private static string NameArgument(in JsCall call) =>
        call.Realm.ToJsString(call.Length > 0 ? call[0] : JsValue.Undefined);

    /// <summary>
    /// Defines a <c>[Replaceable]</c> attribute: a getter, and a setter that replaces the accessor
    /// with a data property holding what was assigned. That is how <c>frames</c> and <c>length</c>
    /// are defined on a window, and it matters for the top window, which is the global object: a
    /// script's <c>var length = 3</c> assigns it, and must read back 3, not the number of frames.
    /// </summary>
    private static void DefineReplaceable(IJsRealm realm, JsValue target, string name, JsNativeFunction getter) =>
        realm.DefineAccessor(target, name, getter, (in call) =>
        {
            call.Realm.DefineValue(target, name, call.Length > 0 ? call[0] : JsValue.Undefined);
            return JsValue.Undefined;
        });

    // ── Windows as a script sees them ───────────────────────────────────────

    /// <summary>
    /// The window of the frame <paramref name="container"/> holds, as the script now running may
    /// have it: the window itself when its document is same-origin with the script's, and otherwise
    /// the frame's cross-origin view (<see cref="CrossOriginViewOf(DomElement)"/>).
    /// </summary>
    /// <remarks>
    /// What <c>contentWindow</c>, <c>frames[i]</c>, <c>frames[name]</c> and a cross-origin view's own
    /// <c>parent</c> and children answer. A cross-origin frame used to be <c>null</c> through the
    /// first and absent from the others, so a page could not start a conversation with a frame of
    /// another origin: it could only answer one, through the <c>source</c> of a message the frame
    /// sent first. reCAPTCHA's frames on any site but Google's own are such frames.
    /// </remarks>
    internal JsValue WindowAsSeen(DomElement container) =>
        _host.IsCurrentIframeCrossOrigin(container) ? CrossOriginViewOf(container) : Build(container);

    /// <summary>
    /// The cross-origin view of the frame that <paramref name="window"/> is the window of, or
    /// <paramref name="window"/> itself when it is not a frame's.
    /// </summary>
    internal JsValue CrossOriginViewOf(JsValue window) =>
        window.IsObject && _browsingContexts.TryGetSubWindowContainer(window, out var container)
            ? CrossOriginViewOf(container)
            : window;

    /// <summary>The frame a cross-origin view stands for, so a call made on the view reaches its window.</summary>
    internal bool TryGetViewedFrame(JsValue view, out DomElement container) =>
        _viewedFrames.TryGetValue(view, out container!);

    /// <summary>Forgets every view: the session is resetting, and the frames they stand for are gone.</summary>
    internal void ResetSession()
    {
        _crossOriginViews.Clear();
        _viewedFrames.Clear();
        _sameOriginTopView = JsValue.Missing;
        _crossOriginTopView = JsValue.Missing;
    }

    // One view per frame, so `frame.contentWindow === frame.contentWindow`, and the view a message
    // names as its `source` is the frame's `contentWindow`. Per container rather than per window, as a
    // browser's WindowProxy is one object for a frame whichever document it shows.
    private readonly Dictionary<DomElement, JsValue> _crossOriginViews = [];
    private readonly Dictionary<JsValue, DomElement> _viewedFrames = [];

    /// <summary>
    /// The window of the frame <paramref name="container"/> holds, as a script of another origin may
    /// have it: <c>window</c>, <c>self</c>, <c>frames</c>, <c>parent</c>, <c>top</c>, <c>opener</c>,
    /// <c>length</c>, <c>closed</c>, <c>close()</c>, <c>focus()</c>, <c>blur()</c>,
    /// <c>postMessage()</c>, its child frames by index and by name, and a <c>location</c> it may only
    /// navigate. Anything else throws a <c>SecurityError</c> (HTML §7.2.3.3).
    /// </summary>
    /// <remarks>
    /// The frame is loaded only when a member needs its window: a page that only compares the view,
    /// or keeps it, costs the frame nothing.
    /// </remarks>
    internal JsValue CrossOriginViewOf(DomElement container)
    {
        if (_crossOriginViews.TryGetValue(container, out var cached))
            return cached;

        var realm = _host.Realm;
        var view = JsValue.Missing;
        var methods = new Dictionary<string, JsValue>(StringComparer.Ordinal);

        JsValue Method(string name) =>
            methods.TryGetValue(name, out var method)
                ? method
                : methods[name] = name == "postMessage"
                    ? realm.NewMethod(name, (in call) => _messaging.PostMessageTo(Build(container), in call), 2)
                    : realm.NewMethod(name, static (in _) => JsValue.Undefined, 0);

        var lookup = realm.NewMethod("lookup", (in call) =>
        {
            var name = call.Realm.ToJsString(call[0]);
            switch (name)
            {
                case "window":
                case "self":
                case "frames":
                    return view;
                case "parent":
                    return ParentAsSeen(container);
                case "top":
                    return TopWindowAsSeen();
                case "opener":
                    return JsValue.Null;
                case "closed":
                    return JsValue.False;
                case "length":
                    return JsValue.Number(FramesIn(container).Count);
                case "location":
                    return LocationViewOf(container);
                case "postMessage":
                case "close":
                case "focus":
                case "blur":
                    return Method(name);
            }

            if (ChildAsSeen(container, name) is { } child)
                return child;

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 1);

        var assign = realm.NewMethod("assign", (in call) =>
        {
            // `location` is [PutForwards=href]: assigning the window's location navigates it.
            if (call.Realm.ToJsString(call[0]) == "location")
                return Navigate(container, "href", call[1]);

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 2);

        var keys = realm.NewMethod("keys", (in call) =>
        {
            var names = new List<JsValue>();
            foreach (var name in (string[])["window", "self", "location", "close", "closed", "focus", "blur",
                         "frames", "length", "top", "opener", "parent", "postMessage"])
                names.Add(JsValue.String(name));
            for (var i = 0; i < FramesIn(container).Count; i++)
                names.Add(JsValue.String(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return call.Realm.NewArray(names.ToArray());
        }, 0);

        view = CrossOriginWindowView.Build(realm, lookup, assign, keys);
        if (!view.IsObject)
            return JsValue.Null;

        _crossOriginViews[container] = view;
        _viewedFrames[view] = container;
        return view;
    }

    /// <summary>
    /// A cross-origin window's <c>location</c>: it can be navigated, by assigning <c>href</c> or
    /// calling <c>replace()</c>, and nothing about it can be read.
    /// </summary>
    /// <remarks>
    /// Navigating goes through the frame's own <c>Location</c>, so it does what navigating the frame
    /// does from inside it.
    /// </remarks>
    private JsValue LocationViewOf(DomElement container)
    {
        var realm = _host.Realm;
        JsValue replace = JsValue.Missing;

        var lookup = realm.NewMethod("lookup", (in call) =>
        {
            if (call.Realm.ToJsString(call[0]) == "replace")
                return replace.IsObject
                    ? replace
                    : replace = call.Realm.NewMethod("replace", (in replaceCall) => Navigate(container, "replace", replaceCall[0]), 1);

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 1);

        var assign = realm.NewMethod("assign", (in call) =>
        {
            if (call.Realm.ToJsString(call[0]) == "href")
                return Navigate(container, "href", call[1]);

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 2);

        var keys = realm.NewMethod("keys", (in call) =>
            call.Realm.NewArray([JsValue.String("href"), JsValue.String("replace")]), 0);

        return CrossOriginWindowView.Build(realm, lookup, assign, keys);
    }

    /// <summary>Navigates the frame through its own <c>Location</c>: <c>href</c> assigned, or <c>replace()</c> called.</summary>
    private JsValue Navigate(DomElement container, string how, JsValue url)
    {
        var realm = _host.Realm;
        var location = realm.GetProperty(Build(container), "location");
        if (!location.IsObject)
            return JsValue.Undefined;

        if (how == "href")
            realm.SetProperty(location, "href", url);
        else if (realm.GetProperty(location, "replace") is { IsFunction: true } replace)
            realm.Invoke(replace, location, [url]);

        return JsValue.Undefined;
    }

    /// <summary>The frames in the document of the frame <paramref name="container"/> holds, loading it if it is not yet.</summary>
    private List<DomElement> FramesIn(DomElement container)
    {
        Build(container);
        return ChildFrameContainers(_host.GetContentDocument(container));
    }

    /// <summary>
    /// The child frame of <paramref name="container"/>'s frame that <paramref name="name"/> names --
    /// by index, or by browsing-context name -- as the running script may have it, or
    /// <see langword="null"/>.
    /// </summary>
    private JsValue? ChildAsSeen(DomElement container, string name)
    {
        var children = FramesIn(container);
        if (uint.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
            index.ToString(System.Globalization.CultureInfo.InvariantCulture) == name)
        {
            return index < (uint)children.Count ? WindowAsSeen(children[(int)index]) : null;
        }

        if (name.Length == 0)
            return null;

        foreach (var child in children)
        {
            if (string.Equals(_browsingContexts.NameOf(child), name, StringComparison.Ordinal))
                return WindowAsSeen(child);
        }

        return null;
    }

    /// <summary>The parent of the frame <paramref name="container"/> holds, as the running script may have it.</summary>
    private JsValue ParentAsSeen(DomElement container)
    {
        var parentFrame = _host.GetFrameForContentDocument(DomBridgeUtils.GetOwningDocument(container));
        return parentFrame is null ? TopWindowAsSeen() : WindowAsSeen(parentFrame);
    }

    // ── The top window as a frame has it ────────────────────────────────────

    /// <summary>
    /// The top window as the script now running may have it: the global object for the page's own
    /// script, and for a frame's a view of it (<see cref="TopWindowAsSeenBy"/>).
    /// </summary>
    internal JsValue TopWindowAsSeen() => TopWindowAsSeenBy(_host.CurrentSubWindow ?? JsValue.Missing);

    /// <summary>
    /// The top window as the script of <paramref name="window"/> may have it: the global object
    /// itself unless <paramref name="window"/> is a frame's, and for a frame a view of the top window
    /// -- the whole window when the frame is same-origin with the page, and its cross-origin view
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The top window is the realm's global object, and so is every frame's.</b> Every document
    /// here shares one, and the global answers for the window whose script is running: its
    /// <c>location</c> is that window's Location, and a frame's script has its own <c>document</c>,
    /// <c>window</c> and <c>postMessage</c> swapped in. Handed the global as <c>top</c> or
    /// <c>parent</c>, a frame reached itself through them: <c>top.location</c> was the frame's own
    /// Location, so a frame-busting <c>top.location = self.location</c> reloaded the frame where a
    /// browser moves the tab, and <c>parent.document</c> was the frame's own document.
    /// </para>
    /// <para>
    /// <b>The view answers for the top window whichever script reads it.</b> It forwards to the
    /// global, so the page's functions and variables are there, but its window members are the
    /// page's: <c>location</c>, which navigates the page when assigned, <c>document</c>,
    /// <c>name</c>, <c>postMessage</c>, and itself as <c>window</c>, <c>self</c>, <c>top</c> and
    /// <c>parent</c>. One object per kind, so <c>top === parent</c> in a frame of the page, and a
    /// message the page posts names it as its <c>source</c>.
    /// </para>
    /// </remarks>
    internal JsValue TopWindowAsSeenBy(JsValue window)
    {
        if (!window.IsObject || !_browsingContexts.IsSubWindow(window))
            return _host.MainWindow;

        var view = _host.IsWindowCrossOriginToTop(window) ? CrossOriginTopView() : SameOriginTopView();
        return view.IsObject ? view : _host.MainWindow;
    }

    /// <summary>Whether <paramref name="value"/> is a view of the top window that a frame holds.</summary>
    internal bool IsTopView(JsValue value) =>
        value.IsObject && (value == _sameOriginTopView || value == _crossOriginTopView);

    private JsValue _sameOriginTopView;
    private JsValue _crossOriginTopView;

    // The top document, whose frames the top window's `frames` and `length` count.
    private Func<DomNode?>? _topDocument;

    // The top window's members a view answers itself, rather than the global object, which in a
    // frame's script answers for the frame.
    private static readonly string[] TopWindowOwnMembers =
        ["window", "self", "top", "parent", "globalThis", "location", "document", "name", "postMessage", "frameElement"];

    /// <summary>The top window as a frame of its origin has it: the whole window, through a forwarding view.</summary>
    private JsValue SameOriginTopView()
    {
        if (_sameOriginTopView.IsObject)
            return _sameOriginTopView;

        var realm = _host.Realm;
        var view = JsValue.Missing;
        var lookup = realm.NewMethod("lookup", (in call) => call.Realm.ToJsString(call[0]) switch
        {
            "location" => _host.TopLocation,
            "document" => _host.MainDocument,
            "name" => JsValue.String(_browsingContexts.TopName),
            "postMessage" => _host.TopPostMessage,
            "frameElement" => JsValue.Null,
            _ => view,
        }, 1);

        var assign = realm.NewMethod("assign", (in call) =>
        {
            switch (call.Realm.ToJsString(call[0]))
            {
                // [PutForwards=href]: assigning the window's location navigates it.
                case "location":
                    NavigateTop("href", call[1]);
                    break;
                case "name":
                    _browsingContexts.TopName = call.Realm.ToJsString(call[1]);
                    break;
            }

            // The others are the window itself, its document and its postMessage, which an
            // assignment does not replace.
            return JsValue.Undefined;
        }, 2);

        var names = new JsValue[TopWindowOwnMembers.Length];
        for (var i = 0; i < names.Length; i++)
            names[i] = JsValue.String(TopWindowOwnMembers[i]);

        view = TopWindowView.Build(realm, _host.MainWindow, realm.NewArray(names), lookup, assign);
        _sameOriginTopView = view;
        return view;
    }

    /// <summary>
    /// The top window as a frame of another origin has it: what <see cref="CrossOriginViewOf(DomElement)"/>
    /// lets a script reach of a frame, with the top window's own frames, and a <c>location</c> that may
    /// only be navigated -- and only after the user has activated the frame (<see cref="NavigateTop"/>).
    /// </summary>
    private JsValue CrossOriginTopView()
    {
        if (_crossOriginTopView.IsObject)
            return _crossOriginTopView;

        var realm = _host.Realm;
        var view = JsValue.Missing;
        var methods = new Dictionary<string, JsValue>(StringComparer.Ordinal);
        JsValue location = JsValue.Missing;

        JsValue Method(string name) =>
            methods.TryGetValue(name, out var method)
                ? method
                : methods[name] = name == "postMessage"
                    ? realm.NewMethod(name, (in call) => _messaging.PostMessageTo(_host.MainWindow, in call), 2)
                    : realm.NewMethod(name, static (in _) => JsValue.Undefined, 0);

        var lookup = realm.NewMethod("lookup", (in call) =>
        {
            var name = call.Realm.ToJsString(call[0]);
            switch (name)
            {
                case "window":
                case "self":
                case "frames":
                case "top":
                case "parent":
                    return view;
                case "opener":
                    return JsValue.Null;
                case "closed":
                    return JsValue.False;
                case "length":
                    return JsValue.Number(ChildFrameContainers(_topDocument?.Invoke()).Count);
                case "location":
                    return location.IsObject ? location : location = TopLocationView();
                case "postMessage":
                case "close":
                case "focus":
                case "blur":
                    return Method(name);
            }

            if (TopChildAsSeen(name) is { } child)
                return child;

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 1);

        var assign = realm.NewMethod("assign", (in call) =>
        {
            if (call.Realm.ToJsString(call[0]) == "location")
                return NavigateTop("href", call[1]);

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 2);

        var keys = realm.NewMethod("keys", (in call) =>
        {
            var names = new List<JsValue>();
            foreach (var name in (string[])["window", "self", "location", "close", "closed", "focus", "blur",
                         "frames", "length", "top", "opener", "parent", "postMessage"])
                names.Add(JsValue.String(name));
            for (var i = 0; i < ChildFrameContainers(_topDocument?.Invoke()).Count; i++)
                names.Add(JsValue.String(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return call.Realm.NewArray(names.ToArray());
        }, 0);

        view = CrossOriginWindowView.Build(realm, lookup, assign, keys);
        _crossOriginTopView = view;
        return view;
    }

    /// <summary>The top window's <c>location</c> as a frame of another origin has it: navigable, and nothing more.</summary>
    private JsValue TopLocationView()
    {
        var realm = _host.Realm;
        JsValue replace = JsValue.Missing;

        var lookup = realm.NewMethod("lookup", (in call) =>
        {
            if (call.Realm.ToJsString(call[0]) == "replace")
                return replace.IsObject
                    ? replace
                    : replace = call.Realm.NewMethod("replace", (in replaceCall) => NavigateTop("replace", replaceCall[0]), 1);

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 1);

        var assign = realm.NewMethod("assign", (in call) =>
        {
            if (call.Realm.ToJsString(call[0]) == "href")
                return NavigateTop("href", call[1]);

            throw CrossOriginWindowView.Refusal(call.Realm);
        }, 2);

        var keys = realm.NewMethod("keys", (in call) =>
            call.Realm.NewArray([JsValue.String("href"), JsValue.String("replace")]), 0);

        return CrossOriginWindowView.Build(realm, lookup, assign, keys);
    }

    /// <summary>
    /// Navigates the top window through its own <c>Location</c>, for a frame's script: <c>href</c>
    /// assigned, or <c>replace()</c> called.
    /// </summary>
    /// <remarks>
    /// A frame of another origin may move the whole page only once the user has activated it, as in
    /// Chromium: an advertisement's frame that sends the tab elsewhere on its own is the abuse that
    /// rule stops. Otherwise the request is logged and nothing happens.
    /// </remarks>
    private JsValue NavigateTop(string how, JsValue url)
    {
        if (!_host.MayNavigateTop())
        {
            Broiler.HtmlBridge.Logging.RenderLogger.LogWarning(Broiler.HtmlBridge.Logging.LogCategory.JavaScript, "DomBridge.location",
                "A frame of another origin asked to navigate the top window without the user having activated it; the page stays.");
            return JsValue.Undefined;
        }

        var realm = _host.Realm;
        var location = _host.TopLocation;
        if (!location.IsObject)
            return JsValue.Undefined;

        if (how == "href")
            realm.SetProperty(location, "href", url);
        else if (realm.GetProperty(location, "replace") is { IsFunction: true } replace)
            realm.Invoke(replace, location, [url]);

        return JsValue.Undefined;
    }

    /// <summary>The top window's child frame that <paramref name="name"/> names, by index or by name, as the running script may have it.</summary>
    private JsValue? TopChildAsSeen(string name)
    {
        var children = ChildFrameContainers(_topDocument?.Invoke());
        if (uint.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index) &&
            index.ToString(System.Globalization.CultureInfo.InvariantCulture) == name)
        {
            return index < (uint)children.Count ? WindowAsSeen(children[(int)index]) : null;
        }

        if (name.Length == 0)
            return null;

        foreach (var child in children)
        {
            if (string.Equals(_browsingContexts.NameOf(child), name, StringComparison.Ordinal))
                return WindowAsSeen(child);
        }

        return null;
    }

    private string GetSubWindowLocationHref(DomElement containerElement)
    {
        if (_browsingContexts.TryGetLocation(containerElement, out var cachedLocation) &&
            !string.IsNullOrWhiteSpace(cachedLocation))
        {
            return cachedLocation;
        }

        if (string.Equals(containerElement.TagName, "iframe", StringComparison.OrdinalIgnoreCase) &&
            DomBridgeUtils.HasAttr(containerElement, "srcdoc"))
            return "about:srcdoc";

        var resolvedUrl = _host.ResolveSubResourceUrl(DomBridgeUtils.GetSubResourceUrl(containerElement), _host.GetInheritedSubDocumentBaseUrl(containerElement));
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
        return document == null ? null : DomBridgeUtils.GetDocumentElement(document);
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
        var el = _host.FindElement(call[0]);
        // The realm's ToString, not the handle's: a page passing an object as the pseudo-element runs
        // its own toString here, which is the coercion the engine performed.
        var pseudoElement = call.Length > 1 ? call.Realm.ToJsString(call[1]) : null;
        return _host.BuildComputedStyleObject(el, pseudoElement);
    }
}
