# /tune — tuning batches until the stopping criteria say stop

One batch = `lossrun tune` on the tuning set → `batch-report.md` → tier-1 fixes made here, tier-2 fixes proposed as pull requests → the next batch. Procedure: `docs/operations/tuning-loop.md`; criteria: `config/tuning-config.json`; overview: `docs/operations/playbook.md`.

## Rules (always)
- **Tuning set only.** `--seal` makes `lossrun tune` run only `tuning[]`; it refuses any path under `blind/`. Never open `$WORK/blind/` or a blind file.
- **Budget.** Each batch has the cap in `config/tuning-config.json` (`batchBudgetUsd`, derived there from tco.md §3 as 0.075 × 100 replays + 0.30 × 10 expected new formats = $10.50; the batch summary prints the formula) or `BUDGET`. Never raise a cap; the developer may, once per batch, and records it in the pull request (`tuning-loop.md` §5). A batch over the cap ends the loop.
- **Tier 1** (glossary and value aliases, accepting or rejecting learned aliases, the ignored-headers list, a deterministic lint repair, a bug fix with a test): make it here, with tests, then run every gate below.
- **Tier 2** (schema, rule semantics, spec-language constructs, classifier or gate thresholds, the learn gate, prices): **do not merge.** Open one pull request per fix from `pr-bodies/tier2-*.md`, attach the batch report, and stop for a human (`tuning-loop.md` §2, §4). When in doubt, it is tier 2.
- **Gates on every change:** the .NET and web tests; `bash tools/generic-guard.sh`; `python tools/prompt-gate.py --check` — and if the learn-prompt artefact changed, a corpus v5 run (`lossrun eval generate --v5`, `eval run`, `eval score`) attached to the pull request; the deck table must reproduce (`tuning-loop.md` §3).
- **Reports and pull requests name categories only** (files, carrier folder labels, rule ids, field names, counts); no claimant names, claim numbers, amounts or quoted lines (§7).

## Do (one batch; BATCH = 01, 02, …)
```bash run
"$LR" tune --corpus "$WORK/corpus" --truth "$WORK/truth.csv" --out "$WORK/tune/batch-$BATCH" --mode "${MODE:-api}" \
  --seal "$WORK/seal/seal-manifest.json" $( [ -n "$BUDGET" ] && echo --budget "$BUDGET" ) $( [ -n "$CU_CACHE" ] && echo --cu-cache "$CU_CACHE" ) \
  $( [ -d "$WORK/tuning-items" ] && echo --tuning-items "$WORK/tuning-items" )
sed -n '1,/^## Routes/p' "$WORK/tune/batch-$BATCH/batch-report.md"
ls "$WORK/tune/batch-$BATCH/pr-bodies"
```
(`$WORK/tuning-items/` holds tuning items exported from the Needs investigation queue: `GET /tuning-items`, ADR 0025.)

## Then
1. Read `batch-report.md`: the verdict, routes, accuracy by stratum, review rate, cost, grammar changes, the ranked fix list.
2. Make the tier-1 fixes on a branch, one pull request each, gates green. Open the tier-2 pull requests from `pr-bodies/` and **stop for the human merge**.
3. Run the next batch (`BATCH` + 1) once the merges are done. Repeat until the verdict is `met`, `stop: not converging` (report it with both batches' grammar counts and fall back to the delta path for that carrier class, `tuning-loop.md` §6), `stop: budget` or `stop: max batches`.
4. Then `/blind-prep`.
