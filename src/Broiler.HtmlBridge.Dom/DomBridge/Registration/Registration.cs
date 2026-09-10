using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

// Engine-typed for three reasons, and only three:
//
//   * RegisterDocument takes the script context the host hands Attach and swaps its code cache for
//     the process-shared one. That is a Broiler.JS optimisation with no JSEAL vocabulary — there is
//     no "compile once per process" member on the realm contract — so this is the floor rather than
//     a step not yet taken.
//   * AdoptRealm (DomBridge.Realm.cs) takes that same context to produce the realm, so the context
//     has to reach it.
//   * The three wrapper-root fields (_documentJSObject, _windowJSObject, _visualViewportJSObject)
//     are engine-typed, DictionaryCodeCache is the engine's own cache type, and
//     RegisterCustomElements (DomBridge/Registration/CustomElements.cs) takes the window as an
//     engine object. The fields and that hub are read by files outside this migration group, so the
//     two ToEngineObject calls below stay until those readers ask with a handle.
//
// The adapters that used to be a fourth reason are gone; see the note at the foot of this file.
// Everything the hubs install is built through the realm, and every module they register is handed
// that realm rather than the context — which is not only tidier: a module handed the context adopted
// it, and a second realm over one context has a job queue of its own that no event loop drains.
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

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
        var realm = _realm = AdoptRealm(context);

        // EventTarget.prototype's three methods, routed by receiver
        // (DomBridge.EventTargetInterface.cs). First, because every wrapper registration below asks
        // whether the routing is in place before installing its own copies — the document's included.
        // It depends only on the realm's own EventTarget, which the context already carries.
        RegisterEventTargetRouting();

        var document = realm.NewObject();

        // Map the document object to the canonical DomDocument so that the node-wrapper hub answers
        // the same object as the 'document' variable visible in JS. This ensures strict equality
        // checks like 'range.commonAncestorContainer === document' work. The registry is keyed on
        // the engine's own object, and a JSEAL handle carries that object rather than wrapping it,
        // so the two names below are one instance.
        var documentObject = Dom.Runtime.JsInterop.ToEngineObject(document);
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

        _documentJSObject = documentObject;
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
        var windowObject = Dom.Runtime.JsInterop.ToEngineObject(window);
        _windowJSObject = windowObject;

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
        RegisterCustomElements(context, windowObject);

        LinkToInterface(documentObject, "HTMLDocument");
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
    /// Copies every own property of <c>window</c> that the global object does not already have
    /// onto the global, preserving each property's descriptor.
    /// <para>
    /// In a browser <c>window</c> <em>is</em> the global object, so <c>getComputedStyle(el)</c>,
    /// <c>location.href</c>, <c>innerWidth</c> and <c>scrollTo(…)</c> are all valid unqualified.
    /// Here the two are distinct objects, so an unqualified reference to a member that lived only
    /// on <c>window</c> raised a <c>ReferenceError</c> — which does not merely skip that one
    /// statement, it aborts the whole script, taking every later statement and every listener the
    /// script would have registered with it. Unqualified spellings are idiomatic in the WPT
    /// corpus, so this silently emptied entire test pages.
    /// </para>
    /// <para>
    /// The mirror list used to be maintained by hand, one global assignment at a time (see the timer
    /// globals in RegisterWindowGlobals), and had drifted: <c>localStorage</c>,
    /// <c>matchMedia</c>, <c>location</c>, <c>alert</c>, <c>getComputedStyle</c>, <c>self</c>,
    /// <c>innerWidth</c>/<c>innerHeight</c>, <c>outerWidth</c>/<c>outerHeight</c>,
    /// <c>scrollX</c>/<c>scrollY</c>, <c>pageXOffset</c>/<c>pageYOffset</c> and
    /// <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c> were all missing. Sweeping instead of listing
    /// keeps the two in step as window members are added.
    /// </para>
    /// <para>
    /// Runs last, after every Register* pass, so it sees the fully-built window. It copies
    /// descriptors rather than values, so accessor-backed members (<c>innerWidth</c> and friends)
    /// stay live getters rather than freezing to a snapshot, and value members share the identical
    /// object — a listener added through the global <c>addEventListener</c> is therefore removable
    /// through <c>window.removeEventListener</c>. Properties the global already owns are left
    /// alone, so engine builtins and the explicit aliases above always win.
    /// </para>
    /// <para>
    /// This pass covers the members <em>the bridge</em> installs. A member a <em>page script</em>
    /// adds later — the shape every WPT support library has, <c>window.foo = …</c> in one
    /// <c>&lt;script&gt;</c> and an unqualified <c>foo(…)</c> in the next — appears after it has
    /// run, so a host that evaluates scripts one at a time must call
    /// <see cref="SyncWindowMembersOntoGlobal"/> between them.
    /// </para>
    /// </summary>
    private static void MirrorWindowMembersOntoGlobal(IJsRealm realm, JsValue window)
    {
        // The bridge now makes `window` the global object (see RegisterDocumentCore), so there is
        // nothing to copy and no gap to close — the sweep would define every own property of the
        // global onto itself. Returning here keeps that off the per-script path a host runs
        // (WptTestRunner calls SyncWindowMembersOntoGlobal after every script) instead of paying for
        // an Object.getOwnPropertyNames walk of the whole global that skips all of its own results.
        // The sweep is kept rather than deleted because it is still correct for any realm where the
        // two are genuinely distinct objects — which is exactly what a provider that does not declare
        // JsCapabilities.GlobalIsVariableScope may present, so the comparison is on the handles
        // rather than on an assumption.
        if (realm.Global == window)
            return;

        realm.SetProperty(realm.Global, "__broilerWindowForGlobalMirror", window);
        try
        {
            realm.EvaluateHostScript(@"
(function() {
  var w = __broilerWindowForGlobalMirror;
  var g = globalThis;
  var names = Object.getOwnPropertyNames(w);
  for (var i = 0; i < names.length; i++) {
    var name = names[i];
    if (name in g) continue;
    var descriptor = Object.getOwnPropertyDescriptor(w, name);
    if (!descriptor) continue;
    // A member that resists definition on the global (a frozen builtin slot, say) is
    // skipped rather than aborting the sweep for every member after it.
    try { Object.defineProperty(g, name, descriptor); } catch (e) {}
  }
})();", "bridge:window-global-mirror");
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.MirrorWindowMembersOntoGlobal",
                $"Error mirroring window members onto the global object: {ex.Message}", ex);
        }
        finally
        {
            realm.EvaluateHostScript(
                "delete globalThis.__broilerWindowForGlobalMirror;", "bridge:window-global-mirror-cleanup");
        }
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
        if (_windowJSObject is not { } window)
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
            MirrorWindowMembersOntoGlobal(Realm, Dom.Runtime.JsInterop.FromEngineObject(window));
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
            MirrorWindowMembersOntoGlobal(Realm, Dom.Runtime.JsInterop.FromEngineObject(window));
        }
        finally
        {
            context.CodeCache = previousCache;
        }
    }

    // ── inert members, in the realm's vocabulary ───────────────────────────────────────────────

    /// <summary>
    /// <c>UndefinedFunction</c> and <c>TrueFunction</c> (DomBridge/JsNative.cs) as the realm mints
    /// them — an inert member that answers <c>undefined</c>, or one that answers <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <c>NewConstructor</c> rather than <c>NewMethod</c> because the engine-built pair carries a
    /// prototype object and is therefore constructable, and preserving that is what makes this a
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
