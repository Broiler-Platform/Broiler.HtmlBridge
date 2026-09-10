namespace Broiler.HtmlBridge.Jseal;

/// <summary>
/// What an engine can do, declared rather than discovered.
/// </summary>
/// <remarks>
/// <para>
/// A host that needs a feature an engine lacks has two honest options — degrade, or refuse — and
/// exactly one dishonest one, which is to call and hope. The bridge already does the dishonest thing
/// once, in <c>EngineModuleSupport</c>: it probes for ES-module support by running a module and
/// checking the binding, guarded by a five-second timeout because on an engine without the fix the
/// probe <em>hangs</em> rather than failing. That is what discovery costs when a contract could have
/// said so.
/// </para>
/// <para>
/// <b>These are capabilities, not versions.</b> Nothing here names an engine or a release. A provider
/// answers for the engine it was built against, and a host branches on the answer.
/// </para>
/// </remarks>
[Flags]
public enum JsCapabilities : uint
{
    /// <summary>An engine that can do none of the below. Not useful; the zero value exists so a
    /// provider under construction has something to return.</summary>
    None = 0,

    /// <summary>
    /// Runs JavaScript this repository authored — the embedded polyfill assets and the 54 in-source
    /// evaluations. An engine without a run-time compiler can still have this, by compiling that
    /// source when the engine is built. See <see cref="IJsSource"/>.
    /// </summary>
    HostScriptSource = 1 << 0,

    /// <summary>
    /// Runs JavaScript the page supplied — <c>eval</c> and <c>new Function</c>. A realm built for a
    /// page whose Content-Security-Policy forbids evaluation does not have this, on any engine.
    /// </summary>
    GuestEval = 1 << 1,

    /// <summary>
    /// Binds a static ES-module import end to end, so <c>import { x } from '…'</c> resolves to a
    /// value. This is what <c>EngineModuleSupport</c> probes for today.
    /// </summary>
    Modules = 1 << 2,

    /// <summary>Resolves a dynamic <c>import()</c> through a host-supplied module map.</summary>
    DynamicImport = 1 << 3,

    /// <summary>
    /// Exposes promises to the host: a pending promise can be created and settled from host code.
    /// Without it, <c>fetch</c>, <c>customElements.whenDefined</c> and the streams polyfill have no
    /// deferred result to hand back.
    /// </summary>
    Promises = 1 << 4,

    /// <summary>
    /// Supports exotic objects whose property lookup the host defines — live collections indexed by
    /// number and by name, <c>CSSStyleDeclaration</c>'s dashed properties, <c>Storage</c>'s keys.
    /// Six of the bridge's objects need it. See <see cref="IJsExotic"/>.
    /// </summary>
    ExoticObjects = 1 << 5,

    /// <summary>
    /// The global object doubles as the realm's variable scope, so a <c>var</c> or a function
    /// declaration at the top level of a script becomes a property of it.
    /// </summary>
    /// <remarks>
    /// The bridge depends on this and does not know it does: nested browsing contexts recover a
    /// frame's declarations by diffing <c>Object.getOwnPropertyNames(globalThis)</c> across the
    /// evaluation, which only finds anything on an engine where declarations land there.
    /// </remarks>
    GlobalIsVariableScope = 1 << 6,

    /// <summary>
    /// A second realm can be created on another thread and values moved between the two by structured
    /// clone — what a Worker needs.
    /// </summary>
    WorkerRealms = 1 << 7,

    /// <summary>
    /// A host function may call back into JavaScript while the engine is inside a host call — what an
    /// event listener, a promise reaction and a <c>toString</c> coercion all are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the capability that decides whether an engine can host a DOM at all.</b> An engine
    /// without it can run a page's script; it cannot dispatch a <c>click</c>.
    /// </para>
    /// <para>
    /// <b>These remarks used to name Broiler.VM as the engine that does not have it, and the reason
    /// they gave was wrong in a way worth keeping.</b> The reason given was that every capability
    /// that profile imports is declared non-reentrant and its core refuses a re-entrant call for the
    /// duration of a host frame. The first half was true and the second was true only of a
    /// capability that DECLARES non-reentrance - the refusal is keyed on the declaration, and
    /// nothing there had ever declared the other mode. The deeper error was reading the capability
    /// channel as the only way host code can reach a guest: on that engine a host object is an
    /// ordinary object in the realm, so a listener call never crosses the core and never meets a
    /// gate. See <c>docs/jseal.md</c> for what a provider there would now cost.
    /// </para>
    /// </remarks>
    ReentrantHostCalls = 1 << 8,

    /// <summary>
    /// Minting an <c>ArrayBuffer</c> over host bytes and reading one back. See
    /// <see cref="IJsValues.NewArrayBuffer"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is a capability rather than an assumption because a realm can genuinely lack it.</b> One
    /// engine has <c>ArrayBuffer</c> unconditionally; the other builds the binary intrinsics only for
    /// a composition that admits its binary surface, so a host that named surfaces explicitly could
    /// get a realm with no <c>ArrayBuffer</c> on the global at all. Calling and hoping is what a
    /// capability exists to replace.
    /// </para>
    /// <para>
    /// <b>It is in <see cref="Document"/> because the bridge's own polyfills cannot install without
    /// it.</b> <c>Polyfills/streams-and-file-reader.js</c> names <c>Uint8Array</c>, and on an engine
    /// whose binary surface is optional an artifact naming a global of a declined surface is refused
    /// at verification rather than at the line that reads it. So a realm without this cannot carry
    /// the streams asset, and without that asset there is no <c>ReadableStream</c>, no
    /// <c>response.body</c>, no <c>blob.stream()</c> and no <c>FileReader</c>. That is not a page
    /// served in a degraded way; it is a page that does not load.
    /// </para>
    /// </remarks>
    BinaryData = 1 << 9,

    /// <summary>Everything a document-bearing page load needs.</summary>
    Document = HostScriptSource | Promises | ExoticObjects | GlobalIsVariableScope |
               ReentrantHostCalls | BinaryData,
}
