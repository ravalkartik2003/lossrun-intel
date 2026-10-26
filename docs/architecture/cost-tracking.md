# Per-document cost tracking (cross-cutting requirement)

**Why.** Stakeholders must see what the POC costs per uploaded document and per path (replay / delta / learn / fallback), and the numbers must reconcile with the Azure bill. It is also the evidence behind the "learn once, replay with zero model calls" claim and the TCO slide in `docs/poc-plan/bakeoff-strategy.md`.

## Design
1. **Meter every billable call at the point it is made.** A `CostMeter` service (singleton, injected everywhere a billable SDK is called) records one `UsageEvent` per call:
   `{ documentId, runId, stage, service, meter, quantity, unit, modelOrAnalyzer, deploymentType, region, timestampUtc, correlationId }`.
   Services and meters:
   - Content Understanding: pages by meter (read/basic, layout/standard, contextualization tokens, custom/agentic) — from the operation result's usage block where provided, else pages submitted.
   - Document Intelligence (if used): pages by model (read, layout, custom).
   - Azure OpenAI: prompt tokens (cached vs uncached), completion tokens, per deployment and deployment type (Data Zone / Global / Batch) — from the response `usage` object.
   - Note: Content Understanding's own model use (classification, contextualization of generative fields) is billed on **our** model deployment, so it appears as Azure OpenAI token meters (from CU's `usage.tokens`, cached tokens a subset of input) in addition to CU's page/contextualization meters.
   - Embeddings: tokens. AI Search: queries counted (cost is capacity-based; attribute by share).
   - Function executions / DTS actions / storage / SQL: recorded as counts; priced as a flat per-run allocation (they are cents).
2. **Price table as configuration**, not code: `config/azure-prices.json` with `{ service, meter, region, unitPrice, unit, source URL, retrievedOn }`, seeded from the Retail Prices API figures in `docs/architecture/azure-extraction-stack.md`. A `terraform output` or startup job refreshes it from `https://prices.azure.com/api/retail/prices` when possible; otherwise the file is updated by hand with the date.
3. **Cost rollups** computed at write time and stored next to the document: `DocumentCost { documentId, runId, path (replay|delta|learn|fallback), llmCalls, pages, tokensIn, tokensOut, costUsd, breakdown[] }`. Aggregates per carrier, per path, per day in SQL views.
4. **Surface it.** In the UI: a cost line on every processed document ("$0.04 · CU layout 12 pages · 0 LLM calls"), a per-carrier learn-vs-replay chart, and a POC-to-date total. In the API: `GET /documents/{id}/cost`, `GET /costs/summary?from&to&groupBy=path|carrier`. In the demo: the badge is shown live after each upload.
5. **Reconcile to the real bill.** Every resource is tagged `project=lossrun-poc`; Cost Management export (daily, to the `evals` container) is compared with the metered total per day. Report the ratio (metered ÷ billed) and explain gaps (price staleness, untracked meters, minimum charges). Target: metered within ±10% of billed for AI services; standing costs (SQL, Search, storage) reported separately as "platform" not "per document".
6. **Audit tie-in.** `UsageEvent` rows are append-only (ledger table) so the cost record cannot be edited after the fact.

## Brief 30: pricing by model name, and no silent $0

- **Price lookup.** A usage event is priced as its model: `UsageEvent.ModelName`, else the deployment's model from the `Models__DeploymentModels` map (Terraform writes `deployment=model;…` from `model_deployments`), else the deployment name.
  - Before, a deployment named apart from its model (`chat-gpt-5.4-mini`) found no price and cost $0.
- **No silent $0.** A line with no price in `config/azure-prices.json` is kept with source `missing price`.
  - `DocumentCost.UnpricedLines` and `Unpriced` name it, `CostUsd` covers the priced lines only, and the summary reads "unpriced (…)".
  - The escalation `missing-price` (ADR 0025) is raised, keyed by the meter.
- **The price file.** The Functions host reads `config/azure-prices.json` from its own config path when it is deployed there (`Prices:Path`, default `<app>/config/azure-prices.json`); the embedded copy is only the fallback.
- **Brief 31:** `lossrun eval reconcile` builds its model lines from `config/models.json` (learn, delta and embedding deployments). Each model price entry in `config/azure-prices.json` carries `billMeterPrefixes` (the Cost Management meter names, e.g. `5.4 mini cd Inp`) and `billUnit`. A model with no price or no bill meters is a configuration error. The gpt-5.2 prefixes follow the gpt-5.4-mini pattern and have not yet been seen on a bill.

## Acceptance (applies to the preprocessing work through the API work and the chat-service work)
- No billable SDK call without a `UsageEvent`; a unit test fakes the SDK and asserts the meter fired.
- The per-document cost is visible in the API before the UI exists.
- The close-out work adds the dashboard and the monthly reconciliation report.
  - It is done: `lossrun eval reconcile`, and the first report is `docs/evaluation/results/2026-10-23-cost-reconciliation/reconciliation.md`.
  - AI services ran 0.71 as recorded. The cause is a test harness that metered nothing. Excluding it the ratio is 0.94.
  - The Costs screen shows the published figure, from blob `extractions/reconciliation/latest.json`.

Note (2026-10-22): Content Understanding token usage (contextualization, completion) is billed on our own Foundry deployment and may appear as separate meters in Cost Management; the reconciliation in the close-out work must map both.
