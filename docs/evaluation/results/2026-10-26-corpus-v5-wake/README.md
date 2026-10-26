# Corpus v5 run for prompt artefact `e0d9753d8f5b` and the briefs 30–31 wake (2026-10-26)

> Synthetic corpus v5 (generated documents, fictional carriers). No client documents.

**What this folder is.** The corpus v5 run the prompt gate asks for: learn-v11, artefact `e0d9753d8f5b`, from clean commit `3228641`. It also holds the chat evals from the same wake.

**It covers four earlier artefacts that never had a corpus run.** `run.json` lists them under `supersedesArtefacts` (brief 31 Answers 3), added after the run, from the artefact history:
- `ed9b8545c4d5` (brief 30);
- `8a98599f2e9f` and `873de4b2f88a` (brief 30's intermediate commits);
- `7cb405f74e86` (brief 29).

The last artefact with its own run is `2d37673f4a1e` (brief 23). From now on, `prompt-gate.py --write` keeps the chain and `eval run` records it.

**Restamped.** `deck-table.md`, `report.md` and `report.json` were re-scored offline from the same run after brief 31 Answers 2, so the stamp names the truth (`corpus-gen-0.9.0 seed 8`). Every number is unchanged.

## The wake (runbook "Briefs 30 and 31: next-wake items")

| Step | What | Result |
|---|---|---|
| Wake | `hibernate = false`, plan + apply | 18 added, 3 changed, 1 replaced; 03:57–04:10 UTC |
| 1 | `eval generate --v5` (corpus-gen-0.9.0, seed 8), `eval run --mode local`, `eval score --all-fields` | 332 documents, 04:14–04:24 UTC; all-fields **0 wrong of 179,409** |
| 2 | `chat init-index`, `chat reindex --from-api` (again after the Functions deploy, so the new indexer fills `reviewStatus`) | index updated; 23 uploads queued twice |
| 4 | `lossrun-db deploy`: 0022, 0023, 0016 re-run; `deploy --check` | 3 applied; 45 current |
| 5 | `Models__DeploymentModels` from Terraform | present (`Learn__LeaseMinutes` left at its default of 35) |
| — | `deploy-functions.ps1` (API, then the mock quote system), `deploy-web.ps1` | both gates passed; smoke uploads `ReplayAccepted` (3) |
| 3 | `eval chat`, `eval chat-multiturn` | 30/30; 34/40, then 37/40 on a re-run: variance, both committed (below) |
| 6 | Mock page key | page 200; grid without key 401, with key 200 |
| — | Drain, then `hibernate = true` | outbox 0 unpublished, queue 0 / DLQ 0, no Event Grid dead letters; 1 added, 3 changed, 18 destroyed; 04:52–04:56 UTC |

**Run mode.** The run is `--mode local`, like every earlier gate run: this commit's pipeline against the live Content Understanding and model, with an empty registry. The runbook line said `--mode api`. That would have uploaded 332 documents into the live demo record and learned with the deployed code against the live registry, so it was not used (operator's choice).

## Corpus v5

`deck-table.md`; the comparison with run-v5-23 (brief 23, the last live run) is in `deck-table-vs-run-v5-23.diff`.

- **Routing.** 253 of 332 delivered (245 replay, 8 learned), 79 to review. That is the same delivered count as run-v5-23.
- **Delivered key-field accuracy.** 100.0 % on every row.
- **All-fields study** (`studies/all-fields.md`): 0 wrong delivered values, caught or silent (scoring-protocol §8).
- **Scan 200 dpi rotated.** Delivered 35 → 34 and review rate 27.1 % → 29.2 %. This is exactly the brief 30 re-score's prediction: c2-008's `0,00` reserve now goes to review.
- **c10 (measures as rows).** All 6 delivered (was 5); review rate 16.7 % → 0 %.
- **Fax pre-fill: 89.1 % → 88.9 % is not a regression.** A blank for the reviewer beats a wrong number shown as a hint, and delivered values are untouched.
- **Fax pre-fill, the detail.** 89.1 % → 88.9 %, same n. The brief 30 re-score could not show this, because it re-ran only delivered documents and fax always goes to review.
  - On fax pre-fill, OCR-mangled amounts (e.g. a seven-digit `paid` with its decimal point lost) are now left blank with an issue, not shown.
  - The net is 0.2 points lower, so some of the blanked values were right.
  - Delivered values are unaffected.
- **Learn calls per document** moved within run-to-run variation on c6–c10 and the first-seen template (one learn more or fewer per carrier).
- **Metered cost:** $3.62 (run-v5-23: $3.67).

## Chat

| Eval | Folder | Result | Gate | Turn cost |
|---|---|---|---|---|
| `eval chat` (30 + 2 probes) | `chat/` | 30/30 exact, 30/30 citations, 0 leaks, injection ignored | pass | $0.137 |
| `eval chat-multiturn` (5 × 8), first | `chat-multiturn/` | 34/40, 0 leaks, 0 invalid, 0 unverified | ≥ 36/40 (90 %): fail | $0.119 |
| `eval chat-multiturn`, re-run | `chat-multiturn-2/` | 37/40, 0 leaks, 0 invalid, 0 unverified | pass | $0.125 |

**Variance, both committed.** 34 and 37 come from the same code, the same quote and the same deployment. One run is not a gate.
- From the next wake, `eval chat-multiturn --runs 3` runs it three times and gates on the median, with 0 leaks and 0 invalid citations in every run (brief 31 Answers 8).
- The model call cannot be pinned instead. gpt-5.4-mini is a reasoning model: it takes no `temperature` other than 1, and the Responses API has no `seed`.
- The gate is 90 % of the turns, 36/40. The runbook's "≥ 37/40" was one earlier run's score; it is corrected.

**The first multi-turn run's misses:**
- Four came from one model error. `oldest` turn 4 named WC-22-0001 instead of WC-24-0003 for the latest date of injury, and turns 5, 6 and 8 built on it.
- `largest` 2 answered the quote's status, not the claim's.
- `metrics` 6 is the known rubric miss: the answer begins "No." where the pattern expects "no,".

**The re-run's misses** are different (`open` 5–6, `metrics` 6). The eval has varied this much before (30/40, then 37/40 on brief 24's wakes).

## Cost of the wake

**Metered** (`config/azure-prices.json`):

| Item | $ |
|---|---:|
| Corpus v5 run (local; CU pages, learn and delta tokens, embeddings) | 3.619 |
| API side, 03:50 UTC on (`dbo.UsageEvent`): 3 chat evals, eval-chat uploads, 3 smoke uploads, chat reindex embeddings | 0.605 |
| **Metered total** | **4.22** |

The API side breaks down as:
- gpt-5.4-mini: 289,294 uncached + 507,392 cached input, 21,278 output = $0.386;
- text-embedding-3-large: 627,397 tokens = $0.090;
- CU: 16 async + 4 inline pages and 16,000 contextualization tokens = $0.126;
- semantic queries: $0.003.

**Standing, while awake** (04:10–04:56 UTC):
- AI Search Basic ($0.101/h) and the Service Bus base unit ($0.013/h): about $0.12.
- demo_mode was off, so no always-ready instances.
- Event Grid and Load Testing: no idle charge.

**Total about $4.35.** The Cost Management bill for 2026-10-26 lags by a day; `lossrun eval reconcile` against it is the check.
