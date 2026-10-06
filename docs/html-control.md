# The HTML control

This component's purpose is to be an **embeddable HTML control**: the thing an application
drops into a window to get a live document — the role `WebView2` fills today and
`mshtml.dll` filled for twenty years before it.

It is not that yet. It has the hard half and not the easy one. This document says exactly
which half is which, so that "full-featured HTML control" is a checklist rather than a
slogan.

## What a host has to do today

There is no `HtmlControl` type. A host composes the pieces itself, which is what
`Broiler.Browser.Core` does:

```csharp
// The profile's network (Broiler.Net): one per profile, shared by navigation and every loader.
var network = new BrowserNetworkSession(new() { Cookies = profileCookies });
var page    = DocumentRequestContext.CreateTopLevel(finalUrl);

var engine  = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
{
    Network = network,                                  // scripts, imports, sheets, frames, fetch()
    Cookies = network,                                  // document.cookie
}));
var content = ScriptExtractionService.ExtractAll(       // Broiler.HtmlBridge.Core
    html, url, deliveredPolicy, new ScriptFetchContext(network, page, cancellation));

using var session = engine.ExecuteInteractive(
    content.Scripts, content.DeferredScripts, html, url, content.ModuleRoots);

var settled = session.SettleLoadWindow();               // run the load window
var document = session.CurrentDocument();               // a live Broiler.Dom tree

if (session.TakePendingNavigation() is { } request)     // the page wants to leave
    /* the host's loader decides */;
```

Everything in that snippet works and is tested. The network is optional: without
`Network` and a `ScriptFetchContext` the loaders use process-wide fallback clients that
send and keep no cookies. What is missing is that the host also has to own the
navigation request, the session history, the paint loop, the input routing and the
lifetime — and every embedder re-answers those the same way.

## The surface, feature by feature

`WebView2` is the comparison because it is the one people have in mind. MSHTML is listed
where it offers something WebView2 dropped and a control of this shape should keep.

### Navigation

| Capability | WebView2 | Here today |
| --- | --- | --- |
| `Navigate(uri)` | ✔ | **Host's own loader.** No HTTP client in this component by design; `Broiler.Browser.Core` owns one. |
| `NavigateToString(html)` | ✔ | ✔ effectively — `ExecuteInteractive(scripts, …, html, url)` is exactly this. Needs a name. |
| `Source`, `Reload`, `Stop` | ✔ | Partly. `NavigationKind.Reload` is *reported*; performing it is the host's. |
| `GoBack` / `GoForward` / history | ✔ | ✔ The page's session history: `pushState`/`replaceState` move the document's URL (`location`, `document.URL`, what relative URLs resolve against) and keep a clone of the state, fragment navigations add entries, and `back()`/`forward()`/`go()` traverse in a later task with `popstate` (and `hashchange` between two fragments of one URL), as Chromium does. The host keeps the window's history: `InteractiveSession.TakeHistoryChanges` reports the page's pushes, replacements and traversals, a traversal past the page's entries is the host's to make, `TraverseHistory` brings the host's back and forward to the page's entries, and `SetSessionHistory` tells the page how many entries surround its own. The page and its frames share one joint history, as in Chromium: a frame's `pushState` or fragment navigation, and its navigation to another document, is an entry the page's `back()` and the host's back go through -- the host hears it at the page's URL -- and going back to a frame's earlier document loads it again. A traversal puts the scroll back where its entry was left, after `popstate`, unless the entry's `scrollRestoration` is `manual`, which is each entry's own. |
| `NavigationStarting` / `NavigationCompleted` | events | **Polled, not evented.** `TakePendingNavigation()` returns a `NavigationRequest` — and the *taking* semantics are deliberate, see `IDomBridgeRuntime`. A control turns it into an event with a cancellable argument. |
| Navigation kinds distinguished | partly | ✔ **Better than WebView2 here.** `NavigationKind` separates `Assign` / `Replace` / `Reload` / `MetaRefresh` / `FormSubmit`, which is what a host needs to keep history right. |
| Who started a navigation | ✖ | ✔ `NavigationRequest.Initiator` is the requesting document's `DocumentRequestContext` (a frame's, when a frame's script navigated the top window; the form's document for `form.submit()`), which the host passes to `RequestContext.TopLevelNavigation` so SameSite is decided from the real initiator. `MetaRefreshDiscovery.Find(html, url, initiator)` does the same for a refresh. |
| Same-document fragment navigation | ✔ | ✔ Performed outright by `LocationBinding`; the host never sees it. Correct. |

### Script

| Capability | WebView2 | Here today |
| --- | --- | --- |
| `ExecuteScriptAsync(js)` | ✔ | ✔ `ScriptEngine.Execute` / `ExecuteDetailed`, synchronous. An async wrapper is a name, not work. |
| Script errors surfaced | partly | ✔ `ScriptExecutionResult` / `ScriptError`. |
| `AddScriptToExecuteOnDocumentCreated` | ✔ | ✖ No document-created hook. The polyfill mechanism (embedded JS in `Broiler.HtmlBridge.Dom`) is the same machinery and could carry it. |
| `AddHostObjectToScript` | ✔ | ✖ **The largest single gap.** JSEAL has the shape for it — `IJsRealm` can define properties — but nothing projects a CLR object into the realm. |
| `WebMessageReceived` / `PostWebMessageAsJson` | ✔ | ✖ No host↔page message channel. |
| Modules, dynamic `import()` | ✔ | ✔ `ModuleRoot` / `ModuleMap`, and the VM provider declares its module capability explicitly. |
| Timers, User Timing, `PerformanceObserver` | ✔ | ✔ `setTimeout` and `setInterval` hand their callback the arguments after the delay, with the window -- a frame's own, a worker's global -- that registered it as `this`. `performance.mark`, `measure`, `clearMarks` and `clearMeasures` record and clear `PerformanceMark` and `PerformanceMeasure` entries, the entry getters answer from the document's buffer in order of start, and a `PerformanceObserver` hears of the marks, measures, resource entries, long tasks and navigation entry it observes in a task, buffered or not; every conversion, ordering and error is Chromium's. Each document keeps its own timeline, a frame's navigation entry its own URL. |
| Resource Timing | ✔ | ✔ A `PerformanceResourceTiming` entry for each script, style sheet and `@import`, frame document, worker script, `fetch()` (once its body is read), `XMLHttpRequest` and beacon a document fetches, and for the scripts its host fetched before running it (`ScriptEngine.DocumentResourceTimings`, measured from the navigation's start when the host hands that over as `ScriptEngine.DocumentFetchTiming`). What another origin may see is gated as Chromium gates it -- `Timing-Allow-Origin` for timings, sizes and protocol, CORS for status and type -- and `Server-Timing` is parsed. The buffer holds 250 entries unless resized, and fires `resourcetimingbufferfull` at `performance` when full. |
| Long Tasks | ✔ | ✔ A `PerformanceLongTaskTiming` entry for each task -- a page script, a timer, an animation frame, a message, a user's input -- that ran for more than 50 ms, to its document (`self`) and its ancestors of the same origin (`same-origin-descendant`, with the frame element). A frame's share of a task, and the loading of its document, is the frame's task; a wait on the network is no task's. |
| Workers | ✔ | ✔ Worker realms inherit the page realm's eval decision. A web page's worker script is fetched from its own origin (another origin's is a `SecurityError`, and so is one the document's `worker-src` → `child-src` → `script-src` → `default-src` refuses), as JavaScript; `importScripts` loads by URL from anywhere; `data:` and `blob:` workers run. A worker's global has `location`, `performance`, `atob`/`btoa`, `crypto` (with `subtle` for a secure page's worker), `isSecureContext`, `TextEncoder`/`TextDecoder`, `URL`, `MessageChannel` and `MessagePort`. A port transferred between a worker and its page, either way, stays entangled with its peer across the two threads and takes its undelivered messages along; the object left behind is inert. A drain lets a busy worker answer before the page's virtual clock moves on, thirty seconds per piece of work at most: compiling a large worker script takes this engine seconds that a browser does not spend. |
| Web Crypto | ✔ | Partial — `crypto.getRandomValues` and `randomUUID`, and in a secure context (HTTPS, or HTTP to a loopback address or a localhost name, for the running script's document and every document containing it, none of them a `data:` document; `isSecureContext` reports it) `crypto.subtle` with `digest` (SHA-1, SHA-256, SHA-384, SHA-512) and AES-GCM's raw `importKey`, `encrypt` and `decrypt`, answering and refusing as Chromium does. Every other algorithm, format and method rejects with `NotSupportedError`. |
| Which engine runs the page | fixed (V8) | ✔ **A choice.** JSEAL makes the engine a provider, and two exist. Nothing in WebView2 or MSHTML can do this. |

### Document

| Capability | WebView2 | MSHTML | Here today |
| --- | --- | --- | --- |
| Live DOM from the host | ✖ (CDP only) | ✔ `IHTMLDocument2` | ✔ `session.CurrentDocument()` hands back a real `Broiler.Dom.DomDocument`. |
| Serialize current state | ✖ | ✔ | ✔ `CurrentHtml()` / `SerializeToHtml()`. |
| CSSOM, computed style | ✖ | ✔ | ✔ Bound. |
| Canvas 2D producing real pixels | ✔ | ✔ | ✔ Rasterises into a `BBitmap`; `getImageData`/`toDataURL` report real pixels. |
| Forms | ✔ | ✔ | ✔ Bound, including submission — with the one seam noted below. A submission a submit button, Enter or `requestSubmit()` asks for is validated on the live value first, as Chromium does: each invalid control gets `invalid`, the first is focused and nothing is submitted; otherwise `submit` fires with its `submitter`, then `formdata` — for `form.submit()` and `new FormData(form, submitter)` too — whose `formData` holds the submitter's entry and whose listeners' `set`/`append`/`delete` reach the submission: the request names the submitter (`SubmitterIndex`, an image button's point) and carries those changes (`FormDataEdits`) for the host to replay on the entry list it builds; the submitter's `formaction` is the submission's. It carries the submission encoded too (`Submission`: the URL, with a `get`'s entries in its query, and a `post`'s body and its type), built from what the page's controls hold -- an option a script chose after the user, a file input a script emptied -- which a host that draws its own controls sends rather than an entry list made from its own record of the user's choices. A `method="dialog"` form closes its dialog with the submitter's value instead. A submission into a frame -- one whose target names it, or a form in the frame's own document -- loads its answer in the frame, the bridge sending it itself: a `get` with its entries in the query, a `post` URL-encoded, `multipart/form-data` or `text/plain`; a frame's form into the page is the page's navigation (`FormIndex` -1): a `get` to the URL built, a `post` with the body encoded (`Body`, `BodyContentType`). A script's submission into a new window is not followed. A file input holds the files the user chose in the host's picker (`SetFilesByUser`, with `input` and `change`; `CancelFilePickByUser` fires `cancel`): `files` lists them, `value` is `C:\fakepath\` and the first one's name, and the entry list, a `FormData` and a multipart body carry them; with nothing chosen it is an empty, nameless `application/octet-stream` file. A blob a page appends to a `FormData` is a `File`. Each option of a select is selected or not as HTML has it -- `option.selected`, `selectedOptions`, `selectedIndex`, `value`, a reset, `multiple`, the tree changing -- a select-one keeps one and a multiple select submits each; `select.remove(index)` and `options.remove(index)` take an option out, `length` set on the select or its options takes the last ones out or appends empty ones (none past 100,000), and `new Option(text, value, defaultSelected, selected)` makes one, as Chromium does; `options` is an `HTMLOptionsCollection` (`add`, `remove`, `length`, `selectedIndex`), and `options[i] = option` or `select[i] = option` puts an option at an index -- replacing the one there, or after empty ones that pad the select to it -- while `null` takes it out and anything else is a `TypeError`; a host that draws its own control for a select passes the user's choice through `SelectOptionByUser`, or a multiple select's whole choice through `SelectOptionsByUser`, and the select hears `input` and `change`. `checkValidity()` and `reportValidity()` judge the same values. `:user-valid` and `:user-invalid` match a control once the user has committed a change to it or tried to submit its form. `form.submit()` fires no `submit` and validates nothing; a submission asked for from the form's own `submit` or `formdata` listener is ignored. A reset — `form.reset()` or a reset button — fires the form's cancelable `reset` first, and one asked for from that listener is ignored. |
| Frames / sub-documents | ✔ | ✔ | ✔ Including per-frame origins and CSP, a frame navigated through its `location`, which keeps its window, and a frame's `top` and `parent`, which are the page's window — the whole of it for a frame of the page's origin, its cross-origin view for another, whose navigation of the page needs the user's activation. A frame's document has its own `readyState` and gets `DOMContentLoaded`, `load` and `pageshow` in Chromium's order, and a frame's script that calls `addEventListener` or sets `onload` without naming a window reaches its own window. |

MSHTML wins that column on purpose: an in-process control whose host can *hold the tree*
is the thing WebView2 gave up and the thing this component already has.

### Rendering and input

| Capability | Here today |
| --- | --- |
| Paint | ✖ Not in this component. `Broiler.Layout` boxes it and `Broiler.HTML` paints it; the control would own the loop that connects them. The page they are handed (`CurrentHtml`) carries what neither can know: the top layer -- an open modal dialog, a showing popover or a fullscreen element, stamped with its place in it (`data-broiler-top-layer`) and its `::backdrop` (`data-broiler-backdrop`; a modal dialog's is the UA's `rgba(0, 0, 0, 0.1)` scrim), which Broiler.HTML paints above everything, out of its ancestors' clips and transforms -- and a showing popover's implicit anchor, the button or `source` that showed it, named in its `position-anchor`. A box placed by `anchor()` is baked against its anchor's box; one placed by `position-area` or `@position-try` is left to the renderer's anchor placement (Broiler.HTML's `PlacesAnchoredBoxes`). The page's own document carries none of it. |
| Hit testing, mouse, keyboard, focus | ✔ A host delivers a pointer press, release, move or leave through `InteractiveSession.DispatchPointer`: hit-tested against the document's layout and into frames, in Chromium's painting order (CSS 2.1 Appendix E -- positioned and stacked boxes over in-flow ones, `z-index`, floats and inline-blocks -- and the top layer above it; `elementsFromPoint` too), with the rest of the document inert under an open modal dialog's backdrop, which takes the presses outside the dialog; and dispatched as trusted `pointerdown`/`mousedown`/`pointerup`/`mouseup`/`click`/`dblclick`/`auxclick` with a checkbox's, radio button's or label's activation, as the target's own window's script — the pointer events and the click `PointerEvent`s, the mouse events `MouseEvent`s. An element that calls `setPointerCapture` in `pointerdown` gets `gotpointercapture`, then the gesture's pointer and mouse events wherever the pointer goes, then `lostpointercapture` before the click. A script's `click()` — or a `MouseEvent` click it dispatches — is the same click, untrusted: a checkbox changes with its `input` and `change`, a label clicks its control, a submit button submits its form validated, a reset button resets it and a link is followed (not one to another window or a download); a disabled control, by its own `disabled` or a fieldset's, is not clicked. A `javascript:` URL a link, a key or `location` navigates to runs its script in that document, as a task, if its Content-Security-Policy allows inline script and the script that navigated is of that document's origin -- another origin's `location` navigation throws the `SecurityError` Chromium throws, and its link runs nothing; a host passes the user's through `RunJavaScriptUrl`. A string the script answers is a document, which replaces the one it ran in at its URL: a frame's here, the page's through `NavigationRequest.Document` for the host to show. A `<dialog>` is displayed while it is open; `show()`, `showModal()` and `close()` fire `beforetoggle` at once, `toggle` in a task and `close` with the next frame, and `requestClose()` -- or Escape on a modal dialog -- a cancelable `cancel` first. Opening a dialog focuses an `autofocus` element in it, else its first focusable one, else the dialog itself, and closing it gives focus back to what had it; while a modal dialog is open the rest of its document is inert -- `focus()`, a press and Tab do not reach it, and Tab goes round the dialog -- as is what an `inert` attribute covers, which a press passes through to what is behind it; a modal dialog taken out of its document is modal no more. A focused element that can no longer have focus -- made inert, disabled or not rendered -- loses it at the next frame, in a task, with `blur` and `focusout`. One taken out of its document -- by itself, with an ancestor, by `innerHTML` or by a move -- loses it before it goes, in the call that removes it, with `blur` and `focusout` while it is still connected; a `blur` listener that moves it makes the removal throw the `NotFoundError` Chromium throws. The popover API is every HTML element's: `showPopover()`, `hidePopover()`, `togglePopover()` and `popover`, with a cancelable `beforetoggle` and a `toggle` task a later change coalesces and re-queues; an auto popover closes the others it is not in, a `hint` popover only the hint popovers it is not in (one shown in a hint popover is a hint one, and hiding the auto popover the first hint one was shown in hides them all), no popover shows while another of its document shows or hides (`InvalidStateError`), a dialog's `show()` and `showModal()` close the popovers it is not in, a `popovertarget` button shows and hides its popover after its click, a press outside the open popovers -- after its `mousedown` -- or Escape closes them, each in its own document, and a closed popover is not displayed -- an open one is the renderer's to place, centred by HTML's own rule (`:popover-open` and `:modal` match by state, and are stamped into the renderer's page). A move fires the `over`/`out`/`enter`/`leave` boundary events of every document it changed in, then `pointermove`/`mousemove`, in Chromium's order. A press moves focus (`focus`/`blur`/`focusin`/`focusout`, and a window's own `focus`/`blur` when focus crosses a frame); `focus()`, `blur()`, `document.activeElement` and `hasFocus()` follow it, in frames too. Keys arrive through `DispatchKey` as `keydown`/`keyup` at the focused element, typed text through `DispatchText` as `keypress` and, in a text field, `beforeinput`/`input`, and a host editor's other changes through `DispatchEdit`; Tab moves focus through the page and its frames, Enter follows a link, clicks a button and submits a text field's form (`change`, the default button's `click`, `submit`), Space clicks a button or checkbox, and leaving an edited field fires `change` — all in Chromium's order. `:hover`, `:active`, `:focus`, `:focus-visible` and `:focus-within` match what the user is doing, in the bridge's selectors and computed style and, stamped as `data-broiler-user-action`, in the page the renderer is handed; a hover or a press the page's sheets only paint -- a colour, a background, an underline -- does not lay the page out again, and neither does handing the page to the renderer. A field's selection is the page's too: `selectionStart`, `selectionEnd`, `selectionDirection`, `setSelectionRange()`, `select()` and `setRangeText()`, with `selectionchange` at the field and, for a selection the user makes (`DispatchSelection`), `select`; a Tab into an input selects it all. A host editor's input-method composition arrives through `DispatchComposition` as `compositionstart`/`compositionupdate`/`compositionend` and `insertCompositionText` edits, in the UI Events specification's order. `:target` matches the element the document's fragment names, and a host following a link into the page tells the page (`NavigateToFragment`), which moves its `location.hash`. A fragment navigation fires `popstate` at once and `hashchange` in a later task, as Chromium does. |
| Scrolling | Partial — `VisualViewport` scroll events dispatch; the scroller is the host's, and the page's viewport follows it (`ScrollViewportTo`, at the host's position as it is, with no layout, and with the page's `scroll` when something listens for it) and it the page's (`ViewportScroll`): a script's `scrollTo`, `scrollIntoView` or fragment navigation, which scrolls the element named to the top as Chromium does. `scroll` and `scrollend` come with the next frame, once per target however often it moved, for the page's own scrolls as for the host's -- not in the call that scrolled. |
| `ZoomFactor`, DPI | ✖ |

### Policy and safety

| Capability | WebView2 | Here today |
| --- | --- | --- |
| CSP enforcement | ✔ | ✔ `ContentSecurityPolicy`, including per-sub-document policies. A `javascript:` URL's document keeps the policy of the document it replaced, with its own added, as Chromium does: a frame's here, the page's through `NavigationRequest.InheritedPolicy` for the host to run it under. |
| Eval / `Function` refusal honoured everywhere | ✔ | ✔ Including `ShadowRealm`, zero-arg `Function`, document-free engines, and work that outlives the call. |
| `User-Agent` control | ✔ | Partial — one constant, `BroilerUserAgent.Value`. |
| Permissions, cookies, storage partitioning | ✔ | Partial — every sub-resource loader sends and stores the profile's cookies through the host's `IBrowserRequestTransport` (per-hop, SameSite and CHIPS by Broiler.Net), with a request context per document and frame; `document.cookie` (the top document and every frame document) is the profile's `IDocumentCookieAccess`, so HttpOnly cookies stay out of reach, an opaque-origin document throws `SecurityError` and a non-HTTP(S) document reads and writes nothing; `localStorage` and `sessionStorage` are partitioned as Chromium partitions them: a document uses the areas of its storage key (its origin, the top-level site, and whether it or a frame above it is cross-site), which are the page's own only when that key is the page's, and a document with an opaque origin (sandboxed, `data:`, or inside one) throws `SecurityError` reading either. |
| What page script can send and read | ✔ | ✔ `fetch()`, `XMLHttpRequest` and `navigator.sendBeacon` honour their credentials, mode and redirect modes through the transport (CORS, preflight, tainting); script-set forbidden headers (`Cookie`, `Host`, `Origin`, `Sec-*`, …) never reach the wire; responses expose only Fetch's filtered headers — never `Set-Cookie` — with opaque responses empty. Bodies are bytes, as Fetch extracts them: a typed array, `DataView` or `ArrayBuffer` is sent as its bytes, a `Blob` with its type, a `FormData` as `multipart/form-data`, a `URLSearchParams` as form data, anything else as UTF-8 text, and a `Content-Type` the page set goes out as written; a response reads back as received through `arrayBuffer()`, `blob()` and `body`, and `text()` decodes UTF-8 (XHR's `responseText` the response's charset). XHR and beacons use the native fetch core, so replacing `window.fetch` cannot intercept them. A linked or `@import`ed stylesheet reaches `getComputedStyle`, `cssRules` and the render projection only as `text/css` (a quirks-mode document may also apply a same-origin or CORS response of another type, unless it is `nosniff`), so a no-cors sheet request cannot read another site's credentialed HTML or JSON. A frame's classic-script `import()` is the frame's request, resolved against the frame's URL and checked against the frame's policy. Without a transport, the cookie-less fallback client applies the same header gates but no CORS or redirect modes. |
| Documents of other origins | ✔ | ✔ Judged from the documents' request contexts, never from `location` or an attribute: a cross-origin frame — any frame with an opaque origin (sandboxed, `file:`) included — is withheld from `contentDocument` (an unsandboxed `data:` frame's DOM is still judged by its creator's origin, kept from earlier releases; HTML makes it cross-origin), and `contentWindow`, `window.frames` and `MessageEvent.source` give a script its cross-origin window: one object per frame, which answers only `window`, `self`, `frames`, `parent`, `top`, `opener`, `length`, `closed`, `close`, `focus`, `blur`, `postMessage`, its child frames by index and by name and a `location` it can only navigate, and throws `SecurityError` for everything else; a cross-origin window reached another way throws `SecurityError` on `document`; `MessageEvent.origin` is the sender's real origin; a linked sheet another origin served without CORS applies, but its `cssRules`, `insertRule` and `deleteRule` throw `SecurityError`; `document.cookie` throws `SecurityError` for a script of another origin. A frame's jobs (microtasks, reactions, `await`s, timers, module scripts) run as the frame and are dropped once it has navigated away. A web document never has a local file read for it — scripts, modules, stylesheets, frames and workers alike. |
| Download interception, new-window policy | ✔ | ✖ |

## Known gaps

Found while the window was brought level with Chromium, and not fixed yet. Each says what is wrong
and where the fix would go.

- **Anchored boxes the layout engine does not place.** A box in the subset Broiler.Layout's anchor
  placement takes (`IsMvpNativeAnchorBox`) is placed by the renderer, for what is drawn and for what a
  script measures. Any other is baked by `ResolveTopLayerAndAnchorsForRender`, and only into a page that
  is drawn: a geometry snapshot's projection has no bakes, so a script measures such a box where it
  would stand with no anchor. The bake of an auto-sized `position-area` box also still stretches it over
  its area, where Chromium gives it its content's size unless it stretches. Widening the engine's subset
  closes both; baking into geometry snapshots would close the first only.
- **Anchors in frames.** A frame's top layer is stamped into the markup the frame is rendered from, but
  nothing in a frame's document resolves `anchor()`, `position-area` or a popover's implicit anchor.
- **Indexed option entries.** `delete options[i]` and `delete select[i]` answer `true` and take nothing
  away, and their entries are described read-only; Chromium answers `false` (a `TypeError` in strict
  code) and describes them `writable: true`. Both come from JSEAL's exotic contract, which describes
  every handler entry read-only and gives an indexed deletion the ordinary path (Broiler.JSeal
  `docs/jseal.md`).
- **A select-one with nothing selected.** A script's `selectedIndex = -1` leaves no option selected,
  and the markup a host renders then marks none -- which is also what a select with nothing marked looks
  like, whose first option is selected. A host that draws its own drop-down shows the first option;
  Chromium draws the select blank. The markup needs a way to say "none".
- **One policy at run time.** `ScriptEngine` runs a document under its own `<meta>` policy when it
  declares one, otherwise under the one the host set (`Csp`): a policy the host delivered -- a header,
  or a `javascript:` document's inherited one -- is dropped when the document declares its own, where
  CSP enforces every policy. Frames already keep a set (`ContentSecurityPolicySet`); the page should too.
- **Removing a frame that holds focus.** The pre-removal hook blurs a focused element taken out of its
  document, but `DomRemoval.Removes` does not look into a frame's document, so removing an `iframe`
  whose document holds focus moves no focus and fires nothing. Not measured in Chromium yet.
- **`eval` refused by a policy throws `Error`.** The engine's eval stub (`ScriptEngine.cs`) throws a
  plain `Error`, on every page, where Chromium throws `EvalError`; `new Function` gets the realm's
  `SyntaxError`. Its comment says what retiring the stub would take.
- **Web storage lives as long as the page.** `localStorage` and `sessionStorage` are in-memory areas
  built with each page's window, so nothing carries over to the next page, where Chromium keeps
  `localStorage` for the origin and its partition, and `sessionStorage` for the tab. A frame that shares
  the page's areas also shares the page's two `Storage` objects, where each Chromium window has its own
  over the same area, so `frames[0].localStorage === localStorage` holds here. A store the host supplies,
  keyed by the storage keys `DomBridge/WebStorage.cs` computes, would close the first.
- **Streaming request bodies.** A `ReadableStream` body is sent as its string conversion; Chromium
  sends one only with `duplex: 'half'`, and refuses it otherwise with a `TypeError`.
- **The rest of Web Crypto.** `crypto.subtle` does digests and AES-GCM with a raw key only. .NET's
  `AesGcm` takes a 96-bit IV and a tag of 96 bits or more, so another IV length or a 32- or 64-bit tag is
  an `OperationError` where Chromium computes it. `crypto.subtle` reads `undefined` outside a secure
  context, where Chromium has no such property at all (`'subtle' in crypto` is `true` here).
- **`getRandomValues` fills one byte per element.** A `Uint16Array` or `Uint32Array` gets values below
  256 (`CryptoBinding.GetRandomValues` sets each element to a random byte); Chromium fills every byte.
- **`frames` is not the window.** In a browser `frames === window`. Here a window's `frames` is a list of
  its frames that answers every other member from the window, so `frames.performance` and
  `frames.x = 1` act on the window, but the two are not one object, and a frame's bare `frames` is the
  page's list: every document shares the one global object, which cannot be given indexed lookup.
- **What the performance timeline does not record.** Paint, event-timing and visibility entries are not
  kept. Resource Timing has no entry for an image (images are fetched by the renderer, not here), a
  `data:` URL or a fetch that failed; the connection's own phases are not observed, so an entry reports
  them where a browser reports a reused connection's, at the fetch's start; and the transport
  decompresses below what the bridge sees, so a compressed response reports its decoded size as its
  encoded size and no `contentEncoding`. Without the navigation's start from the host the time origin is
  taken when the document is attached, after the host's fetches, which are clamped to it. A frame's
  `performance.now()` and `timeOrigin` are the page's, and a frame's navigation entry reports none of its
  timings.
- **No `Referer`.** No request carries one, so a frame's `document.referrer` is empty where Chromium
  names the page that contains it -- its URL, or its origin for a frame of another origin and for
  `srcdoc`.
- **Long tasks are this engine's.** A task runs as long here as this engine takes, which for script is
  far longer than a browser takes, so a page sees long tasks a browser would not have. Every document
  shares one thread, and a task that touches two documents is shared out between them by the time spent
  in each window's context; a document of another origin is told nothing of another's long tasks, as a
  browser that isolates it in a process of its own tells it nothing.
- **Where a viewport scroll is heard.** CSSOM View fires a scroll of the viewport at the document, and it
  bubbles to the window. The bridge fires it at the document element without bubbling and then at the
  window, so a bubbling listener on the document hears nothing and one on the document element hears it.
  `<body onscroll>` hears nothing either: HTML makes it the window's handler, and here it is the body's
  own. Both are in `RunScrollSteps`; per the specifications, not measured, since the built-in browser's
  hidden pane fires no scroll events.
- **A flaky worker test.** `WorkerPortTests.AWorkersOwnChannelCopiesItsMessages` failed once, as
  `waiting`, in a full Release-VM run on a busy machine, and passed in two more full runs and five on
  its own: the worker's answer can miss the load window when the machine is loaded.

## What "finished" would mean

A single `HtmlControl` type a host constructs, gives a surface to draw on and an
`HttpMessageInvoker` to fetch with, and then drives entirely through named members:

- `Navigate`, `NavigateToString`, `Reload`, `Stop`, `GoBack`, `GoForward`, `Source`
- `NavigationStarting` (cancellable), `NavigationCompleted`, `DocumentTitleChanged`,
  `NewWindowRequested`, `ScriptDialogOpening`
- `ExecuteScriptAsync`, `AddScriptToExecuteOnDocumentCreated`, `AddHostObjectToScript`,
  `PostWebMessageAsJson` / `WebMessageReceived`
- `Document` — the live tree, because that is the MSHTML property worth keeping
- an engine selected at construction, because that is the property neither of them has

Nothing on that list needs a new engine or a new DOM. It is a facade over what this
component already does, plus three genuinely new pieces: **host-object projection**, a
**host↔page message channel**, and a **session history**.

## The order to build it in

1. **Name what exists.** `HtmlControl` over `ScriptEngine` + `InteractiveSession`:
   `NavigateToString`, `ExecuteScriptAsync`, `Document`, `CurrentHtml`. No new behaviour,
   so no new failure modes — and every later item lands on a type that already exists.
2. **Turn polling into events.** `TakePendingNavigation` becomes `NavigationStarting` with
   a `Cancel`. Keep the taking semantics underneath; they are why a request is not served
   twice.
3. **Session history** across documents. A page's own already is one
   (`TakeHistoryChanges`, `TraverseHistory`), and `NavigationKind` carries the `Assign` /
   `Replace` distinction the rest needs.
4. **Host objects and web messages.** Both are one question — how a CLR value crosses into
   a realm — and JSEAL is where the answer belongs, so that it holds for both providers
   rather than for Broiler.JS alone.
5. **Own the paint loop**, which is what makes it a *control* rather than a document
   runner, and which is the point at which `Broiler.Browser.Core` should get smaller.

## Why the engine choice is the interesting part

Every other HTML control in existence is a control *for one engine*. JSEAL means a host
can pick: the `Broiler.JSeal.BroilerJs` package runs the page on Broiler.JS,
`Broiler.JSeal.Vm` runs it on the Broiler.VM JavaScript profile, and the DOM
bindings cannot tell which — that is enforced by
[`scripts/check-engine-neutrality.sh`](../scripts/check-engine-neutrality.sh) rather than
hoped for. A control API should surface that as a construction-time choice, not hide it.

See [jseal.md](jseal.md) for how the neutrality is kept.
