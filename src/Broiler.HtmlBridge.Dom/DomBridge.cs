using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using System.Net;
using System.Runtime.CompilerServices;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;
using Broiler.CSS.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

/// <summary>
/// Registers a minimal <c>document</c> object on a <see cref="JSContext"/>
/// so that JavaScript executed via YantraJS can perform basic DOM queries
/// against the current page HTML.
/// </summary>
public sealed partial class DomBridge : IDomBridgeRuntime
{
    /// <summary>
    /// Safety cap for draining bridge-backed microtask/timer work so promise/timer
    /// chains can settle without risking an infinite loop in test and capture paths.
    /// </summary>
    public const int AsyncDrainIterationLimit = 1000;

    /// <summary>
    /// How far onto the virtual clock a drain follows scheduled work (ms from document start); see
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/>.
    /// </summary>
    public const double AsyncDrainVirtualTimeBudgetMs = DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs;

    // P2.6: sub-resource HTTP and the local base path now live in ResourceLoader, the single host
    // resource loader (was the static SharedHttpClient plus the _localBasePath field). Feature
    // callbacks ask the loader instead of reaching a static HttpClient.
    private readonly Dom.Runtime.ResourceLoader _resources = new();
    private Dom.Features.WorkerBinding? _workers;
    private static readonly string[] InlineEventNames = ["click", "load", "change", "input", "submit", "mousedown",
        "mouseup", "mouseover", "mouseout", "keydown", "keyup", "keypress", "focus", "blur", "error", "scroll",
        "scrollend"];
    // Phase 3 (P3.2): the whole MutationObserver feature — the observer registry (P2.5
    // MutationObserverHub), the observe()/disconnect() registration and childList/attribute/
    // characterData record delivery — lives in the MutationObserverBinding module, reached through
    // the narrow IMutationObserverHost contract (see DomBridge.MutationObserverHost.cs).
    private readonly Dom.Features.MutationObserverBinding _mutations;
    // Phase 3 (P3.3): the DOM event dispatch engine — capture/target/bubble propagation, the event
    // object's propagation-control methods and composedPath() — lives in EventDispatchBinding,
    // reached through the narrow IEventDispatchHost contract (see DomBridge.EventDispatchHost.cs).
    private readonly Dom.Features.EventDispatchBinding _eventDispatch;
    // Phase 3 (P3.5): the HTML table DOM interfaces (HTMLTableElement / HTMLTableSectionElement /
    // HTMLTableRowElement) live in TableBinding, reached through the narrow ITableHost contract
    // (see DomBridge.TableHost.cs).
    private readonly Dom.Features.TableBinding _tables;
    // Phase 3 (P3.7): the dialog / popover / details JS API (showModal/show/close/showPopover/
    // hidePopover/open/returnValue) lives in DialogBinding, reached through the narrow IDialogHost
    // contract (see DomBridge.DialogHost.cs); backdrop/top-layer rendering stays in the bridge.
    private readonly Dom.Features.DialogBinding _dialogs;
    // Phase 3 (P3.8): HTMLSelectElement / HTMLOptionElement (add/options/selectedIndex/size/value +
    // option.defaultSelected) live in SelectBinding, reached through the narrow ISelectHost contract
    // (see DomBridge.SelectHost.cs); the shared value property delegates its select branch to it.
    private readonly Dom.Features.SelectBinding _select;
    // Phase 3 (P3.9): HTMLFormElement (elements/length/action) and constraint validation
    // (checkValidity/reportValidity) live in FormBinding, reached through the narrow IFormHost
    // contract (see DomBridge.FormHost.cs).
    private readonly Dom.Features.FormBinding _forms;
    // Phase 3 (P3.60): the form-control IDL reflectors (value/checked/type/name/disabled/hidden/
    // tabIndex/required) live in FormControlBinding, reached through the narrow IFormControlHost
    // contract (see DomBridge.FormControlHost.cs).
    private readonly Dom.Features.FormControlBinding _formControl;
    // Phase 3 (first feature-module slice): TreeWalker/NodeIterator/Range construction, every Range
    // callback and the traversal-scoped active-range / active-node-iterator registries live in the
    // co-located TraversalBinding module. The bridge holds the module through the narrow
    // ITraversalHost contract it implements (see DomBridge.TraversalHost.cs).
    private readonly Dom.Features.TraversalBinding _traversal;
    private readonly DomDocument _document;
    // Per-element inline-style runtime state (the last concern de-globalized off the former process-static
    // ElementRuntimeState table, 2026-07-17); reached via InlineStyleStateFor.
    private readonly ConditionalWeakTable<DomNode, InlineStyleRuntimeState> _inlineStyleStates = [];
    private JSObject? _documentJSObject;
    private JSObject? _windowJSObject;
    private JSObject? _visualViewportJSObject;
    private JSContext? _jsContext;

    // P2.4: the timer/interval/requestAnimationFrame/frame-action queues, their id counters and the
    // drain (FlushTimerStep/FlushTimers) now live in BrowserEventLoop, the single owner of the
    // document's task queues (was the eight scattered _timerIdCounter/_timeoutCallbacks/… fields).
    private readonly Dom.Runtime.BrowserEventLoop _eventLoop = new();
    // Runs the <script> elements the page's own JavaScript inserts — nothing did, so the loader
    // idiom (inject a <script src>, poll until the global it defines appears) never terminated. See
    // ScriptInsertionRunner; fed by the document.createElement funnel below.
    private readonly Dom.Runtime.ScriptInsertionRunner _scriptInsertion;
    // Smooth-scroll continuation tokens: a per-element monotonic marker so a queued smooth-scroll
    // frame action only commits if it is still the active scroll for that element. Touched by
    // scroll/frame-action callbacks that can run on ThreadPool threads, so keep it concurrent.
    private int _smoothScrollTokenCounter;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<DomElement, int> _smoothScrollTokens = new();
    // P2.5: the event-listener stores (per-node addEventListener listeners, window listeners,
    // generic JS-target listeners, target→owner-window map, and visual-viewport scroll listeners)
    // now live in EventTargetRegistry, the single listener owner (was the scattered
    // _windowEventListeners/_eventTargetListeners/_eventTargetOwnerWindows/_visualViewportScrollListeners
    // fields plus ElementRuntimeState.EventListeners for node listeners).
    private readonly Dom.Runtime.EventTargetRegistry _eventTargets = new();
    // Phase 3 (P3.16): the nested-browsing-context state — the per-container sub-document/sub-window
    // JS-object identity, location/base-URL caches, object-load-failure and onload-fired marks, the
    // reverse sub-window→container map, the current-window override, and the P4.4b content-document
    // maps — is owned by the single BrowsingContextManager (was ten fields scattered across
    // SubDocuments.cs / DomBridge.WindowContext.cs / DomBridge.cs). The bridge keeps the algorithms.
    private readonly Dom.Runtime.BrowsingContextManager _browsingContexts = new();
    // Phase 3 (P3.18): the browsing-context window-resolution behaviour (canonicalise/resolve a window,
    // and the RunWithWindowContext global switch) is owned by WindowContextManager, reached through the
    // narrow IWindowContextHost contract (see DomBridge.WindowContextHost.cs); DomBridge.WindowContext.cs
    // keeps thin delegators. It reads the sub-window state from _browsingContexts and _eventTargets.
    private readonly Dom.Runtime.WindowContextManager _windowContext;
    // Phase 3 (P3.10): the whole web-messaging feature — window.postMessage, MessageChannel/
    // MessagePort (which own the P2.6 MessagePortRegistry state) and the generic EventTarget dispatch
    // shared with sub-windows — lives in MessagingBinding, reached through the narrow IMessagingHost
    // contract (see DomBridge.MessagingHost.cs). The module holds a reference to the shared
    // _eventTargets registry (generic-target listeners it does not own).
    private readonly Dom.Features.MessagingBinding _messaging;
    // Phase 3 (P3.11): the fetch / XMLHttpRequest networking surface (fetch + Headers/Request/Response/
    // FormData/Blob/AbortController + the XHR polyfill) lives in FetchBinding, backed by the injected
    // P2.6 ResourceLoader; the only bridge coupling (page URL for redirect resolution) is reached
    // through the narrow IFetchHost contract (see DomBridge.FetchHost.cs).
    private readonly Dom.Features.FetchBinding _fetch;
    // Phase 3 (P3.12): the DOM attribute object model — the element.attributes NamedNodeMap and its
    // Attr nodes — plus the setAttribute/removeAttribute write path live in AttributesBinding, reached
    // through the narrow IAttributesHost contract (see DomBridge.AttributesHost.cs) for the write
    // path's cross-cutting side effects (inline style, inline event handlers, style invalidation,
    // mutation records). The low-level attribute scans stay shared static helpers on DomBridge.
    private readonly Dom.Features.AttributesBinding _attributes;
    // Phase 3 (P3.13): the nested-browsing-context `document` object surface (BuildDocument + every
    // getElementById/createElement/querySelector/… callback + document.implementation) lives in
    // SubDocumentBinding, reached through the ISubDocumentHost contract (see DomBridge.SubDocumentHost.cs).
    // Unblocked by P4.4b's #subdoc-root sever — a sub-document root is now a canonical DomNode. The
    // browsing-context state (sub-document/-window caches, content-document maps, current-window
    // override) is owned by BrowsingContextManager (P3.16); the builders / resource loading / onload
    // algorithms stay bridge-owned and reach that state through it.
    /// <summary>The File API data surfaces — Blob, File and the URL object-URL pair. It needs nothing
    /// from the bridge (a blob is bytes, not a node), so it takes no host contract.</summary>
    private readonly Dom.Features.BlobBinding _blobs;

    /// <summary>
    /// <c>ReadableStream</c>, <c>ProgressEvent</c> and <c>FileReader</c>, plus the seam that mints a
    /// stream over bytes for <c>blob.stream()</c> and a fetch body.
    /// </summary>
    private readonly Dom.Features.StreamsBinding _streams = new();
    private readonly Dom.Features.SubDocumentBinding _subDocuments;
    // Phase 3 (P3.17): the nested-browsing-context `window` (sub-window) object — its
    // document/location/scroll/getComputedStyle surface and the sub-window-scoped helpers — lives in
    // SubWindowBinding, reached through the narrow ISubWindowHost contract (see DomBridge.SubWindowHost.cs);
    // it holds the P3.16 BrowsingContextManager + the shared EventTargetRegistry/MessagingBinding it installs.
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

    /// <summary>The viewport a bridge assumes when its host does not say otherwise.</summary>
    public const int DefaultViewportWidth = 1024;

    /// <inheritdoc cref="DefaultViewportWidth"/>
    public const int DefaultViewportHeight = 768;

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
    private string _pageHash = string.Empty;
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
        _subDocuments = new Dom.Features.SubDocumentBinding(this);
        _subWindows = new Dom.Features.SubWindowBinding(this, _browsingContexts, _eventTargets, _messaging);
        _windowContext = new Dom.Runtime.WindowContextManager(this, _browsingContexts, _eventTargets);
        _scriptInsertion = new Dom.Runtime.ScriptInsertionRunner(this);
        _document = new DomDocument();
        DocumentElement = CreateBridgeElement("html");
        // Phase 4 item 1 (final sentinel): the canonical DomDocument is the document root — the JS
        // `document` object maps to it and <html>/doctype are its direct children (no #document
        // wrapper element). DomDocument enforces DOM child validity (one documentElement, doctype
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

    /// <summary>
    /// The element's authoritative in-memory inline style dictionary (CSS kebab-case),
    /// relocated off the <c>Broiler.Dom.DomElement</c> facade into <see cref="ElementRuntimeState"/>
    /// (RF-BRIDGE-1c Phase B). Lazily seeded once from the element's <c>style=</c>
    /// attribute; thereafter it is the source of truth (JS <c>element.style</c> writes,
    /// anchor/form-control styling), synced back to the attribute at serialization.
    /// </summary>
    // -----------------------------------------------------------------
    // RF-BRIDGE-1c Phase E2: child-node access over canonical ChildNodes,
    // replacing the facade Broiler.Dom.DomElement.Children (LegacyChildList). The bridge
    // tree is homogeneous Broiler.Dom.DomElement today, so ChildElements is a drop-in for
    // the old enumeration; the Cast/ChildAt casts are safe until text/comment
    // flip to canonical DomText/DomComment (Phase F), when ChildElements narrows
    // to OfType and callers gain IsText handling.
    // -----------------------------------------------------------------

    /// <summary>The element's <see cref="DomElement"/> children. RF-BRIDGE-1c Phase F (F3c part 2c):
    /// narrowed from <c>Cast</c> to <c>OfType&lt;Broiler.Dom.DomElement&gt;()</c> so it skips canonical
    /// <c>DomText</c>/<c>DomComment</c> children once construction flips; a no-op on today's
    /// homogeneous tree. Callers that need text/comment children walk raw <c>ChildNodes</c> instead.</summary>
    internal static IEnumerable<DomElement> ChildElements(DomNode element) =>
        element.ChildNodes.OfType<DomElement>();

    /// <summary>The child node at <paramref name="index"/> (old <c>Children[index]</c>). RF-BRIDGE-1c
    /// Phase F (F3c part 2c): returns canonical <see cref="DomNode"/> — a child may be a
    /// <c>DomText</c>/<c>DomComment</c> once construction flips. Element-only callers narrow with
    /// <c>as Broiler.Dom.DomElement</c>/<c>is Broiler.Dom.DomElement</c>; on today's homogeneous tree every child is an element.</summary>
    internal static DomNode ChildAt(DomNode element, int index) => element.ChildNodes[index];

    /// <summary>The child node at <paramref name="index"/>, supporting from-end indices like <c>^1</c>
    /// (old <c>Children[^1]</c>); canonical <c>ChildNodes</c> is an <c>IReadOnlyList</c> with no
    /// from-end indexer.</summary>
    internal static DomNode ChildAt(DomNode element, Index index) =>
        element.ChildNodes[index.GetOffset(element.ChildNodes.Count)];

    /// <summary>Index of <paramref name="child"/> among the element's children, or -1
    /// (old <c>Children.IndexOf</c>, reference equality). Phase 4 item 4/5: canonical
    /// <c>Broiler.Dom.DomNodeCollectionExtensions.IndexOfReference</c> is the byte-identical scan, but the
    /// reference-equality child-index scan is the canonical <c>DomNodeCollectionExtensions.IndexOfReference</c>
    /// (P4.17 reuse), which `patches/0002` made public and which is now pinned — so the former manual loop
    /// delegates to it (byte-identical).</summary>
    internal static int ChildIndexOf(DomNode element, DomNode child) => element.ChildNodes.IndexOfReference(child);

    // RF-BRIDGE-1c Phase F (F3c part 2b): the child-mutation helpers take a DomNode parent so
    // range-extract code (whose ancestor-chain clones are DomNode-typed) can reparent without
    // casts. At runtime the parent is always an element; canonical AppendChild/InsertBefore/
    // RemoveChild enforce nothing text-specific, so this is a safe widen.

    /// <summary>Old <c>Children.Insert(index, child)</c>.</summary>
    internal static void InsertChildAt(DomNode parent, int index, DomNode child)
    {
        var reference = index < parent.ChildNodes.Count ? parent.ChildNodes[index] : null;
        parent.InsertBefore(child, reference);
    }

    /// <summary>Old <c>Children.Remove(child)</c> — removes only if actually a child; returns success.</summary>
    internal static bool RemoveChildFrom(DomNode parent, DomNode child)
    {
        if (!ReferenceEquals(child.ParentNode, parent))
            return false;

        parent.RemoveChild(child);
        return true;
    }

    /// <summary>Old raw <c>Children.RemoveAt(index)</c> (no mutation notifications — matches the
    /// LegacyChildList primitive; distinct from the notifying <c>RemoveChildAt</c> helper).</summary>
    internal static void RemoveNthChild(DomNode parent, int index) => parent.RemoveChild(parent.ChildNodes[index]);

    /// <summary>Old <c>Children.Clear()</c>.</summary>
    internal static void ClearChildren(DomNode parent)
    {
        foreach (var child in parent.ChildNodes.ToArray())
            parent.RemoveChild(child);
    }

    /// <summary>Whether <paramref name="node"/> is a text node (RF-BRIDGE-1c Phase D: replaces
    /// the facade <c>IsText(Broiler.Dom.DomElement)</c>). NodeType-based, so it holds for the current
    /// facade text nodes and for canonical <c>DomText</c> once construction flips in the
    /// <c>Children</c>/text cutover.</summary>
    internal static bool IsText(DomNode node) => node.NodeType == DomNodeType.Text;

    /// <summary>Whether <paramref name="node"/> is a comment node (RF-BRIDGE-1c Phase F).
    /// NodeType-based, so it holds for the current facade comment nodes and for canonical
    /// <c>DomComment</c> once construction flips — the replacement for the many
    /// <c>TagName == "#comment"</c> checks, since a canonical <c>DomComment</c> has no
    /// <c>TagName</c>.</summary>
    internal static bool IsComment(DomNode node) => node.NodeType == DomNodeType.Comment;

    /// <summary>Reads a text/comment node's character data (RF-BRIDGE-1c Phase F). Canonical
    /// <c>DomText</c>/<c>DomComment</c> expose it as <c>Data</c>; the facade text/comment nodes
    /// (pre-flip) expose it as <c>TextContent</c>. The single accessor both models funnel through
    /// during the text cutover; returns <c>""</c> (never null) for character-data nodes.</summary>
    internal static string BridgeText(DomNode node) => node switch
    {
        DomCharacterData characterData => characterData.Data,
        _ => node.NodeValue ?? string.Empty,
    };

    /// <summary>Writes a text/comment node's character data (see <see cref="BridgeText"/>).</summary>
    internal static void SetBridgeText(DomNode node, string value)
    {
        if (node is DomCharacterData characterData)
            characterData.Data = value;
    }

    /// <summary>Mints a canonical <see cref="DomText"/> carrying <paramref name="data"/>
    /// (RF-BRIDGE-1c Phase F, F3c part 2d — construction cutover). The single funnel for text-node
    /// construction; callers treat the result as a <see cref="DomNode"/>.</summary>
    private DomText CreateBridgeTextNode(string data) => NodeFactoryDocument.CreateTextNode(data);

    /// <summary>Mints a canonical <see cref="DomComment"/> carrying <paramref name="data"/>
    /// (see <see cref="CreateBridgeTextNode"/>).</summary>
    private DomComment CreateBridgeCommentNode(string data) => NodeFactoryDocument.CreateComment(data);

    /// <summary>
    /// The single construction funnel for bridge element nodes (RF-BRIDGE-1c Phase F, F4). Every
    /// former <c>new Broiler.Dom.DomElement(...)</c> site routes through here (or <see cref="CreateBridgeElementNS"/>)
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

    /// <summary>Mints a canonical <see cref="DomDocumentType"/> (Phase 4 item 1 — the former
    /// <c>#doctype</c> sentinel element). The doctype name is lowercased to preserve the historical
    /// bridge behaviour (doctype name was always surfaced lowercase via the old <c>GetDocTypeName</c>);
    /// publicId/systemId keep their case. The single funnel for doctype construction over the
    /// canonical document factory.</summary>
    private DomDocumentType CreateBridgeDocumentType(string name, string publicId, string systemId) =>
        NodeFactoryDocument.CreateDocumentType(name.ToLowerInvariant(), publicId, systemId);

    /// <summary>Mints a canonical <see cref="DomDocumentFragment"/> (Phase 4 item 1 — the former
    /// <c>#document-fragment</c> sentinel element). The single funnel for fragment construction over
    /// the canonical document factory (used by <c>createDocumentFragment</c>, Range clone/extract
    /// results, and the internal HTML fragment-parse container).</summary>
    private DomDocumentFragment CreateBridgeDocumentFragment() => NodeFactoryDocument.CreateDocumentFragment();

    /// <summary>Mints a canonical <see cref="DomDocument"/> for a detached browsing context (Phase 4
    /// item 1, P4.4a — the former <c>#subdoc-root</c> sentinel for <c>createDocument</c>/
    /// <c>createHTMLDocument</c>). It is its own document (not the main <c>_document</c>) and, being a
    /// programmatic non-rendered document, is marked viewport-less so hit-testing skips it. Its
    /// children (doctype/documentElement) are appended as true canonical document children.</summary>
    private DomDocument CreateBrowsingContextDocument()
    {
        var document = new DomDocument();
        DocumentStateFor(document).HasViewport.Set(false);
        // Custom element reactions are dispatched off each document's mutation stream, and adoption
        // publishes on the document a node moves *to* — so a document that can receive one has to be
        // listened to as well as the page's own.
        SubscribeBrowsingContextDocument(document);
        return document;
    }

    /// <summary>Sets an element's <c>textContent</c> per DOM (RF-BRIDGE-1c Phase F, F3c part 2d):
    /// replaces all children with a single canonical <see cref="DomText"/> (or none when
    /// <paramref name="value"/> is null/empty). Replaces the former element-store
    /// <c>Broiler.Dom.DomElement.TextContent</c> scalar.</summary>
    private void SetElementTextContent(DomElement element, string? value)
    {
        ClearChildren(element);
        if (!string.IsNullOrEmpty(value))
        {
            var textNode = CreateBridgeTextNode(value);
            element.AppendChild(textNode);
        }
    }

    /// <summary>An element's <c>textContent</c> — the concatenation of its descendant text (RF-BRIDGE-1c
    /// Phase F, F3c part 2d). Replaces reads of the former element-store <c>Broiler.Dom.DomElement.TextContent</c>.</summary>
    internal static string GetElementTextContent(DomElement element)
    {
        var sb = new System.Text.StringBuilder();
        CollectTextContent(element, sb);
        return sb.ToString();
    }

    /// <summary>The element's parent as a <see cref="DomElement"/> (RF-BRIDGE-1c Phase E:
    /// replaces the facade <c>ParentEl(Broiler.Dom.DomElement)</c> getter — <c>ParentNode as Broiler.Dom.DomElement</c>).
    /// A node's parent is always an element, so this is stable when text/comment nodes become
    /// canonical <c>DomText</c>/<c>DomComment</c> in Phase D.</summary>
    internal static DomElement? ParentEl(DomNode node) => node.ParentNode as DomElement;

    /// <summary>Reparents <paramref name="child"/> under <paramref name="parent"/> (RF-BRIDGE-1c
    /// Phase E: replaces the facade <c>ParentEl(Broiler.Dom.DomElement)</c> setter). A null parent detaches;
    /// otherwise the child is appended if not already there — matching the old setter exactly.
    /// RF-BRIDGE-1c Phase F (F3c part 2b): the parent widened to <c>DomNode?</c> so range-extract
    /// code can pass DomNode-typed ancestor-chain clones (always elements at runtime).</summary>
    internal static void SetParent(DomNode child, DomNode? parent)
    {
        if (parent is null)
            child.Remove();
        else if (!ReferenceEquals(child.ParentNode, parent))
            parent.AppendChild(child);
    }

    internal Dictionary<string, string> InlineStyle(DomElement element)
    {
        // Handing out the mutable dictionary is the only inline-style write seam there is, so it is
        // also where a retained geometry snapshot has to be given up: an inline declaration never
        // reaches the DOM `style=` attribute at script time (it is synced only when a projection is
        // built), so DomDocument.Version cannot see the change. Counting the handout rather than the
        // write is deliberately conservative — a caller that only reads costs a rebuild, a caller
        // that writes can never go unnoticed. Read-only consumers on the geometry path call
        // InlineStyleForRead instead; see CurrentLayoutSnapshotKey.
        BridgeRuntimeStateEpoch.Bump();
        return InlineStyleForRead(element);
    }

    /// <summary>
    /// <see cref="InlineStyle"/> without the write-epoch bump, for callers that only read the
    /// dictionary. Reserved for the hot read paths a geometry query runs *after* the snapshot was
    /// built (the cascade's inline-style source and <c>EffectiveInlineStyle</c>): counting those as
    /// writes would invalidate the snapshot on every read and defeat the cache. Never hand the
    /// result to code that mutates it.
    /// </summary>
    internal Dictionary<string, string> InlineStyleForRead(DomElement element)
    {
        var state = InlineStyleStateFor(element);
        if (!state.StyleSeeded)
        {
            state.StyleSeeded = true;
            var styleAttr = element.GetAttribute("style");
            if (!string.IsNullOrEmpty(styleAttr))
            {
                foreach (var kv in ParseStyle(styleAttr))
                    state.Style[kv.Key] = kv.Value;
            }
        }
        return state.Style;
    }

    // Named bookkeeping seams for the set of inline-style properties explicitly set via JS
    // (element.style.foo = …, setProperty, cssText). The Phase 3 (P3.14) StyleDeclarationBinding module
    // records/clears these through these helpers instead of touching the runtime-state object directly;
    // the bridge's own serialization/computed-style paths read InlineStyleStateFor(...).JsSetStyleProps.
    internal void MarkInlineStylePropSetByJs(DomElement element, string property) =>
        InlineStyleStateFor(element).JsSetStyleProps.Add(property);

    internal void UnmarkInlineStylePropSetByJs(DomElement element, string property) =>
        InlineStyleStateFor(element).JsSetStyleProps.Remove(property);

    internal void ClearInlineStylePropsSetByJs(DomElement element) =>
        InlineStyleStateFor(element).JsSetStyleProps.Clear();

    internal IReadOnlyCollection<string> InlineStylePropsSetByJs(DomElement element) =>
        InlineStyleStateFor(element).JsSetStyleProps;

    /// <summary>Read-only diagnostic view of an element's resolved inline-style map — the same
    /// dictionary the anchor resolver reads and writes (display:none, resolved left/top/width/height,
    /// …). RF-BRIDGE-1c Phase F4 removed the <c>Broiler.Dom.DomElement.Style</c> facade member; internal test and
    /// tooling callers that need to inspect post-resolution inline styles route through this accessor
    /// instead. Visible only to <c>InternalsVisibleTo</c> assemblies — not part of the public surface,
    /// so it does not re-open a public facade seam.</summary>
    internal IReadOnlyDictionary<string, string> GetInlineStyleView(DomElement element) =>
        InlineStyle(element);

    /// <summary>Parity-test hook (DOM/CSS promotion §2.1): the bridge's own sparse
    /// computed-style projection. Paired with <see cref="GetSparseComputedStyleForParity"/>
    /// (the canonical engine's candidate replacement over the SAME synced engine) so a
    /// differential test can measure how close the canonical projection is before the
    /// higher-risk swap of the ~98 <c>GetComputedProps</c> call sites. Visible only to
    /// <c>InternalsVisibleTo</c> assemblies — not a public seam.</summary>
    internal Dictionary<string, string> GetComputedPropsForParity(DomElement element) =>
        GetComputedProps(element);

    /// <summary>Parity-test hook (DOM/CSS promotion §2.1): the canonical engine's
    /// <c>CssStyleEngine.GetSparseComputedStyle</c> over the element's synced scoped engine —
    /// the candidate replacement for <see cref="GetComputedPropsForParity"/>.</summary>
    internal IReadOnlyDictionary<string, string> GetSparseComputedStyleForParity(DomElement element) =>
        GetSyncedScopedEngine(element).GetSparseComputedStyle(element, sparseInheritance: true);

    private Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode element) =>
        _eventTargets.NodeListeners(element);

    private Dictionary<string, JSValue> GetInlineEventHandlers(DomNode element) =>
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
        // RF-BRIDGE-1b (Milestone 2.5): reads the memoized position-area resolution,
        // relocated from ElementRuntimeState.Layout to the bridge-level
        // PositionAreaResolutions cache. The four values are always set (and cleared)
        // together, so a cached entry is equivalent to the old "all four slots present".
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
            _pageHash = uri.Fragment;
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
    /// <c>&lt;style&gt;</c> elements (Phase 7 item 5: CSP is a host-layer decision, and DOM/CSS receive
    /// already-authorised content on every path — not just the CLI/WPT hosts that used to call
    /// <see cref="ApplyStyleContentSecurityPolicy"/> by hand). Runs only when a policy is configured via
    /// <see cref="Csp"/>; idempotent, so a host that still applies the policy explicitly removes nothing more.
    /// </summary>
    private void EnforceConfiguredStyleContentSecurityPolicy()
    {
        if (Csp != null)
            ApplyStyleContentSecurityPolicy(Csp);
    }

    /// <summary>
    /// Registers all DOM elements with an <c>id</c> attribute as globals
    /// on the JS context, matching the HTML5 "named access on the Window
    /// object" behaviour (e.g. <c>window.myId</c> → element with id="myId").
    /// </summary>
    public void RegisterNamedElementGlobals(JSContext context)
    {
        foreach (var el in Elements)
        {
            if (IsText(el) || string.IsNullOrEmpty(el.Id))
                continue;
            // Only register if the global doesn't already exist
            // (user-defined globals take precedence — a `var`/`function` or a
            // lexical `const`/`let`/`class` with the same name shadows the named
            // element, per HTML "named access on the Window object"). The JS
            // engine reports the result as a JSBoolean whose ToString() is the
            // lowercase "true"/"false"; comparing against C#'s "True" never
            // matched, so this skip was a no-op and a same-named read-only
            // global lexical made the assignment below throw
            // "Cannot assign to read only variable", crashing the whole render.
            try
            {
                var existing = context.Eval(
                    $"typeof {el.Id} !== 'undefined'");
                if (existing != null && existing.BooleanValue)
                    continue;
            }
            catch
            {
                // If the id isn't a valid JS identifier, or resolving it throws
                // (e.g. a lexical binding still in its temporal dead zone), leave
                // the existing binding untouched.
                continue;
            }

            // Defensive: even past the guard, assigning could hit a read-only
            // binding in an edge case; a named-element convenience global must
            // never crash script execution.
            try
            {
                var jsObj = ToJSObject(el);
                context[el.Id] = jsObj;
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.RegisterNamedElementGlobals",
                    $"Could not register named element global '{el.Id}': {ex.Message}", ex);
            }
        }
    }

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
