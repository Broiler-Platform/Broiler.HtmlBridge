# Third-party notices

Broiler is an independent project. References to upstream projects identify the origin
of inherited code and ideas; they do not imply affiliation, sponsorship, endorsement,
or responsibility for Broiler by the upstream authors.

**The discipline this file follows.** A component whose tree contains third-party-derived
source, or which ingests a third-party corpus, gains a section here **in the same change
that introduces it** — not at the release that discovers it. An attribution obligation
found during a publish is a stop, so the notice moves with the code rather than after it.
Each section names the component, the upstream, its licence, and where the licence text
is retained.

**One has landed since this file was written, and it is the section on ECMA-262 below**: the
JavaScript profile archives the language-specification edition it pins, which is third-party
material in the tree and gained its section in the change that introduced it, as the discipline
above requires.

**Two further subjects are anticipated and neither has landed.** `Broiler.VM.Profile.JavaScript`
plans to start from a snapshot copy of `Broiler.JS`, which is itself Apache-2.0 and derived
from Yantra JS, so the *Yantra JS* section below acquires a second component the moment
that copy lands, and the *Unicode data* section acquires one with it. And
`Broiler.VM.Profile.WebAssembly` plans to ingest a third-party conformance suite as
test-only material. Neither is recorded as a section yet because neither exists; both are
recorded here so that the first person to land one finds the obligation already written
down.

## HTML Renderer

Broiler.HTML is derived in part from [HTML Renderer](https://github.com/ArthurHub/HTML-Renderer),
created by José Manuel Menéndez Poo and developed by Arthur Teplitzki and other
contributors. HTML Renderer is licensed under the BSD 3-Clause License. Broiler retains
its copyright and license conditions in
[`LICENSES/HTML-Renderer-BSD-3-Clause.txt`](LICENSES/HTML-Renderer-BSD-3-Clause.txt).

Broiler.HTML has diverged substantially and is maintained independently. “HTML Renderer”
is used here only to describe provenance. The upstream authors have not reviewed or
endorsed Broiler.

## Yantra JS

Broiler.JS is derived in part from [Yantra JS](https://github.com/yantrajs/yantra) and
retains the contribution history and attribution of that project. Yantra JS is licensed
under the Apache License 2.0, a copy of which is included in [`LICENSE`](LICENSE).

Broiler.JS has diverged substantially and is maintained independently. “Yantra JS” is
used here only to describe provenance. The Yantra JS authors have not reviewed or
endorsed Broiler.

## Unicode data

Broiler.Unicode contains generated tables based on Unicode and CLDR data. Those data
files remain subject to the [Unicode Terms of Use](https://www.unicode.org/terms_of_use.html),
as identified in that component's documentation and notices.

## Jint

The Octane benchmark harness runs one of its reference engines on
[Jint](https://github.com/sebastienros/jint), an independent managed ECMAScript
interpreter by Sébastien Ros and contributors, licensed under the BSD 2-Clause License.
It is consumed as an unmodified NuGet package by
[`tests/octane/jint-host`](tests/octane/jint-host), a benchmark tool that is not part of
any shipped Broiler component and carries no Jint code. Jint is used here only as a
measurement reference; its authors have not reviewed or endorsed Broiler.

## ECMAScript Language Specification (ECMA-262)

`Broiler.VM.Profile.JavaScript` pins the language-specification edition its feature manifests
are defined against, and **archives the document rather than citing it**: roadmap section 24 of
that component asks for the edition retrieved, hashed *and* archived, and a digest is only
checkable by a reader who has the bytes. The edition is **ECMA-262, 17th edition (ES2026)**,
retained at
[`Broiler.VM/src/Broiler.VM.Profile.JavaScript/docs/specification/`](Broiler.VM/src/Broiler.VM.Profile.JavaScript/docs/specification/README.md)
at `tc39/ecma262` commit `0248456c758431e4bb8e5d26333ff1865123c9cd`.

The specification's natural-language text is licensed under the **Alternative copyright notice
of the Ecma text copyright policy**, which permits copying and distribution for any purpose
without fee or royalty on three conditions. Broiler retains the full notice text beside the
document, in
[`ECMA-alternative-copyright-notice.txt`](Broiler.VM/src/Broiler.VM.Profile.JavaScript/docs/specification/ECMA-alternative-copyright-notice.txt);
the document is byte-for-byte unmodified, so its own notices are intact; and the notice of
changes that condition requires records that there are none.

Broiler.VM includes material copied from the ECMAScript Language Specification, ECMA-262,
17th edition (ES2026). Copyright © Ecma International.

**It is a reference document and not code.** Nothing is derived from it, no line of it is copied
into any assembly, and it compiles into nothing. Ecma International has not reviewed or endorsed
Broiler.

## test262 (ECMAScript Test Suite)

`Broiler.VM.Composition.JavaScript.Conformance` scores the JavaScript profile against **test262**,
the ECMAScript conformance suite, and **archives the suite it scores**: roadmap section 14 of that
component asks for the revision retrieved, hashed *and* archived, and a figure is only checkable by
a reader who has the material it came from. The suite is retained at
[`Broiler.VM/src/tests/conformance/pins/`](Broiler.VM/src/tests/conformance/pins/README.md) as the
archive it was retrieved as, at `tc39/test262` commit
`ccaac100ff49d81e9ff47a75ff4c60e0bd3f262e`.

test262 is © 2012 Ecma International, made available under the **BSD 3-Clause** licence, whose
full text Broiler retains beside the archive in
[`test262-LICENSE.txt`](Broiler.VM/src/tests/conformance/pins/test262-LICENSE.txt) as condition 1
of that licence requires. **The suite is unmodified** — it is the archive as retrieved, and its
SHA-256 is what says so — so there is no changed file to mark.

**It is test-only material and reaches nothing that ships.** The harness is handed a suite as a
directory on a command line; rule **N13** asserts that neither the harness nor any suite directory
reaches a package or an advertised composition's closure, and that no project file names one. Ecma
International has not reviewed or endorsed Broiler.
