# Brief 34 Part B wake (2026-10-26, 14:28–16:04 UTC)

> Synthetic corpus v5 (generated documents, fictional carriers), the repository's own test fixtures and the chat evals. No client documents.

## Steps

| Step | What | Result |
|---|---|---|
| 0 | `eval generate --v5` (corpus-gen-0.9.0, seed 8), `eval run --mode local`, `eval score --all-fields`, from `de21d84` (with the exemplar store, brief 34 A2) | 332 documents, 14:34–14:47 UTC. **251 delivered**, 81 to review. **All-fields: 6 wrong of 179,409, all silent** (below) |
| 1 | `hibernate = false`, plan + apply | 18 added, 3 changed, 1 replaced (`terraform_data.blank_webjobs_storage`, a helper, not an Azure resource); 14:36–14:58 UTC. The new settings are live: `Cu__DefaultsHash`, `Quotes__ApplicantMayCreate`, `Models__DeploymentModels` |
| 2 | `lossrun-db deploy --check` → `deploy` → `--check` | 0016 pending (the comment-only rerun), nothing else; 1 applied; 45 current |
| 3 | `chat init-index`; `deploy-functions.ps1` (API, then the mock); `deploy-web.ps1`; `chat reindex --from-api` | all three gates passed, smoke uploads `ReplayAccepted` (3 claims each); 28 uploads queued for indexing |
| 4 | A decision against a paused database (below) | applied once; **no 40613 reached the Durable layer** |
| 5 | `eval chat`, `eval chat-multiturn --runs 3` | 30/30 exact, 30/30 citations, 0 leaks; multi-turn **38, 33, 38 → median 38/40**, 0 leaks, 0 invalid citations: both gates pass |
| 6 | Cost reconciliation | 2026-10-26 is not fully billed yet (about 2026-10-27); pending. The 2026-10-23 reconciliation was re-run against live SQL (below) |
| 7 | gpt-5.2 bill meters | **The gpt-5.2 quota is now granted** (Data Zone Standard 1,100 K TPM, Global Standard 2,500 K, eastus2). Nothing is deployed on it, so there is no bill meter to check yet |
| 8 | Drain, then `hibernate = true` | outbox 0 unpublished, queue 0 / DLQ 0, no Event Grid dead letters; 1 added, 3 changed, 18 destroyed; 16:02–16:04 UTC. The woken resources are gone; the budget (`<budget>`, $10) is intact |

## Step 0: corpus v5 with the exemplar store

`corpus-v5/`: deck table, report, documents, all-fields study, and the diff with the attached run (`deck-table-vs-2026-10-26-corpus-v5-wake.diff`).

- **The exemplar store was filled but never judged a repair.**
  - The store was filled: 9 formats, 53 files.
  - Neither this run nor the attached one made a single delta repair (`Delta` tier: 0 in both). Corpus v5 in local mode has no drift that reaches a repair.
  - So the run is now representative of the production repair gate, but the gate had nothing to decide.
- **Routing.** 251 delivered (243 replay, 8 learned), 81 to review; the attached run had 253 (245, 8), 79.
  - The two extra reviews are c7 (one table per claim), the construct's first two documents: the learn failed its gate twice (5 model calls each), and the third document learned the format.
  - The attached run learned c7 on its first document. This is learn variance on a first-seen layout, not the exemplar store, which only takes part in a delta repair.
- **All-fields: 6 wrong delivered values, all silent.**
  - All six are `document.carrierName` on the c8 (stacked cells) documents. The insured's name was delivered as the carrier name.
  - One learn, on c8-000, mapped both `document.carrierName` and `document.insuredName` to the header label "Insured". Every later c8 document replayed that spec.
  - The spec's own `carrierName` is right ("Illustrative Comp Assurance"). Nothing compares it with the delivered header value, and the spec lint does not refuse two document fields on one label.
  - The attached run learned c8 correctly: this is learn variance too, which the lint should catch. A lint change changes the prompt artefact (`tools/prompt-gate.py`), so it needs its own corpus run.
- **Not attached to the prompt gate.** The gate still holds `2026-10-26-corpus-v5-wake`, and the closeout figures still quote it. Re-attaching this run would make the pack's "0 wrong" untrue until the lint fix lands (brief 34 report, Questions).
- **Metered cost:** $3.72 (attached run: $3.62). 193 model calls (186).

## Step 4: a decision against a paused database

- **Setup.** The database cannot pause while the API is awake: `OutboxSweep` reads `dbo.Outbox` every 30 s.
  - The API was stopped at 15:32:56, after a review-bound upload. The upload was the smoke CSV with one policy cell blanked, the brief 34 A3 fill-down case.
  - SQL paused at 15:53:13. The API was started at 15:53:36, and a decision was posted at once: an override plus `formatChanges {"periods.fillDown": true}`.
- **What happened.** The first POST got the host's "site unavailable" page; the second got 202 at 15:54:46.
  - The request (`DecideReview`) took 56 s and `OutboxSweep` took 47 s: both rode EF's own retries (`EnableRetryOnFailure(8, 15 s, SqlConnections.ResumeErrors)`) through the resume.
  - `ApplyDecision` then ran **once**, 6.2 s, successfully: the database was online by then.
  - App Insights has no exception and no trace with 40613 from 15:53 on.
- **Applied once.**
  - The document has one override set (`medical.paid` 8,000.00 → 1,234 and the re-derived incurred) and one `format.change` (`periods.fillDown` false → true).
  - There is one outbox set (`review.decided`, `quote.revised`, `quote.payload`, all published by 15:54:52) and one revision bump.
  - `synthetic-mutual-csv` v2 is in the live registry as **shadow**: brief 34 A3, live.
- **The placeholder 40613 in `fixtures/captured-failures.json` stays.** A resume does not reach the Durable retry handler: EF absorbs it first. A 40613 reaches the handler only if EF's eight retries run out, which is the `RetryLimitExceededException` case (permanent, captured offline in brief 34 A1).

## Answers 2: the 2026-10-23 reconciliation against live SQL

`lossrun eval reconcile` over 2026-10-21..22, with the committed bill query, `eval-runs/` and live `dbo.UsageEvent`, reproduces the period totals exactly. AI services: $25.48 metered vs $35.92 billed (0.71). Platform: $6.46. The file differs in three places, none of them a total:

1. **Row order:** one row ("Document Intelligence pages") moved.
2. **Cached input.** The metered gpt-5.4-mini input is now split $3.58 uncached + $0.66 cached; the committed file has $4.24 + $0.00, the same total. The price table started pricing cached input after that file was written.
3. **Hand-added tables.** The committed file's hourly table and its study-window table were added by hand. `reconcile` does not write them.

## Wake cost, line by line

Metered lines are exact. Platform lines are estimates from list prices and hours awake; the bill for 2026-10-26 comes in about a day, and the reconciliation then replaces them.

| Line | How | $ |
|---|---|---:|
| Corpus v5 run (CU, learn/delta model, embeddings) | metered, `corpus-v5/report.md` | 3.72 |
| `eval chat` (30 questions + 2 probes) | metered | 0.12 |
| `eval chat-multiturn --runs 3` (3 × 40 turns) | metered (0.119 + 0.125 + 0.122) | 0.37 |
| Smoke uploads (3) and the two step 4 uploads | replay of hand formats: 0 model calls, CSV needs no CU | 0.00 |
| AI Search Basic, about 1.1 h awake | estimate, list price | ~0.11 |
| Service Bus, Event Grid, Load Testing, about 1.1 h | estimate | ~0.03 |
| SQL serverless, online from about 14:59 (the database deploy) until it pauses after the hibernate, less the 20-minute pause | estimate (min capacity) | ~0.5 |
| Durable Task Scheduler actions, Functions, Blob, Key Vault | estimate | ~0.1 |
| Log Analytics ingestion (cap lifted while awake) | estimate | ~0.2 |
| **Total** | | **~5.2** |

The brief's estimate after its Answers was ~$5.
