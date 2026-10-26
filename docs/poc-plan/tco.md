# TCO sheet — build (Azure) vs buy (`docs/poc-plan/closeout/README.md`)

All figures are USD.
- **Measured:** from the POC's own metering and the Azure bill (2026-10-21..23). Each measured figure names its source file.
- **List:** Azure retail list prices in `config/azure-prices.json`, with each price's source.
- **Assumption:** stated as such, for the client to replace.

Synthetic corpus only. Real loss runs average about 12 pages, against 1–5 here, so every per-document figure below is also given per page.

## 1. Per-document cost by path (measured)

The source is corpus v3 run `run-v3`: 302 documents, 564 pages, $2.98 in total (`docs/evaluation/results/2026-10-23-corpus-v3/studies/cost-by-path.md`). The costs are AI services: Content Understanding layout and classifier, and model tokens. Orchestration (section 2) is separate.

| Path | Docs | Mean per document | Per page | Model calls per document | Notes |
|---|---:|---:|---:|---:|---|
| Replay, PDF or scan (format memory hit) | 144 | $0.0130 (2.1 pages) | **$0.0063** | 0.66 (classification only) | Content Understanding $0.005 per page; zero extraction model calls |
| Replay, spreadsheet / CSV | 78 | **$0.0000** | $0.0000 | 0 | Parsed natively: no layout call, no model call |
| Review (fax, rotated scans, narrative statements, one inconsistent workbook) | 78 | $0.0122 | $0.0066 | 0.81 | The same AI cost as replay, plus reviewer time (section 4) |
| Learn: a new format, one time per carrier format | 3 | $0.0539 | — | 3.3 | c2 PDF $0.060, c4 xlsx $0.058, c5 csv $0.044 |
| First-seen native PDF, 5 fresh learns (study) | 5 | $0.0429 per learn | — | 2.4 | `studies/first-seen.md`: 5/5 delivered, 100 % key fields |

**Scaled to a 12-page PDF loss run:**
- Replay is about **$0.075**: 12 × $0.0063.
- Inline layout (demo host, documents of ≤ 5 pages) adds $0.0025 per page.
- A learn on a 12-page document should cost $0.10–0.30. That is an assumption: learn tokens scale with the table rows the model sees, and the measured learns were on 1–2 page documents.

## 2. Platform costs by month

### POC actual: October 2026 to date

The source is the Cost Management daily export and the Cost Query API, reconciled in `docs/evaluation/results/2026-10-23-cost-reconciliation/reconciliation.md`.

| Line | 2026-10-21 | 2026-10-22 | 2026-10-23 (partial at export) | Notes |
|---|---:|---:|---:|---|
| AI services (Content Understanding, models, embeddings) | $0.00 | $35.92 | $0.03 | $9.5 of 10-22 was the unmetered learn-reliability studies |
| Durable Task Scheduler (consumption) | $0.00 | **$6.46** | $0.12 | 2,153 actions × $0.003. 121 documents came through the API that day: **≈ $0.05 per uploaded document**, learn-queue timers included |
| Functions (Flex Consumption) | $0.00 | $0.00 | $0.00 | Inside the monthly free grant |
| SQL Database (serverless GP_S_Gen5, 2 vCore max) | $0.00 | $0.00 | $0.00 | Free offer: 100K vCore-seconds a month |
| Storage, Key Vault, App Service, Bandwidth, Log Analytics | < $0.01 | < $0.01 | < $0.01 | |
| AI Search Basic, Service Bus Standard, Load Testing, Defender for AI | — | — | created 10-23 | Not in the bill yet; see the production estimate |

### Monthly estimate

> **Assumptions, not measurements.** The production column assumes Basic Search, serverless SQL (1 vCore × 8 h × 22 days) and the Function, Defender and logging sizing shown. State Fund's architect replaces it after green light (`docs/poc-plan/closeout/README.md`).

| Line | POC (as run) | Demo-mode delta | Production estimate *(assumption)* | Basis |
|---|---:|---:|---:|---|
| Functions always-ready, 2 × 2 GB (http + durable) | $0 | **+$1.38 per demo day** | **$42** | List: $0.000004 per GB-second → $0.0576 per hour for both |
| AI Search Basic (chat index) | $74 | — | **$74** | List: about $0.101 per hour. Verify in the calculator for Central US |
| Service Bus Standard | $10 | — | $10 | List: $0.013441 per hour, plus $0.0000008 per operation |
| Defender for Cloud: AI threat protection | ~$5 | — | **$10–30** | List: $0.80 per 1M tokens scanned. Replay uses no tokens, so this is classification plus chat |
| SQL Database serverless | $0 (free offer) | — | $110–150 | List: $0.000173919 per vCore-second. Assumption: 1 vCore active 8 h a day, 22 days, plus storage |
| Durable Task Scheduler | per document | — | ~$0.05 per document | Consumption. At about 4,000 documents a month, compare against the Dedicated SKU |
| Log Analytics / App Insights | < $1 | — | $5–15 | Assumption: 2–5 GB a month |
| Static Web App | $0 (Free) | — | $9 | Standard plan for production (SLA, custom auth). Verify |
| Storage, Key Vault, Event Grid, Bandwidth | < $1 | — | < $10 | |
| Load Testing | $0.69 per 25-user run | — | $0 (on demand) | List: $0.15 per virtual-user hour |
| **Standing platform total** | **≈ $90 per month** | **+$1.38 per demo day** | **≈ $270–350 per month** | Before per-document AI and orchestration |

## 3. Annual cloud cost by volume (build)

Assumptions:
- 12 pages per loss run;
- 85 % replay, 15 % review;
- one new carrier format per 200 documents, at $0.20 per learn;
- 30 % on top for re-runs, evaluation and retries (bakeoff-strategy KQ3);
- the production platform from section 2.

| Documents per year | Per-document AI | Orchestration (DTS) | Learns | +30 % | Platform (12 × $310) | **Cloud per year** |
|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | $75 | $50 | $1 | $38 | $3,720 | **≈ $3,900** |
| 10,000 | $750 | $500 | $10 | $378 | $3,720 | **≈ $5,400** |
| 50,000 | $3,750 | $2,500 | $50 | $1,890 | $3,720 | **≈ $11,900** |

At low volume the standing platform dominates. Above about 20,000 documents a year the variable cost catches up, and even then the AI share is smaller than the orchestration share. Two levers bring that down:
- move DTS to the Dedicated SKU;
- skip the classifier on single-document uploads (study `classifier-skip`).

## 4. Three-year TCO

**Maintainer FTE assumption.** 0.25–0.5 FTE at a loaded **$180,000 a year**. The rate is an assumption for the client to replace with State Fund's own. The maintainer covers:
- format churn and new-carrier review;
- model migrations, about every 18 months (gpt-5.4-mini, then its successor);
- rule-catalog updates;
- the quarterly accuracy re-audit.

**Review labor**, for comparison only (it is the same work under build and buy): 15 % of documents at 5 minutes each.
- At 10,000 documents a year, 125 hours a year.
- At 50,000 documents a year, 625 hours a year.

> **Assumptions, not measurements.** Maintainer 0.25–0.5 FTE at a loaded $180,000 a year. Cloud from the section 3 assumptions (12 pages per document, 85 % replay, one new format per 200 documents, +30 %). State Fund supplies the real rate and sizing after green light; each is a cell change, not a rewrite (`docs/poc-plan/closeout/README.md`).

| *Assumption-based* | 1,000 docs/yr | 10,000 docs/yr | 50,000 docs/yr |
|---|---:|---:|---:|
| Cloud, 3 years | $11.7k | $16.1k | $35.7k |
| Maintainer at 0.25 FTE, 3 years | $135k | $135k | $135k |
| Maintainer at 0.5 FTE, 3 years | $270k | $270k | $270k |
| **Build, 3 years (0.25–0.5 FTE)** | **$147k–282k** | **$151k–286k** | **$171k–306k** |
| Architect review time, hypercare (12 months) | client estimate | client estimate | client estimate |

**What is sunk and what is not.** The v1 build (this POC) is sunk. People dominate the three-year build cost, and cloud spend is 4–21 % of it. Key-person risk is the failure mode evaluators cite. The mitigation is a named State Fund owner, plus the runbooks and governance pack in `docs/poc-plan/closeout/`.

## 5. Vendor comparison

| Vendor | Pricing basis | 3-year at list | What it does not include |
|---|---|---:|---|
| **Bevaya** (Roots Automation) | List: **$4,000 per agent per month, billed annually**, with 100k–200k pooled credits (1 credit ≈ 1 standard page). 2 agents (UC1 + UC2) $91,200 a year after a 5 % discount (bakeoff-strategy KQ2, bevaya.ai/pricing) | **$144k** (1 agent) to **$274k** (2 agents) | Implementation services. Credits over the pool: at 50k documents × 12 pages = 600k pages, the Growth tier. Internal integration and UAT labor. DPA and security review. Renewal uplift |
| Nomad Data | **Quote-only — request page-volume pricing** | — | |
| SortSpoke | **Quote-only — request page-volume pricing** | — | |
| Convr | **Quote-only — request page-volume pricing** | — | |

Ask every vendor in the RFP for the same unit: price per 1,000 pages at 120k, 600k and 1.2M pages a year, with implementation, the review UI and integration priced separately. Without that unit the rows cannot be compared.

## 6. Learn-vs-replay break-even per carrier (measured)

| | |
|---|---|
| **Format memory** | Pays the learn once per carrier format. Every repeat document then costs layout only. |
| **A model-per-document pipeline** | Pays at least one extraction call of learn size on every document. That is a lower bound: a full extraction also writes every row. |
| **Layout cost** | The same on both paths, so it is left out. |

The source is `run-v3` (`studies/cost-by-path.md`, `studies/first-seen.md`).

| Carrier (synthetic) | Format | Learn spend (once) | Learn calls | Cost per learn-sized call | Docs in corpus | Model-per-document lower bound | Break-even at document # |
|---|---|---:|---:|---:|---:|---:|---:|
| c1 Synthetic Mutual | hand spec | $0 | 0 | — | 110 | — | **1**: no learn |
| c2 Fictional Casualty | PDF, learned in-run | $0.060 | 3 | $0.020 | 100 | $2.00 | **3** |
| c4 Notional WC Exchange | xlsx, learned | $0.058 | 4 | $0.015 | 40 | $0.58 | **4** |
| c5 (CSV export) | csv, learned | $0.044 | 3 | $0.015 | 40 | $0.58 | **3** |
| c3 narrative statements | — | — | — | — | 12 | — | Always reviewed by design |
| First-seen PDFs, 5 fresh learns | PDF | $0.043 mean | 2.4 mean | $0.018 | — | — | **≈ 3** |

**Reading.**
- From the third or fourth document of a carrier format, format memory is cheaper than calling a model per document.
- Every later document of that format runs with zero extraction calls. 47 % of corpus documents made no model call at all. A replayed PDF averaged 0.66 calls, all classification.
- The dollars are small either way. The stronger arguments are determinism (the same input gives the same output, with cell lineage) and latency: replay p50 is 3.5 s against 48–59 s for a learn.

**Duplicate learns.** In the integration work the large account learned one new workbook format 35 times under concurrent uploads, before the learn lock was held until commit. The fix makes concurrent first documents of a format wait for one learn (`docs/evaluation/results/2026-10-23-loadtest/`). The 9 duplicate `notional-workers-comp-exchange-xlsx` versions it left behind are retired by the pre-bake-off reset (runbook).
