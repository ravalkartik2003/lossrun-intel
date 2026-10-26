# Brief 35 Part B wake (2026-10-26, 17:14–17:53 UTC)

> Synthetic corpus v5 (generated documents, fictional carriers) and the repository's own test fixtures. No client documents.

**The attached run.** `corpus-v5-gpt-5.4-mini/` is the corpus v5 run the prompt gate holds from this wake: artefact `f913ce4e28a5` (learn-v12; it supersedes `80c6a46a4c42` and `720baedafd45`), git `8063514`, `--mode local`. The governance pack quotes it. It replaces `2026-10-26-corpus-v5-wake/` (artefact `e0d9753d8f5b`) as the attached run.

## Steps, in the brief 35 Answers' order

| Step | What | Result |
|---|---|---|
| — | Un-hibernate (`hibernate = false`, plan + apply) | 18 added, 3 changed, 1 helper replaced; 17:15–17:27 UTC |
| — | `lossrun-db deploy --check` | up to date, 45 changesets (no changeset in brief 35) |
| 1 | Deploy the API (replay carrier check, carrier names, R-34) | **first smoke failed**, as the brief 34 runbook note warned: it replayed on the shadow `synthetic-mutual-csv` v2 left by brief 34 step 4 and waited in a confirm-format review. After the retire (step 3) the redeploy passed: smoke `ReplayAccepted`, 3 claims |
| 1 | Deploy the mock quote system | **refused: 403 on the zip deploy, twice**; not redeployed. Its code is functionally unchanged since brief 34; it picks up `Contracts` only through the new carrier normalization, which it does not use. The brief 34 build is running (Questions) |
| 1 | Deploy the web app (the same-carrier toggle) | passed; smoke `ReplayAccepted` |
| 2 | Corpus v5 on gpt-5.4-mini, `--mode local`, `--all-fields`; attached | 17:14–17:23 UTC. **0 wrong of 179,409 delivered values.** 252 delivered (244 replay, 8 learned), 80 to review. **c8 learned once, with carrier and insured kept apart**: carrier from its own line, insured from "Insured"; all 6 delivered. $3.59 metered |
| 3 | Retire `synthetic-mutual-csv` v2 | retired (audited `format.retire`). The stuck smoke document was rejected with a note (it replayed on the test version) |
| 4 | Attempt gpt-5.2 (`deploy_gpt52 = true`, capacity 300) | **refused**: `400 Bad Request`, `Our system has detected this request as unusual activity for your account. If you are confident this is in error, please contact support.` `deploy_gpt52` back to false; a second apply restored the CU defaults and app settings (only the known ledger-digest drift remains). The learn-model study was not run (ADR 0004 amendment 1, outcome) |
| — | Drain, then `hibernate = true` | outbox 0 unpublished, queue 0 / DLQ 0, no Event Grid dead letters; 1 added, 3 changed, 18 destroyed; 17:51–17:53 UTC. The woken resources are gone; the budget is intact |

## The attached run against the one it replaces

`corpus-v5-gpt-5.4-mini/deck-table-vs-2026-10-26-corpus-v5-wake.diff`.

- **Delivered:** 252 vs 253.
- **The one difference:** c4-025 (XLSX, summary workbook) went to review with 2 hard failures, R-10 and R-11: the printed totals do not tie under this run's c4 learn (4 model calls). The workbook was caught, not delivered wrong.
  - The offline re-score of the replaced run, on this same code, moved nothing in c4 (`2026-10-26-brief35-rescore/`). So this is learn variance on c4, not the brief 35 checks.
  - XLSX/CSV review rate: 1.3 % → 2.5 %, both met.
- **c8:** 6 delivered, 0 wrong. The brief 34 wake's run had 6 silent wrong values.
- **Insight accuracy:** 42/42 catalog metrics within tolerance on 252 delivered documents (`corpus-v5-gpt-5.4-mini/insights/`).

## Wake cost, line by line

Metered lines are exact. Platform lines are estimates from list prices and the time awake; the reconciliation of the 26th replaces them once it is billed (runbook).

| Line | How | $ |
|---|---|---:|
| Corpus v5 run on gpt-5.4-mini (CU, learn and delta model, embeddings) | metered, `corpus-v5-gpt-5.4-mini/report.md` | 3.59 |
| gpt-5.4-mini vs gpt-5.2 learn-model study | not run (gpt-5.2 refused) | 0.00 |
| gpt-5.2 deployment attempt | refused, nothing created | 0.00 |
| Smoke uploads (4: API ×2, web ×1, the failed one) and the reject decision | replay of a hand format, 0 model calls, CSV needs no CU | 0.00 |
| AI Search Basic, about 0.45 h | estimate, list price | ~0.05 |
| Service Bus, Event Grid, Load Testing, about 0.45 h | estimate | ~0.01 |
| SQL serverless, online from about 17:28 until it pauses after the hibernate | estimate (minimum capacity) | ~0.3 |
| Durable Task Scheduler, Functions, Blob, Key Vault | estimate | ~0.05 |
| Log Analytics ingestion while awake | estimate | ~0.1 |
| **Total** | | **~4.1** |

The brief estimated ~$7–9 with the gpt-5.2 study; without it, about $3.7 plus platform.
