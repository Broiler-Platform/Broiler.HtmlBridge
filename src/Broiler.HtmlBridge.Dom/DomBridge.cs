using System.Net;
using System.Runtime.CompilerServices;
using Broiler.JavaScript.Engine;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.Dom;
using Broiler.CSS.Dom;
using Broiler.CSS;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Registers a minimal <c>document</c> object on a <see cref="JSContext"/>
/// so that JavaScript executed against it can perform basic DOM queries
/// against the current page HTML.
/// </summary>
/// <remarks>
/// That parameter is what <c>IDomBridgeRuntime.Attach</c> hands over, and the interface lives in
/// <c>Broiler.HtmlBridge.Core</c>. It is not the last thing holding the bridge to one engine:
/// <c>DomBridge/Lifecycle.cs</c>, <c>Runtime/JsInterop.cs</c> and <c>BridgeModuleContext.cs</c>
/// say what else still does.
/// </remarks>
public sealed partial class DomBridge : IDomBridgeRuntime
{
    // Sub-resource HTTP and the local base path live in ResourceLoader, the single host resource
    // loader. Feature callbacks ask the loader instead of reaching a static HttpClient.
    private readonly Dom.Runtime.ResourceLoader _resources = new();
    private Dom.Features.WorkerBinding? _workers;
    // The whole MutationObserver feature — the observer registry (MutationObserverHub), the
    // observe()/disconnect() registration and childList/attribute/characterData record delivery —
    // lives in the MutationObserverBinding module, reached through the narrow IMutationObserverHost
    // contract (see DomBridge/Hosts.Window.cs).
    private readonly Dom.Features.MutationObserverBinding _mutations;
    // The DOM event dispatch engine — capture/target/bubble propagation, the event object's
    // propagation-control methods and composedPath() — lives in EventDispatchBinding, reached
    // through the narrow IEventDispatchHost contract (see DomBridge/Hosts.Window.cs).
    private readonly Dom.Features.EventDispatchBinding _eventDispatch;
    // The HTML table DOM interfaces (HTMLTableElement / HTMLTableSectionElement /
    // HTMLTableRowElement) live in TableBinding, reached through the narrow ITableHost contract
    // (see DomBridge/Hosts.Elements.cs).
    private readonly Dom.Features.TableBinding _tables;
    // The dialog / popover / details JS API (showModal/show/close/showPopover/hidePopover/open/
    // returnValue) lives in DialogBinding, reached through the narrow IDialogHost contract
    // (see DomBridge/Hosts.Elements.cs); backdrop/top-layer rendering stays in the bridge.
    private readonly Dom.Features.DialogBinding _dialogs;
    // HTMLSelectElement / HTMLOptionElement (add/options/selectedIndex/size/value +
    // option.defaultSelected/text) live in SelectBinding, reached through the narrow ISelectHost contract
    // (see DomBridge/Hosts.Elements.cs); the shared value property delegates its select branch to it.
    private readonly Dom.Features.SelectBinding _select;
    // HTMLFormElement (elements/length/action) and constraint validation (checkValidity/
    // reportValidity) live in FormBinding, reached through the narrow IFormHost contract
    // (see DomBridge/Hosts.Elements.cs).
    private readonly Dom.Features.FormBinding _forms;
    // The form-control IDL reflectors (value/checked/type/name/disabled/hidden/tabIndex/required)
    // live in FormControlBinding, reached through the narrow IFormControlHost contract
    // (see DomBridge/Hosts.Elements.cs).
    private readonly Dom.Features.FormControlBinding _formControl;
    // TreeWalker/NodeIterator/Range construction, every Range callback and the traversal-scoped
    // active-range / active-node-iterator registries live in the co-located TraversalBinding module.
    // The bridge holds the module through the narrow ITraversalHost contract it implements
    // (see DomBridge/Hosts.Nodes.cs).
    private readonly Dom.Features.TraversalBinding _traversal;
    private readonly DomDocument _document;
    // Per-element inline-style runtime state; reached via InlineStyleStateFor.
    private readonly ConditionalWeakTable<DomNode, InlineStyleRuntimeState> _inlineStyleStates = [];
    private JSContext? _jsContext;

    /// <summary>
    /// The <c>document</c> wrapper as a handle, or <see cref="JsValue.Missing"/> before one exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Missing rather than null or undefined</b> because "the bridge has not registered a document
    /// yet" is not a value any page can observe — every caller either tests it or coalesces it to the
    /// JavaScript value its own contract promises (<c>INodeAccessorsHost.DocumentWrapper</c> answers
    /// <c>null</c>, for instance). Choosing one of those here would hide the distinction from the
    /// other. Missing is also <c>default(JsValue)</c>, so a bridge that has not attached yet and one
    /// that has been disposed read the same way.
    /// </para>
    /// <para>
    /// <b>This is the root itself, not a view of one.</b> It used to wrap an engine-typed field on
    /// every read. A handle over an object carries that object, so the field and this were always one
    /// instance and <c>document === document</c> was never in question; what went is the field and
    /// the conversion.
    /// </para>
    /// </remarks>
    internal JsValue DocumentHandle { get; private set; }

    /// <inheritdoc cref="DocumentHandle"/>
    internal JsValue WindowHandle { get; private set; }

    /// <inheritdoc cref="DocumentHandle"/>
    internal JsValue VisualViewportHandle { get; private set; }

    /// <summary>
    /// Drops the three wrapper roots. Called from <see cref="Dispose"/>, which owns the teardown
    /// order; the fields are cleared here because this file declares them.
    /// </summary>
    private void ClearWrapperRoots()
    {
        DocumentHandle = JsValue.Missing;
        WindowHandle = JsValue.Missing;
        VisualViewportHandle = JsValue.Missing;
    }

    // The timer/interval/requestAnimationFrame/frame-action queues, their id counters and the drain
    // (FlushTimerStep/FlushTimers) live in BrowserEventLoop, the single owner of the document's task
    // queues. Built in the constructor rather than initialised in place because it takes the bridge's
    // JSEAL realm accessor — a queued page callback is invoked through the realm, which is adopted at
    // Attach and so does not exist when this field does.
    private readonly Dom.Runtime.BrowserEventLoop _eventLoop;
    // Runs the <script> elements the page's own JavaScript inserts — nothing did, so the loader
    // idiom (inject a <script src>, poll until the global it defines appears) never terminated. See
    // ScriptInsertionRunner; fed by the document.createElement funnel below.
    private readonly Dom.Runtime.ScriptInsertionRunner _scriptInsertion;
    // Smooth-scroll continuation tokens: a per-element monotonic marker so a queued smooth-scroll
    // frame action only commits if it is still the active scroll for that element. Touched by
    // scroll/frame-action callbacks that can run on ThreadPool threads, so keep it concurrent.
    private int _smoothScrollTokenCounter;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DomElement, int> _smoothScrollTokens = new();
    // The event-listener stores (per-node addEventListener listeners, window listeners, generic
    // JS-target listeners, target→owner-window map, and visual-viewport scroll listeners) live in
    // EventTargetRegistry, the single listener owner.
    private readonly Dom.Runtime.EventTargetRegistry _eventTargets = new();
    // The nested-browsing-context state — the per-container sub-document/sub-window JS-object
    // identity, location/base-URL caches, object-load-failure and onload-fired marks, the reverse
    // sub-window→container map, the current-window override, and the content-document maps — is
    // owned by the single BrowsingContextManager. The bridge keeps the algorithms.
    private readonly Dom.Runtime.BrowsingContextManager _browsingContexts = new();
    // The browsing-context window-resolution behaviour (canonicalise/resolve a window, and the
    // RunWithWindowContext global switch) is owned by WindowContextManager, reached through the
    // narrow IWindowContextHost contract (see DomBridge/Hosts.Window.cs), which also
    // keeps the thin delegators. It reads the sub-window state from _browsingContexts and _eventTargets.
    private readonly Dom.Runtime.WindowContextManager _windowContext;
    // The whole web-messaging feature — window.postMessage, MessageChannel/MessagePort (which own
    // the MessagePortRegistry state) and the generic EventTarget dispatch shared with sub-windows —
    // lives in MessagingBinding, reached through the narrow IMessagingHost
    // contract (see DomBridge/Hosts.Window.cs). The module holds a reference to the shared
    // _eventTargets registry (generic-target listeners it does not own).
    private readonly Dom.Features.MessagingBinding _messaging;
    // The fetch / XMLHttpRequest networking surface (fetch + Headers/Request/Response/
    // FormData/Blob/AbortController + the XHR polyfill) lives in FetchBinding, backed by the injected
    // ResourceLoader; the only bridge coupling (page URL for redirect resolution) is reached
    // through the narrow IFetchHost contract (see DomBridge/Hosts.Window.cs).
    private readonly Dom.Features.FetchBinding _fetch;
    // The DOM attribute object model — the element.attributes NamedNodeMap and its
    // Attr nodes — plus the setAttribute/removeAttribute write path live in AttributesBinding, reached
    // through the narrow IAttributesHost contract (see DomBridge/Hosts.Nodes.cs) for the write
    // path's cross-cutting side effects (inline style, inline event handlers, style invalidation,
    // mutation records). The low-level attribute scans stay shared static helpers on DomBridge.
    private readonly Dom.Features.AttributesBinding _attributes;
    // The nested-browsing-context `document` object surface (BuildDocument + every
    // getElementById/createElement/querySelector/… callback + document.implementation) lives in
    // SubDocumentBinding, reached through the ISubDocumentHost contract (see DomBridge/Hosts.Documents.cs).
    // A sub-document root is a canonical DomNode. The browsing-context state (sub-document/-window
    // caches, content-document maps, current-window override) is owned by BrowsingContextManager; the
    // builders / resource loading / onload algorithms stay bridge-owned and reach that state through it.
    /// <summary>The File API data surfaces — Blob, File and the URL object-URL pair. It needs nothing
    /// from the bridge (a blob is bytes, not a node), so it takes no host contract.</summary>
    private readonly Dom.Features.BlobBinding _blobs;

    /// <summary>
    /// <c>ReadableStream</c>, <c>ProgressEvent</c> and <c>FileReader</c>, plus the seam that mints a
    /// stream over bytes for <c>blob.stream()</c> and a fetch body.
    /// </summary>
    /// <remarks>
    /// Built in the constructor rather than initialised in place because it now takes the bridge's
    /// JSEAL realm accessor — a function, since the realm is adopted at Attach and this field exists
    /// before that.
    /// </remarks>
    private readonly Dom.Features.StreamsBinding _streams;
    private readonly Dom.Features.SubDocumentBinding _subDocuments;
    // The nested-browsing-context `window` (sub-window) object — its
    // document/location/scroll/getComputedStyle surface and the sub-window-scoped helpers — lives in
    // SubWindowBinding, reached through the narrow ISubWindowHost contract (see DomBridge/Hosts.Documents.cs);
    // it holds the BrowsingContextManager + the shared EventTargetRegistry/MessagingBinding it installs.
    private readonly Dom.Features.SubWindowBinding _subWindows;
    private double _visualViewportScale = 1.0;
    private double _visualViewportPageLeftOffset;
    private double _visualViewportPageTopOffset;

    /// <summary>
    /// Index into the tree-derived <see cref="Elements"/> view of the
    /// <c>&lt;script&gt;</c> element
    /// that is currently executing.  Used by <c>document.write()</c> to insert
    /// content at the correct DOM position.  Set to &lt;0 when no script is
    /// running.
    /// </summary>
    internal int CurrentScriptIndex { get; set; } = -1;

    int IDomBridgeRuntime.CurrentScriptIndex
    {
        get => CurrentScriptIndex;
        set => CurrentScriptIndex = value;
    }

    // viewport dimensions for window.innerWidth/innerHeight and element box-model properties
    private int _viewportWidth = DefaultViewportWidth;
    private int _viewportHeight = DefaultViewportHeight;

    /// <summary>
    /// The CSS viewport this document is laid out against, in pixels. Everything the bridge
    /// resolves against the viewport reads it: <c>window.innerWidth</c>/<c>innerHeight</c>,
    /// <c>vw</c>/<c>vh</c> lengths, media-query evaluation, and the maximum scroll offset.
    /// </summary>
    /// <remarks>
    /// These were fixed at 1024×768 and never written, so a host rendering at any other size got a
    /// document whose script-visible geometry disagreed with the pixels: a page built to be "taller
    /// than the viewport" at 200×200 scrolled to somewhere that was not the bottom of the canvas,
    /// and a test asserting on what is on screen then failed for a reason that had nothing to do
    /// with what it was testing. A host that renders at a size is expected to set this to the same
    /// size; leaving it alone keeps the historical default.
    /// </remarks>
    public int ViewportWidth
    {
        get => _viewportWidth;
        set => _viewportWidth = value > 0 ? value : DefaultViewportWidth;
    }

    /// <inheritdoc cref="ViewportWidth"/>
    public int ViewportHeight
    {
        get => _viewportHeight;
        set => _viewportHeight = value > 0 ? value : DefaultViewportHeight;
    }

    /// <summary>
    /// Optional callback invoked after each queued timer, interval, animation-frame,
    /// or frame action task. Callers use this to run spec-like microtask checkpoints.
    /// </summary>
    public Action? TaskCheckpointCallback { get; set; }

    /// <summary>
    /// Optional Content Security Policy applied while compiling inline script-bearing
    /// bridge surfaces such as <c>on*</c> attributes.
    /// </summary>
    public ContentSecurityPolicy? Csp { get; set; }

    // window.location fields
    private string _pageUrl = string.Empty;

    /// <summary>
    /// The document-lifecycle marks the <c>PerformanceNavigationTiming</c> entry reports. Created
    /// with the performance object (which fixes the time origin they are measured against) and
    /// stamped by the load sequence in <c>DomBridge.WindowLoad</c>. Null until the window is
    /// registered, so every stamp site is null-guarded.
    /// </summary>
    private Dom.Features.NavigationTimingState? _navigationTiming;
    private string _pageProtocol = string.Empty;
    private string _pageHost = string.Empty;
    private string _pageHostName = string.Empty;
    private string _pagePort = string.Empty;
    private string _pagePathName = "/";
    private string _pageSearch = string.Empty;
    // No _pageHash: `location.hash` is derived from _pageUrl by LocationBinding, which owns it
    // because a fragment navigation has to move it and `location.href` together.
    private string _pageOrigin = string.Empty;

    public DomBridge()
        : this(null)
    {
    }

    /// <summary>
    /// Creates a bridge with dependencies scoped to this document/session.
    /// </summary>
    public DomBridge(DomBridgeSessionOptions? sessionOptions)
    {
        _layoutViewFactory = sessionOptions?.LayoutViewFactory;
        // Null until Attach adopts one, and null again after teardown — states in which no page
        // callback can be queued, because only script queues one and script needs that realm.
        _eventLoop = new Dom.Runtime.BrowserEventLoop(() => _realm);
        _selectorMatcher = new CssSelectorMatcher(new BridgeSelectorStateProvider(this));
        _traversal = new Dom.Features.TraversalBinding(this);
        _mutations = new Dom.Features.MutationObserverBinding(this);
        _eventDispatch = new Dom.Features.EventDispatchBinding(this);
        _tables = new Dom.Features.TableBinding(this);
        _dialogs = new Dom.Features.DialogBinding(this);
        _select = new Dom.Features.SelectBinding(this);
        _forms = new Dom.Features.FormBinding(this);
        _formControl = new Dom.Features.FormControlBinding(this);
        _messaging = new Dom.Features.MessagingBinding(this, _eventTargets);
        _workers = new Dom.Features.WorkerBinding(this);
        // Every worker thread is stopped and joined when the bridge tears down; see WorkerBinding.
        _disposal.Add(_workers);
        _fetch = new Dom.Features.FetchBinding(this, _resources);
        _attributes = new Dom.Features.AttributesBinding(this);
        _blobs = new Dom.Features.BlobBinding();
        _streams = new Dom.Features.StreamsBinding(() => Realm);
        _subDocuments = new Dom.Features.SubDocumentBinding(this);
        _subWindows = new Dom.Features.SubWindowBinding(this, _browsingContexts, _eventTargets, _messaging);
        _windowContext = new Dom.Runtime.WindowContextManager(this, _browsingContexts, _eventTargets);
        _scriptInsertion = new Dom.Runtime.ScriptInsertionRunner(this);
        _document = new DomDocument();
        // A sheet's text changing through the DOM invalidates computed style; see OnStyleSheetSourceMutation.
        _document.Mutated += OnStyleSheetSourceMutation;
        DocumentElement = CreateBridgeElement("html");
        // The canonical DomDocument is the document root — the JS `document` object maps to it and
        // <html>/doctype are its direct children (no #document wrapper element).
        // DomDocument enforces DOM child validity (one documentElement, doctype
        // first); the constructor's single <html> child satisfies it.
        _document.AppendChild(DocumentElement);
    }

    /// <summary>
    /// The canonical document that owns every bridge-visible DOM node.
    /// </summary>
    public DomDocument Document => _document;

    /// <summary>
    /// The current document title, kept in sync with JavaScript reads/writes.
    /// </summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// All elements parsed from the HTML source.
    /// </summary>
    public IReadOnlyList<DomElement> Elements =>
        [.. _document.InclusiveDescendants().OfType<DomElement>()];

    internal InlineStyleRuntimeState InlineStyleStateFor(DomNode node) =>
        _inlineStyleStates.GetValue(node, static _ => new InlineStyleRuntimeState());

    /// <summary>Mints a canonical <see cref="DomText"/> carrying <paramref name="data"/>.
    /// The funnel for every text node the
    /// bridge constructs itself; callers treat the result as a <see cref="DomNode"/>. A
    /// <c>textContent</c> write does not come through here: the canonical <see cref="DomNode.TextContent"/>
    /// setter mints its text node from the written node's own document.</summary>
    private DomText CreateBridgeTextNode(string data) => NodeFactoryDocument.CreateTextNode(data);

    /// <summary>Mints a canonical <see cref="DomComment"/> carrying <paramref name="data"/>
    /// (see <see cref="CreateBridgeTextNode"/>).</summary>
    private DomComment CreateBridgeCommentNode(string data) => NodeFactoryDocument.CreateComment(data);

    /// <summary>
    /// The single construction funnel for bridge element nodes. Every
    /// <c>new Broiler.Dom.DomElement(...)</c> site routes through here (or <see cref="CreateBridgeElementNS"/>)
    /// so element construction lives in exactly one place over the canonical <c>Broiler.Dom</c>
    /// document factories. The tag name may be an HTML element literal (HTML namespace) or a
    /// <c>#</c>-sentinel (<c>#document</c>, <c>#subdoc-root</c>, …), which keeps a null namespace and
    /// its preserved name — the bridge-internal document/fragment/shadow model over canonical types.
    /// </summary>
    private DomElement CreateBridgeElement(string tagName, string? id = null, string? className = null, Dictionary<string, string>? attributes = null) =>
        // A leading '#' marks a bridge sentinel (document/fragment/shadow/doctype root): null
        // namespace, name preserved verbatim. Every other tag is an HTML element. CreateElementNS
        // preserves the given name's case exactly as the old facade ctor did (no ToLowerInvariant).
        CreateBridgeElementNS(tagName.StartsWith('#') ? null : DomNamespaces.Html, tagName, id, className, attributes);

    /// <summary>
    /// Element construction with an explicit namespace used verbatim (may be <c>null</c>), for the
    /// <c>createElementNS</c> handlers, sub-document roots, and clones (which preserve the source
    /// element's namespace). See <see cref="CreateBridgeElement"/>.
    /// </summary>
    private DomElement CreateBridgeElementNS(string? namespaceUri, string tagName, string? id = null, string? className = null, Dictionary<string, string>? attributes = null)
    {
        var element = NodeFactoryDocument.CreateElementNS(namespaceUri, tagName);
        if (attributes is not null)
            foreach (var (name, value) in attributes)
                element.SetAttribute(name, value);
        if (id is not null)
            element.Id = id;
        if (className is not null)
            element.ClassName = className;
        return element;
    }

    /// <summary>Mints a canonical <see cref="DomDocumentType"/>. The doctype name is lowercased to
    /// preserve the historical bridge behaviour of always surfacing it lowercase;
    /// publicId/systemId keep their case. The funnel for the doctypes script creates
    /// (<c>createDocumentType</c>, <c>createHTMLDocument</c>) over the canonical document factory; a
    /// parsed document's doctype is the shared parser's own node, moved across with the tree.</summary>
    private DomDocumentType CreateBridgeDocumentType(string name, string publicId, string systemId) =>
        NodeFactoryDocument.CreateDocumentType(name.ToLowerInvariant(), publicId, systemId);

    /// <summary>Mints a canonical <see cref="DomDocumentFragment"/>.
    /// The single funnel for fragment construction over
    /// the canonical document factory (used by <c>createDocumentFragment</c>, Range clone/extract
    /// results, and a template's contents). A parsed HTML fragment is the shared parser's own
    /// fragment instead.</summary>
    private DomDocumentFragment CreateBridgeDocumentFragment() => NodeFactoryDocument.CreateDocumentFragment();

    /// <summary>Mints a canonical <see cref="DomDocument"/> for a detached browsing context
    /// (<c>createDocument</c>/<c>createHTMLDocument</c>).
    /// It is its own document (not the main <c>_document</c>) and, being a
    /// programmatic non-rendered document, is marked viewport-less so hit-testing skips it. Its
    /// children (doctype/documentElement) are appended as true canonical document children.</summary>
    private DomDocument CreateBrowsingContextDocument()
    {
        var document = new DomDocument();
        DocumentStateFor(document).HasViewport.Set(false);
        // Its sheets resolve through the same computed-style memo as the page's.
        document.Mutated += OnStyleSheetSourceMutation;
        // Custom element reactions are dispatched off each document's mutation stream, and adoption
        // publishes on the document a node moves *to* — so a document that can receive one has to be
        // listened to as well as the page's own.
        SubscribeBrowsingContextDocument(document);
        return document;
    }

    private Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element) =>
        _eventTargets.NodeListeners(element);

    private Dictionary<string, JsValue> GetInlineEventHandlers(DomNode element) =>
        InlineStyleStateFor(element).InlineEventHandlers;

    internal bool TryGetStoredScrollOffset(DomElement element, bool vertical, out double offset)
    {
        var slot = vertical
            ? ScrollStateFor(element).Top
            : ScrollStateFor(element).Left;
        if (slot.TryGet(out var value) && value is double storedOffset)
        {
            offset = storedOffset;
            return true;
        }

        offset = 0;
        return false;
    }

    internal double? GetStoredScrollOffsetOrDefault(DomElement element, bool vertical) =>
        TryGetStoredScrollOffset(element, vertical, out var offset) ? offset : null;

    internal bool TryGetResolvedLayout(
        DomElement element,
        out double left,
        out double top,
        out double width,
        out double height)
    {
        // Reads the memoized position-area resolution. The four values are always set (and
        // cleared) together, so a cached entry either has all four or none.
        if (TryGetPositionAreaResolution(element, out var rect))
        {
            (left, top, width, height) = rect;
            return true;
        }

        (left, top, width, height) = (0, 0, 0, 0);
        return false;
    }

    /// <summary>
    /// Parse the supplied <paramref name="html"/> and register a
    /// <c>document</c> global on the given <paramref name="context"/>.
    /// </summary>
    public void Attach(JSContext context, string html)
    {
        ThrowIfDisposed();
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(
            Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.ParseHtml))
            ParseHtml(html);
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(
            Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegisterDocument))
            RegisterDocument(context);
        EnforceConfiguredStyleContentSecurityPolicy();
    }

    /// <summary>
    /// Parse the supplied <paramref name="html"/> and register a
    /// <c>document</c> global on the given <paramref name="context"/>,
    /// with the page URL available via <c>window.location</c>.
    /// </summary>
    public void Attach(JSContext context, string html, string url)
    {
        ThrowIfDisposed();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _pageUrl = uri.ToString();
            _pageProtocol = uri.Scheme + ":";
            _pageHost = Origin.HostOf(uri);
            _pageHostName = uri.Host;
            // Empty for a URL on its scheme's default port — that default is exactly what `host`
            // leaves out, so `location.port === ""` is how a page asks whether one was given.
            _pagePort = uri.IsDefaultPort ? string.Empty : uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _pagePathName = uri.AbsolutePath;
            _pageSearch = uri.Query;
            _pageOrigin = Origin.Of(uri);
        }
        else
        {
            _pageUrl = url;
        }
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(
            Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.ParseHtml))
            ParseHtml(html);
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(
            Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegisterDocument))
            RegisterDocument(context);
        EnforceConfiguredStyleContentSecurityPolicy();
    }

    /// <summary>
    /// Enforces the CSP <c>style-src</c> family on the freshly parsed DOM as the final step of attach, so
    /// the document the host hands to scripts and rendering already excludes CSP-blocked inline styles and
    /// <c>&lt;style&gt;</c> elements (CSP is a host-layer decision, and DOM/CSS receive
    /// already-authorised content on every path). Runs only when a policy is configured via
    /// <see cref="Csp"/>; idempotent, so a host that still applies the policy explicitly removes nothing more.
    /// </summary>
    private void EnforceConfiguredStyleContentSecurityPolicy()
    {
        if (Csp != null)
            ApplyStyleContentSecurityPolicy(Csp);
    }

    /*
     * HTML "named access on the Window object" -- `window.myId` resolving to the element with
     * id="myId" -- IS NOT IMPLEMENTED HERE. The absence is stated rather than left implicit;
     * anyone implementing it does so fresh, against IJsRealm.
     */

    /// <summary>
    /// Sets a local base directory for resolving relative sub-resource URLs.
    /// When set, sub-resource URLs (e.g. iframe src) are first looked up as files
    /// relative to this directory before falling back to HTTP fetch.
    /// </summary>
    public void SetLocalBasePath(string basePath) => _resources.LocalBasePath = basePath;

    /// <summary>
    /// Executes all pending <c>setTimeout</c>, <c>setInterval</c>, and
    /// <c>requestAnimationFrame</c> callbacks. Repeats until no new
    /// callbacks are queued, up to a maximum of 500 iterations to prevent
    /// infinite loops. The higher limit supports test harnesses like Acid3
    /// that chain 100+ tests via <c>setTimeout</c>. Call this before DOM
    /// capture/serialisation.
    /// </summary>
    public void FlushTimers()
    {
        ThrowIfDisposed();
        _eventLoop.DrainAll(TaskCheckpointCallback);
    }

    /// <summary>
    /// Returns <c>true</c> when there are queued <c>setTimeout</c>,
    /// <c>setInterval</c>, or <c>requestAnimationFrame</c> callbacks
    /// waiting to execute.
    /// </summary>
    public bool HasPendingTimers
    {
        get
        {
            ThrowIfDisposed();
            return _eventLoop.HasPendingWork;
        }
    }

    /// <summary>
    /// Whether queued timer/animation-frame work is due at or before <paramref name="virtualHorizonMs"/>
    /// on the event loop's virtual clock, measured from document start.
    /// </summary>
    /// <remarks>
    /// The question a drain actually needs. <see cref="HasPendingTimers"/> stays <c>true</c> forever on
    /// any page holding a <c>setInterval</c>, because an interval always has a next tick, so a drain
    /// waiting for it to go false cannot stop on an ordinary page.
    /// </remarks>
    public bool HasPendingTimersDueBy(double virtualHorizonMs)
    {
        ThrowIfDisposed();
        return _eventLoop.HasWorkDueBy(virtualHorizonMs);
    }

    /// <summary>
    /// Executes one batch of pending timer and animation-frame callbacks.
    /// Returns <c>true</c> if callbacks were executed (more may be pending);
    /// <c>false</c> if there was nothing to run.
    /// Used by interactive rendering to step through animations one frame at
    /// a time so that intermediate visual states are displayed.
    /// </summary>
    public bool FlushTimerStep()
    {
        ThrowIfDisposed();
        return _eventLoop.DrainStep(TaskCheckpointCallback);
    }

}
