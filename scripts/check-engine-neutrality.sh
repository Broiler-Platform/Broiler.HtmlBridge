#!/usr/bin/env bash
#
# Fails when a project under src/ gains JavaScript-engine coupling it did not have.
#
# The browser binds its DOM to Broiler.JS by naming it: 905 occurrences of the
# Broiler.JavaScript namespace across src/, twelve project references into
# Broiler.JS/ and Broiler.VM/, and 61 evaluation sites. JSEAL
# (src/Broiler.HtmlBridge.Jseal) is the engine-neutral contract those are being
# moved onto, and moving them is a 250-file job.
#
# A 250-file migration lands one of two ways. As one merge, which nobody can
# review and which is blocked behind every other change to the same files for as
# long as it takes. Or incrementally -- and the failure mode of incremental is
# not that it stalls, it is that it goes BACKWARDS while looking like it is
# working: a hundred files get ported, and meanwhile new bindings are written in
# the old vocabulary, because the old vocabulary is what the neighbouring file
# still uses and nothing anywhere says not to. The migration then finishes at
# whatever number the two rates happened to meet at, and the last dozen files are
# the ones nobody can find.
#
# So this is a ratchet rather than a threshold. eng/jseal-budget.json records what
# each project's coupling is TODAY; this recounts and fails on any project that
# went up. Nothing is forbidden outright, which matters -- Broiler.HtmlBridge.Dom
# is at 891 and every honest change to it lands before it reaches 0 -- but the
# direction is fixed, and a project already at 0 cannot take a new engine-coupled
# file at all.
#
# It also guards the one claim in this repository the compiler makes rather than
# the budget: Broiler.HtmlBridge.Jseal.csproj has no ProjectReference and no
# PackageReference, so that assembly cannot name a Broiler.JavaScript type no
# matter what any grep says. The counts below are only worth reading while that
# holds, so a reference of either kind appearing there is a failure ahead of all
# of them.
#
# Three things it deliberately does NOT check.
#
# It does not read C#. It counts text, and a file that reaches the engine through
# a type alias, an `extern alias`, or a helper in some other assembly is invisible
# to it. That is accepted rather than regretted: the namespace IS the migration's
# vocabulary, so hiding it is not something anyone does by accident, and a diff
# that hides it to get past this check is a diff a reviewer can see doing that.
#
# It does not look outside src/. Broiler.JS and Broiler.VM are engines; naming
# their own types is the whole of what they are. Only this repository's own
# binding layer is bound by a budget.
#
# It does not build, restore, or evaluate MSBuild -- it reads the .csproj files as
# XML and the .cs files as text, so it finishes in under a second and can run
# before the long jobs rather than after them. The cost is that a ProjectReference
# injected by a Directory.Build.targets rather than written in the project body is
# not counted. Today none of the injected ones point at an engine (they collapse
# the vendored Graphics and Media checkouts), and a check that evaluated projects
# to find out would cost a restore and a workload to answer a question the project
# bodies already answer.
#
# Usage: scripts/check-engine-neutrality.sh
#        No arguments. Every directory directly under src/ must have an entry in
#        eng/jseal-budget.json -- every directory, not only the ones with a
#        project file, because src/Broiler.App has .cs files and no .csproj and is
#        otherwise the one place an engine reference could sit unbudgeted.
#
# Requires: bash, python3.

set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root" || exit 1

budget="eng/jseal-budget.json"
jseal="src/Broiler.HtmlBridge.Jseal/Broiler.HtmlBridge.Jseal.csproj"

if [ ! -f "$budget" ]; then
  echo "::error::$budget is missing. It is the ratchet; without it there is nothing to compare against and this check cannot pass vacuously."
  exit 1
fi

# GitHub renders ::error and ::notice annotations; a local run should not be
# shouted at in workflow-command syntax it cannot use.
annotate() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    echo "::error::$1"
  else
    echo "ERROR: $1" >&2
  fi
}

# Under budget is good news, not a failure -- but it is news the reader has to act
# on in the same commit, so it is annotated rather than printed into the scroll.
observe() {
  if [ -n "${GITHUB_ACTIONS:-}" ]; then
    echo "::notice::$1"
  else
    echo "note: $1"
  fi
}

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# One pass over the tree, in python because the .csproj side has to be parsed as
# XML rather than grepped: this project file talks about ProjectReference in a
# comment explaining why it has none, and a grep would read that comment as the
# thing it warns about. Everything it decides comes back as tab-separated records
# so the annotating, and the exit code, stay in bash where GITHUB_ACTIONS is.
python3 - "$budget" >"$work/records.txt" 2>"$work/python.log" <<'PY'
import json, os, re, sys
import xml.etree.ElementTree as ET

budget_path = sys.argv[1]
SRC = "src"
JSEAL = os.path.join("src", "Broiler.HtmlBridge.Jseal", "Broiler.HtmlBridge.Jseal.csproj")
METRICS = ("engineReferences", "engineProjectRefs", "guestEvalSites")
SKIP = {"bin", "obj"}

out = []
def emit(*fields):
    out.append("\t".join(str(f) for f in fields))

def tag(element):
    # MSBuild's old format carries an xmlns; the SDK format does not. Either way
    # the local name is what identifies the item.
    return element.tag.rsplit("}", 1)[-1]

def project_references(path):
    """Every ProjectReference Include in a project file, as forward-slash paths.

    ElementTree drops comments, which is exactly the reading wanted: a
    commented-out reference links nothing."""
    root = ET.parse(path).getroot()
    return [(e.get("Include") or "").replace("\\", "/")
            for e in root.iter()
            if tag(e) == "ProjectReference"]

def package_references(path):
    root = ET.parse(path).getroot()
    return [(e.get("Include") or "") for e in root.iter() if tag(e) == "PackageReference"]

def files_under(directory, suffix):
    for base, dirs, names in os.walk(directory):
        dirs[:] = [d for d in dirs if d not in SKIP]
        for name in sorted(names):
            if name.endswith(suffix):
                yield os.path.join(base, name)

# ── The claim the compiler makes, checked before the counts that depend on it ──
if not os.path.isfile(JSEAL):
    emit("error", "%s is missing. Every 0 in %s is a claim about that assembly; without it they "
                  "assert nothing." % (JSEAL.replace(os.sep, "/"), budget_path))
else:
    try:
        offending = [("ProjectReference", i) for i in project_references(JSEAL)]
        offending += [("PackageReference", i) for i in package_references(JSEAL)]
    except ET.ParseError as exc:
        offending = None
        emit("error", "%s could not be parsed as XML (%s), so its neutrality could not be checked."
             % (JSEAL.replace(os.sep, "/"), exc))
    if offending:
        for kind, include in offending:
            emit("error",
                 "%s declares a %s to '%s'. That project is engine-neutral BY COMPILATION -- it "
                 "references nothing, so it cannot name an engine type -- and this reference ends "
                 "that. Put the dependency in a provider assembly instead; see the comment at the "
                 "bottom of that project file." % (JSEAL.replace(os.sep, "/"), kind, include))

# ── The budget file ──
try:
    with open(budget_path, encoding="utf-8") as handle:
        budget = json.load(handle)
except (OSError, ValueError) as exc:
    emit("error", "%s could not be read as JSON: %s" % (budget_path, exc))
    print("\n".join(out))
    sys.exit(0)

projects = budget.get("projects")
if not isinstance(projects, dict) or not projects:
    emit("error", "%s has no 'projects' object. This check cannot pass vacuously." % budget_path)
    print("\n".join(out))
    sys.exit(0)

for name, entry in sorted(projects.items()):
    if not isinstance(entry, dict):
        emit("error", "%s: the entry for %s is not an object." % (budget_path, name))
        continue
    for metric in METRICS:
        value = entry.get(metric)
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            emit("error", "%s: %s has no whole-number '%s'. Every entry must budget every metric; "
                          "a missing one is an unbudgeted metric, not a zero."
                 % (budget_path, name, metric))
    role = entry.get("role")
    if role is not None and role not in ("provider",):
        emit("error", "%s: %s has role '%s'. The only role is 'provider', which relaxes the "
                      "source-reference metrics for an assembly whose job is to name an engine."
             % (budget_path, name, role))
    for key in entry:
        if key not in METRICS and key not in ("note", "role"):
            emit("error", "%s: %s carries an unrecognised key '%s'. A misspelt metric budgets "
                          "nothing." % (budget_path, name, key))

RELATIVE_ENGINE_NAMESPACE = re.compile(r"(?<![\w.])JavaScript\.[A-Z]")

# ── Recount ──
if not os.path.isdir(SRC):
    emit("error", "There is no src/ directory to measure. This check cannot pass vacuously.")
    print("\n".join(out))
    sys.exit(0)

on_disk = sorted(d for d in os.listdir(SRC) if os.path.isdir(os.path.join(SRC, d)))

for name in sorted(set(projects) - set(on_disk)):
    emit("error",
         "%s budgets '%s', which is not a directory under src/. A stale entry lets a project come "
         "back at its old count without anyone arguing for it; delete the entry, or fix the name."
         % (budget_path, name))

scanned_files = 0
scanned_projects = 0

for name in on_disk:
    directory = os.path.join(SRC, name)

    engine_references = 0
    guest_eval_sites = 0
    for path in files_under(directory, ".cs"):
        scanned_files += 1
        with open(path, encoding="utf-8", errors="replace") as handle:
            text = handle.read()
        engine_references += text.count("Broiler.JavaScript")
        # AND THE SAME NAMESPACE SPELLED WITHOUT ITS ROOT, WHICH THIS CHECK COULD NOT SEE UNTIL 108
        # TURNED OUT TO BE 129. Every project under src/ has a RootNamespace beginning `Broiler.`, so
        # `JavaScript.Runtime.JSObject` compiles and means exactly what
        # `Broiler.JavaScript.Runtime.JSObject` means -- and the substring count above matches only
        # the second. Twenty-one real engine references were spelled the first way, in the files most
        # likely to have them: the ones mid-migration, where somebody dropped a `using` and qualified
        # what was left. A metric that a rename can walk out of is not a ratchet.
        #
        # The lookbehind is what keeps this from double-counting: in `Broiler.JavaScript.Runtime` the
        # `JavaScript` is preceded by a dot, so only the bare spelling matches here. Text, not
        # semantics, exactly as the count above -- a mention in a doc comment counts, and that is the
        # deliberate bargain the budget file's header already explains.
        #
        # The trailing [A-Z] is not decoration. Without it the pattern matches the last word of
        # "...source the page supplied, which is JavaScript." -- and it did, in four doc comments,
        # two of them inside Broiler.HtmlBridge.Jseal, whose whole claim is that it contains no
        # engine reference at all. A namespace segment never follows the dot with a space; an
        # English sentence always does.
        engine_references += len(RELATIVE_ENGINE_NAMESPACE.findall(text))
        guest_eval_sites += text.count(".Eval(")

    engine_project_refs = 0
    for path in files_under(directory, ".csproj"):
        try:
            includes = project_references(path)
        except ET.ParseError as exc:
            emit("error", "%s could not be parsed as XML (%s)."
                 % (path.replace(os.sep, "/"), exc))
            continue
        engine_project_refs += sum(
            1 for include in includes
            if re.search(r"(^|/)Broiler\.(JS|VM)/", include))

    actual = {
        "engineReferences": engine_references,
        "engineProjectRefs": engine_project_refs,
        "guestEvalSites": guest_eval_sites,
    }

    entry = projects.get(name)
    if entry is None:
        emit("error",
             "src/%s has no entry in %s. Every directory under src/ needs one, so that a new "
             "project starts from a number somebody chose rather than from no number at all. It "
             "measures %d/%d/%d (engineReferences/engineProjectRefs/guestEvalSites) -- add it with "
             "those, or with 0/0/0 if it is meant to be neutral."
             % (name, budget_path, engine_references, engine_project_refs, guest_eval_sites))
        continue

    scanned_projects += 1
    if not isinstance(entry, dict):
        continue

    # A PROVIDER IS THE ONE KIND OF PROJECT WHOSE ENGINE REFERENCES ARE SUPPOSED TO GROW.
    # Naming Broiler.JavaScript is what src/Broiler.HtmlBridge.Jseal.BroilerJs is FOR: every
    # reference in it is one the bridge no longer has to make. Ratcheting its source-reference count
    # to an exact number would fail the build for fixing a bug in it, and the pressure that creates is
    # to make the provider thinner by pushing engine detail back into the bridge -- the exact opposite
    # of the point. So for an entry marked "role": "provider", engineReferences and guestEvalSites are
    # RECORDED, not enforced.
    #
    # engineProjectRefs stays enforced even for a provider, because "which engine assemblies does this
    # link" is a real boundary and the failure it catches is real: a Broiler.JS provider that quietly
    # acquired a Broiler.VM reference, or reached past the two projects it needs into the rest of the
    # engine, should have to argue for it in a diff.
    is_provider = entry.get("role") == "provider"
    enforced = ("engineProjectRefs",) if is_provider else METRICS

    verdict = "ok"
    for metric in METRICS:
        allowed = entry.get(metric)
        if not isinstance(allowed, int) or isinstance(allowed, bool):
            verdict = "bad"
            continue
        if metric not in enforced:
            # Keep the recorded number honest even though it is not a gate: a figure nobody updates
            # is worse than no figure, because the next reader believes it.
            if actual[metric] != allowed:
                emit("record", name, metric, allowed, actual[metric])
            continue
        if actual[metric] > allowed:
            emit("over", name, metric, allowed, actual[metric])
            verdict = "over"
        elif actual[metric] < allowed:
            emit("under", name, metric, allowed, actual[metric])
            if verdict == "ok":
                verdict = "under"

    # The per-project summary line is always an "ok" row, even when a metric came in under budget.
    # It used to be emitted as `emit(verdict, ...)`, which for an under-budget project produced a
    # SECOND "under" row carrying the summary's three counts -- and the shell renders an "under" row
    # with the tightening message, so it read "Dom is UNDER budget: 861 is 55, and the budget still
    # says 4", pairing one metric's numbers with another's. The per-metric "under" rows above already
    # say what to lower and to what; this row only reports where the project now stands.
    if verdict in ("ok", "under"):
        emit("ok", name, engine_references, engine_project_refs, guest_eval_sites)

emit("scanned", scanned_projects, scanned_files)
print("\n".join(out))
PY

if [ ! -s "$work/records.txt" ] && [ -s "$work/python.log" ]; then
  annotate "The measurement itself failed, so nothing was proved about any project."
  sed 's/^/    /' "$work/python.log" >&2
  exit 1
fi

status=0
loose=0
scanned_projects=0
scanned_files=0

while IFS=$'\t' read -r kind a b c d; do
  case "$kind" in
    error)
      annotate "$a"
      status=1
      ;;
    over)
      annotate "src/$a is over budget: $b is $d, and $c is what eng/jseal-budget.json allows. Port the new coupling onto Broiler.HtmlBridge.Jseal rather than raising the number -- these may fall and may never rise."
      status=1
      ;;
    under)
      observe "src/$a is UNDER budget: $b is $d, and eng/jseal-budget.json still says $c. Lower it to $d in this commit; a ratchet nobody tightens stops at the worst day it ever had."
      loose=1
      ;;
    record)
      observe "src/$a records $b as $c and it is now $d. This project is a provider, so the number is reported rather than enforced -- update it in this commit so the file keeps describing the tree."
      loose=1
      ;;
    ok)
      printf '   ok  %-31s %4s references  %2s engine project refs  %3s eval sites\n' "$a" "$b" "$c" "$d"
      ;;
    scanned)
      scanned_projects="$a"
      scanned_files="$b"
      ;;
  esac
done <"$work/records.txt"

# A run that measured nothing must not report success. Every failure above is a
# statement about a project that WAS measured; none of them fires when the walk
# found no projects at all, which is what a moved directory or a broken checkout
# looks like from here.
if [ "$scanned_projects" -eq 0 ] || [ "$scanned_files" -eq 0 ]; then
  annotate "No projects were measured ($scanned_projects projects, $scanned_files source files). This check cannot pass vacuously."
  exit 1
fi

echo
if [ "$status" -ne 0 ]; then
  echo "Engine coupling grew, or the neutrality claim broke. See the errors above."
elif [ "$loose" -eq 1 ]; then
  echo "$scanned_projects projects, $scanned_files source files: nothing grew, and at least one budget is now looser than the tree. Lower it here."
else
  echo "$scanned_projects projects, $scanned_files source files: every count is exactly its budget."
fi
exit "$status"
