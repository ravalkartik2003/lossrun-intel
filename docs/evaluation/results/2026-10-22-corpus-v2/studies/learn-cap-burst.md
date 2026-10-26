### Learn-cap burst test (`docs/operations/runbook.md`)

**When and where:** 2026-10-22, 20:52–20:57 UTC, on the deployed host `<function-app>`. The `LearnGate` cap was 3, the `gpt-5.4-mini` quota 100K TPM.

**Input:** 8 simultaneous uploads (`lossrun-sim --concurrency 8`), each a synthetic CSV from a different never-seen fictional carrier with its own header vocabulary. All 8 missed the fingerprint and routed to Learn.

**Source of the numbers:** Application Insights activity telemetry (`requests` for Route/Learn/ValidateAndPersist, `dependencies` for the AOAI endpoint).

| Document | Queue wait (Route end → Learn start) | Learn activity |
|---|---:|---:|
| 1 | 0.8 s | 66.8 s |
| 2 | 0.8 s | 81.4 s |
| 3 | 0.8 s | 56.7 s |
| 4 | 60.9 s | 72.5 s |
| 5 | 70.9 s | 89.4 s |
| 6 | 90.5 s | 134.9 s |
| 7 | 141.0 s | 109.0 s |
| 8 | 161.3 s | 108.7 s |

**Cap behaviour:**
- Peak concurrent Learn activities: **3**.
- 5 of 8 documents queued. Mean wait 65.9 s, max 161.3 s.
- Each queued document started within one retry interval (≤ 10 s) of a slot freeing.

**Latency:**
- Client time-to-complete: p50 145.8 s, p95 282.0 s.
- These totals include the queue. Excluding it, a document's own processing was the learn activity plus about 4 s.

**429s:**
- **44 HTTP 429** against 26 HTTP 200 on the AOAI endpoint during the burst, even with the cap.
- At 3 concurrent multi-iteration learns, 100K TPM is still exceeded.
- The client's retry absorbed every 429: all 8 Learn activities completed, and none failed. The cost was learn time of 57–135 s, against roughly 25 s unthrottled.

**Outcome of the learns:**
- No spec was published. All 8 learns used their 5 iterations and went to Review with pre-fill (for example, 23 claims read and no rule failures on one).
- These ad-hoc CSVs were built to exercise the cap, not as learn exemplars, so this outcome says nothing about learn quality.
- The live registry is unchanged.

**Cost:** $0.50 metered (40 LLM calls), against the $0.30 estimate: every learn ran all 5 iterations.
