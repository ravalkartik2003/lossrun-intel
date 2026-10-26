# 1. AI-system inventory entry

| Field | Entry |
|---|---|
| System name | Loss Run Intelligence (POC) |
| Inventory id | to be assigned by State Fund's AI inventory owner |
| Business owner | **State Fund to name** (underwriting operations). Proposed: a State Fund employee who owns the product after the POC (bakeoff-strategy KQ7, key-person risk) |
| Technical owner (POC) | The POC build developer. Architecture: the State Fund senior Azure cloud architect |
| Purpose | Extract claims data from workers' compensation loss runs (PDF, scans, XLSX/CSV) into a canonical schema. Use Case 1: quoting intake. Use Case 2: underwriter analytics, what-if and chat over the extracted record |
| Decision role | **Decision support only.** It makes no pricing, eligibility or claims decision. Underwriters review, override and price; every override is audited |
| Inputs | Loss runs and submission documents uploaded by underwriters or applicants. Kept non-loss-run documents (payroll reports, letters, applications) are indexed for chat |
| Outputs | Canonical claims JSON with cell-level lineage, rule outcomes (R-01…R-26), review items, an upload manifest, feature snapshots (41 catalog metrics), a pricing memo, quote-system events, and cited chat answers |
| Models and versions | Model names are configuration, not code. Today:<br>• `gpt-5.4-mini` (2026-03-17, Data Zone Standard, US) for learn, delta repair, classification and chat;<br>• `text-embedding-3-large` v1 (chat index);<br>• Azure Content Understanding `prebuilt-layout` and a custom classifier.<br>`gpt-5.2` is configured as the learn model but blocked by an Azure risk check (ADR 0004). Prompt `PromptBuilder.Version`, chat prompt `chat-v1`. Model versions are recorded per document and per value in the lineage |
| Deterministic components | Format replay (no model), the rule catalog (R-01…R-26), the analytics engine (C# with a byte-identical TypeScript twin, ADR 0011) |
| Data | POC: synthetic only. Production: State Fund documents in State Fund's Azure tenant (Data Zone US). No data leaves the tenant except to Azure OpenAI under Microsoft's DPA |
| Hosting | Azure: Functions (Flex Consumption), Durable Task Scheduler, Azure SQL (system of record, ledger and temporal tables), Blob, AI Search, Service Bus, Event Grid, Static Web Apps. Terraform only (`infra/`) |
| Human oversight | A review queue for every document that fails a hard rule, an unknown format, a low-resolution scan or a narrative statement. Override with a rationale. "Confirm new format" before a learned format runs straight-through |
| Monitoring | Application Insights. Cost reconciliation to the Azure bill (`lossrun eval reconcile`). Defender for Cloud AI threat protection (on, Standard, since 2026-10-23). `lossrun-db audit-proof`. Format lifecycle (shadow → active) |
| Version control | Git (this repository). Changesets in `db/changelog/`. Format specs versioned in the registry with `supersedes`. Each eval run records its git SHA, models, prompt version, rule catalog and price table |
| Material changes log | ADRs in `docs/decisions/` (0001–0015) |
| Risk tier (proposed) | Low degree of potential harm, with a human in the loop (artefact 2) |
| Status | POC complete, pending the sealed blind-set run on State Fund's archive |
