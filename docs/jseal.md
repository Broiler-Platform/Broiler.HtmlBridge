# JSEAL — the JavaScript Engine Abstraction Layer

JSEAL is the seam between the HTML bridge and a JavaScript engine. The bridge binds a document
against JSEAL's contracts; a **provider** implements those contracts over one engine; and which
provider serves a page is a registration, not a compile-time fact about the bridge.

This document says what the contracts are, why they have the shape they do, how to add an engine, and
— the part most worth reading before planning work — exactly how much of the bridge has actually
moved and what is still in the way.

## The problem it exists to solve

The browser already had an engine abstraction before this one: `IScriptEngine`, in
`src/Broiler.HtmlBridge.Scripting`. It is a good interface and it is at the wrong altitude.

`IScriptEngine` abstracts *running a list of scripts*. That is enough for the document-free entry
points, and `VmScriptEngine` really does serve those on the Broiler.VM JavaScript profile. It is not
enough for a page, because the moment a document is involved the engine reappears in the signature:

| Where | What leaks |
|---|---|
| [`IDomBridgeRuntime.cs:50,52`](../src/Broiler.HtmlBridge.Core/Dom/IDomBridgeRuntime.cs) | `Attach(JSContext, …)` — the "engine-neutral" core assembly takes a Broiler.JS type |
| [`InteractiveSession.cs:21`](../src/Broiler.HtmlBridge.Scripting/InteractiveSession.cs) | `internal InteractiveSession(JSContext …)` — the only session type cannot be built from outside Broiler.JS |
| `src/Broiler.HtmlBridge.Dom` | 891 `Broiler.JavaScript` references across 250 files — the DOM's JavaScript objects *are* Broiler.JS objects |

So `RenderingPipeline` can be handed any `IScriptEngine` it likes and a page still runs on Broiler.JS,
because the only method it calls — `ExecuteInteractive` — has to return something built from a
`JSContext`. [`docs/vm-javascript-profile.md`](vm-javascript-profile.md) states this plainly and lists
it as the reason no page load runs on the VM.

**A realm is the right altitude.** A realm is what a document has. JSEAL's central type is therefore
`IJsRealm`, and the end state of the migration is `Attach(IJsRealm, …)`.

## Layering

```
                    ┌─────────────────────────────────────────┐
                    │  Broiler.Browser.Core / RenderingPipeline│
                    └───────────────────┬─────────────────────┘
                                        │
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.HtmlBridge.Scripting            │   owns engine selection;
                    │    ScriptEngine, InteractiveSession      │   registers the providers
                    │    JsEngineHosting                       │   this build linked
                    └───────────────────┬─────────────────────┘
                                        │
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.HtmlBridge.Dom                  │   the DOM bindings —
                    │    DomBridge + ~130 feature bindings     │   the "base component"
                    └───────────────────┬─────────────────────┘
                                        │  binds the document against
                    ┌───────────────────▼─────────────────────┐
                    │  Broiler.HtmlBridge.Jseal      ◄─ JSEAL  │   ZERO ProjectReferences
                    │    JsValue JsCall IJsRealm              │
                    │    JsCapabilities JsEngineRegistry      │
                    └───────────────────┬─────────────────────┘
                                        │  implemented by
              ┌─────────────────────────┴─────────────────────────┐
              │                                                   │
  ┌───────────▼──────────────┐                    ┌───────────────▼──────────────┐
  │ …Jseal.BroilerJs         │                    │ …Jseal.<NextEngine>          │
  │   over Broiler.JavaScript│                    │   (none yet — see below)     │
  └──────────────────────────┘                    └──────────────────────────────┘
```

**`Broiler.HtmlBridge.Jseal` has no `ProjectReference` and no `PackageReference`, and that is the
whole neutrality claim.** Everywhere else in this repository engine neutrality is asserted by
grepping for a namespace. Here it is asserted by the compiler: an assembly that references nothing
cannot name a `Broiler.JavaScript` type, a `Broiler.VM` type, or anything either drags in. Adding a
reference is a diff a reviewer sees, and `scripts/check-engine-neutrality.sh` fails if the element
ever gains a child.

The corollary is that JSEAL cannot reach `Broiler.HtmlBridge.Core` either — no `ContentSecurityPolicy`,
no `MicroTaskQueue`, no `RenderLogger`. That costs less than it looks: what JSEAL needs from those is
a *capability*, not a dependency.

## The value model

`JsValue` is a `readonly struct` of three fields — a kind, a `double`, and an `object?` reference.
24 bytes.

**Why not an interface hierarchy.** The obvious design is `IJsValue` with `IJsObject`/`IJsFunction`
beneath it, implemented by provider types deriving from the engine's own. For objects that is exactly
what happens. It cannot work for primitives: Broiler.JS's `JSNumber` is `sealed`, so a provider cannot
derive from it, and an interface would force a carrier allocation per number on a path that already
runs per property read.

**Why this layout in particular.** Tag + double + reference is what Broiler.VM's own `JsValue`
([`JsValue.cs:83`](../Broiler.VM/src/Broiler.VM.Profile.JavaScript/JsValue.cs), `internal readonly
struct`) chose, for the reason it records: the collector is the CLR's, so a value that must sometimes
hold a managed reference cannot be a NaN-boxed word. Matching it means a future Broiler.VM provider
re-tags rather than converts.

**`Missing` is kind zero.** Broiler.JS's `Arguments` indexer returns a CLR `null` — not `undefined` —
past the end, and 598 argument reads in the bridge depend on telling those apart before coercing:
`scrollTo()` and `scrollTo(undefined)` are different calls. Broiler.VM's `JsType.Empty` is zero for
the same reason.

**The reference is the engine's own value, not a wrapper.** Under the Broiler.JS provider an object
handle carries the `JSObject` the engine already has. That is what keeps wrapper identity working
across a half-migrated bridge: `el === el`, the seven `ConditionalWeakTable<JSObject, …>` the bridge
keys on wrapper identity, and `JsObjectRegistry`'s reverse map all keep asking the question they
always asked. A provider whose engine hands out pointers or stack slots is expected to canonicalise
handles itself.

**`==` is `===`; `Equals` is reflexive.** The two deliberately differ on NaN alone — the same split
`System.Double` makes, and for the same reason: the bridge stores JS values in `List<>`s (event
listeners, collection contents) and the BCL reaches `Equals`/`GetHashCode` when it searches them, so a
value that is not equal to itself could not be found in the collection it was put into.

**`JsValue.ToString()` never enters the engine.** This is not a small point. On Broiler.JS,
`JSValue.ToString()` on an object *runs the object's JavaScript `toString`* — so what reads like a
debug rendering is the observable ECMAScript coercion, and can execute page script, throw, or
re-enter the realm. The bridge has 343 such calls. JSEAL's renders `[object]` and the real coercion is
`IJsValues.ToJsString`, which is honest about entering the engine.

## The realm

`IJsRealm` aggregates six narrow contracts, the way `IScriptEngine` was split in this repository's
Phase 8. Every binding depends on the aggregate, so nothing at a call site gets longer; the split is
for the provider implementing them one at a time, and for the reviewer asking what an engine must be
able to do.

| Contract | What it covers |
|---|---|
| `IJsValues` | creating objects, arrays, methods, constructors, exotics; the two coercions that can run user code |
| `IJsMembers` | `DefineValue` / `DefineAccessor` / `DefineIndex`, reads and writes, own keys, prototype link |
| `IJsCalls` | `Invoke`, `Construct`, and raising an `Error` or a `DOMException` from host code |
| `IJsJobs` | the microtask queue, and promises the host can settle |
| `IJsSource` | evaluating **host** script and **guest** source — separately |
| `IJsExotic` | host-completed property lookup, for the six DOM objects whose members are not a fixed list |

Three shapes in there are load-bearing.

**The realm is on the call, not ambient.** `JsCall` carries its `IJsRealm`. A
`[ThreadStatic] Js.Current` would read better at every one of the ~900 callback sites and would be
wrong here: three threads run one page's JavaScript. `ScriptEngine` installs a synchronization context
before building the realm precisely because promise and generator continuations were resuming on the
thread pool; `BrowserEventLoop`'s queues are all `ConcurrentDictionary` for the same reason; a Worker
builds a second realm on a thread of its own. An ambient realm turns each of those into a null
reference at run time that no compiler can see — in code being migrated file by file, which is where a
missing realm is exactly the mistake a reviewer cannot spot.

*This hazard does not go away underneath.* Broiler.JS resolves realm intrinsics from a thread-static:
`new JSObject()` reads the current context's `Object.prototype`, and with no context current it mints
an object with a null prototype and **no error**. The provider pays for that with a scope on every
entry point, so the contract does not have to expose it.

**`NewMethod` and `NewConstructor` are different methods.** WebIDL says only interface objects are
constructors: `el.setAttribute.prototype` is `undefined` and `new el.setAttribute()` throws. On
Broiler.JS that is one boolean — `createPrototype: false` — which also makes the function
non-constructable, because `JSConstructorOperations.IsConstructor` tests
`prototype != null || IsConstructable`. The same boolean is a load-bearing memory fix: an element
wrapper's members were each minting an unreachable prototype object plus its `constructor`
back-reference, and dropping them was the difference between a WPT test fitting the memory budget and
being aborted (see [`DomFunction.cs`](../src/Broiler.HtmlBridge.Dom/DomBridge/DomFunction.cs)). Sixteen
interface objects a page may legitimately `new` use `NewConstructor`; everything else uses `NewMethod`.

**Ordinary properties beat exotic handlers.** `IJsExotic` is consulted only when the object's own
property storage found nothing. This is what WebIDL's named-property semantics require and what all
six existing subclasses do — each calls the base lookup first. Getting it backwards is silently wrong
rather than loudly wrong: a collection that happens to contain an element named `item` would start
shadowing its own `item()` method.

**Host script and guest source are separate capabilities.** The bridge authors JavaScript — two
embedded `.js` assets totalling 1,891 lines, plus 55 `Eval` sites across 28 files that install
polyfills, probe for a global, or re-link a prototype. That source ships with this repository and is
not subject to the page's Content-Security-Policy. Guest source is what `eval`, `new Function` and a
dynamic `import()` ask for on the page's behalf, and is exactly what a CSP may forbid. Conflating the
two is what makes an engine with no run-time compiler look impossible to host: it can support the
first by compiling the bridge's own JavaScript when the engine is built, and refuse the second.

## Capabilities

`JsCapabilities` is declared, not discovered. The bridge currently *discovers* one — `EngineModuleSupport`
runs a real ES module and checks the binding, on a worker thread behind a five-second timeout, because
on an engine without the fix the probe **hangs** rather than failing. That is what discovery costs when
a contract could have said so.

A realm's capabilities may be **narrower** than its provider's but never wider: a page whose CSP forbids
evaluation gets a realm without `GuestEval` from an engine that has it.

One flag decides whether an engine can host a DOM at all:

> **`ReentrantHostCalls`** — a host function may call back into JavaScript while the engine is inside a
> host call. An event listener, a promise reaction and a `toString` coercion are all exactly that. An
> engine without it can run a page's script; it cannot dispatch a `click`.

## Choosing an engine

`JsEngineRegistry` is a process-wide registry keyed by provider name. `BROILER_JS_ENGINE=<name>`
overrides the default for a run.

**What is still a build-time decision, and should be:** whether an engine's assemblies are *linked at
all*. That remains a `ProjectReference` under a configuration condition, which is what keeps `Debug`
free of Broiler.VM and keeps `scripts/check-component-graph.sh`'s second run meaningful. The build
decides which providers are present; the registry decides among the ones that are.

Two things want more than one engine in a process, and a `#if` cannot give either: a conformance suite
that runs the same assertions against every registered provider, and a bisect asking whether a page
renders differently on the other engine — a question about a *run*, not a *build*.

## Adding an engine

1. A new project `src/Broiler.HtmlBridge.Jseal.<Engine>` referencing `Broiler.HtmlBridge.Jseal` and the
   engine.
2. `IJsRealm` over the engine's realm. Split it by capability, one file per contract — the Broiler.JS
   provider does, and the shape is worth copying.
3. `IJsEngineProvider` with a stable lower-case hyphenated `Name`, an honest `Capabilities`, and a
   `[ModuleInitializer]` that registers it.
4. Optionally `IJsRealmAdoption`, if the engine's realms can be host-constructed and then wrapped.
5. Add it to `eng/jseal-budget.json` — a provider is the one kind of project whose engine references
   are *supposed* to be high.
6. Run the conformance suite. It is data-driven over `JsEngineRegistry.All`, so a new provider adds no
   test code.

The trap to know about before starting: **the provider is where ambient engine state is paid for.**
Broiler.JS's is the thread-static current context described above. A provider that skipped it would
work in every test that happened to run right after an evaluation.

## Where the migration actually stands

`eng/jseal-budget.json` is the ratchet. Per project it records engine references, engine project
references, and guest-eval sites; `scripts/check-engine-neutrality.sh` fails when a count *rises* and
reports when one *falls* so the budget is lowered in the same commit. A CI job runs it. This is what
lets the remaining files migrate incrementally instead of in one cliff-edge merge, and what stops
in-flight feature work re-adding coupling behind the migration's back.

`Broiler.HtmlBridge.Dom/Runtime/JsInterop.cs` is the seam between the migrated and unmigrated halves,
and it is scaffolding meant to be deleted. It is a cast, not a conversion — a JSEAL object handle
already carries the engine's `JSObject` — and every use of it is one place the migration has not
reached.

**Where it stands as this landed.** Nine feature bindings are migrated and each reaches *zero* engine
tokens — `ConsoleBinding`, `Base64Binding`, `WindowBarPropBinding`, `ScreenOrientationBinding`,
`PerformanceMemoryBinding`, `StorageQuotaBinding`, `ClassListBinding`, `CryptoBinding` and
`NodeConstantsBinding`. `Broiler.HtmlBridge.Dom`'s engine references fell from **891 to 861**. The
conformance suite is 51 tests over `JsEngineRegistry.All`.

**That is about 3% of the coupling, and the remaining 97% is the honest headline.** What has been
proved is not that the bridge is portable — it is that the seam holds under the real build for real
bindings: a migrated binding names no engine type, its objects are indistinguishable to a page from
the ones the unmigrated half builds, and both halves share one realm. The budget file is the number
to trust; this paragraph will go stale and it says so.

The conformance suite earned its place immediately by finding two defects in the provider it was
written to check — `JsCall.NewTarget` always reporting `Missing` inside a host constructor, and an
exotic object's supported names being filtered out of `Object.keys` and object spread. Both are
fixed; both would have reached a page.

## Broiler.VM: why there is no provider yet

There is no `Broiler.HtmlBridge.Jseal.Vm`, and **no JSEAL of any shape would let one serve a page
today.** This is not an unfinished port. Four facts, each checked against the submodule at the pinned
commit:

1. **The JavaScript profile's value model is `internal`.**
   [`JsValue.cs:83`](../Broiler.VM/src/Broiler.VM.Profile.JavaScript/JsValue.cs) is
   `internal readonly struct JsValue`; `JsObject`, `JsFunction`, `JsRealm*` and `JsProxy` likewise. The
   only public value type is `JavaScriptValueKind`, which has three members — `Undefined`, `Boolean`,
   `Number` — and says of itself that the representation "is provisional and JS-4 lands what replaces
   it".
2. **No host capability can accept or return an object.** `VmCapabilityKind`
   ([`VmHostCapabilityDescriptor.cs:29`](../Broiler.VM/src/Broiler.VM.Abstractions/VmHostCapabilityDescriptor.cs))
   has exactly two members, `Value` and `ArtifactProvider`. The profile imports exactly three
   capabilities (`JavaScriptProfile.cs:292`, `:325`, `:374`) and their signatures are bytes-shaped.
   **A DOM accessor is a host call that returns an object.**
3. **No capability may re-enter the guest.** All three are declared `NonReentrant`
   (`JavaScriptProfile.cs:298`, `:331`, `:380`), and `ReentrantIntoInvokingRuntime` appears nowhere in
   the Broiler.VM tree except its own declaration and one predicate that tests for it. The core holds
   a per-runtime in-capability flag and refuses a re-entrant call — enforced, not merely declared.
   **An event listener is a host→guest call from inside a host frame.**
4. Consequently `JsCapabilities.Document` — which requires `ReentrantHostCalls` — cannot be declared
   by a VM provider, and a host that branches on it correctly declines to load a page.

**The upstream ask, which is the real deliverable of this layer.** It is smaller and more answerable
than "publish your value model":

- a `VmCapabilityKind` whose signature admits an opaque host object in and out;
- a capability declared `ReentrantIntoInvokingRuntime` for the DOM binding surface;
- a public value type with a host-attachable state slot, so `JsValue`'s reference can be a weak-table
  key the way `JSObject` is today.

Until those exist, `VmScriptEngine` remains the right integration for Broiler.VM: it serves the
document-free entry points on the profile and delegates the document-bearing ones, exactly as
[`docs/vm-javascript-profile.md`](vm-javascript-profile.md) describes.

**The measurement that answers "is this working".** Not "the bridge no longer names Broiler.JS" —
that is relocation, and any of these designs achieves it. The number that matters is how many DOM
operations are expressible without a host→guest re-entry and without a host capability returning an
object. That is what the second engine is waiting on, and it is worth reporting beside the reference
count rather than instead of it.
