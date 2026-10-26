# /blind-prep — get ready for the blind run (the day before)

Records what the blind run will use and checks it can run: the seal still holds, the learned-alias set is snapshotted, learned formats are reset (recorded), and the registry and deployment pass their checks (`docs/operations/blind-run.md` §5–6, `docs/operations/playbook.md`).

## Rules (always)
- **Never open `$WORK/blind/`** or a blind file. `eval verify` reads hashes only. `.cursorignore` does not cover a `$WORK` outside the workspace; the CLI does (`eval run --only-blind` exits 2 when its `--out` has no `blind` path segment).
- **The format reset is an Admin action on the deployed API** (`MODE=api`). It is audited; record its audit id and ledger transaction in the bake-off log. Dry run first. In a local or stub rehearsal there is no shared registry: the step is skipped and says so.
- **No credentials in commands or chat:** `LOSSRUN_API_TOKEN` is set in the shell by the operator.
- **If `verify` names files, stop.** The evaluator restores them or re-seals; never edit the seal.

## Do
```bash run
"$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json"
mkdir -p "$WORK/blind-prep"
last=$(ls -d "$WORK"/tune/batch-* 2>/dev/null | sort | tail -1)
if [ -n "$last" ]; then "$LR" glossary export --run "$last/results" --out "$WORK/blind-prep/glossary.learned.json"; fi
if [ "${MODE:-api}" = "api" ]; then
  "$LR" formats reset --learned --reason "pre-blind-run reset $(date +%F)" --dry-run
  "$LR" formats reset --learned --reason "pre-blind-run reset $(date +%F)"
else
  echo "formats reset: skipped (MODE=${MODE}: no shared registry in a local or stub rehearsal)"
fi
cat > "$WORK/blind-prep/checklist.md" <<EOF
# Blind-run checklist ($(date +%F))
- [x] seal verified: every blind file matches its hash
- [$( [ -n "$last" ] && echo x || echo ' ' )] learned-alias snapshot: blind-prep/glossary.learned.json (from ${last:-no tuning batch})
- [$( [ "${MODE:-api}" = "api" ] && echo x || echo ' ' )] learned formats reset (record the audit id / ledger tx in the bake-off log)
- [ ] deploy check: infra/scripts/deploy-functions.ps1 ran its database check and smoke upload on the release being used
- [ ] tuning verdict recorded (last batch-report.md) and every tier-2 pull request merged or closed
- [ ] the operator, not the assistant, runs: "\$LR" eval run --corpus "\$WORK/corpus" --out "\$WORK/blind/results/build" --mode api --concurrency 8 --only-blind "\$WORK/seal/seal-manifest.json"
EOF
cat "$WORK/blind-prep/checklist.md"
```

## Then tell the operator, and stop
The checklist, with what is still open. The blind run itself (`blind-run.md` §6) and the alignment and adjudication (§7–8) are the operator's and the evaluator's, into `$WORK/blind/`. Afterwards: `/score`.
