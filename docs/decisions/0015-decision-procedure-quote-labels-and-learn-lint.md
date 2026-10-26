# ADR 0015 — Decisions through one stored procedure, quote labels instead of renames, and three more deterministic learn fixes

**Status:** accepted (`docs/poc-plan/closeout/README.md`) · **Date:** 2026-10-23

## Context

**The override took too long.** The integration work measured a p95 of 1.87 s against a 1.5 s target, under 25 concurrent underwriters. The time went into about 17 SQL round trips from East US 2 to the database in Central US (ADR 0003). The integration work asked for one stored procedure with at most 3 round trips. ADR 0010 had settled on EF Core for the command path.

**Demo hygiene needed renames.** The close-out work asked to "rename" the load-test quote to the demo account and retire others through an audited API. But quotes have no table of their own (they are `Document.QuoteId`), and the ledger rows that name a quote cannot change.

**The learn path failed on corpus v3 and on the live system.** Three failures, each one a deterministic shape the model gets wrong:
1. WCIO code columns were mapped to the whole `CodedValue` field with an `integer` normalizer. Every row failed the type check, and the fix hint never mentioned codes.
2. A workbook's summary sheet was read for dates only. The printed totals went unchecked, so a planted $100 inconsistency was delivered.
3. A valuation-date constant was rewritten by the corpus v2 run auto-fix into a regex for the literal date. On the next workbook it matched a claim's date.

## Decision

1. **`dbo.ApplyDecision @json`** (changeset 0016, `CREATE OR ALTER`, `runOnChange`) applies a review decision or an override.
   - **The app computes every row:** overrides, audits, changed-claim projections, rule outcomes, the result JSON, the decision, and the `review.decided` outbox row.
   - **The procedure writes them in one transaction** under `UPDLOCK, HOLDLOCK` on the document row:
     - it checks the revision first;
     - override and audit ids come from `dbo.OverrideSeq` and `dbo.AuditEventSeq`;
     - each changed claim gets one temporal version;
     - it invalidates the quote's feature snapshots after the commit.
   - **EF stays** for reads and for every other command. The procedure is the one write path that crosses regions often enough to matter.
2. **`dbo.QuoteLabel`** (changeset 0017, temporal) holds a display name and a retired flag, set through `PATCH /quotes/{id}` (UWManager, Admin).
   - The quote id never changes.
   - A retired quote leaves `GET /quotes`, but nothing is deleted.
   - Changes are audited as `quote.rename`, `quote.retire` and `quote.restore`.
3. **Three lint auto-fixes** run without a model call, on the learn path and (codes) on the delta path:
   - `RetargetCodedColumns`;
   - `MapLookupSubtotals`, limited to the glossary's unambiguous count and total-incurred headers;
   - `LocatePrintedConstants` locates a **date** constant by the label printed left of it, or leaves it to the model.

## Consequences

- **Override.** p95 is 860 ms in load-test run 3 (was 1,874 ms), with 3 SQL calls on the response path.
  - The procedure duplicates the claim-column list that EF maps. `ClaimColumns.Project` builds the JSON from the same projection, and `The_ef_model_matches_the_deployed_tables…` guards the schema, but a new claim column now needs the procedure updated too.
  - It is noted in the changeset comment.
- **The audit proof's temporal check** now matches each override to the version that names it, because EF hi/lo ids are not in time order. It passes on the live database.
- **Quote labels.** A real id change would need re-keying across documents, outbox, snapshots and context. That is out of scope; the report's Questions ask whether a label is enough.
- **Learn fixes.** The planted inconsistent workbook is now reviewed (corpus v3: XLSX/CSV review rate 1.3 %, delivered accuracy unchanged). Learned workbook specs are admitted by the `spreadsheet-totals` gate when the summary prints totals.
