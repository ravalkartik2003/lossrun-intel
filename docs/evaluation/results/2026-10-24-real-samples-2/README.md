# Real-sample re-run after the layout fixes (2026-10-24, brief 18)

This is the same 17 redacted sample documents as the first fitness run (`../2026-10-24-real-samples/`), re-run on the **local runner** after:
- **ADR 0016:** R-14 per policy period;
- **ADR 0017:** summary-only loss runs;
- **P4:** header repair;
- **P6:** a narrower classifier fallback;
- the conditional "Loss" component word (brief 17 Answers 2).

As before, this is a defect-finding run, not an accuracy measurement: no sample has keyed truth. The report names files, carriers (folder labels), rule ids, field names and counts only.

**In one line:** nothing wrong was delivered.
- Two documents that should never have been learned no longer are: laccd and the Travelers manual.
- R-14 no longer fails correct runs: Starr and Point Isabel.
- The summary-only St Charles run now replays as 36 period rows instead of 36 fake claims.
- Every extracted document still ends in review.

## How it was run

| | |
|---|---|
| Documents | The 17 eligible redacted copies staged in `eval-runs/real-samples-2026-10-24/archive/` (brief 17 Answers 4); local only, never committed |
| Runner | `lossrun eval run --mode local --concurrency 2 --cu-cache …`. Content Understanding results come from the brief 17 cache, so it was not called again. Learn budget: **5 iterations**, like for like with brief 17 run 1 (brief 17 run 2 used 3). Registry, results and meter are in memory: **nothing reached Blob, SQL, AI Search, the quote list or the demo host** |
| Runs | **Run 3:** all 17 documents.<br>**Run 3b:** Starr and Liberty, after narrowing the document-level statement fallback (see below).<br>**Run 3d:** AXIS and the two self-insured city files, after the period-summary subtotal fix and the form-row guard.<br>The "after" column is the latest run of each document. "Before" is brief 17 run 2 |
| Spend | **$3.98 for brief 18** (cap $6):<br>- synthetic corpus v3 regression: $2.97, of which $2.58 Content Understanding;<br>- real-sample model calls: $1.01 over runs 3, 3b, 3c and 3d.<br>Content Understanding for the real samples was cached and not paid again; the metered figures in the run folders show it at list price |
| Privacy | Redacted copies only. Azure OpenAI abuse monitoring cannot be disabled on this subscription, so prompts may be kept by Microsoft for up to 30 days. Nothing else left the machine |

## Routes before → after

| File | Carrier (folder) | Route, before → after | Hard rules, before → after | Headers resolved, before → after | What changed |
|---|---|---|---|---|---|
| alvin-isd-loss-runs | Multi-carrier bundle | 7 review → 7 review | — → — | 128/157 → 125/157 | 4 "Loss" columns without a medical column are now ambiguous-composite, not indemnity (Answers 2); net −3. Still a 150 dpi scan, so review by design |
| axis-cameron-county | AXIS Insurance | 1 review → 1 review | — → — | 18/26 → 16/26 | "Loss / Losses" are now ambiguous (−2). Two-line claim records remain (P2) |
| chubb-weslaco-isd | Chubb | 1 review → 1 review | — → — | 9/18 → 9/18 | Unchanged: our 150 dpi intermediate, review-only (Answers 3) |
| collin-county-wc | Self-insured county | none → none | — | — | Unchanged: stacked two-field columns (P3) |
| liberty-mutual-mercedes-isd | Liberty Mutual | 2 review → 2 review | R-14, R-19 → R-14, R-19 | 19/19 → 13/19 | "Loss Paid / Loss Reserve" with no medical column are now ambiguous (−6). R-14 still fails: the segment has one period, a "no losses" statement and two rows that belong to the nested records of P2 |
| marin-exhibits | Self-insured county | none → none | — | — | Unchanged. CU splits it into three "Other" segments, so the single-document fallback does not apply |
| okc-ownrisk-wc-selfins | Self-insured city | none → none | — | segment score 0.33 → 0.44 | The form row was dropped (P4). The remaining headers ("Court claim #", "Date of Injury/death", "Status (PTD or Death)") are glossary gaps, so the segment is still under 0.5 |
| sf-hacp-wc-add1 | Housing authority | 1 review → 1 review | — → — | 42/247 → 42/247 | 26 merged header cells are now split per column (P4). The split columns then stack the two lines of a two-line record ("Incident Date" over "Reported Date"), which is P2/P3 |
| starr-cameron-county | Starr | 1 review → 1 review | R-14 → **—** | 15/15 → 15/15 | **R-14 is cleared:** two empty periods carry the statement, so R-14 passes. Still review: the best of the 5 learn attempts misses 8 required fields and has 2 lint issues |
| stcharles-wc | Self-insured city | 1 review → 1 review | — → — | 8/13 → 11/13 | **Summary-only:** the segment is marked summary-only; the learned spec uses `rowShape: periodSummary`, giving **36 calendar-year periods** with recoveries by kind, 100 % of the summary gate's fields and no issues. Still review: no grand-total row was read, so R-26 has nothing to tie |
| cccta-wc-addendum1 | RFP document | none → none | — | — | Unchanged. Its loss-run page scores 0.25 because header cells carry values (P1 is not reached) |
| point-isabel-isd | RFP document | 3 review → 3 review | R-14, R-19 → **R-19** | 46/53 → 41/53 | **R-14 is cleared** (per period). R-19 remains: nested records (P2). The headers lost are "Loss" columns, now ambiguous |
| laccd | RFP document | **1 review (learned) → none** | R-14 → — | 3/9 → — | **Kept, not processed: "no loss table found".** Its one resolving table had 2 headers. No learn calls (brief 17 run 1 spent 6) |
| travelers-loss-reports-manual | Travelers | **1 review (learned) → none** | — → — | 25/50 → — | **Kept, not processed:** loss-run-like tables on 3 of 25 pages only, below the 50 % share the fallback now requires |
| ontario-wc-rfp, oswego-tpa-rfp, sorm-wc-valuation | RFP / state agency | none → none (correct) | — | — | Unchanged, correct |

**Totals:**
- **Learned when they should not have been:** 2 → 0.
- **Documents failing R-14:** 4 → 1. Liberty's is a genuine statement over rows within its one period, and goes away with P2.
- **Rows extracted as claims from a summary-only run:** 36 → 0.
- **Routes:** every extracted document still ends in review. None replays and none publishes; there is no prior spec to replay, and no learn passed its gate.

## Cause list: closed and remaining

| # | Proposal | State after brief 18 |
|---|---|---|
| P1 | Summary-only loss runs | **Closed in design (ADR 0017), partly in effect.**<br>- stcharles: period rows, no fake claims.<br>- cccta and marin: still not reached. A header cell carrying values and a multi-segment "Other" classification come first |
| P2 | Multi-line claim records | Open: brief 19. It now explains the remaining R-14 (Liberty) and R-19 failures (Liberty, Point Isabel) and the hacp stacking |
| P3 | Stacked two-field columns | Open: brief 19 (collin, hacp) |
| P4 | Header repair for merged CU cells | **Closed:**<br>- merged cells are split only when every part is a known alias (hacp: 26 splits);<br>- form rows are dropped only when that resolves more headers (okc 0.33 → 0.44);<br>- every repair is logged in the graph (`GraphTable.headerRepairs`, original text kept);<br>- no repair on any of the 302 synthetic graphs |
| P5 | Non-subrogation recoveries | **Closed (ADR 0017):**<br>- `totals.recoveries {deductible, reinsurance, sif, other}`;<br>- glossary `ignoredHeaders`, where "Total Recoveries" moved from the subrogation aliases;<br>- a new lint issue when two columns map to one target (the stcharles learn mapped six recovery columns to subrogation) |
| P6 | Classifier fallback | **Closed:**<br>- a table qualifies with ≥ 3 resolved headers at or above the review threshold;<br>- only those pages go on, and only when they are ≥ 50 % of the document;<br>- the rest is kept with "no loss table found";<br>- laccd and travelers no longer learned; synthetic classification 100 % / 100 % |
| P7 | 150 dpi intermediates | Closed as review-only (Answers 3). The organisation's archive will have the originals at native resolution |
| P8 | R-14 per policy period | **Closed (ADR 0016).** Starr and Point Isabel cleared |

**Found in this run:**
- **A document-level "no losses" statement over a run whose every period lists claims.** The first version of ADR 0016 gave that statement to every period, which failed R-14 on all three of Starr's terms. It is now applied to all periods only when the run has one period. Otherwise it stays unattributed, and lineage says so.
- **A learned period-summary spec classified every year row as a `subtotal` row (stcharles).** In a period-summary table such a row is now read as the period's summary, with no model call.
- **The form-row drop first removed a real header row on AXIS** (5 columns blank). It is now kept unless the drop resolves more headers.
- **Not fixed: composite headers naming several components.** "Incurred CLM + MED + EXP" (the Travelers manual) resolves to the medical measure through the composite grammar. A header naming two or more components should be the total. This is a glossary-grammar change for the next brief.

## Synthetic regression (corpus v3)

Full `eval run` + `eval score` of the 302-document corpus v3 with this code: `eval-runs/run-v3-18`, `eval-runs/score-v3-18`, CU cache in `eval-runs/corpus-v3-cu-cache`.

- **The deck table reproduces.** Every row has the same delivered key-field accuracy, review rate, pre-fill accuracy, n and commitment verdict as `../2026-10-23-corpus-v3/deck-table.md`. The DQ / quote-success table is identical too.
- **One cell moved:** learn calls per document on the n = 1 first-seen native PDF row, 3.000 → 1.000. That is one c2 learn needing one call instead of three, which is model variance.
- **No delivered value wrong.**
- **Classification:** precision and recall 100.0 % [98.5 %–100.0 %] (n = 302), bundle split 8/8.
- **No side effects on the synthetic graphs:** no header repair, no ambiguous composite and no per-period "no losses" statement on any of the 302.
- **New period-level coverage** stays out of the corpus plan, so the deck table stays reproducible. These are c5 renderings: a multi-period CSV with a "no claims" policy year, and a summary-only CSV by fiscal year with deductible recoveries and a grand total. Tests are in `src/LossRun.Evaluation.Tests/PeriodLevelTests.cs`.
