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

**Preview.** The bridge is real and heavily exercised — 975 passing tests at `Release`,
1,095 at `Release-VM` (23 skipped in each, measured on 2026-09-19) — but the *named control surface* described in
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
git clone https://github.com/Broiler-Platform/Broiler.HtmlBridge.git
cd Broiler.HtmlBridge
dotnet build Broiler.HtmlBridge.slnx -c Release
dotnet test  Broiler.HtmlBridge.slnx -c Release
```

Four build types, and the `-VM` pair is not cosmetic — it changes the project graph:

| Configuration | JavaScript engine | Notes |
| --- | --- | --- |
| `Debug` / `Release` | Broiler.JS | The default test and scripting configuration. |
| `Debug-VM` / `Release-VM` | Broiler.VM JavaScript profile | Adds `Scripting.Vm` and `Jseal.Vm`, defines `BROILER_VM_JS`, and gains the ~120 tests that only exist under it. |

Either engine can also be selected without changing configuration:
`dotnet build … -p:BroilerJavaScriptEngine=Vm`.

External Broiler components, including both JavaScript engines, are pinned NuGet
dependencies. `NuGet.config` maps `Broiler.*` to the Broiler-Platform GitHub Packages
feed, which requires authentication even for public packages. Configure credentials
for the `github` source before a fresh restore; CI supplies its `GITHUB_TOKEN` through
`NuGetPackageSourceCredentials_github`.

The solution builds all eight shipping assemblies in every configuration. The `-VM`
configurations additionally link the VM provider into the test suite and compile its
engine-specific cases. No external component checkout is required.

## Packaging

Every shipping project carries NuGet metadata and `eng/pack.ps1` builds and validates
all eight packages, including symbols, metadata and internal dependency versions.

CI follows Broiler.JS and Broiler.VM: .NET 10 and Node.js 24, Release builds and tests on
Linux and Windows, preview-version tests, and package artifacts from Windows. HtmlBridge
also keeps its Linux `Release-VM` run, engine-neutrality guard and test-report artifacts.
The coupling guard counts direct engine package references as well as project references.

`publish.yml` resolves one preview version, calls CI with that version, then runs
`eng/verify-feed.ps1` against an isolated consumer cache before pushing the validated
artifacts. Manual runs default to a dry run and can target GitHub Packages or NuGet.org;
`v*` tags target NuGet.org. The chosen feed must contain all external dependencies, and
NuGet.org publication requires the `NUGET_API_KEY` secret.

## Documentation

- [docs/html-control.md](docs/html-control.md) — the control surface: what WebView2 and
  MSHTML offer, what this component already does, and what is missing.
- [docs/jseal.md](docs/jseal.md) — engine neutrality, the contracts, the providers, the
  budget.
- [docs/script-initiated-navigation.md](docs/script-initiated-navigation.md) — how a
  script's navigation reaches the host.

## License

Apache-2.0. See [LICENSE](LICENSE) and [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
