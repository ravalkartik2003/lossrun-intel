# Playbook proof: from a folder of historical documents to the blind score, offline (brief 27)

`prove.sh` drives the playbook (`docs/operations/playbook.md`) on the 10-file import fixture `src/TestShared/fixtures/import/`.
- **What it runs.** Each `/command` executes the fenced `bash run` blocks of `.cursor/commands/<command>.md`, verbatim, in a strict shell.
- **The people's steps.** The evaluator's seal, the data team's export and the operator's blind run are separate, labelled steps in the script; the assistant's command files do not run them.
- **Stub mode** (`MODE=stub`):
  - no Azure call;
  - Content Understanding comes only from the corpus-v3 cache (the fixture's PDFs are corpus-v3 renderings), and a miss is "not run";
  - no embeddings;
  - a learn model whose answers never verify.

```bash
bash docs/evaluation/results/2026-10-25-playbook-proof/prove.sh   # log: prove.log
```

| Step | Result (`prove.log`) |
|---|---|
| `/import` | 10 files: the carrier × input-type table, then `truth-request.csv` with 12 policy rows and 2 carrier rows, and what the data team must export |
| data team | `truth.csv` (`make_truth.py` from the generator's truth, as in the blind-run work proof) |
| `/seal-handoff` | `evaluator-instructions.md` with the exact `eval seal` command and `<SEED>`; checks `blind/` is in `.gitignore` and `.cursorignore`. **Does not seal** |
| evaluator | `eval seal --tuning 10 --seed 27` on the fixture: 8 tuning, 2 blind |
| `/tune` batch 01 | The archive is first cut to the tuning documents' policies (brief 30 item 21: 331 of 355 rows kept; the blind-only policies' rows are never read). 8 tuning documents: 1 ReplayAccepted, 6 Review, 1 not run (protected PDF, no cached CU). 6 failures: 4 (b) layout not expressible (the stub's learns never verify), 2 (d) scan quality. 6 tier-2 pull-request bodies (`batch-01-pr-bodies/`), 0 tier-1 fixes. Grammar changes: baseline. Verdict **continue** (review rate 86 % > 30 %; every stratum under 5 documents; zero design changes can only be shown from batch 2) |
| between batches | Nothing to apply at tier 1. The tier-2 bodies are "opened, not merged" (no human merge in the proof) |
| `/tune` batch 02 | Same routes and failures (deterministic). **Tier-2 (grammar) changes since batch 01: 0** (asserted). Verdict **continue** (review rate only) |
| `/blind-prep` | Seal verified (2 blind files match); learned-alias snapshot from batch 02 (0 aliases); format reset **skipped** (`MODE=stub`: no shared registry); `checklist.md` with what is still open |
| operator and evaluator | Blind run into `$WORK/blind/results/build` (stub), align, truth. `eval truth` first **refuses** the 343 archive-only rows left at their default (exit 3, brief 30 item 21), then the evaluator accepts them explicitly with `--accept-defaults` |
| `/score` | Seal verified again; `eval score` into `$WORK/blind/report/build/` (`blind-deck-table.md`: 2 documents, every row an insufficient sample) |
| blind guard | `lossrun tune` on a path under `blind/` is refused (exit 2) |

**What this proves, and what it does not.**
- **It proves** that the commands run in order from their own files, and that `lossrun tune` runs a batch end to end: run, align, truth, score, classify, rank, PR bodies, verdict. It also proves the second batch compares against the first (grammar changes, previous-batch columns), and that the blind folder is refused.
- **It does not** measure extraction: in stub mode every learn ends in review by construction. A real batch uses `--mode api` (or `local`) with the model.

Files: `batch-01-report.md`, `batch-02-report.md`, `batch-0N-pr-bodies/`, `evaluator-instructions.md`, `blind-prep-checklist.md`, `blind-deck-table.md`, `prove.log`. Everything here is synthetic (the fixture's fictional carriers).
