# HtmlBridge compatibility TODOs: Sannysoft and CreepJS

**Current status:** the practical scope is complete locally with preview.32;
Canvas/SVG, render projection and validation are described below. Realm redesign
and larger graphics/media subsystems are deferred. The following baseline is historical.

Baseline **2026-10-09** from Browser's published-package reinvestigation. Tested
HtmlBridge `0.1.0-preview.25+784cd50f76897a1b8682abd2543f881f546c18b1`, JavaScript
preview.7 and JSEAL preview.6, using Browser `a18f0b6`. Both live sites render;
neither is fully compatible. No assembly overlay or site-script bypass was used.

Browser owns the
[cross-component roadmap](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/fingerprinting-compatibility-roadmap.md),
[dated report](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/fingerprinting-reinvestigation-2026-10-09.md)
and [recorded evidence](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/repros/fingerprinting-observed-2026-10-09.json).
This document owns the bridge implementation checklist, using the same FP IDs.
It complements the [control-surface roadmap](html-control.md) and
[component review](component-review.md); it does not replace their other work.

## Verified baseline

- [x] Sannysoft's image load/error, PluginArray, parser-time write and innerText
  fixes are consumed through preview.25. Its live result has 20 scanner rows,
  fingerprint JSON with 32 keys and five populated canvas hashes.
- [x] The 1280×900 viewport/screen probe passes with Browser's current wiring.
- [x] CreepJS fragment construction, frame indexing/document separation, console
  grouping and Crypto interface exposure pass the reduced fixture.
- [x] Upstream proxy recursion is contained by JavaScript preview.7; both sites
  survive Browser analysis, window comparison and ordinary image capture.

These are scoped observations, not claims of complete interface conformance.
In particular, separate frame documents still share JavaScript intrinsics.

## P1: implementation sequence

### FP-01 — worker environment (complete)

Observed: a same-origin HTTP worker posts a message successfully, but reports
`navigator`, `WorkerNavigator`, `WorkerGlobalScope` and
`DedicatedWorkerGlobalScope` undefined. Live CreepJS throws on its worker thread
and then reads missing workerScope fields. Its initial worker detection uses
`!self.document && self.WorkerGlobalScope`.

- [x] Extend [JSWorker.InstallWorkerGlobals](../src/Broiler.HtmlBridge.Dom/Features/JSWorker.cs)
  with the worker-global prototype chain and realm-local WorkerNavigator.
- [x] Reuse host identity/language/concurrency data from
  [NavigatorIdentityBinding](../src/Broiler.HtmlBridge.Dom/Features/NavigatorIdentityBinding.cs)
  without copying page-realm JavaScript objects into the worker.
- [x] Cover descriptors, illegal construction where applicable, receiver checks,
  worker-only exposure and same-object behavior against the
  [HTML worker contract](https://html.spec.whatwg.org/multipage/workers.html#the-workernavigator-interface).
- [x] Verify successful worker-branch detection and posted identity data in the
  Browser fixture, then rerun CreepJS. Require removal of the missing-navigator
  rejection and record any subsequent blocker before claiming worker completion.
- [x] Preserve structured clone, termination, CSP and event-loop behavior with
  both supported engine providers. This investigation exercised Broiler.JS only.

### FP-02 — Window/Navigator interface shape (complete)

- [x] Start at [window registration](../src/Broiler.HtmlBridge.Dom/DomBridge/Registration/Window.cs).
  `window` and `navigator` objects exist, but the interface globals are undefined;
  CreepJS catches a missing `Window` ReferenceError in its Headless collector.
- [x] Supply proper prototype chains, branding, property descriptors and receiver
  validation. Test parent and child windows; a constructor-name stub is insufficient.
- [x] Verify that the live collector advances and audit the next reached interface
  failures. Keep this scoped interface work separate from FP-06 realm isolation.
  See [validation note](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/fp02-window-navigator-fix-2026-10-09.md).

### FP-03 — geometry and rectangle interfaces (complete)

- [x] Repair [ElementGeometryBinding](../src/Broiler.HtmlBridge.Dom/Features/ElementGeometryBinding.cs):
  its nonzero-dimension filter discards attached zero-size boxes. Distinguish boxes
  with zero area from elements with no layout box.
- [x] Return proper DOMRect/DOMRectList objects with indexed access and `item()`;
  current element and Range methods return Arrays and expose neither interface.
- [x] Trace the empty selected-text result through
  [Range geometry](../src/Broiler.HtmlBridge.Dom/Features/TraversalBinding.Range.cs)
  and its layout host. Use actual line fragments, transforms and scroll offsets.
- [x] Cover attached zero-size, detached/display:none, wrapped inline text,
  selected text, collapsed ranges and iframe ownership with focused tests.
- [x] Follow [CSSOM View](https://drafts.csswg.org/cssom-view/#dom-element-getclientrects)
  and compare minimized fixtures with a versioned reference browser. The live
  CreepJS stack fails in the element map before Range; isolate its exact element
  and require no remaining `domRect.bottom` error after repair.
  See [validation note](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/fp03-client-rects-fix-2026-10-09.md).

### FP-04 — media capability contract (complete)

- [x] Defined WebIDL `MediaDevices` and `MediaDeviceInfo` interfaces in
  [MediaDevicesBinding](../src/Broiler.HtmlBridge.Dom/Features/MediaDevicesBinding.cs).
  Prototype inherits from `EventTarget.prototype`, constructor throws `TypeError: Illegal constructor`,
  and receiver checks enforce WebIDL brand validation.
- [x] Implemented modern enumeration, constraints, and device access:
  `enumerateDevices()` returns Promise resolving to truthful empty device set `[]`;
  `getUserMedia()` rejects with `NotFoundError`; `getDisplayMedia()` rejects with `NotAllowedError`;
  `getSupportedConstraints()` returns standard supported dictionary.
- [x] Rechecked Sannysoft: `let devices = await navigator.mediaDevices.enumerateDevices()` resolves
  without missing-property rejection and populates the results table.
- [x] Implemented legacy `navigator.getUserMedia()` on `Navigator.prototype`: returns `undefined`
  for 0 arguments (eliminating Sannysoft inline-16 failure) and invokes error callback with
  `NotFoundError` when callbacks are supplied.
- [x] Sannysoft live analysis reports **0 JavaScript failures** across all 31 scripts.
  See [validation note](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/fp04-mediadevices-fix-2026-10-09.md).

### FP-05 — Canvas2D/SVG behavior (scoped completion)

- [x] Draw linear/radial gradients, ellipses, cubic and quadratic paths into the
  real bitmap. Preserve paint objects and affine state through save/restore/reset.
- [x] Apply affine transforms to paths, rectangles, text and image copies; expose
  independent six-component transform snapshots.
- [x] Add SVG getBBox/getExtentOfChar and replace character-count text estimates
  with font advances. Test shape bounds, group unions, CSS changes and index errors.
- [x] Project canvas PNG snapshots into the renderer without changing the canonical
  script-visible DOM. A host without a registered image encoder still serializes safely.
- [x] Validate both providers and verify the live CreepJS Canvas2D/SVG collectors.
  Browser's integration test checks an actual screenshot pixel, not just API presence.

Limits: this is not full Canvas/SVG conformance. Text bounds use advance cells;
complex shaping/text paths and general SVG group transforms remain incomplete.
Canvas transform snapshots are not full DOMMatrix instances, gradient objects do
not implement the full interface hierarchy, transformed image sampling is nearest
neighbour, and stroke widths stay in device pixels. Existing strokeText/shadow/clip
limitations remain. Do not use collector completion as evidence that these work.

### FP-06 — iframe realm isolation (deferred by request)

Child windows still share Function/Object intrinsics. The separate-realm/provider
redesign, including cross-realm prototype and brand behavior, is outside this pass.
Document separation and existing frame behavior remain covered by the suites.

## Cross-component completion and release status

- [x] **FP-07:** Layout removes unused empty projected lines, retains real blank
  lines and supports emergency long-word wrapping with resize restoration.
- [x] **FP-08:** Browser filters non-content style text, classifies SVG element
  names in context and aggregates repeated network failures per scope. The CreepJS
  `<image/>` warning is from HTML markup; it was not suppressed as an SVG false positive.
- [x] Release and Release-VM suites and engine-neutrality checks pass.
- [x] Browser consumes local HtmlBridge preview.32 and Layout preview.23 packages,
  runs both live sites and ordinary captures, and compares the reduced fixture to Chrome.

WebGL, WebRTC, offline audio, speech and capture-device backends (**FP-09**) are
explicitly deferred. Package publication is not part of this local completion;
preview.32 is packed under `artifacts/compatibility-ready`. Rebuild that local feed
when reproducing. No public package upload was performed.

Browser owns the current [completion report](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/docs/fingerprinting-completion-2026-10-09.md),
[runner](https://github.com/Broiler-Platform/Broiler.Browser/blob/main/eng/verify-fingerprinting.ps1)
and compact evidence. The earlier baseline paragraphs above are historical, not
statements that the newly implemented methods are still absent.
