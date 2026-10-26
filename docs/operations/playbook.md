# The assistant playbook: from a folder of historical documents to the blind score

This page explains five commands that take an operator, with an AI assistant doing the routine work, from a folder of historical loss runs to the blind score:
- `/import`
- `/seal-handoff`
- `/tune`
- `/blind-prep`
- `/score`

Each command is a Markdown file in `.cursor/commands/` (the source). Cursor runs it as a slash command. The same files are in `docs/operations/playbook/`, written by `python tools/playbook-sync.py --write` and checked in CI (`--check`), for Copilot, Codex and other assistants (through `AGENTS.md`) and for readers: edit the command file, never the copy. Each file carries its own rules inline, so an assistant that reads only that file still keeps them.

The procedure behind the commands:
- `docs/operations/blind-run.md` for the evaluation itself;
- `docs/operations/tuning-loop.md` for changing the system on the tuning set;
- `docs/evaluation/scoring-protocol.md` for how it is scored.

## The flow, and who does what

| Step | Command | The assistant | A person |
|---|---|---|---|
| 1. Import | `/import` | runs `eval import`, prints the carrier × input-type table and the strata too small to split, and says what the data team must export (`truth-request.csv` → `truth.csv`) | the data team exports `truth.csv` |
| 2. Seal | `/seal-handoff` | writes the evaluator's instructions with the exact `eval seal` command and a `<SEED>` placeholder; **never runs it**; checks `blind/` is in `.gitignore` and `.cursorignore` | the **evaluator** publishes a seed, seals, publishes the blind hash, releases the tuning set |
| 3. Tune | `/tune` | runs `lossrun tune` batch by batch; makes tier-1 fixes with tests and the gates; opens one pull request per tier-2 fix from `pr-bodies/` and **stops for the human merge**; repeats until the stopping criteria say stop | the developer reviews and merges design changes |
| 4. Blind prep | `/blind-prep` | verifies the seal, snapshots the learned-alias set, resets learned formats on the deployed API (recorded), writes the pre-run checklist | an Admin holds the reset token; the operator ticks the checklist |
| 5. Blind run | — | nothing: it never opens `$WORK/blind/` | the **operator** runs the blind set into `$WORK/blind/results/build`; the **evaluator** aligns and adjudicates in `$WORK/blind/align/build` and builds truth (`eval truth` refuses archive-only rows left at their default, exit 3, unless `--accept-defaults`) |
| 6. Score | `/score` | `eval verify` (with `--published-hash` when `PUBLISHED_HASH` is set), then `eval score` into `$WORK/blind/report/build/`; prints only `report.md`'s head and `deck-table.md`; reports the deck table by input type | the evaluator publishes |

## `lossrun tune`: one tuning batch

```bash
"$LR" tune --corpus "$WORK/corpus" --truth "$WORK/truth.csv" --out "$WORK/tune/batch-01" --mode api \
  --seal "$WORK/seal/seal-manifest.json" [--budget 10.50] [--tuning-items "$WORK/tuning-items"] [--cu-cache <dir>]
```

**What a batch does:**
1. Runs the seal's **tuning** files only (`eval run --only-tuning`).
2. Aligns them to `truth.csv` and builds truth with the default decisions (`eval align`, then `eval truth --accept-defaults`: a tuning batch has no evaluator, so it accepts the defaults explicitly and prints their count; the blind set's `eval truth` refuses archive-only rows left at their default, exit 3, unless the evaluator passes the flag).
3. Scores them (`eval score` into `score/`).
4. Classifies every failure into one cause, deterministically, by the first rule that applies:

| Cause | Tier | Rule (first match wins) |
|---|---|---|
| (g) tool bug | 1 | the run threw (and the fallback: a review with no other known cause) |
| (f) source inconsistency | 1 | the document's printed totals disagree with each other (no code change; kept out of the fix list) |
| (d) scan quality | 2 | a scan or fax that went to review or misread values, or reasons about dpi or OCR |
| (c) classifier miss | 2 | the archive has claims but no loss-run document was extracted |
| (a) glossary gap | 1 | unmapped headers on the route; or, reading the format's column map, a wrong value from a plain column mapping (an alias or mapping mistake), or a field with no column while headers were left unmapped |
| (b) layout not expressible | 2 | a learn that did not publish a verified spec; or, from the column map, a value read through a construct (line, under, part, components, records, a derived sum) or not reached by any column |
| (e) rule too strict or loose | 2 | values match the archive but hard rules fail |

**Mismatched values are classified from the spec** (brief 27 Answers 2): `lossrun tune` reads the column map of the format that read the document (the batch's learned specs, then the hand specs), field by field, and the majority cause wins; the why names the spec. Without a spec it falls back to a first cut (wrong value → a, missing → b). A human may re-categorise in the pull request; that is the override.

**Tuning items from the escalation queue** (brief 26, ADR 0025) come in with the category a reviewer gave them (`--tuning-items`, the files `GET /tuning-items` returns).

**What a batch writes into `--out`:**
- `batch-report.md`:
  - the verdict;
  - routes;
  - key-field accuracy by stratum with 95 % intervals clustered by document — input type, with native PDFs split into recurring and first-seen templates; the fax stratum is scored on the reviewer's pre-fill;
  - review rate;
  - model calls per document;
  - metered cost against the cap;
  - grammar changes since the last batch;
  - failures by cause;
  - the ranked fix list (cause, tier, format or carrier or rule, documents, evidence, action);
  - one line per failing document.
- `batch.json`, which the next batch compares against.
- `pr-bodies/tier2-NN-<cause>.md`, one pull-request body per design fix, with the tuning-loop checklist.
- `results/`, `align/` and `score/`, the underlying runs.

**Grammar changes** (brief 27 Answers 3) are files changed since the previous batch's commit under the grammar paths in `config/tuning-config.json`:
- the learn prompt and the model-facing schema (`PromptBuilder.cs`, `ModelSchema.cs`);
- the lint files (`SpecLint*.cs`);
- the spec schema and the canonical schema;
- the rule catalog.

`FormatLearner.cs` (where bug fixes also land) counts only in commits labelled `tier-2`: the tier-2 pull-request bodies carry the label, and the merge commit keeps `[tier-2]`; a human labels any other construct change. Grammar changes count as tier-2 changes for the batch (`tuning-loop.md` §6).

**Evidence** is rule ids and reasons with numbers and quoted text removed (`EscalationTraceText.Sanitize`), never values (`tuning-loop.md` §7).

**Stopping criteria** (`config/tuning-config.json`, data), checked in this order:
1. **`stop: budget`.** The batch's metered cost is over `batchBudgetUsd` (or `--budget`). It is derived in `config/tuning-config.json` from `docs/poc-plan/tco.md` §3: `replayUsdPerDocument` 0.075 × `batchDocuments` 100 + `learnUsdPerFormat` 0.30 × ceil(`expectedNewFormatsShare` 0.10 × 100) = $10.50; the batch summary prints the formula. The assistant never raises a cap.
2. **`stop: not converging`.** From the second batch on, grammar changes in batch *n* are at or above batch *n−1*, and above zero. Fall back to the delta path for those carrier classes (`tuning-loop.md` §6).
3. **`met`.** All three hold:
   - every stratum with at least `minStratumDocuments` documents has the lower bound of its key-field accuracy at or above its target, and its review rate within its own cap where it has one. The targets are the acceptance table's (Answers 1): recurring native templates ≥ 0.99 with review ≤ 5 %, first-seen native ≥ 0.97, spreadsheets ≥ 0.99, 300 dpi scans ≥ 0.95, 200 dpi rotated ≥ 0.90, fax pre-fill ≥ 0.85;
   - the overall review rate is at most `maxReviewRate` (20 %);
   - the last batch made no grammar change (so `met` is possible from the second batch).
4. **`stop: max batches`** at `maxBatches`; otherwise **`continue`**.

**`tune` never reads the blind set.** It runs only the seal's tuning list and refuses any path with a `blind` folder in it.

## Folders

```
$WORK/corpus/                 import: manifest.json, truth-request.csv, truth/ (built by eval truth)
$WORK/truth.csv               from the data team
$WORK/handoff/                /seal-handoff: evaluator-instructions.md
$WORK/seal/                   the evaluator's seal (seal-manifest.json)
$WORK/tune/batch-NN/          /tune: batch-report.md, batch.json, pr-bodies/, results/, align/, score/
$WORK/tuning-items/           optional: tuning items exported from the escalation queue
$WORK/blind-prep/             /blind-prep: glossary.learned.json, checklist.md
$WORK/blind/results/<entrant>/ the operator's blind run (eval run --only-blind; the build is "build") — never opened by an assistant
$WORK/blind/align/<entrant>/   the evaluator's alignment; adjudication.xlsx in align/build/
$WORK/blind/report/<entrant>/  /score: report.md, deck-table.md, documents.csv (the assistant prints only report.md's head and deck-table.md)
```

This is the same layout as `docs/operations/blind-run.md` (*Folder layout*); the blind run's dry run on the tuning set goes to `$WORK/tune/dry-run/`.

Keep `$WORK` outside the repository: archive files and everything built from them are never committed.

**What guards `$WORK/blind/`.** `.gitignore` and `.cursorignore` list `blind/`, but they cover only the repository workspace, and `$WORK` is outside it, so they do not hide it from an assistant. The real guard is the CLI: `eval run --only-blind` exits 2 when its `--out` has no `blind` path segment, and `lossrun tune` refuses any path with one. Keep every blind output under `$WORK/blind/` so both refusals apply.

## Proof (offline)

`docs/evaluation/results/2026-10-25-playbook-proof/` runs the whole flow on the 10-file import fixture with `MODE=stub`, executing each command file's `bash run` blocks verbatim:

```bash
bash docs/evaluation/results/2026-10-25-playbook-proof/prove.sh
```

The order: `/import` → `/seal-handoff` → evaluator seals → `/tune` batch 01 → `/tune` batch 02 (zero tier-2 changes) → `/blind-prep` → operator and evaluator → `/score` → `tune` refuses the blind folder.

**Stub mode** (`eval run --mode stub`, `lossrun tune --mode stub`) calls no Azure service:
- Content Understanding comes only from a `--cu-cache` (a miss is "not run");
- there are no embeddings;
- the learn model's answers never verify.

So the proof exercises the commands, the classification and the reports, not extraction quality.
