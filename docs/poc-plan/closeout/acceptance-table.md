# Final acceptance table

Every row maps to a criterion in `docs/requirements/poc-requirements.md`. The client document leaves the thresholds blank ("≥ XX %"); the targets below are the ones proposed in `docs/evaluation/scoring-protocol.md` §6 and the briefs. **Synthetic corpus: the sealed blind set on the client's archive replaces every UC1 number.**

## Use Case 1: loss-run ingestion for quoting

The deck table comes from the corpus v5 run the prompt gate holds (332 documents, `docs/evaluation/results/2026-10-26-brief35-wake/corpus-v5-gpt-5.4-mini/deck-table.md`; Run 2026-10-26 17:14–17:23 UTC · git `806351499c` · mode local · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v12` (artefact `f913ce4e28a5`) · gate `learn-gate-0.3.0` · catalog `0.12.0` · prices `0.1.0` · truth `corpus-gen-0.9.0 seed 8`). Delivered = values that leave without a human; 95 % CIs are cluster-adjusted. The first-seen row is the corpus v3 study of 5 fresh learns (`docs/evaluation/results/2026-10-23-corpus-v3/studies/first-seen.md`): the corpus run has one first-seen document.

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill accuracy (reviewed) | Commitment | Result |
|---|---:|---|---|---|---|---|
| Native PDF, recurring template | 65 | 100.0 % [100.0–100.0] (n=13,386) | 3.1 % | 100.0 % | ≥ 99 % delivered | **met** |
| Native PDF, first-seen template (5 fresh learns) | 5 | 100.0 % [98.8–100.0] (n=384) | 0/5 | — | ≥ 98 % delivered | **met** (n = 5) |
| XLSX / CSV | 80 | 100.0 % [100.0–100.0] (n=14,670) | 2.5 % | 100.0 % | ≥ 98 % delivered | **met** |
| Scan ≥ 300 dpi | 48 | 100.0 % [100.0–100.0] (n=9,990) | 4.2 % | 100.0 % | ≥ 95 % delivered | **met** |
| Scan 200 dpi rotated | 48 | 100.0 % [99.9–100.0] (n=6,948) | 29.2 % | 99.8 % | measured, not committed | reported |
| Fax < 200 dpi | 48 | — | 100 % (by design) | 88.9 % (a mangled amount is left blank, not shown: not a regression) | review by design | reported |
| Narrative statement | 12 | — | 100 % (by design) | no structured values | review by design | reported |

| Criterion (UC1) | Measured | Target | Result |
|---|---|---|---|
| Auto-classification precision / recall | 100.0 % [98.5–100.0] / 100.0 % [98.5–100.0] (n=302); bundle split exact 8/8 | ≥ 97 % / ≥ 95 % | **met** |
| Data-quality first pass (source-consistent, excluding narrative and fax) | 95.3 % [91.7–97.5] (n=235) | ≥ 85 % | **met** |
| Quote success (priceable, delivered), by input type | 100.0 % on every delivered row (`deck-table.md`, second table) | ≥ 90 % of valid uploads | **met** on delivered rows |
| Source-inconsistent documents kept out of straight-through | 100 % (all 9 planted, including the v3 workbook) | 100 % | **met** |
| End-to-end latency, ≤ 15 MB upload | Per document in-process p50 3.8 s, p95 8.3 s. Deployed and warm: first claim p50 4.3 s async, 1.3 s inline (ADR 0008) | ≤ 60 s p95 (proposed) | **met** |
| End-to-end latency, 50 MB mixed batch | **89 s** for all 72 files to be terminal. One upload of 50.8 MB and 144 pages, mixing XLSX, CSV, native PDF, fax, and 200 and 300 dpi scans. 202 accepted at 10.8 s, first document at 53 s, median at 76 s. 59 delivered, 13 reviewed (10 faxes by design). $0.26 metered. Deployed API, warm, 2026-10-23 (`docs/evaluation/results/2026-10-23-batch50/`) | ≤ XXX s (client) | **measured** on synthetic data; re-measure on the blind set's largest real bundles |
| Model calls per document | 47 % of documents had none; replayed PDFs average 0.66 (classification only); a learn takes 2–4 calls once per format | Minimize (constraint) | **met** |
| Cost per document | Replay $0.0063 per page (PDF), $0 for spreadsheets; a learn about $0.05 once (`tco.md`) | — | reported |
| Deliverables | API spec (`docs/api/openapi.yaml`) and sample code (`docs/api/README.md`, the simulator); canonical mapping dictionary (`docs/evaluation/glossary.json`, `canonical-schema.json`); latency and accuracy report (the corpus-v3 report, load test); exception workflow (Review screen, `/reviews`) | — | **delivered** |

## Use Case 2: underwriter analytics

| Criterion (UC2) | Measured | Target | Result |
|---|---|---|---|
| **Insight accuracy vs reference set** | **42/42 catalog metrics within tolerance** (catalog 1.3.0) on 252 delivered corpus-v5 documents (money ±$1 / 0.1 %, ratios ±1 % rel., days ±0.5). C# and TypeScript engines byte-identical on 60 golden cases. Client reference-set scoring is ready: `lossrun eval insights --reference <csv>` | ≥ XX % (client) | **met** on synthetic truth; **the client's reference set is open** |
| **UI responsiveness, p95 interactive actions** under 25 concurrent underwriters, 1,046-claim account (load test run 3, 2026-10-23) | Features (cached) 158 ms; what-if 364 ms; open document 465 ms; override 860 ms; features after an override 693 ms; chat full answer p50 2.9 s / p95 7.4 s. 0 errors over 1,357 samples | p95 ≤ X.X s (proposed: 300 ms features, 1.0 s document, 1.5 s override) | **met** |
| Sub-second chart interactions on 1,000+ claims over 5 policy years | The browser what-if recomputes in milliseconds (the analytics work and the workbench work perf tests) | < 1 s | **met** |
| **Audit completeness** (100 % of edits with user, time, old → new) | Every override, review decision, reopen, context change, quote label and push is an audit row in a SQL ledger table. `audit-proof` passes all four checks (3,396 claims, 492 overrides, 2,002 audit events) | 100 % | **met** |
| **Integration fit** (read/write to the quote-management sandbox via APIs and webhooks) | Mock quote system: flags, annotations, follow-ups and loss-run analysis written through the outbox → Service Bus; `review.decided` events through Event Grid; exactly-once delivery; 0 dead letters (ADR 0014) | Successful read/write | **met against the mock**; **the client's sandbox is open** |
| Scenarios | Derived metrics (41, including CT, litigation, subrogation, closure, lag); drill-down and filters; what-if sliders; the lineage view (source cell → field → feature); confidence; override log; memo export; flags; follow-ups; role-based permissions | All five scenarios | **delivered** |
| Deliverables | Analytics catalog (`docs/analytics/catalog.yaml`); UX demo (`demo-script.md`); sample UW report package (pricing memo); audit and governance model (artefact 3); role-based access configuration (artefact 6); performance test results (`docs/evaluation/results/2026-10-23-loadtest/`) | — | **delivered** |

## Chat (beyond the requirement)

| Criterion | Measured | Target | Result |
|---|---|---|---|
| Numeric answers (ADR 0013) | 30/30 exact; 30/30 correct citations; planted instruction ignored (`docs/evaluation/results/2026-10-23-chat/`) | ≥ 90 % | **met** |
| Text answers, rubric | 10/10, 0 fabricated numbers | ≥ 90 %, 0 fabrications | **met** |
| Scope leakage | 0 (11 tests + live probes) | 0 | **met** |
| Chat p50 on the large account | 2.6 s quiet, 2.9 s under 25 users | ≤ 4 s | **met** |
