# ADR 0017 — Summary-only loss runs are a first-class document

**Status:** accepted (brief 18 Answers) · **Date:** 2026-10-24

## Context

Self-insured and public-entity accounts often send loss runs with **no claim rows**:
- one row per fiscal, report or policy year;
- counts by status;
- paid / reserve / incurred;
- often recoveries by kind (deductible, excess or reinsurance, SIF).

The brief 17 fitness run found three:
- **stcharles-wc:** its 36 year rows were extracted as "claims";
- **cccta-wc-addendum1:** no document produced;
- **marin-exhibits:** no document produced, classified Other.

That is proposals P1 and P5 in `docs/evaluation/results/2026-10-24-real-samples/`.

The canonical schema already had `detailLevel: summaryOnly` on a policy period, with printed subtotals and R-26 (period subtotals tie to the grand total). Corpus v3 uses it for one summary-only *period* inside a claims run. What was missing:
- the period's identity when it is not a policy term (a fiscal year label);
- recoveries other than subrogation;
- a way for a spec to say "each row is a period";
- a learn gate that does not require claim fields;
- the downstream meaning: analytics, quote payload and UI.

## Decision

1. **Representation: a summary period is a `PolicyPeriod`.** It has `detailLevel: summaryOnly`, no claims, and its printed numbers in `subtotals`. No separate `periodSummary` type is added. The brief asked for a "periodSummary record", and this is that record under the existing name, for four reasons:
   - R-10/R-11 (skip summary-only), R-26, the scorer, the lineage paths (`policyPeriods[i].subtotals.*`) and the review UI already understand it.
   - A mixed document, with claims for some terms and summaries for others, needs no second collection.
   - `detailLevel: summaryOnly` on the existing record stays as the marker for mixed documents, as the brief asks.
   - A **summary-only document** is one whose every period is summary-only.

   *Accepted (brief 18 Answers 1):* a summary-only run *is* a list of policy periods with totals and no claims. A second record type would duplicate the period concept, and every consumer would have to merge the two. The document says which it is with `granularity: period` (quote payload, item 7).
2. **Canonical schema 0.5.0:**
   - `policyPeriod.periodKind` takes `policyTerm | fiscalYear | reportYear | accidentYear | calendarYear`;
   - `policyPeriod.periodLabel` holds the printed label;
   - `totals.recoveries {deductible, reinsurance, sif, other}` is added. Subrogation stays `recoverySubrogation`, which claims use too, so nothing is duplicated.
   - Dates (`effectiveDate`, `expirationDate`) and counts (`claimCount`, `openCount`, `closedCount`) already existed and are used when printed.
3. **Glossary 0.3.0:**
   - aliases for period labels (Fiscal Year, Report Year, Accident Year, Calendar Year) and for the recovery kinds;
   - an **`ignoredHeaders`** list for printed columns deliberately not modelled: Total Recoveries (a sum of kinds), net or anticipated recoveries, and staff columns. "Total Recoveries" moves there from the `claim.recoverySubrogation` aliases (glossary 0.2.0 had added it, from stcharles).
   - Brief 17 Answers 2 is applied in the same version: "Loss" / "Losses" become **conditional** component words (`compositeHeaders.conditionalComponents`). They mean indemnity only beside a separate medical column. Otherwise:
     - the column is `ambiguous-composite`, unmapped in the graph;
     - a spec that reads it so is not published, and its replay is not accepted, unless a printed total confirms the reading.
4. **Spec language 0.3.0:**
   - **`tableLocator.rowShape: periodSummary`** makes each data row a period.
     - Money columns (`claim.*` money paths or `period.subtotals.*`) and count columns (`period.subtotals.claimCount|openCount|closedCount`) fill that period's subtotals.
     - `period.periodLabel|effectiveDate|expirationDate|policyNumber` columns fill its identity.
     - A `grandTotal` row fills `reportTotals`.
   - **`periods.kind`** sets `periodKind`.
   - **`period.subtotals.*` targets** are also read from total rows of claims-shaped specs (recoveries printed on a total row).
   - The learn prompt (learn-v6) explains this. SpecLint accepts these targets.
5. **Verification.**
   - R-26 is the totals gate for a summary-only document: period rows must tie to a printed grand total, like claims to subtotals.
   - `learn-gate.json` 0.2.0 has a `summaryOnly` section. Required are:
     - document carrier and valuation date;
     - a printed total incurred on every period;
     - a label, date or policy number on every period;
     - R-26 reconciling.
   - "No claim rows" is not a failure for such a document: neither in the verifier, nor in the router, nor in the attempt ranking.
6. **Classifier.**
   - A loss-run segment whose tables print period rows (a year-label column or counts) and money, but no claim identity column (claim number, claimant, date of injury), is marked `summaryOnly` on the segment.
   - This is shown in its derived-confidence method for review and learn.
7. **Downstream.**
   - **Analytics:**
     - the claim-level engine and its TypeScript twin see no claims from a summary-only document, so their snapshots and goldens do not change;
     - `catalog.yaml` 1.2.0 marks each of the 41 metrics `fromSummary: yes|no`. 11 are yes: counts, paid, reserve and incurred totals, severity, paid-to-incurred, and the three by-year breakdowns, keyed by period label;
     - `LossRun.Analytics.PeriodSummaries` computes the "yes" metrics from the period rows, and marks the rest not measurable ("summary-only loss run: claim-level metric").
   - **Quote payload v2:**
     - `periodSummaries {granularity: "period", rows, metrics}`;
     - each document entry carries `granularity: claim | period | mixed`;
     - the OpenAPI schema is `PeriodSummaries`.
   - **UI:** the workbench shows a period table instead of the claims grid when every period is summary-only. Every printed value opens its lineage (sheet and cell, or page and polygon), like the totals strip. There is a design mock: `docs/ui/mock/document-result-summary-only.json`.

## Consequences

- **Summary-only runs are no longer read as claims.** Learning such a run produces period rows that tie to the grand total or go to review.
- **Analytics on these accounts are thinner.** Underwriters get period-level analytics only; lag, CT, litigation, large-loss and loss-driver metrics are honestly not measurable there.
- **The what-if scenario does not apply to period rows.** It edits claims.
- **Synthetic coverage** is a c5 summary-only rendering by fiscal year with a deductible recovery, `Renderers.CsvPeriodSummary`. It is kept out of the corpus v3 plan, and its tests are in `src/LossRun.Evaluation.Tests/PeriodLevelTests.cs`. Real-sample effect: see `docs/evaluation/results/2026-10-24-real-samples-2/`.
- **The UI's captured mocks** (`docs/ui/mock/capture/`) predate the new payload fields. They pick them up on the next capture (`scripts/capture-mock.mjs`).

## Amendment (brief 22 Answers 2 and 6: every amount checked; no rules gate for PDFs)

**What went wrong.** A learned summary-only spec for a carrier-published frequency analysis passed `pdf-totals`. Its printed total tied on incurred and on the claim count, while two analysis columns ("Average Cost Per Claim", "Largest Loss") were read as recoveries. No totals rule checks recoveries, so nothing failed. Brief 22 added a lint (a header both mapped and ignored). The gate rule below would have stopped it before that lint existed. Both stay.

**Decision.**
- **Every amount must be checked.** In `learn-gate.json` (learn-gate 0.3.0), `summaryOnly.everyAmountChecked: true`: every money amount a summary-only replay reads must be the subject of a passing R-26 for its component and measure. That covers period subtotals and report totals: indemnity, medical, expense, total, and recoveries by kind.
- **Unchecked amounts are issues.** Each amount no check covers is an issue (`SpecVerifier.UncheckedAmounts`), so the spec is not published and the learn feedback names it.
- **What this means for recoveries.** R-26 does not cover recoveries, so a summary-only spec that reads them routes to review. A reviewer confirms such a format once; then it replays.
- **No rules gate for PDFs.** A summary-only PDF with no printed total that reconciles stays in review (Answers 2). There is no `summary-rules` gate. A summary page with nothing to tie against is exactly where a human should look once.
