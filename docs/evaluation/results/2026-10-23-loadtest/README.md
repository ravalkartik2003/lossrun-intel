# Load test, the integration work: 25 concurrent underwriters on the deployed POC (2026-10-23)

**Setup**

- **Tool:** Azure Load Testing (`<load-testing>`), JMeter plan `infra/loadtest/underwriters.jmx`, 1 engine.
- **Load:** 25 virtual underwriters for 10 minutes after a 1-minute ramp, plus one background uploader posting one synthetic loss run a minute.
- **Account:** `Q-LOAD-1000`, 34 synthetic spreadsheets, 1,046 claims.
- **Each underwriter's cycle**, with 8–14 s of think time between actions:
  1. open the features (cached snapshot, or a recompute after someone's override);
  2. three server what-ifs;
  3. open two documents;
  4. save one override (reserve on claim 0 of the thread's document);
  5. ask one chat question about the account.
- **Browser side:** 25 Playwright sessions on the live Static Web App ran at the same time (`e2e/underload.spec.ts`).

**Runs**

- **Run 1** `run-1023-1339`, 13:39–13:53 UTC, before the lock fix.
- **Run 2** `run-1023-1359`, 13:59–14:13 UTC, after it.

## Pass/fail criteria (client-side, JMeter on Azure Load Testing)

| Criterion | Run 1 | Run 2 | Target | Run 2 |
|---|---|---|---|---|
| Features read, cached (`features-hit`) p95 | 155 ms (n=132) | **194 ms** (n=126) | ≤ 300 ms | pass |
| Override p95 | 2,308 ms (n=150) | **1,874 ms** (n=151) | ≤ 1.5 s | **fail**: root cause below |
| Document p95 | 437 ms (n=304) | **394 ms** (n=305) | ≤ 1.0 s | pass |
| Error rate | 0 % (1,286 samples) | **0 %** (1,284 samples) | < 0.5 % | pass |
| 429 on Content Understanding / model | 0 of 156 calls | **0 of 134 calls** | 0 | pass |

## All labels, run 2 (client-side)

| Label | n | p50 | p95 | max |
|---|---|---|---|---|
| features (snapshot hit) | 126 | 102 ms | 194 ms | 1,466 ms |
| features (recompute after an override) | 47 | 550 ms | 875 ms | 2,111 ms |
| what-if (server) | 495 | 133 ms | 333 ms | 2,750 ms |
| document | 305 | 209 ms | 394 ms | 734 ms |
| override | 151 | 964 ms | 1,874 ms | 2,913 ms |
| chat turn (non-streaming, full answer) | 150 | 7,581 ms | 10,536 ms | 21,533 ms |
| upload (202 accepted) | 10 | 562 ms | 2,873 ms | 2,873 ms |

## Server-side, run 2 (Application Insights, `infra/loadtest/server-metrics.kql`)

| Endpoint | requests | failed | p50 | p95 |
|---|---|---|---|---|
| GetQuoteFeatures | 173 | 0 | 94 ms | 634 ms |
| QuoteWhatIf | 495 | 0 | 113 ms | 323 ms |
| GetDocument | 645 | 0 | 194 ms | 387 ms |
| DocumentOverrides | 151 | 0 | 939 ms | 1,862 ms |
| ChatTurn | 150 | 0 | 7,557 ms | 10,518 ms |
| GetQuoteClaims (browser sessions) | 340 | 0 | 135 ms | 842 ms |
| GetQuotePayloadV2 (browser sessions) | 340 | 0 | 206 ms | 876 ms |

Platform figures for run 2:

- **Function instances:** at most 2 (platform `InstanceCount` metric). Application Insights saw 11 distinct role instances, because workers recycle.
- **Azure SQL** (GP_S_Gen5, 0.5–2 vCores): CPU at most 19 % (run 1: 23 %), 563 billed vCore-seconds, at most 41 sessions.
- **Service Bus:** DLQ 0, active 0.
- **Event Grid:** 321 published, 321 delivered, 0 failed attempts, 0 dead letters; the mock recorded every event, deduplicated by id.
- **Outbox:** 341 rows in the two runs, all published on the first attempt.
  - Events (321): published 0.47–1.0 s after commit on average, at most 5.1 s.
  - Commands (20 `quote.payload`): all delivered, 2.0 s on average, at most 14.7 s.

## Front end under load (25 browser sessions, one workstation, live Static Web App)

| Measure | Run 1 | Run 2 | One session alone |
|---|---|---|---|
| Slider → KPI update p95 | 112 ms (3,490 samples) | **140 ms** (3,400) | 41 ms |
| Browser event timing (INP proxy) p95 | 1,720 ms | 1,848 ms | 56 ms |
| Document open (navigation → ledger interactive) p95 | 1,479 ms | 1,614 ms | ~1.0 s |
| Analytics open (1,046-claim account loaded) p95 | 4,435 ms | 5,327 ms | 9.4 s cold |

All 25 Chromium sessions ran on one laptop. The KPI recompute, the client-side what-if, stayed at a 140 ms p95. Event timing reflects the workstation's CPU: one session alone shows 56 ms. A per-user INP needs one browser per machine (Playwright Workspaces or real users), so treat 1.8 s as an upper bound, not a per-user number.

## Override: root cause of the 1.87 s p95

Server-side, an override is about 17 SQL round trips: p50 0.92 s and p95 1.31 s of SQL, the slowest single statement 0.52 s at p95. Non-SQL time is 0.05 s at p50 and 0.54 s at p95.

- **Cross-region latency.** The app runs in East US 2 and the database in Central US (ADR 0003, a vCore-quota workaround), so each round trip costs 41–54 ms. The floor is about 17 × 45 ms ≈ 0.75 s before any work.
- **Lock waits.** The slowest-statement p95 (0.52 s) is lock waits on the document row when two underwriters touch the same document. The test rotates 25 threads over 34 documents.
- **What was already fixed during the integration work** (ADR 0014):
  - a 2.6 s override (51 round trips, the rule-outcome inserts batched by EF) became 0.7 s warm;
  - run 1's 2.3 s p95 (the snapshot invalidation held locks on the shared quote's snapshot rows until commit) became 1.87 s.
- **To meet 1.5 s:**
  - co-locate SQL with the app, which roughly halves every round trip; or
  - collapse the override into one stored procedure, about 3 round trips.

  The first is a production prerequisite in ADR 0014. The second is a contained change for the next brief.

## Cost of the runs

| Item | Run 1 | Run 2 | Basis |
|---|---|---|---|
| Azure Load Testing | 4.52 VUH → $0.678 | 4.49 VUH → $0.674 | $0.15/VUH (Retail Prices API, `config/azure-prices.json`) |
| Chat (model, embeddings, semantic ranker) | 150 turns → $0.312 | 150 turns → $0.376 | metered per turn (`dbo.ChatTurn.CostUsd`) |
| Functions on demand | 4,778 GB-s + 6,740 executions → $0.127 | 5,312 GB-s + 6,604 executions → $0.141 | $0.000026/GB-s, $0.40/M (inside the monthly free grant, so $0 marginal) |
| Azure SQL serverless | 573 vCore-s → $0.100 | 563 vCore-s → $0.098 | $0.62611/vCore-hour |
| Event Grid, Service Bus, mock app | < $0.01 | < $0.01 | 321 events, ~60 messages |
| **Total per run** | **≈ $1.22** | **≈ $1.29** | Application Insights ingestion not metered here |

Preparation was outside the runs:

- **Two smoke runs:** about $0.10 together, at the 10-VU-per-engine minimum.
- **The 1,046-claim account:** $0.12 of embeddings, 0 model calls.
- **A first attempt, stopped:** 35 spreadsheets of a new carrier format queued to learn one by one; 70 model calls, about $1.04. That is what led to the burst-learning fix in ADR 0014.

Files: `run1/`, `run2/`, each holding `jmeter-summary.json` (from `infra/loadtest/summarize.py`) and `browser-underload.json`.

## Run 3 (`docs/poc-plan/closeout/README.md`): the single-procedure override and the chat result cache

The same Azure Load Test (`lossrun-12b-underwriters`, run `run13-1023-1538`) ran on 2026-10-23 at 15:38–15:51 UTC. These changes were deployed for it:
- `dbo.ApplyDecision` (changeset 0016) applies an override in one round trip;
- `ResultCache` holds results by (document, revision) for the chat tools;
- the integration work fixes were already in.

This run had no browser sessions, so it is API load only. Summary: `run3/jmeter-summary.json`.

| Criterion | Run 2 | **Run 3** | Target | Run 3 |
|---|---|---|---|---|
| Features read, cached (`features-hit`) p95 | 194 ms | **158 ms** (n=119) | ≤ 300 ms | pass |
| **Override p95** | 1,874 ms | **860 ms** (n=161) | ≤ 1.5 s | **pass** |
| Document p95 | 394 ms | **465 ms** (n=333) | ≤ 1.0 s | pass |
| Error rate | 0 % | **0 %** (1,357 samples) | < 0.5 % | pass |
| **Chat turn p50 / p95** (1,046-claim account) | 7,581 / 10,536 ms | **2,881 / 7,400 ms** (n=158) | p50 ≤ 4 s | **pass** |

All labels:

| Label | n | p50 | p95 | max |
|---|---|---|---|---|
| features (snapshot hit) | 119 | 101 ms | 158 ms | 2,095 ms |
| features (recompute after an override) | 56 | 481 ms | 693 ms | 3,957 ms |
| what-if (server) | 520 | 131 ms | 364 ms | 3,621 ms |
| document | 333 | 237 ms | 465 ms | 1,576 ms |
| override | 161 | 557 ms | 860 ms | 1,611 ms |
| chat turn (full answer) | 158 | 2,881 ms | 7,400 ms | 23,477 ms |
| upload (202 accepted) | 10 | 368 ms | 3,870 ms | 3,870 ms |

**Server side (Application Insights).** Each sample comes from one request, SQL round trips are counted per operation, and the SQL time is the sum of its calls.

| Endpoint | requests | p50 | p95 | SQL calls p50 | SQL time p50 / p95 |
|---|---|---|---|---|---|
| DocumentOverrides | 161 | 543 ms | 813 ms | 5 (3 on the response path) | 556 / 792 ms |
| ChatTurn | 158 | 2,862 ms | 7,385 ms | 13 | 469 / 607 ms |
| GetDocument | 335 | 220 ms | 417 ms | 3 | 104 / 136 ms |
| GetQuoteFeatures | 175 | 92 ms | 608 ms | 2 | 75 / 489 ms |

**The override, call by call.**
1. Document head, 37 ms.
2. Current result, 38 ms.
3. `EXEC dbo.ApplyDecision`, about 380 ms. The overrides, audits, claim versions, outcomes, review item and outbox row commit together under the document lock.

The response returns after step 3. The relay's two calls, load the outbox row and mark it published, run after the response and do not count against it. In the integration work the override took about 17 round trips at about 45 ms each across regions.

**What is left in the 380 ms.** The procedure's own writes: ledger inserts and a temporal update, in one transaction in Central US. The SQL-region move (runbook) removes about 40 ms per round trip. It does not remove the write cost.

