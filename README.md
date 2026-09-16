# Broiler.HtmlBridge

An embeddable HTML control for .NET: a live DOM, a running JavaScript realm, and the
host seam that lets an application drive both — the role WebView2 and `mshtml.dll` fill,
built out of Broiler's own engines rather than someone else's browser process.

This component is the part of Broiler that turns *parsed markup* into *a document that
behaves like one*. Broiler.DOM holds the tree, Broiler.CSS resolves style, Broiler.Layout
does the box model and Broiler.HTML paints. HtmlBridge is what makes
`document.getElementById('x').style.color = 'red'` reach all four and repaint — and what
lets a host reach in from the other side.

It was extracted from
[Broiler.Browser](https://github.com/Broiler-Platform/Broiler.Browser) in September 2026,
with its history, because everything here is about *hosting a document* and nothing is
about *being a browser*. The browser is now one embedder among the possible ones.

## Status

**Preview.** The bridge is real and heavily exercised — 622 tests at `Release`, 742 under
the VM profile — but the *named control surface* described in
[docs/html-control.md](docs/html-control.md) is not written yet. Today a host composes
`DomBridge`, `ScriptEngine` and a layout view itself, which is what
`Broiler.Browser.Core` does. That document is the plan for closing the gap, feature by
feature, against what WebView2 and MSHTML actually offer.

## The eight assemblies

| Assembly | What it is |
| --- | --- |
| `Broiler.HtmlBridge.Core` | Shared models with no engine in them: CSP, origins, the microtask queue, navigation requests, the render logger, fetch timing. |
| `Broiler.HtmlBridge.Jseal` | **JSEAL** — the JavaScript Engine Abstraction Layer. Engine-neutral contracts the DOM is bound against. It has *no* `ProjectReference` and *no* `PackageReference`, and that is enforced. |
| `Broiler.HtmlBridge.DomBridgeUtils` | The bridge's static helpers that need no bridge instance: tree, attribute, CSS, layout-geometry and serialization utilities. Sits *below* `Dom`. |
| `Broiler.HtmlBridge.Dom` | The DOM bridge itself: tree building, the ~300 files of DOM/CSSOM/canvas/forms/frames/workers bindings, and the polyfills shipped as embedded JavaScript. |
| `Broiler.HtmlBridge.Scripting` | `IScriptEngine` and the interactive session: script extraction, module roots, evaluation policy. |
| `Broiler.HtmlBridge.Jseal.BroilerJs` | The JSEAL provider over [Broiler.JS](https://github.com/Broiler-Platform/Broiler.JS). |
| `Broiler.HtmlBridge.Jseal.Vm` | The JSEAL provider over the [Broiler.VM](https://github.com/Broiler-Platform/Broiler.VM) JavaScript profile's in-realm host surface. |
| `Broiler.HtmlBridge.Scripting.Vm` | An `IScriptEngine` on the same profile, selected by the `Debug-VM` / `Release-VM` configurations. |

The dependency rule that shapes all of it: **a binding never names an engine.** It names
JSEAL, and a *provider* names the engine. `eng/jseal-budget.json` records how much
engine coupling each project still has, `scripts/check-engine-neutrality.sh` recounts it
on every push, and those numbers may fall and may never rise. See
[docs/jseal.md](docs/jseal.md).

## Building

```bash
git clone --recurse-submodules https://github.com/Broiler-Platform/Broiler.HtmlBridge.git
cd Broiler.HtmlBridge
dotnet build Broiler.HtmlBridge.slnx -c Release
dotnet test  Broiler.HtmlBridge.slnx -c Release
```

Four build types, and the `-VM` pair is not cosmetic — it changes the project graph:

| Configuration | JavaScript engine | Notes |
| --- | --- | --- |
| `Debug` / `Release` | Broiler.JS | The default. Nothing of Broiler.VM is restored, built or copied. |
| `Debug-VM` / `Release-VM` | Broiler.VM JavaScript profile | Adds `Scripting.Vm` and `Jseal.Vm`, defines `BROILER_VM_JS`, and gains the ~120 tests that only exist under it. |

Either engine can also be selected without changing configuration:
`dotnet build … -p:BroilerJavaScriptEngine=Vm`.

`Broiler.JS` and `Broiler.VM` are submodules. When this repository is itself checked out
inside another (Broiler.Browser does exactly that), the parent sets `$(BroilerJsRoot)` and
`$(BroilerVmRoot)` to its own checkouts and these go uncompiled — one engine assembly per
build graph rather than two. `Directory.Build.props` says why that matters.

## Packaging

Every shipping project carries NuGet metadata and `eng/pack.ps1` builds and validates the
packages. **Publishing is blocked upstream, not here:** these packages would depend on
`Broiler.JavaScript.*` and `Broiler.VM.*`, and neither engine publishes to a feed yet.
`eng/verify-feed.ps1` runs a real consumer restore in `publish.yml` and will say so
plainly rather than shipping a package nobody can install.

## Documentation

- [docs/html-control.md](docs/html-control.md) — the control surface: what WebView2 and
  MSHTML offer, what this component already does, and what is missing.
- [docs/jseal.md](docs/jseal.md) — engine neutrality, the contracts, the providers, the
  budget.
- [docs/script-initiated-navigation.md](docs/script-initiated-navigation.md) — how a
  script's navigation reaches the host.

## License

Apache-2.0. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
