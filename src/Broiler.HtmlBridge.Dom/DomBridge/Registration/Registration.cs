using System.IO;
using System.Text;
using Broiler.JSeal;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using static Broiler.HtmlBridge.DomBridgeHostUtils;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// Engine-typed for two reasons, and only two:
//
//   * RegisterDocument swaps the code cache of the script context the host hands Attach for the
//     process-shared one, and SyncWindowMembersOntoGlobal repeats the swap on the same context.
//     That is a Broiler.JS optimisation with no JSEAL vocabulary — there is no "compile once per
//     process" member on the realm contract — so this is the floor rather than a step not yet taken.
//   * AdoptRealm (DomBridge/Lifecycle.cs) takes that same context to produce the realm, so the context
//     has to reach it.
//
// The adapters that used to be a further reason are gone; see the note at the foot of this file.
// Everything the hubs install is built through the realm, and every module they register is handed
// that realm rather than the context — which is not only tidier: a module handed the context adopted
// it, and a second realm over one context has a job queue of its own that no event loop drains.

/// <summary>
/// JavaScript bridge registration — wires up the <c>document</c>,
/// <c>window</c>, <c>console</c>, and <c>XMLHttpRequest</c> globals
/// on the realm the host handed over.
/// </summary>
public sealed partial class DomBridge
{
    // ------------------------------------------------------------------
    //  JavaScript bridge
    // ------------------------------------------------------------------


    /// <summary>
    /// The element backing <c>document.documentElement</c> (the &lt;html&gt; element).
    /// </summary>
    public Broiler.Dom.DomElement DocumentElement { get; }

    /// <summary>
    /// Publishes the document, window and DOM API surface onto <paramref name="context"/>, with the
    /// bridge's own JavaScript compiled once per process instead of once per document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this wrapper is for.</b> A profile of a WPT run put <c>RegisterDocument</c> at
    /// <b>50.6–53.6% of the whole run</b> — 436–446 ms per document, twice per reftest, and the
    /// single largest item measured anywhere in that investigation. It is also <em>fixed</em> cost:
    /// 2% apart across two unrelated subsets, while the DOM parse next to it, which does scale with
    /// the document, stays under a millisecond.
    /// </para>
    /// <para>
    /// <b>The cost was compiling, not executing.</b> Registration evaluates a fixed set of
    /// bridge-owned JavaScript sources — the content-rendering polyfill asset, the DOMException /
    /// Node / SVGLength constructors, XMLHttpRequest, the mutation-observer and event shims, and the
    /// window→global mirror. Every document got a fresh script context, and a fresh context builds
    /// its own <c>DictionaryCodeCache</c>, so all of that was parsed and compiled again from
    /// nothing every time. Installing the process-shared cache for the duration takes
    /// <c>RegisterDocument</c> from <b>422.10 ms to 13.74 ms per call (30.7×)</b> and a 41-test
    /// reftest run from 69.5 s to 39.3 s, with execution untouched — which is what identifies the
    /// cost as compilation rather than the work the sources do.
    /// </para>
    /// <para>
    /// <b>Why the swap is scoped to this call rather than set on the context.</b> The engine already
    /// offers a context option, <c>UseProcessSharedCodeCache</c>, which would apply the shared cache
    /// to <em>everything</em> the context evaluates, including page script. That is a different and
    /// much larger claim: it would put one document's compiled code where the next document's
    /// evaluation can find it. Nothing here needs that. Within this method the only sources
    /// evaluated are compile-time constants owned by this assembly — verified rather than assumed:
    /// no evaluation reachable from here takes an interpolated or page-derived string, and page
    /// script does not run until the host's own loop, after <c>Attach</c> has returned. Inline event
    /// handlers, which <em>are</em> page-controlled, are compiled at dispatch time and so still go
    /// through the context's own cache. The bridge's own evaluations reach the engine through
    /// <see cref="IJsSource.EvaluateHostScript"/>, which is the same context underneath and so is
    /// covered by the swap exactly as a direct evaluation was.
    /// </para>
    /// <para>
    /// <b>What the shared cache can therefore hold</b> is a fixed, bounded set of strings that ship
    /// in this assembly — it does not grow with the number of documents rendered, and no
    /// page-controlled source can enter it through this path.
    /// </para>
    /// <para>
    /// <b>Correctness rests on the cache key, which the engine already defines.</b>
    /// <c>DictionaryCodeCache</c> keys on source, location, argument list and
    /// <c>JSCompilationOptions</c>, so an entry is only ever served to a context that would have
    /// compiled exactly the same thing; the compiled code binds to whichever context executes it.
    /// The same property is what lets item #16's compile-ahead hand a worker's output to the eval
    /// loop.
    /// </para>
    /// <para>
    /// A context is single-threaded by construction (item #15), so saving and restoring the cache
    /// around the call cannot race a second user of the same context.
    /// </para>
    /// </remarks>
    private void RegisterDocument(JSContext context)
    {
        var previousCache = context.CodeCache;
        context.CodeCache = DictionaryCodeCache.Current;
        try
        {
            RegisterDocumentCore(context);
        }
        finally
        {
            context.CodeCache = previousCache;
        }
    }

    private void RegisterDocumentCore(JSContext context)
    {
        _jsContext = context;
        // THE PAGE'S POLICY REACHES THE REALM HERE, AND THIS LINE IS WHAT MAKES THE SOURCE CONTRACT
        // MEAN ANYTHING FOR A PAGE.
        //
        // Csp is set by the host before Attach, so it is available at this point -- the one moment a
        // realm is built for this document. AllowsEval answers only whether the policy permits
        // evaluation -- 'unsafe-eval' in script-src, or in default-src when script-src is absent, and
        // yes when neither is present: a page that forbids it still runs its own script elements,
        // which is the whole reason the source contract has a member for those separately.
        //
        // A page with no policy answers true, and so does one whose policy permits evaluation, so the
        // common case is unchanged.
        var realm = _realm = AdoptRealm(
            context,
            RealmOptionsFor(Csp));

        // EventTarget.prototype's three methods, routed by receiver
        // (DomBridge/Events.cs). First, because every wrapper registration below asks
        // whether the routing is in place before installing its own copies — the document's included.
        // It depends only on the realm's own EventTarget, which the context already carries.
        RegisterEventTargetRouting();

        var document = realm.NewObject();

        // Map the document object to the canonical DomDocument so that the node-wrapper hub answers
        // the same object as the 'document' variable visible in JS. This ensures strict equality
        // checks like 'range.commonAncestorContainer === document' work. The registry holds the
        // handle, keyed on the DomNode going out and on JsValue.ObjectIdentity coming back — not
        // on the engine's own object, which is only what that identity happens to be under the
        // Broiler.JS provider. The document root assigned below holds that same handle.
        _jsObjects.Set(_document, document);

        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegDocumentObject))
        {
            RegisterDocumentBasics(document);
            RegisterDocumentEventsAndMutationObservers();
            RegisterDocumentWriting(document);
            RegisterDocumentTraversalApis(document);
            RegisterDocumentNodeAndCollectionApis(document);
            RegisterDocumentEventTargetAndMetadata(document);
        }

        DocumentHandle = document;
        realm.SetProperty(realm.Global, "document", document);

        // `window` IS the global object, exactly as it is in a browser — the realm's global is the
        // script context itself, which derives from the engine's object type. It used to be a
        // separate object, which made every `window.foo = …` invisible to the unqualified `foo` that
        // a page writes next, because identifier resolution consults the global object and nothing
        // else. That is not a corner case: it is how google.com bootstraps itself, in one script —
        //   (function(){var _g={kEI:…}; (function(){… window.google=_g;}).call(this);})();
        //   (function(){google.sn='webhp'; google.kHL='en';})();
        // — so the second IIFE threw `google is not defined`, which aborts the whole <script>. Every
        // later Google script then referenced the `google` namespace the aborted one would have
        // published and died the same way, and the page rendered with none of its script-driven
        // content. The window→global mirror (MirrorWindowMembersOntoGlobal) papered over the
        // between-scripts half of this by copying window members onto the global after the fact; it
        // could never cover the within-one-script half, because there is no point between the write
        // and the read at which a host could run. Making the two one object removes the class of bug
        // rather than the symptom, and the mirror becomes the no-op it should always have been.
        //
        // That the two are one is a fact about this engine and the realm says so:
        // JsCapabilities.GlobalIsVariableScope is what a provider asserts it with.
        var window = realm.Global;
        WindowHandle = window;

        var windowBasicsScope = Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegWindowBasics);
        var console = RegisterWindowBasics(document, window);
        var fetchFn = _fetch.Install(realm, window);
        // MessageChannel (messaging) and getComputedStyle (CSSOM) historically lived inside the fetch
        // registration; they are registered here alongside the other window globals now that the fetch
        // networking surface is an isolated feature module.
        var messageChannelCtor = realm.NewConstructor(
            "MessageChannel",
            (in _) => _messaging.CreateChannel(),
            0);
        realm.DefineValue(window, "MessageChannel", messageChannelCtor);
        realm.SetProperty(realm.Global, "MessageChannel", messageChannelCtor);
        // CSSStyleSheet constructor (constructable stylesheets / adoptedStyleSheets — CSSOM).
        var cssStyleSheetCtor = realm.NewConstructor(
            "CSSStyleSheet", (in _) => BuildConstructedStyleSheetObject([]), 0);
        realm.DefineValue(window, "CSSStyleSheet", cssStyleSheetCtor);
        realm.SetProperty(realm.Global, "CSSStyleSheet", cssStyleSheetCtor);
        // getComputedStyle (CSSOM), co-located in the ComputedStyleBinding feature module (Phase 3).
        // Keeps the name, arity and constructable shape it had; the module separates the element from
        // the pseudo-element string off the call's own frame.
        realm.DefineValue(
            window,
            "getComputedStyle",
            realm.NewConstructor("getComputedStyle", (in c) => Dom.Features.ComputedStyleBinding.GetComputedStyle(this, in c), 2));
        windowBasicsScope.Dispose();

        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegWindowGlobals))
            RegisterWindowGlobals(document, window, console, fetchFn);
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegWindowObjects))
        {
            RegisterPerformanceObject(window);
            RegisterHistoryObject(window);
            RegisterObservationStubs(window);
            RegisterNavigatorObject(window);
            RegisterViewportObjects(window);
        }
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegContentPolyfills))
            RegisterContentRenderingPolyfills(document);
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegSecurityPolyfills))
            RegisterSecurityAndConstructorPolyfills(window);

        // Interface prototypes, which have to be applied *here* rather than where each object is
        // built: the constructors they point at are registered by the polyfill pass immediately
        // above, so an earlier link finds nothing and silently leaves Object.prototype behind. That
        // is the same ordering the document's lazy collections are built around.
        //
        // The document object is built rather than minted as a node wrapper, so it never passes the
        // choke point at all. The re-link covers the wrappers that *did* pass it but were minted
        // during attach, before the constructors existed — `document.documentElement` is eagerly
        // materialized as a value property, so the <html> wrapper is always one of them. Doing this
        // by sweeping the registry rather than naming that one keeps a future eager mint from
        // silently reverting to "Object".
        // Custom elements last among the constructor globals: its HTMLElement replaces the
        // non-constructible one the polyfill pass registers, and it keeps that interface's
        // prototype object so every element wrapper already linked to it stays linked.
        RegisterCustomElements(window);

        LinkToInterface(document, "HTMLDocument");
        foreach (var (node, wrapper) in _jsObjects.Entries)
        {
            // Handle inequality, not ReferenceEquals: the registry hands back a JsValue now, and
            // ReferenceEquals on a struct boxes both sides and answers false every time - which would
            // have re-prototyped the document wrapper the line above has just linked. CA2013 is an
            // error in this repository and caught it.
            if (wrapper != document)
                ApplyInterfacePrototype(wrapper, node);
        }
        // Worker (multithreading item #18). Registered after the window globals so the constructor
        // lands on a fully-built window, and before the global mirror below so it is reachable
        // unqualified the way page scripts spell it.
        _workers?.Register(realm, window);
        using (Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Measure(Broiler.HtmlBridge.Core.Diagnostics.BridgePhaseTrace.Phases.RegWindowMirror))
            MirrorWindowMembersOntoGlobal(realm, window);
    }

    /// <summary>
    /// Re-runs the <c>window</c> → global mirror over the members present now, picking up whatever
    /// the scripts that have run since the last call added to <c>window</c>.
    /// <para>
    /// Because <c>window</c> and the global object are distinct here, a script that assigns
    /// <c>window.foo = …</c> leaves the unqualified <c>foo</c> a <c>ReferenceError</c> — and that
    /// aborts the whole <c>&lt;script&gt;</c> that referenced it, not just the one statement. Every
    /// WPT support library has exactly that shape: <c>/css/support/interpolation-testcommon.js</c>
    /// closes by exporting <c>window.test_interpolation</c> and friends, and the test's own inline
    /// script then calls <c>test_interpolation({…})</c> unqualified. Broiler dropped every such
    /// page on the floor and rendered it blank (issue #1552 problems 4, 18 and 22 — the
    /// <c>transform</c>, <c>row-gap</c> and <c>column-gap</c> interpolation tests — and the ~100
    /// other <c>*-interpolation</c> tests behind them).
    /// </para>
    /// <para>
    /// Idempotent and cheap to repeat: a name the global already has is left alone, so a second
    /// call only copies what is genuinely new. A host that evaluates a document's scripts one at a
    /// time should call this after each one, which is where a browser would have had nothing to do
    /// because its <c>window</c> <em>is</em> the global object.
    /// </para>
    /// </summary>
    public void SyncWindowMembersOntoGlobal()
    {
        var window = WindowHandle;
        if (window.IsMissing)
            return;

        // THE MIRROR IS THE WORK; THE CACHE SWAP IS AN OPTIMISATION, AND THEY USED TO SHARE A GUARD.
        // Asking for a context first meant a bridge holding a realm and no context returned here
        // having done nothing -- silently, which for this method is the worst shape available: a
        // host calls it after every script, and what it skips is the window-to-global mirror that
        // makes `window.foo = 1` in one script visible as `foo` in the next. Under an engine whose
        // window and global are distinct objects that is the difference between a page working and
        // a page whose scripts cannot see each other. The mirror runs on the realm alone, so it now
        // runs whenever there is one.
        if (_jsContext is not { } context)
        {
            MirrorWindowMembersOntoGlobal(Realm, window);
            return;
        }

        // Same reasoning as RegisterDocument's swap, and the same bounded set: the source
        // MirrorWindowMembersOntoGlobal evaluates is a compile-time constant in this assembly. This
        // path matters more than it looks — a host calls it after *every* script, so the mirror was
        // being recompiled once per script per document (253 calls across 41 reftests, 12 ms each,
        // 8.2% of a WPT run). Restores the context's own cache, under which the host's page scripts
        // continue to compile.
        var previousCache = context.CodeCache;
        context.CodeCache = DictionaryCodeCache.Current;
        try
        {
            MirrorWindowMembersOntoGlobal(Realm, window);
        }
        finally
        {
            context.CodeCache = previousCache;
        }
    }

    // ── inert members, in the realm's vocabulary ───────────────────────────────────────────────

    /// <summary>
    /// The retired <c>UndefinedFunction</c> and <c>TrueFunction</c> (see DomBridge/JsObjects.cs) as the
    /// realm mints them — an inert member that answers <c>undefined</c>, or one that answers <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <c>NewConstructor</c> rather than <c>NewMethod</c> because the engine-built pair carried a
    /// prototype object and was therefore constructable, and preserving that is what makes this a
    /// refactor. (WebIDL says an operation should not be constructable; that is a pre-existing
    /// deviation shared by every constructable-function member of the registration hubs, and
    /// correcting it belongs in its own change.)
    /// </remarks>
    private JsValue UndefinedMember(string name, int length = 0) =>
        Realm.NewConstructor(name, static (in _) => JsValue.Undefined, length);

    /// <inheritdoc cref="UndefinedMember"/>
    private JsValue TrueMember(string name, int length = 0) =>
        Realm.NewConstructor(name, static (in _) => JsValue.True, length);

    // The three engine-typed adapters that used to live here — PinnedMethod, PinnedConstructor and
    // PinnedAccessor — are gone. Each existed because the feature module behind a member still took
    // Broiler.JS's own `in Arguments` frame, and an Arguments cannot be built from a JsCall, so the
    // function had to be minted by the engine and handed to the realm as a handle over it. Every one
    // of those modules reads a JsCall now, and every member the hubs install is minted by
    // realm.NewMethod / realm.NewConstructor / realm.DefineAccessor — which is where the shape each
    // adapter was careful to reproduce came from in the first place.
}

public sealed partial class DomBridge
{
    // Phase 3: the DOM traversal surface (NodeFilter, TreeWalker, NodeIterator, Range and
    // createComment) is installed by the co-located TraversalBinding feature module. This thin
    // entry point keeps the historical registration call site source-compatible.
    //
    // Both sides speak JSEAL now, so the document wrapper crosses as a handle over the same object
    // and nothing else crosses at all: the module reaches this realm through ITraversalHost.Realm
    // rather than being handed a script context. The two parameters this had — a context it did not
    // pass on and an engine object it converted — were the shape of the half-migrated seam, and the
    // seam is gone.
    private void RegisterDocumentTraversalApis(JsValue document) =>
        _traversal.RegisterDocumentApis(document);
}

public sealed partial class DomBridge
{
    /// <summary>
    /// Installs the typed-event constructor shims and the <c>MutationObserver</c> feature.
    /// </summary>
    /// <remarks>
    /// This took a script context it never used: both halves speak JSEAL — the shims are host script
    /// run through <c>Realm.EvaluateHostScript</c>, and the observer module asks the realm for
    /// itself — and the parameter survived only because the registration hub that calls it was not
    /// owned by the round that migrated this file. The hub has moved, so the adapter is gone.
    /// </remarks>
    private void RegisterDocumentEventsAndMutationObservers()
    {
        // Event / typed event constructors — DOM Level 4
        Realm.EvaluateHostScript(@"
                function Event(type, options) {
                    options = options || {};
                    var evt = document.createEvent('Event');
                    evt.initEvent(type, options.bubbles === true, options.cancelable === true);
                    return evt;
                }

                function CustomEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('CustomEvent');
                    evt.initCustomEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.detail !== undefined ? options.detail : null);
                    return evt;
                }

                function MouseEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('MouseEvents');
                    evt.initMouseEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0,
                        options.screenX !== undefined ? options.screenX : 0,
                        options.screenY !== undefined ? options.screenY : 0,
                        options.clientX !== undefined ? options.clientX : 0,
                        options.clientY !== undefined ? options.clientY : 0,
                        options.ctrlKey === true,
                        options.altKey === true,
                        options.shiftKey === true,
                        options.metaKey === true,
                        options.button !== undefined ? options.button : 0,
                        options.relatedTarget !== undefined ? options.relatedTarget : null);
                    return evt;
                }

                function FocusEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('FocusEvents');
                    evt.initFocusEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0,
                        options.relatedTarget !== undefined ? options.relatedTarget : null);
                    return evt;
                }

                function KeyboardEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('KeyboardEvents');
                    evt.initKeyboardEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.key !== undefined ? options.key : '',
                        options.location !== undefined ? options.location : 0,
                        options.ctrlKey === true,
                        options.altKey === true,
                        options.shiftKey === true,
                        options.metaKey === true,
                        options.repeat === true,
                        options.keyCode !== undefined ? options.keyCode : 0,
                        options.charCode !== undefined ? options.charCode : 0);
                    return evt;
                }

                function WheelEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('WheelEvents');
                    var modifiers = [];
                    if (options.ctrlKey === true) modifiers.push('Control');
                    if (options.altKey === true) modifiers.push('Alt');
                    if (options.shiftKey === true) modifiers.push('Shift');
                    if (options.metaKey === true) modifiers.push('Meta');
                    evt.initWheelEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0,
                        options.screenX !== undefined ? options.screenX : 0,
                        options.screenY !== undefined ? options.screenY : 0,
                        options.clientX !== undefined ? options.clientX : 0,
                        options.clientY !== undefined ? options.clientY : 0,
                        options.button !== undefined ? options.button : 0,
                        options.relatedTarget !== undefined ? options.relatedTarget : null,
                        modifiers.join(' '),
                        options.deltaX !== undefined ? options.deltaX : 0,
                        options.deltaY !== undefined ? options.deltaY : 0,
                        options.deltaZ !== undefined ? options.deltaZ : 0,
                        options.deltaMode !== undefined ? options.deltaMode : 0);
                    return evt;
                }

                function UIEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('UIEvents');
                    evt.initUIEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.detail !== undefined ? options.detail : 0);
                    return evt;
                }

                function InputEvent(type, options) {
                    options = options || {};
                    var evt = document.createEvent('InputEvent');
                    evt.initInputEvent(
                        type,
                        options.bubbles === true,
                        options.cancelable === true,
                        options.view !== undefined ? options.view : null,
                        options.data !== undefined ? options.data : null,
                        options.inputType !== undefined ? options.inputType : '',
                        options.isComposing === true);
                    return evt;
                }
            ", "polyfill:event-constructors");
        // MutationObserver (constructor/prototype + host bridge functions) is installed by the
        // Phase 3 MutationObserverBinding feature module.
        _mutations.RegisterDocumentApis();
    }

}

public sealed partial class DomBridge
{
    private void RegisterContentRenderingPolyfills(JsValue document)
    {
        // Google Search Compliance content-rendering / fidelity polyfills — Image, IntersectionObserver,
        // ResizeObserver, TextEncoder/TextDecoder, URL/URLSearchParams and AbortController — are a versioned
        // embedded .js asset (Phase 3 work item 6, externalized from inline C# string literals) evaluated
        // once here. See Polyfills/content-rendering-polyfills*.js.
        //
        // Host script: this repository authored it, it ships in this assembly, and it is not subject
        // to the page's content policy — which is what IJsSource.EvaluateHostScript promises, and the
        // reason a realm built without GuestEval still runs it.
        Realm.EvaluateHostScript(PolyfillAssets.ContentRendering, "polyfill:content-rendering");

        // document.cookie — get/set stub (in-memory, non-persistent). Host-driven (not pure JS), so it stays
        // here rather than in the JS asset. Order-independent of the pure-JS polyfills above.
        //
        // The store is a local the pair closes over — SetCookie takes it by reference — and the
        // assignment coerces through the realm, because `document.cookie = obj` is entitled to run
        // that object's toString exactly as the engine's own ToString() did here.
        var cookieStore = "";
        Realm.DefineAccessor(
            document,
            "cookie",
            (in _) => JsValue.String(cookieStore),
            (in c) => Dom.Features.WindowDocumentMiscBinding.SetCookie(ref cookieStore, in c));
    }

    private void RegisterSecurityAndConstructorPolyfills(JsValue window)
    {
        var realm = Realm;

        // window.crypto — the getRandomValues/randomUUID subset (Phase 3: co-located CryptoBinding module)
        var cryptoObj = Dom.Features.CryptoBinding.Build(realm);
        realm.DefineValue(window, "crypto", cryptoObj);
        realm.SetProperty(realm.Global, "crypto", cryptoObj);

        // window.CSS — the CSSOM namespace object (supports/escape). Host-driven rather than a
        // pure-JS polyfill because supports() has to answer from the CSS engine's own @supports
        // evaluator; answering from the CSSOM instead would claim support for everything, since
        // Broiler's CSSOM stores declarations without validating them.
        var cssObj = Dom.Features.CssBinding.Build(realm);
        realm.DefineValue(window, "CSS", cssObj);
        realm.SetProperty(realm.Global, "CSS", cssObj);

        // DOMException constructor
        RegisterDOMException(realm);

        // Node constructor with type constants
        RegisterNodeConstructor(realm);

        // Element/HTMLElement/HTMLUnknownElement/… interface globals. After Node, whose
        // @@hasInstance it installs.
        RegisterDomInterfaceConstructors(realm);

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
        RegisterSVGLength(realm);

        // AbstractRange/Range — the one DOM interface here whose members really live on its
        // prototype, so it has to be registered before the first document.createRange() can link a
        // range to it.
        _traversal.RegisterRangeInterface();

        // Blob/File, and the URL.createObjectURL pair they need. After the content-rendering
        // polyfills, which is where the URL constructor these attach to comes from.
        _blobs.RegisterInterfaces(realm);

        // ReadableStream (with its default reader and controller), ProgressEvent and FileReader,
        // plus blob.stream(). After blobs, because the stream reads a blob's bytes and the stream
        // member goes onto Blob.prototype.
        _streams.Register(_blobs);

        // ElementInternals/ValidityState/CustomStateSet — the objects a form-associated custom
        // element's attachInternals() hands back. Registered here rather than with the custom-element
        // pass because they are ordinary interface globals a page can name and feature-detect.
        ElementInternals.RegisterInterfaces();

        // Storage interface global — the name a page tests before it touches an area.
        RegisterStorageConstructor();

        // Notification — the interface, with its permission already settled at "denied" because
        // there is no surface to show one on (NotificationBinding).
        var notification = Dom.Features.NotificationBinding.Build(realm);
        realm.DefineValue(window, "Notification", notification);

        // MediaSource — the Media Source Extensions entry point, whose isTypeSupported answers for
        // the playback pipeline the HTML layer does not yet have (MediaCapabilityBinding, which also
        // installs canPlayType on the media elements themselves).
        var mediaSource = Dom.Features.MediaCapabilityBinding.BuildMediaSource(realm);
        realm.DefineValue(window, "MediaSource", mediaSource);
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
    private void RegisterStorageConstructor() =>
        Realm.EvaluateHostScript(@"
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
        ", "polyfill:storage-interface");
}

/// <summary>
/// Loads the embedded polyfill JavaScript assets (Phase 3 work item 6 — the content-rendering polyfills are
/// versioned <c>.js</c> resources embedded in <c>Broiler.HtmlBridge.Dom</c> rather than inline C# string
/// literals). Each asset is read from the assembly manifest once and cached for the process.
/// </summary>
internal static class PolyfillAssets
{
    private const string ResourcePrefix = "Broiler.HtmlBridge.Polyfills.";

    // Each script is kept in files of a readable size and evaluated as one: the parts are joined with a
    // newline, in this order, so a declaration in one part is in scope for the parts after it.
    private static readonly string[] ContentRenderingParts =
    [
        "content-rendering-polyfills.js",
        "content-rendering-polyfills.url.js",
        "content-rendering-polyfills.abort-and-fonts.js",
    ];

    private static readonly string[] StreamsParts = ["streams.js", "file-reader.js"];

    private static string? _contentRendering;

    private static string? _streams;

    /// <summary>
    /// The content-rendering polyfill bundle: <c>Image</c>, <c>IntersectionObserver</c>,
    /// <c>ResizeObserver</c>, <c>TextEncoder</c>/<c>TextDecoder</c>, <c>URL</c>/<c>URLSearchParams</c>,
    /// <c>AbortController</c> and the CSS Font Loading API. Evaluated once per document into the
    /// browsing-context global.
    /// </summary>
    public static string ContentRendering => _contentRendering ??= Load(ContentRenderingParts);

    /// <summary>
    /// <c>ReadableStream</c> (with its default reader and controller), <c>ProgressEvent</c> and
    /// <c>FileReader</c>. JavaScript rather than host functions for the same reason the
    /// specification is written that way — the queue, the pending read requests and the pull
    /// back-pressure are a state machine over promises.
    /// </summary>
    public static string Streams => _streams ??= Load(StreamsParts);

    private static string Load(string[] parts) => string.Join("\n", parts.Select(LoadPart));

    private static string LoadPart(string fileName)
    {
        var resourceName = ResourcePrefix + fileName;
        var assembly = typeof(PolyfillAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded polyfill asset not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
