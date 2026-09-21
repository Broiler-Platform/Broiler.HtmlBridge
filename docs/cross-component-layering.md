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

Pinned at the time of audit, and now:

| Package | At audit | Now |
| --- | --- | --- |
| `Broiler.Dom.Html` | 0.1.0-preview.5 | 0.1.0-preview.6 |
| `Broiler.Dom` (transitive) | 0.1.0-preview.5 | 0.1.0-preview.6 |
| `Broiler.CSS.Dom` | 0.1.0-preview.5 | 0.1.0-preview.6 |
| `Broiler.Layout` | 0.1.0-preview.4 | 0.1.0-preview.5 |

The right-hand column is the published form of the merged work below. What this component then did
with it is [Adopted after the bump](#adopted-after-the-bump).

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

### Where the work landed

Thirteen of the fifteen are implemented and **merged**, one pull request per component, each green on
both platforms before it landed:

| Pull request | Closes | Tests |
| --- | --- | --- |
| [Broiler.CSS#57](https://github.com/Broiler-Platform/Broiler.CSS/pull/57) | #53, #54, #55, #56 | 1,022 → 1,179 |
| [Broiler.Layout#11](https://github.com/Broiler-Platform/Broiler.Layout/pull/11) | #6, #7, #9, #10 | 1,386 → 1,540 |
| [Broiler.DOM#23](https://github.com/Broiler-Platform/Broiler.DOM/pull/23) | #21, #22 | 526 → 577 |
| [Broiler.HTML#230](https://github.com/Broiler-Platform/Broiler.HTML/pull/230) | #228, #229 | 31 node + 2 suite cases |

**Two are deliberately still open**, and the reason is the same one tier D records below.
[Broiler.Layout#5](https://github.com/Broiler-Platform/Broiler.Layout/issues/5) and [#8](https://github.com/Broiler-Platform/Broiler.Layout/issues/8) both ask to
enrich a read model that component cannot populate, so implementing them would have meant adding
public members nothing fills. #8 is worse than it looks from here: CSS 2.1 §10.1 is *already*
implemented in Layout, privately, with one call site — there is simply no public path from an element
to a rectangle to hang it on. #5 has a second, independent blocker that the audit did not know about:
`WritingMode` appears zero times in both `ComputedStyle.cs` and `Fragment.cs`, so Layout's public
fragment model cannot express a reversed block axis at all, and computing the region over it would
ship exactly the `vertical-rl` defect this component had already hit. Both wait on the open half of
[#4](https://github.com/Broiler-Platform/Broiler.Layout/issues/4).

One new issue came out of the work rather than the audit:
[Broiler.HTML#231](https://github.com/Broiler-Platform/Broiler.HTML/issues/231) — an external sheet's `url()` resolves against the
document base rather than the sheet's own URL, which CSS Values §4.2 requires. Both the old and the
new answer are wrong, so it is not a regression, but a `<base>` makes them diverge where they used to
coincide.

Three defects were caught reviewing the implementations, none of them in the filed issues: a popover
nested inside an open dialog was dropped entirely by the new hit test; foster parenting escaped a
template, reintroducing the very leak the template-contents change exists to close; and the `<base>`
fix recorded a document base for *every* document, so a parse-time copy of the embedder's own URL
silently outranked the live, publicly settable `BaseUrl`.

### Adopted after the bump

Eight of the merged changes landed here as calls, and the local re-implementations they replace are
gone: net −283 lines in `src/`, +767 in `tests/`. A ninth was attempted and reverted — see
[The transform resolver, attempted and reverted](#the-transform-resolver-attempted-and-reverted) —
and a tenth cannot be reached at all, see
[The paint-order hit test, and what it needs](#the-paint-order-hit-test-and-what-it-needs).

| What went | Where it was | Now | Upstream |
| --- | --- | --- | --- |
| The `<template>` contents side table, the pass that filled it and its three call sites | `DomBridge/HtmlParsing.cs`, `DomBridge/Traversal.cs`, `DomBridge/JsObjects.cs` | `DomElement.TemplateContents` | [Broiler.DOM#22](https://github.com/Broiler-Platform/Broiler.DOM/issues/22) |
| The post-parse declarative shadow-root pass | `DomBridge/HtmlParsing.cs` | `HtmlParseOptions.AllowDeclarativeShadowRoots` | [Broiler.DOM#21](https://github.com/Broiler-Platform/Broiler.DOM/issues/21) |
| `DomBridgeUtils.TryParseExponentNumber` and its two fallback call sites | `DomBridgeUtils/AnchorResolver.cs` | `CssValueParser.TryParseNumeric` alone | [Broiler.CSS#53](https://github.com/Broiler-Platform/Broiler.CSS/issues/53) |
| `DomBridgeUtils.SimpleMatchesElement`, the hand-rolled selector stub | `DomBridgeUtils/Animations.cs`, `DomBridge/Animations.cs` | `CssSelectorMatcher.TryMatch` | [Broiler.CSS#56](https://github.com/Broiler-Platform/Broiler.CSS/issues/56) |
| The two-index guard written around `FindMatching`'s no-match sentinel | `DomBridgeUtils/Animations.cs` | a sign test | [Broiler.CSS#54](https://github.com/Broiler-Platform/Broiler.CSS/issues/54) |
| The subject-compound scan, the type-selector scan and the comma split behind shadow-tree scoping | `DomBridgeUtils/Selectors.cs` | `CssSelector.Subject`, `CssCompoundSelector.TypeSelectorEnd`, `CssSyntax.SplitTopLevel` | [Broiler.CSS#55](https://github.com/Broiler-Platform/Broiler.CSS/issues/55) |
| `SelectsStandardsMode`'s doctype-name test | `DomBridge/Serialization.cs` | `DocumentModeContext.IsQuirksDoctype` | [Broiler.Layout#9](https://github.com/Broiler-Platform/Broiler.Layout/issues/9) |
| The `viewBox` mapping, hardcoded to the default `preserveAspectRatio` | `DomBridge/LayoutMetrics.Svg.cs` | `IR.SvgViewBox.Resolve` | [Broiler.Layout#10](https://github.com/Broiler-Platform/Broiler.Layout/issues/10) |

The last three are **correctness fixes, not preserving substitutions**, and each carries the test
that fails against the code it replaced. The selector one corrupted any selector carrying a comment
or an escaped comma and marked a `-`-initial or escaped type selector at the wrong offset; the
doctype one flipped a legacy-doctype document into standards mode on the way out; the `viewBox` one
answered `xMidYMid meet` for every `preserveAspectRatio`, including the ones nobody writes unless
they mean something else.

**Two answers this component kept rather than took, and both are the same shape: a dependency is
right for its own consumers and wrong for this one.**

`TryParseNumeric` reading an exponent means a number can now overflow a double, so `1e400px` is a
*successful* parse answering `(+∞, Px)`. Upstream is not wrong — `CssLengthParser` in the same
package has always answered that way, and CSS Values 4 §11.1 clamps an out-of-range number rather
than invalidating the declaration. But tier A removed infinities from this component's geometry for
a reason: the ~94 call sites behind `TryParsePx`/`TryParsePercent` multiply what they are handed
into a box with no clamp of their own, which is exactly what made `"Infinitypx"` a defect. The
finiteness test therefore moved out of the deleted fallback and into the two helpers, where it
covers every route rather than only the ones spelled with an `e`.

The parser's declarative shadow roots are on for the **navigation** parse only. The Standard gates
them on a flag the entry point sets, not the markup: `innerHTML` deliberately does not set it, which
is the whole of the difference between it and `setHTMLUnsafe`. The two sub-document parses keep the
default as well — the deleted pass never reached them, so switching them on would be a behaviour
change no finding asked for.

One thing had to move rather than go: `_hasShadowRoots`, the flag that lets a document with no
shadow DOM skip the per-serialization descendant walk that confines each shadow tree's style rules
to that tree. The deleted pass set it as it attached; the parser cannot, so the parse asks the
finished tree once instead — strictly less work than the walk-plus-attach it replaces.

The selector substitution is the one that changes what pages render, and in both directions at
once. `SimpleMatchesElement` understood a bare tag name, `#id`, `.class` and `:root`, and answered
`false` for everything else, so an `animation` declared on a compound, a combinator, `*`, an
attribute selector or a functional pseudo-class reached nothing at all. `TryMatch` answers all of
them. What it does *not* do is guess: its lenient sibling `Matches` reports `true` for a recognised
but unmodelled pseudo-class and for any vendor-prefixed name, and this caller reads "no answer" as
"no match" — the stub's own conservative error, kept deliberately, and now the only thing left of
it. That is what [Broiler.CSS#56](https://github.com/Broiler-Platform/Broiler.CSS/issues/56) was filed for, and why the substitution
below was reverted before it existed.

### The transform resolver, attempted and reverted

[Broiler.Layout#7](https://github.com/Broiler-Platform/Broiler.Layout/issues/7) shipped `IR.CssTransform`, a CSS Transforms 1 used-value resolver, and
`IR.CssTransformOrigin`, which this component already calls. Substituting `CssTransform.Resolve`
for the ~150-line local engine behind `ApplyTransformChain` was tried, measured and **reverted**.
It is not a matter of taste: the substitution loses answers this component gives today, and the
only public entry point is all-or-nothing.

- **`calc()` in an argument invalidates the whole list.** `transform: translate(calc(100% - 10px),
  20px)` is valid CSS that a browser resolves. `CssTransform` refuses the declaration outright and
  answers the identity; the local engine resolves the components it can and contributes zero for
  the one it cannot, so the `20px` survives. Two of this repository's own regression tests —
  `ANestedFunctionArgumentDoesNotEndTheFunction` and `AFunctionFollowingANestedArgumentIsStillApplied`,
  which exist because the tier A split fixed exactly this — fail against the substitution, and the
  answer they assert is the one closer to a browser.
- **The SVG `transform` attribute goes through the same call site.** `GetElementTransformValue`
  falls back to the `transform` attribute when the computed property is absent, and there the
  grammar is SVG's: bare numbers are user units. Put `translate(10,20)` through that fallback and
  the bridge answers a `(10, 20)` offset today and the identity under `CssTransform` — measured
  both ways on the geometry harness in `DependencyAlignmentTransformSplitTests`. Upstream says so
  itself and points at `IR.SvgTransform`, which is **not public** in `0.1.0-preview.5` — though see
  the correction below, because it is not unreachable either. (The two spellings the SVG grammar
  allows that CSS does not — `translate(10 20)` and `rotate(45 50 50)` — are already lost today;
  only the comma form regresses.)
- **Nothing smaller is reachable.** `TryResolveFunction`, `TryResolveLength`, `TryParseAngle` and
  `TryParseScaleFactor` all appear in the package's XML documentation and are all private in the
  assembly. Per-function adoption — which would have kept the local rule that an unrecognised
  function contributes the identity rather than killing the declaration — is not on offer.

**One correction to the middle point above, found while judging the hit test.** `IR.SvgTransform`
is not *public*, but it is `internal` and **`Broiler.Layout` grants `InternalsVisibleTo` to
`Broiler.HtmlBridge.Dom`** — `DomBridge/Serialization.Rendering.cs` already calls
`SvgTransform.TryParse` through that grant. So "there is no way to route the attribute to the
parser written for it" is wrong as stated. What is true is narrower and still blocking:
`ApplyTransformChain` lives in `Broiler.HtmlBridge.DomBridgeUtils`, which holds no such grant and
must not acquire one (it is the project budgeted at zero dependencies), and the `calc()` loss is
decisive on its own regardless of where the code sits. The grant is recorded here because an
argument that rests on a `CS0122` needs to name *which assembly* saw it.

The strictness `CssTransform` brings *is* an improvement taken on its own: an invalid or 3D
function should invalidate the declaration, and a unitless `rotate(45)` is not an angle. None of it
can be taken without the two losses above. What would make this adoptable is `calc()` support (or
a way to hand over pre-resolved arguments) plus a public `SvgTransform`; until then the local
engine stays and this paragraph is the evidence that the obvious substitution was tried.

### The paint-order hit test, and what it needs

[Broiler.Layout#6](https://github.com/Broiler-Platform/Broiler.Layout/issues/6) shipped `IR.FragmentHitTest`, a point query over the
fragment tree with the paint-order model the bridge's `CollectHitTestMatches` does not have —
`CreatesStackingContext`, `StackLevel`, `TopLayerOrder`. Nothing was written against it, because
**this component cannot obtain a fragment tree and could not read the answer if it had one.**
Three independent blockers, each measured by reflecting over the `0.1.0-preview.5` assembly rather
than read off the documentation:

- **No public path from a document to a `Fragment`.** Of the seven public members in
  `Broiler.Layout` that hand out a `Fragment`, one is `Fragment.Children` (you must already hold
  the tree) and the rest are queries that take a root. Nothing builds one. `Fragment`'s setters are
  public, so the bridge *could* construct a tree — but a point query over a tree this component
  laid out itself is not a substitution, it is the same estimator wearing the dependency's types.
- **A `Fragment` cannot be turned back into an element.** `Fragment`, `ComputedStyle`,
  `LineFragment` and `InlineFragment` expose **zero** members typed from `Broiler.Dom`;
  `ComputedStyle.TagName` is a string, so two `<div>`s are indistinguishable.
  `elementFromPoint`/`elementsFromPoint` must answer with an `Element`, and no correspondence
  survives the query. Matching the winner back by `Bounds` and tag name is a heuristic, which is
  the class of answer tier A exists to have removed.
- **The internal route stops one step short, and stops there for a reason.** `Broiler.Layout`
  grants `InternalsVisibleTo` to `Broiler.HtmlBridge.Dom`, so `IR.FragmentTreeBuilder.Build(CssBox
  root)` *is* callable from the project hit testing lives in — and `Engine.CssBox` carries an
  `internal DomElement SourceElement`, which is exactly the identity `Fragment` lacks. But
  `FragmentTreeBuilder.Build` returns the tree and nothing else: no box-to-fragment correspondence
  comes back, so `SourceElement` cannot be carried across it. And obtaining the `CssBox` root in
  the first place means running Layout's own engine over a document, which is the missing
  `ILayoutView` implementation of tier D: `CssBox`'s only accessible constructor is
  `(CssBox parent, HtmlTag tag, Uri baseUrl)` and `CssLayoutEngine` exposes line-boxing and cell
  alignment, not a document entry point.

What would make it adoptable is one member: a public path from a `DomDocument` to a fragment tree
whose fragments name their source element — which is [#4](https://github.com/Broiler-Platform/Broiler.Layout/issues/4) again, and is why that issue gates
this one as much as it gates tier B and tier D.

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

> **This claim was too broad, and the correction is worth reading.** Closing `TryParsePx` closed one
> route, and tier A read as though it had closed the problem. It had not. The length evaluator on the
> other side — `DomBridge.TryEvaluateCssLengthWithViewport` — guarded only `double.IsNaN`, and every
> one of its unit branches parses with `NumberStyles.Float` of its own. So `border-top-width: 1e400px`
> still answered `element.clientTop === Infinity`, as did `1e400em`, `1e400rem`, `1e400vw`,
> `calc(1e400px)` — and `Infinityem` and `Infinityrem`, the very spelling this paragraph claims to
> have removed.
>
> **And that correction was too narrow in its turn.** Four audits since have found **sixteen** routes
> by which a value this component cannot represent reached a page, in six subsystems that share no
> code with the length evaluator or with each other; six more turned up while the sixteen were being
> closed, and five more again when those fixes were reviewed. What a page actually read back,
> measured rather than reasoned about:
>
> | Where | What a page read |
> | --- | --- |
> | The transform pipeline (`DomBridgeUtils/Animations.cs`) — translations, `matrix()` components, angles, scale factors, percentage translations, `transform-origin`, the composition of two representable functions, and an `animate()` keyframe | `getBoundingClientRect()` answering `left === Infinity` **and** `width === NaN` off one declaration; `rotate(1e400deg)` making all four numbers `NaN`, because `Math.Cos` of a non-finite angle is; `transform: translateX(Infinitypx)` written into the serialized document |
> | SVG geometry attributes (`DomBridge/LayoutMetrics.Svg.cs`) — extents, origins, text coordinates, a `<textPath>` moveto, and the `viewBox` mapping | infinite and `NaN` client rects; and `elementFromPoint` answering a `<rect>` for every point on its row, 580px clear of its right edge, because the candidate test is `x < rect.Left + rect.Width` |
> | The `line-height` multiplier (`DomBridge/LayoutMetrics.Geometry.cs`) | a list-box `<select>` answering `scrollHeight === Infinity`, and `NaN` for the symbolic spelling |
> | A frame's `width`/`height` content attribute (same file, and three more parses in `DomBridge/Css.cs` and `DomBridgeUtils/Css.cs`) | the sub-document answering `documentElement.clientWidth === Infinity`, and its media queries matching `(min-width: 2000000000px)` — because `(int)` of an infinity saturates rather than failing |
> | Used zoom (`DomBridge/LayoutMetrics.Svg.cs`, and the two serialization walks) | `getBoundingClientRect()` answering `NaN`, `offsetWidth` answering `0`, and `style="width: Infinitypx"` baked into the document |
> | `img.width` / `img.height` (`Dom/Features/ComputedStyleBinding.cs`) | `Infinity` from the CSS branch and from the content attribute alike, and `NaN` from `width="NaN"` |
> | The SVG DOM's own readers of a geometry attribute (`Dom/Features/SvgElementBinding.cs`) — `SVGAnimatedLength`, `SVGAnimatedRect`, and the text metrics off a `font-size` attribute | `rect.width.baseVal.value === Infinity` and `svg.viewBox.baseVal.width === Infinity`; `getComputedTextLength()` answering `Infinity` and `getStartPositionOfChar(0)` answering `{x: NaN, y: Infinity}` |
> | The lengths serialization *computes* (`DomBridge/Serialization.Rendering.cs`, `DomBridgeUtils/Serialization.cs`) — an SVG attribute rescaled by a used zoom, and a `<progress>` track placeholder | `width="Infinity"` and `d="M Infinity 10"` written into the serialized document at `zoom: 2` from a `1e308` a double holds; `<progress style="width: 1e400px">` serialized with `width: Infinitypx` |
>
> The last two rows are the review of the fixes above finding five more the audits had not named,
> and they are worth their own sentence, because both say the same thing about *where* to look. The
> SVG DOM rows are a **second reader of markup a fix had already closed**: `ResolveSvgLength` refuses
> `<rect width="1e400">` so it has no client rect and takes no hit test, while the IDL stub beside it
> answered `Infinity` to `width.baseVal.value` on the same page. Closing one reader of an attribute
> is not closing the attribute. The serialization rows are the **arithmetic with no parse to guard**:
> the zoom is finite because an earlier round refuses one that is not, the attribute is finite
> because a page wrote `1e308`, and their product is not.
>
> **The rule that would have prevented all of them**, stated once:
>
> 1. **Test representability, not spelling.** An exponent overflows a double **without any symbol in
>    it**, and so does a long enough run of digits, so `1e400px` and a 401-digit coordinate both slip
>    past every guard written against `NaN` and `Infinity`. `!double.IsNaN(x)` is not that test — it is
>    true of `+∞` — and neither is `x > 0`, which is false for `NaN` and true for `+∞`, nor a cast to
>    `int`, which saturates. `double.IsFinite(x)` is.
> 2. **Refuse where the value comes into existence, not where its parts were read.** Half of these
>    cannot be caught at a parse at all, because both operands are representable and the arithmetic is
>    not: `scale(1e200) scale(1e200)`, `line-height: 1e307` times a `16px` font, an SVG rect mapped
>    through a `viewBox` scale, two nested zooms. So the test goes on the assembled matrix, the
>    resolved rect, the resolved line height, the used zoom — one exit each, covering every unit and
>    every function a later round adds.
> 3. **Unless the read's own fallback is the grammar's.** Where a reader already has a defined answer
>    for a value it cannot *read* — an SVG attribute falling to its lacuna chain, `img.width` falling
>    to the content attribute and then to `0`, a frame falling to the default viewport — refusing at
>    the read gives the same answer and a better one, and `DomBridgeUtils.TryParseFiniteScalar` is the
>    one helper that does it. Where the reader's fallback would be a *substitution* — `0` for a length
>    or `1` for a scale factor, in the middle of an expression the page wrote — it must not be used,
>    because `translate(1e400px, 20px)` becoming `translate(0, 20px)` invents a number too.
> 4. **Do not clamp.** `double.MaxValue`, `int.MaxValue` and `0` are all numbers the page never wrote.
>    What cannot be represented is not a length, not an angle, not a matrix — it takes the same path a
>    value that cannot be *parsed* takes, refused at the level the CSS grammar would refuse it.
>
> Each route has a test file that argues its own case and says which of its cases failed at HEAD:
> `tests/NonFiniteLengthTests.cs`, `NonFiniteTransformTests.cs`,
> `NonFiniteKeyframeInterpolationTests.cs`, `NonFiniteSvgGeometryTests.cs`,
> `NonFiniteLineHeightTests.cs`, `NonFiniteFrameViewportTests.cs`, `NonFiniteUsedZoomTests.cs`,
> `NonFiniteImageDimensionTests.cs`, `NonFiniteSvgDomLengthTests.cs` and
> `NonFiniteSerializedLengthTests.cs`. Every one of them reads its route back the way a page does —
> `clientTop`, `getBoundingClientRect()`, `scrollHeight`, `clientWidth`, `img.width`,
> `rect.width.baseVal.value`, `elementFromPoint`, the serialized document — and never through the
> helper that was changed. `tests/NonFiniteValueSurfaceTests.cs` is the net over all of them: one
> class, one question per route, and a roster whose count is asserted so that the next route has to
> be added to it deliberately — which is how the five above landed there rather than in a report.

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

**Closed, and the stub is gone** — `0.1.0-preview.6` adds `TryMatch`, which reports whether the
answer is one the matcher can stand behind, with the lenient `Matches` untouched for the cascade.
The pass takes the strict answer and reads "cannot say" as no match, so the silently-dropped
declarations above are now applied and `:read-only` still attaches its animation to nothing. See
[Adopted after the bump](#adopted-after-the-bump).

### A gap found in the dependency

`CssValueParser.TryParseNumeric` does not scan an exponent, so `1e2px` — valid `<length>` per
css-syntax-3 §4.3.12, and accepted by `CssLengthParser` in the same package — does not parse.
`DomBridgeUtils.TryParseExponentNumber` covers exactly that shape so no valid length regressed. When
`TryParseNumeric` learns exponents the fallback becomes dead rather than wrong. Filed as
[Broiler.CSS#53](https://github.com/Broiler-Platform/Broiler.CSS/issues/53), which also covers the opposite error found while writing
it up: `1.px` parses *successfully* as `1px`.

**Closed, and the fallback is gone** — `0.1.0-preview.6` reads exponents and rejects `1.px`. The
finiteness test inside the fallback did not go with it; see [Adopted after the bump](#adopted-after-the-bump)
for why an overflowing exponent is a length upstream and is not one here.

`CssSyntax.FindMatching` answers `text.Length - 1` when nothing matches, not `-1`, so a caller must
test the landing character rather than the sign. An unterminated `translateY(` otherwise takes the
rest of the string as its argument. Filed as [Broiler.CSS#54](https://github.com/Broiler-Platform/Broiler.CSS/issues/54).

**Closed, and the guard is a sign test** — `0.1.0-preview.6` answers `-1`. The two guards agree on
every input (checked over 8,546 value/index pairs), so no behaviour test distinguishes them and the
dependency's new contract is pinned directly instead.

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

**Closed, and the name test is gone** — `0.1.0-preview.5` adds `IsQuirksDoctype(name, publicId,
systemId)`, the same condition over a parsed triple. The gap was never a duplication and never
depended on the bump for its existence: `IsQuirksHtml` read the public identifier in
`0.1.0-preview.4` too (checked against that assembly in a process that loaded only it), so a
legacy-doctype document parsed in quirks mode and serialised into standards. What the bump supplied
was the only predicate a caller holding a `DomDocumentType` could ask. See
[Adopted after the bump](#adopted-after-the-bump).

## Tier C — belongs downstream

These are recommendations for the named repository. The bridge cannot act on them alone, and should
not: re-implementing them better here would deepen the mislayering rather than fix it.

### Broiler.Layout

| What the bridge owns | Where | Lines | Filed |
| --- | --- | --- | --- |
| The CSS Overflow 3 §3.1 scrollable overflow region, walked and unioned per element | `DomBridge/LayoutMetrics.cs:355` | ~170 | [Broiler.Layout#5](https://github.com/Broiler-Platform/Broiler.Layout/issues/5) |
| A complete CSS Transforms 1 used-value engine — a transform list folded into an affine matrix | `DomBridgeUtils/Animations.cs` | ~150 | [Broiler.Layout#7](https://github.com/Broiler-Platform/Broiler.Layout/issues/7) — **implemented upstream; adoption reverted, [see above](#the-transform-resolver-attempted-and-reverted)** |
| `elementFromPoint`/`elementsFromPoint` paint-order hit testing, recursing the DOM in reverse child order | `DomBridge/HitTesting.cs:74` | ~40 | [Broiler.Layout#6](https://github.com/Broiler-Platform/Broiler.Layout/issues/6) — **implemented upstream; not reachable from here, [see above](#the-paint-order-hit-test-and-what-it-needs)** |
| ~~The SVG `viewBox` user-space mapping, hardcoded to the default `preserveAspectRatio`~~ | `DomBridge/LayoutMetrics.Svg.cs` | ~3 | [Broiler.Layout#10](https://github.com/Broiler-Platform/Broiler.Layout/issues/10) — **adopted, and the other eight alignments with it** |

The transform engine looked like the clearest case: Layout already had a transform model
(`IR.TransformItem.Matrix`, `AffineLayerMap`, `SvgTransform`) and the bridge had built a second one
beside it. #7 shipped the public resolver and the substitution still does not hold — the second
engine is not a duplicate of the first so much as a more forgiving one, and the forgiveness is
load-bearing for two inputs this caller actually sees. The hit-testing one is a correctness gap
rather than a duplication — a point query needs
the paint-order model (`IR.Fragment` with `CreatesStackingContext`, `StackLevel`, `TopLayerOrder`),
and #6 shipped exactly that. It still cannot be called: the query takes a fragment tree this
component has no public way to obtain, and answers with fragments that name no element. The
measurements are in [The paint-order hit test, and what it needs](#the-paint-order-hit-test-and-what-it-needs).

### Broiler.CSS

| What the bridge owns | Where | Lines | Filed |
| --- | --- | --- | --- |
| ~~Selector scoping into a shadow tree — parsing selector lists into compounds and combinators~~ | `DomBridgeUtils/Selectors.cs` | ~120 | [Broiler.CSS#55](https://github.com/Broiler-Platform/Broiler.CSS/issues/55) — **adopted, deleted** |
| `url()` tokenising and rebasing a sheet's relative URLs against its own base | `DomBridgeUtils/Css.cs:240` | ~33 | dropped — see above |
| The used value of `line-height`, resolved three independent times and never shared | `DomBridgeUtils/AnchorResolver.cs:290` | ~27 | dropped — see above |

The selector one was blocked by a shape, not a gap: `CssSelectorParser.Parse` is public but
`CssSelector` exposed only `Text` and `Specificity` — no compounds, no combinators, no offsets — so
the bridge re-parsed to get at structure it could not otherwise see.

**Closed, and the scans are gone** — `0.1.0-preview.6` adds `Compounds`, `Combinators` and
`Subject`, each compound carrying `Start`/`Length`/`TypeSelectorEnd`/`PseudoElementStart` as offsets
into the selector's own text, and `CssSyntax.SplitTopLevel` for the comma split above them. The
rewrite is now one `Insert` at `Subject.TypeSelectorEnd`, and it turned out not to be a preserving
substitution: the old scans corrupted a selector carrying a comment, split on a comma inside a
comment or behind a backslash, and marked a `-`-initial or escaped type selector before the type
selector rather than after it. Old and new were run against each other over 177,217 generated
preludes plus four further alphabets, with zero differences on well-formed input outside those two
classes. The `:host`/`::slotted`/`::part` guard is unchanged and now reads the parsed subject
compound.

`line-height` being resolved three times **within this component** is worth fixing here regardless of
where it eventually lives; see the open items below.

### Broiler.DOM

| What the bridge owns | Where | Lines | Filed |
| --- | --- | --- | --- |
| ~~Declarative shadow roots: a post-parse pass finding `<template shadowrootmode>` and attaching~~ | `DomBridge/HtmlParsing.cs` | ~70 | [Broiler.DOM#21](https://github.com/Broiler-Platform/Broiler.DOM/issues/21) — **adopted, deleted** |
| ~~The `<template>` contents model (HTML §4.12.3), held as a side table keyed on the element~~ | `DomBridge/HtmlParsing.cs` | ~65 | [Broiler.DOM#22](https://github.com/Broiler-Platform/Broiler.DOM/issues/22) — **adopted, deleted** |
| Parsing a `<meta http-equiv=refresh>` content value | `Core/Dom/MetaRefreshDiscovery.cs:59` | ~40 | dropped — see above |

The first two were tree-construction output, which is why they went upstream and then away from here
— template contents is part of a node's data model, and keeping it in a
`Dictionary<DomElement, DomDocumentFragment>` beside the tree was a workaround for the parser not
producing it. `HtmlMetaScanner` already owns *finding* the meta-refresh element; reading its content
value belongs next to it, and that one is still open.

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
- **`img.width`/`.height` read their content attribute as a number, not by HTML's integer grammar**
  — `Dom/Features/ComputedStyleBinding.cs`. The *culture* half of this is now fixed: the read goes
  through `DomBridgeUtils.TryParseFiniteScalar`, so a dot is a decimal point everywhere. What
  remains is the grammar. HTML applies the rules for parsing non-negative integers, which take the
  leading digit run and stop, so a browser answers `1` for `<img width="1.5">` where this answers
  `1.5`, and ignores a negative value outright where this uses it. Smaller than the culture defect
  was, and in a different direction — it makes this component *more* precise than the Standard
  rather than differently precise on different machines.

  > The culture defect is worth remembering for its shape. The read used the parameterless
  > `double.TryParse`, which takes the machine's current culture *and* `AllowThousands`. On a German
  > machine the group separator is `.`, so `<img width="1.234">` answered **1234** — a thousandfold
  > error, growing with the number of digits in the group — and `<img width="1,5">` answered `1.5`,
  > the correct answer to a *different* spelling, which is what made it hard to see. Every other
  > numeric read in these files already went through `InvariantCulture`; this one had been missed
  > rather than decided, and a machine with an English locale would never have shown it.
- **A frame dimension a double holds is still truncated by a cast to `int`** —
  `DomBridgeUtils.ParseViewportDimensionAttribute` and `CascadedFrameViewport.ResolveFrameLength`.
  `<iframe width="1e30">` is a number the page wrote and this component can represent, and it still
  reaches the frame's media queries as `int.MaxValue`. Only the *unrepresentable* half was closed;
  the viewport being an `int` at all is the wider question.
- **`ParseKeyframeEntries` admits a non-finite `@keyframes` selector percentage** —
  `DomBridgeUtils/Animations.cs`. Recorded by the transform round, which could not reach the CSS
  animation bake from a page at all and so declined to change it without a test. Still open.
- **`line-height` is resolved a *fourth* and *fifth* time, and neither is finiteness-guarded** —
  `DomBridgeUtils.ResolveLineHeight` (`DomBridgeUtils/AnchorResolver.cs`) and the copy inside
  `EstimateInlineContentHeight` (`Dom/DomBridge/AnchorResolver/InlineContainingBlocks.cs`) both take
  a unitless multiplier with a bare `NumberStyles.Float` parse and return `fontSize * multiplier`.
  Both feed the anchor walk that promotes an absolutely positioned child out of an inline containing
  block and writes its offset into the child's baked `top`, so an infinity there would reach the
  serialized document. Left alone: no page reached that promotion in the fixtures tried (an
  `<span style="position: relative">` with an abspos child and a `<br>` before it was not promoted at
  all), and the rule is not to push a change that cannot be read back the way a page reads it. Worth
  a route of its own if the promotion can be reached — and worth noting that the "resolved three
  times" item above undercounts.
- **The SVG DOM IDL parses its attributes with `NumberStyles.Any`** —
  `Dom/Features/SvgElementBinding.cs`. That admits `AllowThousands`, so `<rect width="1,5">` answers
  `width.baseVal.value === 15` while the geometry side of the same attribute reads `1,5` as no length
  at all. The same defect class as the `img.width` locale item above — a separate change with a
  separate test, not folded into the finiteness one that shares the line.
