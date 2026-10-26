#!/usr/bin/env bash
# Brief 27 proof: the assistant playbook (.cursor/commands/*.md) driven end to end on the 10-file import fixture, offline:
# /import → /seal-handoff → (evaluator seals) → /tune batch 01 → /tune batch 02 → /blind-prep → (operator runs the blind set,
# evaluator aligns and builds truth) → /score. Each /command runs the fenced "bash run" blocks of its file, verbatim.
# MODE=stub: no Azure — Content Understanding only from the corpus-v3 cache (the fixture's PDFs are corpus-v3 renderings),
# no embeddings, and a learn model that never verifies. Run from the repository root:
#   bash docs/evaluation/results/2026-10-25-playbook-proof/prove.sh
# Needs eval-runs/corpus-v3 (lossrun eval generate --out eval-runs/corpus-v3 --seed 8) for the fixture's truth, and
# eval-runs/corpus-v3-cu-cache for the PDFs (without it they are reported "not run (stub)").
set -euo pipefail
export PYTHONIOENCODING=utf-8   # Windows: Python would write the extracted scripts (and their heredocs) in the console code page
SP="$PWD/docs/evaluation/results/2026-10-25-playbook-proof"

echo "== build"
MSBUILDDISABLENODEREUSE=1 dotnet build src/LossRun.Tool -c Release -o tools/lossrun -nodeReuse:false -v q | grep -E " error |Elapsed" || true
export LR="$PWD/tools/lossrun/lossrun"
export ARCHIVE="$PWD/src/TestShared/fixtures/import"
export WORK="${WORK:-eval-runs/playbook-proof}"   # relative: Windows Python cannot open /c/... paths
export MODE=stub
export BUDGET=1
# CU_CACHE and CORPUS_V3 (read by make_truth.py) may point at another checkout's eval-runs (never copied into the repo).
export CU_CACHE="${CU_CACHE:-eval-runs/corpus-v3-cu-cache}"
export CORPUS_V3="${CORPUS_V3:-eval-runs/corpus-v3}"
rm -rf "$WORK"; mkdir -p "$WORK"
cp src/TestShared/fixtures/import-carriers.csv "$WORK/carriers.csv"

# Runs the ```bash run blocks of a command file, in order, in one strict shell.
command() {
  echo; echo "==== /$1  (.cursor/commands/$1.md)"
  python - ".cursor/commands/$1.md" > "$WORK/.cmd-$1.sh" <<'PY'
import re, sys
text = open(sys.argv[1], encoding="utf-8").read()
print("\n".join(re.findall(r"^```bash run\n(.*?)^```", text, flags=re.S | re.M)))
PY
  bash -euo pipefail "$WORK/.cmd-$1.sh"
}

command import

echo; echo "==== data team: truth.csv (make_truth.py stands in for the export, from the generator's truth)"
python docs/evaluation/results/2026-10-23-blind-run-proof/make_truth.py "$WORK/truth.csv"

command seal-handoff

echo; echo "==== evaluator (not the assistant): publish the seed, seal (fixture: --tuning 10 of 10 files gives 8 tuning, 2 blind)"
"$LR" eval seal --corpus "$WORK/corpus" --out "$WORK/seal" --tuning 10 --seed 27

export BATCH=01
command tune
echo; echo "==== assistant between batches: tier-1 fixes in the list: $(python -c "import json,sys; b=json.load(open(sys.argv[1],encoding='utf-8')); print(sum(1 for f in b['fixes'] if f['tier']==1))" "$WORK/tune/batch-01/batch.json");" \
  "tier-2 pull-request bodies: $(ls "$WORK/tune/batch-01/pr-bodies" | wc -l) (opened, not merged: the human has not merged any)"

export BATCH=02
command tune
python - "$WORK/tune/batch-02/batch.json" <<'PY'
import json, sys
b = json.load(open(sys.argv[1], encoding="utf-8"))
assert b["tier2Changes"] == 0, b["tier2Changes"]
print(f"batch 02: tier-2 (grammar) changes since batch 01 = {b['tier2Changes']}; verdict {b['verdict']['status']}")
PY

command blind-prep

echo; echo "==== operator (blind-run.md §6) and evaluator (§7–8), into \$WORK/blind/ — the assistant never opens it"
"$LR" eval run --corpus "$WORK/corpus" --out "$WORK/blind/results/build" --mode "$MODE" --only-blind "$WORK/seal/seal-manifest.json" --cu-cache "$CU_CACHE"
"$LR" eval align --archive "$WORK/truth.csv" --results "$WORK/blind/results/build" --out "$WORK/blind/align/build" --corpus "$WORK/corpus"
# truth.csv covers the whole pool (tuning claims too) and the stub run delivers little, so most archive claims are
# archive-only with no decision: eval truth refuses them (brief 30 item 21) until the evaluator accepts the drops explicitly.
if "$LR" eval truth --archive "$WORK/truth.csv" --aligned "$WORK/blind/align/build/aligned.csv" --corpus "$WORK/corpus"; then
  echo "TRUTH SHOULD HAVE REFUSED DEFAULTED ARCHIVE-ONLY ROWS"; exit 1; else echo "truth refused the defaulted archive-only rows (exit $?)"; fi
"$LR" eval truth --archive "$WORK/truth.csv" --aligned "$WORK/blind/align/build/aligned.csv" --corpus "$WORK/corpus" --accept-defaults

command score

echo; echo "==== tune refuses the blind folder"
if "$LR" tune --corpus "$WORK/blind/results/build" --truth "$WORK/truth.csv" --out "$WORK/tune/batch-99" --mode stub; then echo "TUNE SHOULD HAVE REFUSED"; exit 1; else echo "refused (exit $?)"; fi

echo; echo "==== evidence (synthetic fixture only)"
for b in 01 02; do
  cp "$WORK/tune/batch-$b/batch-report.md" "$SP/batch-$b-report.md"
  mkdir -p "$SP/batch-$b-pr-bodies"; cp "$WORK/tune/batch-$b/pr-bodies/"*.md "$SP/batch-$b-pr-bodies/" 2>/dev/null || true
done
cp "$WORK/handoff/evaluator-instructions.md" "$SP/evaluator-instructions.md"
cp "$WORK/blind-prep/checklist.md" "$SP/blind-prep-checklist.md"
cp "$WORK/blind/report/build/deck-table.md" "$SP/blind-deck-table.md"
echo "== done"
