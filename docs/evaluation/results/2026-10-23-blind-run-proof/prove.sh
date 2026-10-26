# Brief 14 proof: docs/operations/blind-run.md, commands as written, on the 10-file fixture (tuning 3, limit 3).
set -euo pipefail
# Run from the repository root: bash docs/evaluation/results/2026-10-23-blind-run-proof/prove.sh
# Needs eval-runs/corpus-v3 (lossrun eval generate --out eval-runs/corpus-v3 --seed 8) for the fixture's truth, and az login (local run).
# Offline (brief 30): MODE=stub CU_CACHE=<corpus-v3-cu-cache> [CORPUS_V3=<corpus-v3>] [WORK=<dir>] — Content Understanding only
# from the cache, no Azure; CORPUS_V3 and CU_CACHE may point at another checkout's eval-runs (never copied into the repo).
SP="$PWD/docs/evaluation/results/2026-10-23-blind-run-proof"
MODE="${MODE:-local}"
CU=(); if [ -n "${CU_CACHE:-}" ]; then CU=(--cu-cache "$CU_CACHE"); fi
export CORPUS_V3="${CORPUS_V3:-eval-runs/corpus-v3}"

echo "== 0. build"
MSBUILDDISABLENODEREUSE=1 dotnet build src/LossRun.Tool -c Release -o tools/lossrun -nodeReuse:false -v q | grep -E " error |Elapsed" || true
export LR="$PWD/tools/lossrun/lossrun"
export ARCHIVE="$PWD/src/TestShared/fixtures/import"
export WORK="${WORK:-eval-runs/blind-run-proof}"  # relative: Windows Python cannot open /c/... paths
rm -rf "$WORK"; mkdir -p "$WORK"
cp src/TestShared/fixtures/import-carriers.csv "$WORK/carriers.csv"
python "$SP/make_truth.py" "$WORK/truth.csv"

echo "== 1. import"
"$LR" eval import --in "$ARCHIVE" --out "$WORK/corpus" --carriers "$WORK/carriers.csv"
echo "== 2. truth request (to the data team)"
cat "$WORK/corpus/truth-request.csv"
echo "== 3. seal (the seed lives in the sealed envelope, not the shared shell)"
"$LR" eval seal --corpus "$WORK/corpus" --out "$WORK/seal" --tuning 3 --seed 14
PUBLISHED=$(python -c "import json,sys; print(json.load(open(sys.argv[1]))['blindManifestSha256'])" "$WORK/seal/seal-manifest.json")
echo "== 6.1 verify (against the published blind manifest hash)"
"$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json" --published-hash "$PUBLISHED"
echo "== 6.2 run ($MODE, limit 3, blind only) into \$WORK/blind/"
if "$LR" eval run --corpus "$WORK/corpus" --out "$WORK/results/build" --mode "$MODE" --limit 3 --only-blind "$WORK/seal/seal-manifest.json" "${CU[@]}"; then
  echo "RUN SHOULD HAVE REFUSED AN --out WITHOUT A blind SEGMENT"; exit 1; else echo "refused --out without a blind segment (exit $?)"; fi
"$LR" eval run --corpus "$WORK/corpus" --out "$WORK/blind/results/build" --mode "$MODE" --limit 3 --only-blind "$WORK/seal/seal-manifest.json" "${CU[@]}"
echo "== 7. align"
"$LR" eval align --archive "$WORK/truth.csv" --results "$WORK/blind/results/build" --out "$WORK/blind/align/build" --corpus "$WORK/corpus"
echo "== 8. adjudicate adjudication.xlsx (scripted from the generator's ground truth) + truth"
python "$SP/adjudicate.py" "$WORK"
if "$LR" eval truth --archive "$WORK/truth.csv" --aligned "$WORK/blind/align/build/aligned.csv" --adjudication "$WORK/blind/align/build/adjudication.xlsx" --corpus "$WORK/corpus"; then
  echo "TRUTH SHOULD HAVE REFUSED DEFAULTED ARCHIVE-ONLY ROWS"; exit 1; else echo "truth refused the defaulted archive-only rows (exit $?)"; fi
# --limit 3 leaves the other documents' claims unextracted: the evaluator accepts dropping them, explicitly.
"$LR" eval truth --archive "$WORK/truth.csv" --aligned "$WORK/blind/align/build/aligned.csv" --adjudication "$WORK/blind/align/build/adjudication.xlsx" --corpus "$WORK/corpus" --accept-defaults
echo "== 9. score"
"$LR" eval score --corpus "$WORK/corpus" --results "$WORK/blind/results/build" --out "$WORK/blind/report/build"
echo "== seal-break checks"
cp "$WORK/corpus/manifest.json" "$WORK/manifest.bak"
python -c "import json;m=json.load(open('$WORK/corpus/manifest.json'));m['docsRoot']='swapped';json.dump(m,open('$WORK/corpus/manifest.json','w'))"
if "$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json"; then echo "VERIFY SHOULD HAVE FAILED"; exit 1; else echo "verify refused a moved blind set (exit $?)"; fi
cp "$WORK/manifest.bak" "$WORK/corpus/manifest.json"
# An edited blind list (one entry dropped, the recorded hash rewritten to match): only the published hash catches it.
python -c "import json,hashlib,sys; p=sys.argv[1]; s=json.load(open(p)); s['blind']=s['blind'][1:]; s['blindManifestSha256']=hashlib.sha256('\n'.join(b['file']+' '+b['sha256'] for b in s['blind']).encode()).hexdigest(); json.dump(s,open(p,'w'))" "$WORK/seal/seal-manifest.json"
"$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json"
if "$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json" --published-hash "$PUBLISHED"; then echo "VERIFY SHOULD HAVE FAILED"; exit 1; else echo "verify refused an edited blind list against the published hash (exit $?)"; fi
echo "== done"
