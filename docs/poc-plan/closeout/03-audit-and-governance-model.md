# 3. Audit & governance model

## What is governed, and who approves it

| Change | Who may make it | Approval | Recorded as |
|---|---|---|---|
| Override a value | Underwriter, UWManager, Admin | Self, with a rationale. A hard rule failure must be decided in review | `override` + `review.override` audit rows; `dbo.Override` (ledger) |
| Decide a review (accept, override, reject) | Underwriter, UWManager, Admin | Self, with a rationale | `review.*` audit row; the decision JSON on the review item |
| Reopen a processed document for review | Underwriter, UWManager, Admin | Self, with a reason | `review.reopened` |
| Promote a learned format (shadow → active) | Automatic after 3 clean replays, or on underwriter approval | Lifecycle record per version | `format.approve`; registry stats |
| Retire a format version / pre-bake-off reset | UWManager, Admin (reset: Admin) | Reason required | `format.retire` / `formats.reset` |
| Rename or retire a quote | UWManager, Admin | Rationale required | `quote.rename` / `quote.retire` / `quote.restore` |
| Rule catalog (R-01…R-26), canonical schema, glossary | Technical owner | Business-owner sign-off; golden-set regression must pass (artefact 4) | Git commit + `catalogVersion` stamped on every rule outcome and override |
| Model or prompt version | Technical owner | Golden-set regression; ADR when it deviates from the research | Git; model and prompt versions in each value's lineage and in each eval run's record |
| Database schema | Technical owner | Rerunnable changeset, reviewed; ledger tables are never altered (new tables instead) | `dbo.SchemaChangeLog` |
| Infrastructure | Technical owner, architect | Terraform plan reviewed; no portal changes (any manual change is captured back or recorded in an ADR) | Git; Terraform state |

## Tamper protection: the guarantee, in one sentence

**Tamper protection is preventive on `ExtractedValue`, `Override` and `AuditEvent`, and detective on `Claim`.**
- The first three are append-only SQL ledger tables. The engine rejects UPDATE and DELETE, and database digests in immutable Blob make the history verifiable.
- `Claim` is a temporal table. Every change is versioned, and `lossrun-db audit-proof` flags any version without an audited decision.
- Production option: make `Claim` an updatable ledger table, so claim rows are tamper-evident in the engine too (ADR 0010).

**Evidence.**
- `lossrun-db audit-proof` checks:
  - (a) every override is audited;
  - (b) UPDATE and DELETE on the ledger tables are rejected with error 37359;
  - (c) `sp_verify_database_ledger` passes against the stored digests, and from immutable digest storage;
  - (d) the temporal AS OF value before each override equals the old value the override recorded.
- **The close-out work finding on (d), fixed.** After the integration work load test, (d) flagged override 2113 on a load-test claim (medical reserve, 25 concurrent sessions on one field).
  - The claim's temporal history was right: the version naming 2113 starts at 14:09:07.055, and the value just before it is 8,065, which is the old value 2113 recorded.
  - The check was wrong. It took "the first version with a larger override id", but EF hi/lo ids from concurrent sessions are not in time order.
  - The check now takes the version that names the override. Overrides from `dbo.ApplyDecision` take their ids from `dbo.OverrideSeq` under the document lock.
  - Run on 2026-10-23 after the fix: **all four checks pass**. That covers 3,396 claims, 492 overrides, 2,002 audit events, and the ledger verified against 3 digests.

## Prompt Shields status (ADR 0013; verbatim)

> Prompt Shields policy defined; attached when the dedicated deployment is approved; mitigations in force today: tool-layer scope enforcement, document chunks treated as data (planted-instruction test), structured output, audit of every turn.

**Context.**
- The policy is the content filter `lossrun-chat`: Prompt Shields blocking indirect attacks (`infra/environments/poc.tfvars`).
- It needs a dedicated chat deployment. Azure's risk check refused that deployment on 2026-10-23 (ADR 0004 and ADR 0013). A ticket is open.
- **Defender for Cloud AI threat protection is on** (Standard, enabled 2026-10-23). It alerts on jailbreak, data leakage and credential theft using Prompt Shields signals.

## The chat leakage suite

`src/LossRun.Chat.Tests/ScopeLeakageTests.cs` has 11 cases over four leak paths. The scope is asserted at the tool layer, not in the prompt.
- **An index that ignores the scope filter.** The post-filter drops foreign chunks.
- **A record store that returns another quote's documents.** It is dropped and never reaches the model.
- **Injected ids in tool arguments.** They are rejected.
- **A model that cites sources it was never given.** The citation is dropped and reported as `invalidCitations`.

The live evals add a cross-quote probe and a planted-instruction letter. Both stayed clean in the chat-service work, and in the close-out work 0 of 10 text answers held a fabricated number.

## Cadence

| Activity | Frequency | Evidence |
|---|---|---|
| Accuracy re-audit (golden set + a sample of production documents re-keyed) | Quarterly, and after any model, prompt, rule-catalog or format-gate change | Eval run record (git SHA, models, prompt, catalog, price table) + `deck-table.md` |
| Cost reconciliation (metered vs billed) | Monthly | `lossrun eval reconcile` report; the Costs screen line |
| `audit-proof` + ledger digest verification | Monthly, and before any audit or examination | Command output, archived with the digest location |
| Model retirement calendar check | Monthly | Runbook (artefact 7) |
| This pack (artefacts 1, 2, 4, 8) | Annually and on material change | An audit event with the reviewer |
