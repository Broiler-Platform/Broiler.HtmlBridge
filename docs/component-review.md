# Component review — 2026-09-19

Reviewed from `b2c424b`, with CI/CD compared against the local Broiler.JS and
Broiler.VM repositories. CI/CD alignment and the P1 event-dispatch fix are implemented;
the remaining runtime findings are recommendations.

## Refactoring priorities

### 1. Consolidate event-listener dispatch and fix reentrancy (P1 — completed)

`Features/EventDispatchBinding.cs:151`, `DomBridge/Lifecycle.cs:548` and
`Features/MessagingBinding.EventTarget.cs:169` in `src/Broiler.HtmlBridge.Dom`
previously repeated the same snapshot/invoke/remove loop. Each removed `once` listeners after
calling them, and none checked whether a snapshotted registration was removed
by an earlier listener.

Two public `ScriptEngine.Execute` probes reproduced the consequences on an element:

- A `once` listener dispatching the same event again ran twice. The probe bounded
  recursion at two calls; an unbounded version can keep recursing.
- Listener A removed listener B during dispatch, yet the recorded calls were
  `AB`, rather than `A`.

The dispatch paths now share `EventListenerBinding.InvokeListeners`, with reference
identity and a removal flag on each registration. `once` registrations are removed
before invocation, and snapshots skip registrations removed by earlier callbacks.
Re-registering a callback creates a distinct registration. The form-submission path
uses the helper too, and resetting the listener registry invalidates active snapshots.
Capture filtering, passive flags, exception handling and owner-window routing remain
with their existing callers. These requirements follow the
[DOM listener invocation algorithm](https://dom.spec.whatwg.org/#concept-event-listener-inner-invoke).

Added 44 regression cases covering elements, documents, windows, message ports,
frames, form submission and registry reset. Twenty-one of the new dispatch cases
failed against the original implementation. After the fix, both full builds pass
without warnings: Release has 975 passing tests and Release-VM has 1,095, with
23 existing skips in each. The engine-neutrality budget is unchanged.

### 2. Keep animation-frame cancellation effective until invocation (P2)

`src/Broiler.HtmlBridge.Dom/Runtime/BrowserEventLoop.cs:296` removes every frame
callback into a snapshot before running timers. `CancelAnimationFrame` only
removes callbacks from the original dictionary, so cancellation during the batch
cannot affect the snapshot.

A probe scheduled a frame callback and a zero-delay timer that cancelled its ID.
The timer ran first, but the frame callback still ran. Snapshot IDs, then check
and remove each callback immediately before invoking it. Preserve registration
order and defer newly registered callbacks to the next batch. This also handles
one frame callback cancelling another, as required by the
[HTML animation-frame algorithm](https://html.spec.whatwg.org/multipage/imagebitmap-and-animations.html#run-the-animation-frame-callbacks).

### 3. Bound cancellation bookkeeping during incremental draining (P2)

`src/Broiler.HtmlBridge.Dom/Runtime/BrowserEventLoop.cs:152` adds every cancelled
ID to `_clearedTimerIds`, even when the ID never existed. The set is cleared only
by `DrainAll` or `Clear`; interactive sessions use `DrainStep`.

Cancelling 10,000 distinct IDs while repeatedly calling `DrainStep` left 10,000
entries retained even though `HasPendingWork` was false. Track cancellation only
for pending or in-flight registrations and retire it when the batch completes.
Keep tests for cancellation from another callback and during interval rescheduling.

### 4. Share the bounded load-settling loop (P2)

`src/Broiler.HtmlBridge.Scripting/ScriptEngine.cs:433` and
`src/Broiler.HtmlBridge.Scripting/InteractiveSession.cs:142` duplicate the
microtask/timer loop, including its virtual-time and iteration limits. They have
already diverged: `ScriptEngine` records and logs exhaustion, while an interactive
settle reaches the same limit and returns HTML without an exhaustion result.

Extract one internal drain operation returning a settled/exhausted outcome, with
cancellation and an optional between-batches callback. Keep serialization in the
caller. Test an ordinary finite timer sequence and a callback that reschedules
itself without advancing virtual time.

### 5. Reduce historical commentary and narrow warning suppression (P3)

Several source and project files carry long migration histories alongside small
implementations, including `DomBridge.cs`, `InteractiveSession.cs` and
`Directory.Build.props`. Some history describes dependencies that have since
changed. Keep current invariants and ownership rules near the code; move the
historical rationale into architecture documentation or rely on Git history.

`Directory.Build.props:113` also suppresses the nullable-warning family globally
despite individual projects enabling nullable analysis. Move necessary exceptions
to the affected projects or members and reduce them incrementally. Preserve the
existing `CA2013` and `CS8073` error checks.

## CI/CD changes applied

- Removed the submodule-pin job, recursive checkout, obsolete `.gitmodules` and
  unused engine-root properties. HEAD contains no gitlinks; the old pins job
  therefore failed every run before publication could proceed.
- Added Node.js 24 and the existing preview-version tests to CI, and moved package
  creation/upload to Windows Release, matching both reference components.
- Retained Linux/Windows Release coverage, Linux Release-VM coverage, test report
  uploads, configuration assertions and the engine-neutrality gate.
- Made the neutrality gate count engine NuGet packages as well as source project
  references. Tightened the direct-dependency budgets to the actual graph and
  added seven fixture-based regression tests.
- Raised executed-test floors to 900/1,000. The old VM floor of 700 was below the
  ordinary suite's current 931 tests, so losing every VM-only test could pass it.
- Aligned the packaging output-path calculation with Broiler.JS/Broiler.VM and
  corrected the README and release-workflow comments for package consumption.
- Confirmed that the publish workflow matches both reference workflows apart from
  the component name: one resolved preview version, reusable CI, artifact download,
  isolated consumer restore, and conditional publication. Manual runs remain dry
  runs by default.

## Initial CI/CD validation

On Windows with .NET SDK 10.0.401:

| Check | Result |
| --- | --- |
| Release build | Passed, zero warnings/errors |
| Release tests | 931 passed, 23 skipped |
| Release-VM build | Passed, zero warnings/errors |
| Release-VM tests | 1,051 passed, 23 skipped |
| VM configuration properties | Optimized; `BROILER_VM_JS` present; `RELEASE` occurs once |
| Preview-version tests | 5 passed |
| Engine-neutrality regression tests | 7 passed |
| Engine-neutrality recount | All eight project budgets match |
| Pack with explicit preview version | Eight packages and their symbol packages validated |
| Workflow syntax | YAML parsed; Bash steps passed syntax checks |
| Publish-workflow comparison | Matches both reference components after normalizing the name |
| Isolated GitHub feed consumer restore | Failed with HTTP 401; local credentials lack package-feed access |

The build and pack checks used the existing local NuGet cache. Fresh authenticated
feed restore and execution on hosted Linux/Windows runners still need CI validation.
No packages were published. The runtime probes demonstrated the findings above;
they do not change the passing test-suite counts.
