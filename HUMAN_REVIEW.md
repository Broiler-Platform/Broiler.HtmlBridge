# Human review summary: Broiler.HtmlBridge preview

> **Status: PENDING.** No human reviewer has attested to the code in this repository.
> Until a reviewer is named below with a reviewed commit, evidence and a decision, this
> component must not be described as human-approved.

This component was extracted from
[Broiler.Browser](https://github.com/Broiler-Platform/Broiler.Browser) on 2026-09-16 with
its history. **The extraction carried no review with it, because there was none to carry:**
that repository's own record listed `src/` — "browser heads, shared chrome, HtmlBridge" —
as a single `PENDING` scope with no reviewer assigned. Splitting an unreviewed scope in two
produces two unreviewed scopes. Nothing here has been reviewed and nothing here was
reviewed before.

## What a reviewer of this component is taking on

This is the code that runs **untrusted page script against a live document**, which makes
it the highest-value review target in the platform and the one with the least margin:

- `src/Broiler.HtmlBridge.Dom` binds roughly 300 files of DOM, CSSOM, canvas, forms,
  frames, storage and worker surface into a JavaScript realm. Every one of them is reachable
  from a hostile page.
- `src/Broiler.HtmlBridge.Core` holds the **policy** decisions — Content-Security-Policy
  parsing and matching, origins, the microtask queue. A defect here is a policy that does
  not apply rather than a crash.
- `src/Broiler.HtmlBridge.Jseal` is the seam the whole design rests on. It references
  nothing, deliberately, and CI enforces that; a reviewer should check that claim rather
  than accept it.
- The two providers are where an engine's semantics meet the bridge's assumptions. They are
  the place a value crosses a trust boundary.

**Neither engine is a security sandbox.** Broiler.JS records that about itself and is
itself `PENDING`; Broiler.VM's core contract is implemented but unreviewed. An embedder must
restrict CLR and host capabilities before running untrusted content, and this component does
not do that for it.

## This repository

| Review | Scope | Status |
|---|---|---|
| [ ] | `src/` — the eight bridge assemblies | **PENDING** |
| [ ] | `tests/` — 622 cases at `Release`, 742 under the VM profile | **PENDING** |

Reviewer: _not yet assigned_
Reviewed commit: _none_
Evidence: _none_
Decision: **PENDING**

## Dependency components

Statuses as recorded by each component at the commit pinned here. Re-read these after any
submodule bump — an approval is revision-scoped and does not carry forward.

| Review | Component | How it is reached | Recorded status |
|---|---|---|---|
| [ ] | [Broiler.JS](Broiler.JS/HUMAN_REVIEW.md) | submodule, unconditional | **PENDING** — usable in preview only with its safety warning |
| [ ] | [Broiler.JS/Broiler.DateTime](Broiler.JS/Broiler.DateTime/HUMAN_REVIEW.md) | nested submodule | Approved for preview |
| [ ] | [Broiler.JS/Broiler.Regex](Broiler.JS/Broiler.Regex/HUMAN_REVIEW.md) | nested submodule | Approved for preview |
| [ ] | [Broiler.JS/Broiler.Unicode](Broiler.JS/Broiler.Unicode/HUMAN_REVIEW.md) | nested submodule | Approved with conditions |
| [ ] | [Broiler.VM](Broiler.VM/HUMAN_REVIEW.md) | submodule, `-VM` build types only | **PENDING** — core contract implemented, verification bounded by a retained corpus and a fuzz target, none of it reviewed |
| [ ] | Broiler.Dom.Html | NuGet package | see the Broiler.DOM component record |
| [ ] | Broiler.Layout | NuGet package | see the Broiler.Layout component record |

`Broiler.VM` is a **conditional** dependency and that is a statement about the build graph,
not about review scope. Under `Debug`/`Release` nothing of it is restored, built or copied.
Under `Debug-VM`/`Release-VM` — which CI builds and tests, and which gains ~120 test cases —
its code runs. **A reviewer scoping a `-VM` build is reviewing that code too.**

That component landed its implementation unreviewed under a rule its owner recorded on
2026-08-28: human review gates a *release*, not a development step. That rule is that
component's, not this one's. It is stated so a reader knows the unreviewed code in
`Broiler.VM/` is a recorded position rather than an oversight.

## Overall position

Suitable only for first-preview, controlled development, testing and evaluation. It must
not be presented as production-ready, security-audited, or free of defects or
vulnerabilities.

HTML, CSS, JavaScript and Unicode-data inputs all cross complex parser boundaries in this
component's closure. None has had a focused security review.
