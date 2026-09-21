# Where the bridge's work belongs — an audit against Broiler.DOM, CSS, Layout and HTML

Audited 2026-09-21, from `62fda21`. Tier B and tier C were filed upstream the same day, against each
target repository's `main` rather than against the pinned package; the table below links them.

This component's job is to project a live document into a JavaScript realm. It is not supposed to
own DOM, CSS or Layout semantics — those have components of their own. This document records where
the bridge does own them anyway: what was fixed, what is blocked, what belongs in another repository,
and what was examined and deliberately left alone.

It exists so that nobody repeats the analysis, and so that a rejected proposal is not re-raised
without new evidence.

## How the findings were established

Thirty-seven candidates were raised by reading the bridge's helper layers against each dependency's
surface, then each was handed to an independent reviewer told to **refute** it. Twenty-eight
survived, nine did not.

The one rule that decided most of them: **the bridge consumes pinned NuGet packages, not the source
checkouts.** A member present in `D:/Broiler.DOM` may be absent from `Broiler.Dom 0.1.0-preview.5`,
and several claims collapsed on exactly that. Availability was verified against the pinned package's
XML documentation, and in two cases against the assembly metadata directly — a name in a DLL's string
heap proves nothing about whether the member is public.

Pinned at the time of audit:

| Package | Version |
| --- | --- |
| `Broiler.Dom.Html` | 0.1.0-preview.5 |
| `Broiler.Dom` (transitive) | 0.1.0-preview.5 |
| `Broiler.CSS.Dom` | 0.1.0-preview.5 |
| `Broiler.Layout` | 0.1.0-preview.4 |

`Broiler.HTML` is **not referenced by this component at all**. Anything the two share is parallel
evolution between components that cannot see each other, which is why those findings are written as
recommendations rather than substitutions.

## Filed upstream

Every tier B and tier C finding was re-verified against the target repository's **current `main`**
before filing — not against the pinned package, and not against this document. Four were dropped at
that point and two of this document's own claims were corrected; both are recorded below.

| Issue | Tier |
| --- | --- |
| [Broiler.Layout#4](https://github.com/Broiler-Platform/Broiler.Layout/issues/4) — `ILayoutView`'s documented implementation was deleted | D |
| [Broiler.Layout#5](https://github.com/Broiler-Platform/Broiler.Layout/issues/5) — no scrollable overflow region in the read model | C |
| [Broiler.Layout#6](https://github.com/Broiler-Platform/Broiler.Layout/issues/6) — no point query over the layout result | C |
| [Broiler.Layout#7](https://github.com/Broiler-Platform/Broiler.Layout/issues/7) — `transform` is carried as an unresolved string | C |
| [Broiler.Layout#8](https://github.com/Broiler-Platform/Broiler.Layout/issues/8) — the inline containing block is computed but not surfaced | B |
| [Broiler.Layout#9](https://github.com/Broiler-Platform/Broiler.Layout/issues/9) — quirks mode can only be asked about a raw HTML string | B |
| [Broiler.Layout#10](https://github.com/Broiler-Platform/Broiler.Layout/issues/10) — the `viewBox` mapping is implemented but not exposed | C |
| [Broiler.CSS#53](https://github.com/Broiler-Platform/Broiler.CSS/issues/53) — `TryParseNumeric` rejects exponents and accepts a trailing dot | A-gap |
| [Broiler.CSS#54](https://github.com/Broiler-Platform/Broiler.CSS/issues/54) — `FindMatching` returns `text.Length - 1` on no match | A-gap |
| [Broiler.CSS#55](https://github.com/Broiler-Platform/Broiler.CSS/issues/55) — `CssSelector` exposes no structural model | C |
| [Broiler.CSS#56](https://github.com/Broiler-Platform/Broiler.CSS/issues/56) — `CssSelectorMatcher` has no strict mode | A7 |
| [Broiler.DOM#21](https://github.com/Broiler-Platform/Broiler.DOM/issues/21) — the parser does not attach declarative shadow roots | C |
| [Broiler.DOM#22](https://github.com/Broiler-Platform/Broiler.DOM/issues/22) — no `<template>` contents model | C |
| [Broiler.HTML#228](https://github.com/Broiler-Platform/Broiler.HTML/issues/228) — `CorrectProgressBoxes` overwrites the cascaded box | C |
| [Broiler.HTML#229](https://github.com/Broiler-Platform/Broiler.HTML/issues/229) — `<base href>` is ignored when resolving resources | C |

### Dropped when re-verified against `main`

- **`url()` rebasing on `@import` (Broiler.CSS).** The claim assumed this component runs its own
  import pass that adopting `CssImportResolver` would retire. It already adopted it: it rebases
  inside the `ICssStyleSheetLoader` seam the package provides, before the text reaches the resolver
  (`StyleSheets.Imports.cs`). Filing it would have told a maintainer their API breaks a caller that
  in fact ships it working.
- **A used-value `line-height` resolver (Broiler.CSS).** Broiler.CSS resolves no `url()` and carries
  no used-value layer at all; the cascade passes these through as opaque strings. Resolving
  `line-height` three times remains this component's own problem — see the open items below.
- **Meta-refresh content parsing (Broiler.DOM).** That repository has already decided the question in
  writing: its `docs/roadmap.md` tells `HtmlMetaScanner` to return directive strings and leave
  parsing to the consumer, and names `MetaRefreshDiscovery` as needing only a package bump. The two
  spec departures found while checking are **this** component's bugs, in
  `Core/Dom/MetaRefreshDiscovery.cs`.
- **The `<progress>`/`<meter>` duplication, as a duplication.** Re-reading turned it into something
  sharper — a cascade bug in the other component, where author declarations on those elements are
  overwritten — and it was filed as that instead.

### Corrections to this document

- **`CssValueParser.TryParseNumeric` is wrong in *both* directions**, not only on exponents: it also
  accepts `1.px` as `1px`, because the `NumberStyles.Float` parse tolerates a trailing dot that
  css-syntax-3 forbids. `CssLengthParser` in the same package answers both cases correctly, so the
  package disagrees with itself. `TryParseNumeric` also backs that file's `TryParsePercentage`, so
  `hsl(120, 5e1%, 50%)` does not parse as a colour there either.
- **`DocumentModeContext.IsQuirksHtml(string)` is public**, contrary to "not public in any version"
  below. The gap is narrower than recorded: the predicate is reachable, but only over *raw markup*.
  `SelectsQuirksMode(publicId, systemId)` — the part a caller holding a parsed doctype needs — is
  private, as is `ReadDoctype`.

## Tier A — done

Applied on this branch. The bridge now calls the pinned dependency where it used to keep its own
copy, and `tests/DependencyAlignmentTests.cs` pins every answer that changed.

| What went | Where | Kind |
| --- | --- | --- |
| A fabricated table-cell hit rect | `DomBridge/HitTesting.cs:195` | fixes a defect |
| Hand-rolled slot assignment | `DomBridge/LayoutMetrics.ScrollGeometry.cs:67` | fixes a defect |
| Four depth-first descendant walkers | `DomBridgeHostUtils.cs`, `DomBridgeUtils/SubDocuments.cs`, `DomBridgeUtils/Serialization.cs` | preserving |
| Two CSS numeric parsers, ~94 call sites | `DomBridgeUtils/AnchorResolver.cs:50` | fixes a defect |
| An unbalanced transform-function scan | `DomBridgeUtils/Animations.cs:520` | fixes a defect |
| A re-stated document-base rule | `Core/Scripting/PreloadScanner.cs` | preserving |
| Nine dead `IsText`/`IsComment` guards inside `ChildElements` loops | various | preserving |

Three were worth more than their line count:

**Table cells were hit-tested against a fabrication.** `TryGetSimpleTableCellHitTestRect` took the
*table's* rect and divided it into a uniform grid — `width / max-column-count`,
`height / row-count` — and it was tried FIRST, so it masked the real box. It agreed with layout only
for uniform columns, uniform rows, no `colspan`, no caption and no per-cell padding. A point in a
wide first column reported the narrow second cell; a `colspan` cell got one column's width and the
rest of its span belonged to no element. `border-spacing: 0` parsed to zero and was then overridden
back to a hardcoded 2px. Broiler.Layout had already laid the table out correctly the whole time.

**Slot assignment ignored the first-slot rule.** The bridge tested `SlotAcceptsNode` directly, a pure
name test, so a slottable was claimed by *every* accepting slot. DOM §4.2.2.3 assigns it to the first
only, which is what `DomSlotting.GetAssignedNodes` implements. The observable case is a winning slot
outside the subtree being measured, where a scroll container counted content that renders elsewhere.

**`NaN` and `Infinity` parsed as lengths.** `TryParsePx` used `NumberStyles.Float`, which accepts
.NET's symbolic forms, so `"Infinitypx"` was a *successful* parse feeding an infinity into geometry
behind ~94 call sites. `CssValueParser.TryParseNumeric` requires a digit.

### Reverted after review: the selector matcher (A7)

`DomBridgeUtils.SimpleMatchesElement` is a hand-rolled matcher whose own comment admits it is "very
simple", and `Broiler.CSS.Dom.CssSelectorMatcher` is available and far better. The substitution was
made and then **reverted**, because of how the dependency ends its pseudo-class switch:

```csharp
_ => name.StartsWith('-') || RecognizedPseudoClasses.Contains(name),
```

A recognised-but-unmodelled pseudo-class (`:read-only`, `:defined`) or any vendor-prefixed one
matches **every element**. That is a defensible trade for the cascade — it is where the rule came
from — but this caller collects *animation declarations*, so `:read-only { animation: spin 1s }`
would attach that animation document-wide. The bridge's stub answers `false` for what it does not
understand, which is the safer error for this use.

Taking it needs one of: a strict-matching option on `CssSelectorMatcher`, or a way to ask whether a
selector is fully modelled — filed as [Broiler.CSS#56](https://github.com/Broiler-Platform/Broiler.CSS/issues/56). Until then the stub stays, and the real cost is recorded: it answers
`false` for compounds, combinators, `*`, attribute and functional selectors, so animation
declarations on any rule but the simplest are silently dropped today.

### A gap found in the dependency

`CssValueParser.TryParseNumeric` does not scan an exponent, so `1e2px` — valid `<length>` per
css-syntax-3 §4.3.12, and accepted by `CssLengthParser` in the same package — does not parse.
`DomBridgeUtils.TryParseExponentNumber` covers exactly that shape so no valid length regressed. When
`TryParseNumeric` learns exponents the fallback becomes dead rather than wrong. Filed as
[Broiler.CSS#53](https://github.com/Broiler-Platform/Broiler.CSS/issues/53), which also covers the opposite error found while writing
it up: `1.px` parses *successfully* as `1px`.

`CssSyntax.FindMatching` answers `text.Length - 1` when nothing matches, not `-1`, so a caller must
test the landing character rather than the sign. An unterminated `translateY(` otherwise takes the
rest of the string as its argument. Filed as [Broiler.CSS#54](https://github.com/Broiler-Platform/Broiler.CSS/issues/54).

## Tier B — real, but not reachable from here

Neither a call nor a package bump fixes these. They need a change in the dependency first.

### A hand-rolled line-box layout (~187 lines) — [Broiler.Layout#8](https://github.com/Broiler-Platform/Broiler.Layout/issues/8)

`DomBridge/AnchorResolver/InlineContainingBlocks.cs:175`. `EstimateInlineContentWidth` and
`EstimatePrecedingInlineWidth` measure inline runs as `charCount * fontSize` — the comment at :202
says outright "Ahem font: 1ch = font-size". That is a text-measurement stand-in, and it is wrong for
every font that is not Ahem.

`Broiler.Layout` computes the real inline boxes, but the members that carry them
(`BoxGeometry.ForInlineBox`, `Engine.CssBox.GetInlineBoundingBox`) are **private or internal in
Layout's own HEAD**, not merely absent from the pinned package. Exposing them is a Layout change.

Until then the estimator is a documented fallback rather than a defect, but it should not grow.

### The doctype decides standards mode by name alone (0 lines today) — [Broiler.Layout#9](https://github.com/Broiler-Platform/Broiler.Layout/issues/9)

`DomBridge/Serialization.cs:64`. `SelectsStandardsMode` tests only that the doctype's *name* is
`html`, so a doctype carrying a legacy public or system identifier — the ones that put a real browser
into quirks mode — still serialises as standards mode.

`Broiler.Layout.DocumentModeContext.SelectsQuirksMode(string, string)` implements the HTML Standard's
actual conditions, reading the public and system identifiers. It is not public in any version, so no
bump reaches it. Recorded as a latent correctness gap, not a duplication.

## Tier C — belongs downstream

These are recommendations for the named repository. The bridge cannot act on them alone, and should
not: re-implementing them better here would deepen the mislayering rather than fix it.

### Broiler.Layout

| What the bridge owns | Where | Lines | Filed |
| --- | --- | --- | --- |
| The CSS Overflow 3 §3.1 scrollable overflow region, walked and unioned per element | `DomBridge/LayoutMetrics.cs:355` | ~170 | [Broiler.Layout#5](https://github.com/Broiler-Platform/Broiler.Layout/issues/5) |
| A complete CSS Transforms 1 used-value engine — a transform list folded into an affine matrix | `DomBridgeUtils/Animations.cs:408` | ~158 | [Broiler.Layout#7](https://github.com/Broiler-Platform/Broiler.Layout/issues/7) |
| `elementFromPoint`/`elementsFromPoint` paint-order hit testing, recursing the DOM in reverse child order | `DomBridge/HitTesting.cs:74` | ~40 | [Broiler.Layout#6](https://github.com/Broiler-Platform/Broiler.Layout/issues/6) |
| The SVG `viewBox` user-space mapping, hardcoded to the default `preserveAspectRatio` | `DomBridge/LayoutMetrics.Svg.cs:450` | ~3 | [Broiler.Layout#10](https://github.com/Broiler-Platform/Broiler.Layout/issues/10) |

The transform engine is the clearest case: Layout already has a transform model
(`IR.TransformItem.Matrix`, `AffineLayerMap`, `SvgTransform`) and the bridge has built a second one
beside it. The hit-testing one is a correctness gap rather than a duplication — a point query needs
the paint-order model (`IR.Fragment` with `CreatesStackingContext`, `StackLevel`, `TopLayerOrder`),
and no amount of exposing existing members substitutes for a Layout point-query API.

### Broiler.CSS

| What the bridge owns | Where | Lines | Filed |
| --- | --- | --- | --- |
| Selector scoping into a shadow tree — parsing selector lists into compounds and combinators | `DomBridgeUtils/Selectors.cs:186` | ~120 | [Broiler.CSS#55](https://github.com/Broiler-Platform/Broiler.CSS/issues/55) |
| `url()` tokenising and rebasing a sheet's relative URLs against its own base | `DomBridgeUtils/Css.cs:240` | ~33 | dropped — see above |
| The used value of `line-height`, resolved three independent times and never shared | `DomBridgeUtils/AnchorResolver.cs:290` | ~27 | dropped — see above |

The selector one is blocked by a shape, not a gap: `CssSelectorParser.Parse` is public but
`CssSelector` exposes only `Text` and `Specificity` — no compounds, no combinators, no offsets — so
the bridge re-parses to get at structure it cannot otherwise see. A public structural selector model
would delete all 120 lines.

`line-height` being resolved three times **within this component** is worth fixing here regardless of
where it eventually lives; see the open items below.

### Broiler.DOM

| What the bridge owns | Where | Lines | Filed |
| --- | --- | --- | --- |
| Declarative shadow roots: a post-parse pass finding `<template shadowrootmode>` and attaching | `DomBridge/HtmlParsing.cs:127` | ~70 | [Broiler.DOM#21](https://github.com/Broiler-Platform/Broiler.DOM/issues/21) |
| The `<template>` contents model (HTML §4.12.3), held as a side table keyed on the element | `DomBridge/HtmlParsing.cs:305` | ~65 | [Broiler.DOM#22](https://github.com/Broiler-Platform/Broiler.DOM/issues/22) |
| Parsing a `<meta http-equiv=refresh>` content value | `Core/Dom/MetaRefreshDiscovery.cs:59` | ~40 | dropped — see above |

The first two are tree-construction output. Template contents is part of a node's data model, and the
bridge keeping it in a `Dictionary<DomElement, DomDocumentFragment>` beside the tree is a workaround
for the parser not producing it. `HtmlMetaScanner` already owns *finding* the meta-refresh element;
reading its content value belongs next to it.

### Broiler.HTML

Not referenced by this component, so these are observations about two implementations that cannot
see each other:

- `<progress>`/`<meter>` UA chrome is generated in `DomBridge/Serialization.cs:413` (~81 lines) and
  again in `Broiler.HTML`'s `DomParser.CorrectProgressBoxes`. Two components independently decide
  what a progress bar looks like — **and they disagree**: that one overwrites the cascaded box, so an
  author's `width`, `border` and even `display: none` are discarded, where this one keeps a computed
  `width`/`height` that is not `auto`. Filed as [Broiler.HTML#228](https://github.com/Broiler-Platform/Broiler.HTML/issues/228).
- Rebasing `url()` in the render projection (`DomBridge/StyleSheets.cs:226`) overlaps the painting
  component's own resource resolution — which turns out not to read `<base href>` on the render path
  at all, so this component's pre-pass is the only thing applying it, and only for the two URL kinds
  it knows about. Filed as [Broiler.HTML#229](https://github.com/Broiler-Platform/Broiler.HTML/issues/229).

## Tier D — blocked

`DomBridge/AnchorResolver/AnchorRegistry.cs:54`. `ComputeElementBox` is a second block/absolute
layout written over computed style: it resolves four insets and derives width from `left`+`right`
against the containing block.

The obvious answer is `Broiler.Layout.BoxGeometry.BorderBox` through `ILayoutView.GetGeometry`, and
it does not work. The reason is worse than "not in the pinned package", which is what this document
claimed before the finding was filed: **no implementation of `ILayoutView` exists on any main branch
of any component.**

| repository | what implements `ILayoutView` |
| --- | --- |
| Broiler.Layout (`a261826`) | nothing — three textual mentions, no implementors |
| Broiler.HTML (`cce61ad`) | nothing — `Broiler.HTML.Headless` was **deleted** on 2026-09-15 in `603c808` |
| this repository (`2212008`) | nothing in `src/`; the only `LayoutViewFactory` assignments anywhere are the fake views in `tests/DependencyAlignmentTests.cs` |

`Broiler.Layout/ILayoutView.cs:18-19` still names `Broiler.HTML.Headless.HeadlessLayoutView` as "the
concrete implementation", its README repeats it, and its `.csproj` still grants that assembly
`InternalsVisibleTo`. The deletion commit is candid about why the project went — unreferenced, absent
from the solution, and unable to build after the move to packages — and notes in passing that
"nothing assigns the bridge's `LayoutViewFactory`, so the aggregate always ran its null layout view
regardless."

So `_layoutView ??= _layoutViewFactory?.Invoke() ?? LayoutViewFactory?.Invoke() ?? NullLayoutView.Instance`
(`DomBridge/LayoutMetrics.Geometry.cs:30`) always lands on `NullLayoutView`. Every geometry answer
this component gives comes from its own estimators — `ComputeElementBox` here, and the
`charCount * fontSize` line-box measurement in tier B.

That is the correction worth carrying forward: **those estimators are not a fallback for when no host
supplies a view. They are the only code path that runs.** Filed as
[Broiler.Layout#4](https://github.com/Broiler-Platform/Broiler.Layout/issues/4).

## Claims that were refuted — do not re-raise without new evidence

Nine candidates did not survive. They are recorded because each looks plausible on a second reading
and will be proposed again otherwise.

| Claim | Why it fails |
| --- | --- |
| `SnapshotChildren` duplicates `DomNode.ChildElements` | It *calls* it. `Css.cs:57` is `ChildElements(root).ToList()`; the surrounding lines are concurrent-mutation tolerance, which is the bridge's own problem because page script mutates during a walk. |
| The `@import` prelude wrapper duplicates `CssomRuleMetadata.ParseImportPrelude` | The wrapper is a deliberate **correction** of a defect in the pinned Broiler.CSS, and it already calls the dependency on the next line. |
| A transform-argument split duplicates `CssValueParser.TryParseNumeric` | Available, but the bridge's scan answers differently for inputs the transform grammar allows. |
| SVG font-relative unit ratios belong on `CssUnit` | The enum draws the distinctions; the *ratios* are an SVG-rendering concern, not a unit taxonomy. |
| Containing-block width duplicates `BoxGeometry.PaddingBox` | Same blocker as tier D: no `ILayoutView` implementation exists here. |
| `ScrollPositioning` is a cruder scrollable-overflow extent | Its doc comment says it deliberately skips `position:absolute/fixed` children. Different question, not a worse answer. |
| `AccumulatedSvgTranslate` duplicates `IR.SvgTransform` | It is an ancestor walk accumulating a translate, not a transform parser. |
| The inline-element tag fallback duplicates `CssUserAgentDefaults.DisplayValues` | Available and public — but the fallback exists for elements with no computed style at all, which is when the defaults table cannot help. |
| `HtmlBaseHref.ResolveDocumentBaseUrl` duplicates `HtmlDocumentQueries` | `HtmlDocumentQueries` answers "what does the markup say"; this answers "what URL does a relative reference resolve against", which needs the page URL the markup does not carry. |

## Decisions taken across the refactoring series

Recorded so the reasoning survives the diff.

- **File-per-type stays.** 53 `I*Host` interface files cost ~259 lines of using/namespace prologue.
  That is the price of an idiomatic C# convention, not a defect.
- **The narrow host contracts stay narrow.** The shared primitives were hoisted into single-member
  base interfaces so that no contract widened. Collapsing 53 contracts into a handful would have been
  a larger reduction and would have handed every feature module the whole bridge.
- **The two loopback test servers stay separate.** One maps paths, 404s and logs; the other serves one
  body for any path. Merging needs a wildcard mode — more complexity, not less.
- **`ContentSecurityPolicy` keeps its seven typed directive fields.** A string-keyed table turns a
  typo into a runtime exception where a field is a compile error, in code whose defects are policies
  that silently do not apply. The table is used only where the code genuinely looks a name up.
- **Public members are never deleted as dead from within this repository.** Four "dead" members were
  live in `Broiler.Wpt` and `Broiler.Cli` through `InternalsVisibleTo`. An in-repo grep is not
  evidence.

## Open items inside this component

Not cross-component, but found while looking and worth keeping:

- **The HTML element-name vocabulary exists twice and the two copies disagree** —
  `DomBridgeUtils/DomInterfaces.cs:40` in C#, and again in the embedded polyfill JavaScript.
- **`line-height` is resolved three times** by three helpers unaware of each other (see Broiler.CSS
  above); sharing one resolver is worth doing here even before the downstream question is settled.
- **~20 `file:line` citations in test prose** point into source files and drift silently whenever
  those files move. The ones this series invalidated were changed to name the file only.
- **`HUMAN_REVIEW.md` is stale** — its dependency table still describes Broiler.JS and Broiler.VM as
  submodules, with links to paths that do not exist. Left untouched deliberately: it is an
  attestation record, and correcting it is its owner's call.
