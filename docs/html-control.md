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
| `GoBack` / `GoForward` / history | ✔ | ✖ No session history anywhere in this component. |
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
| Workers | ✔ | ✔ Worker realms inherit the page realm's eval decision. |
| Which engine runs the page | fixed (V8) | ✔ **A choice.** JSEAL makes the engine a provider, and two exist. Nothing in WebView2 or MSHTML can do this. |

### Document

| Capability | WebView2 | MSHTML | Here today |
| --- | --- | --- | --- |
| Live DOM from the host | ✖ (CDP only) | ✔ `IHTMLDocument2` | ✔ `session.CurrentDocument()` hands back a real `Broiler.Dom.DomDocument`. |
| Serialize current state | ✖ | ✔ | ✔ `CurrentHtml()` / `SerializeToHtml()`. |
| CSSOM, computed style | ✖ | ✔ | ✔ Bound. |
| Canvas 2D producing real pixels | ✔ | ✔ | ✔ Rasterises into a `BBitmap`; `getImageData`/`toDataURL` report real pixels. |
| Forms | ✔ | ✔ | ✔ Bound, including submission — with the one seam noted below. |
| Frames / sub-documents | ✔ | ✔ | ✔ Including per-frame origins and CSP. |

MSHTML wins that column on purpose: an in-process control whose host can *hold the tree*
is the thing WebView2 gave up and the thing this component already has.

### Rendering and input

| Capability | Here today |
| --- | --- |
| Paint | ✖ Not in this component. `Broiler.Layout` boxes it and `Broiler.HTML` paints it; the control would own the loop that connects them. |
| Hit testing, mouse, keyboard, focus | ✖ Host's. `Broiler.Browser.Core` routes input today. |
| Scrolling | Partial — `VisualViewport` scroll events dispatch; the scroller is the host's. |
| `ZoomFactor`, DPI | ✖ |

### Policy and safety

| Capability | WebView2 | Here today |
| --- | --- | --- |
| CSP enforcement | ✔ | ✔ `ContentSecurityPolicy`, including per-sub-document policies. |
| Eval / `Function` refusal honoured everywhere | ✔ | ✔ Including `ShadowRealm`, zero-arg `Function`, document-free engines, and work that outlives the call. |
| `User-Agent` control | ✔ | Partial — one constant, `BroilerUserAgent.Value`. |
| Permissions, cookies, storage partitioning | ✔ | Partial — every sub-resource loader sends and stores the profile's cookies through the host's `IBrowserRequestTransport` (per-hop, SameSite and CHIPS by Broiler.Net), with a request context per document and frame; `document.cookie` (the top document and every frame document) is the profile's `IDocumentCookieAccess`, so HttpOnly cookies stay out of reach, an opaque-origin document throws `SecurityError` and a non-HTTP(S) document reads and writes nothing; web storage is bound but not partitioned. |
| What page script can send and read | ✔ | ✔ `fetch()`, `XMLHttpRequest` and `navigator.sendBeacon` honour their credentials, mode and redirect modes through the transport (CORS, preflight, tainting); script-set forbidden headers (`Cookie`, `Host`, `Origin`, `Sec-*`, …) never reach the wire; responses expose only Fetch's filtered headers — never `Set-Cookie` — with opaque responses empty. XHR and beacons use the native fetch core, so replacing `window.fetch` cannot intercept them. A linked or `@import`ed stylesheet reaches `getComputedStyle`, `cssRules` and the render projection only as `text/css` (a quirks-mode document may also apply a same-origin or CORS response of another type, unless it is `nosniff`), so a no-cors sheet request cannot read another site's credentialed HTML or JSON. A frame's classic-script `import()` is the frame's request, resolved against the frame's URL and checked against the frame's policy. Without a transport, the cookie-less fallback client applies the same header gates but no CORS or redirect modes. |
| Documents of other origins | ✔ | ✔ Judged from the documents' request contexts, never from `location` or an attribute: a cross-origin frame — any frame with an opaque origin (sandboxed, `file:`) included — is withheld from `contentDocument`, `contentWindow` and `window.frames` (an unsandboxed `data:` frame's DOM is still judged by its creator's origin, kept from earlier releases; HTML makes it cross-origin), and a cross-origin window reached another way throws `SecurityError` on `document`; `MessageEvent.source` from one is a stand-in that can only be posted to, and `MessageEvent.origin` is the sender's real origin; a linked sheet another origin served without CORS applies, but its `cssRules`, `insertRule` and `deleteRule` throw `SecurityError`; `document.cookie` throws `SecurityError` for a script of another origin. A frame's jobs (microtasks, reactions, `await`s, timers, module scripts) run as the frame and are dropped once it has navigated away. A web document never has a local file read for it — scripts, modules, stylesheets, frames and workers alike. |
| Download interception, new-window policy | ✔ | ✖ |

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
3. **Session history**, which is the first thing on this list that is genuinely absent.
   `NavigationKind` already carries the `Assign` / `Replace` distinction history needs.
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
