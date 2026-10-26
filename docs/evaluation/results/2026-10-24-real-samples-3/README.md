# Spec-language constructs and learn quality: results (2026-10-24, brief 19)

This covers ADR 0018 (multi-line claim records, stacked cells, component-naming headers) and the learn-quality fixes, on three sets:
- **the new constructs corpus**: generator 0.4.0, 24 synthetic documents;
- **the corpus v3 regression**: 302 synthetic documents;
- **the 17 redacted real samples**, on the local runner.

Real-sample reporting names files, carriers (folder labels), rule ids, field names and counts only, as before.

**In one line:**
- The three constructs read their synthetic layouts exactly: 0 wrong key fields on 3,080.
- No delivered value was wrong anywhere.
- The corpus v3 deck table did not fully reproduce: one first-seen learn (c2) still fails, and its root cause was found and fixed but not re-measured, because the $6 budget was reached.
- No real sample publishes yet; the closest (Starr) is blocked by a gate-policy question.

## Spend

**About $6.03, at the cap.** Content Understanding was cached wherever possible. Cost by run:

| Run | Paid |
|---|---:|
| v3 run 1 (models) | $1.06 |
| v3 run 2 | $0.64 |
| v3 run 3 | $0.63 |
| CU for 10 uncached v3 pages, three times | ~$0.15 |
| Constructs run 1, CU and models | $1.47 |
| Constructs run 2, c6–c8 | $1.03 |
| Fixture CU (3 documents) | $0.06 |
| Real samples | $0.99 |

Model spending stopped there. The last fixes are verified by tests and by re-verifying saved learn attempts offline (deterministic, no model call), not by another run.

## 1. Constructs corpus (generator 0.4.0, `eval generate --constructs`)

| Carrier | Construct | Documents | Learn (first documents) | Published learned spec replayed on all six documents (offline, deterministic) |
|---|---|---:|---|---|
| c6 | two-line records (records mode rows) | 6 | Published from the 4th document (1 call); the first three learns failed (see below) | 6/6 read, 0 wrong of 1,050 key fields, 6/6 pass the publish gate |
| c7 | one table per claim with loose lines (records mode lines) | 6 | Published from the 2nd document (2 calls) | 6/6 read, 0 wrong of 1,055 key fields, 6/6 pass the gate |
| c8 | stacked two-field cells (part 0 / 1) | 6 | Published from the 6th document (3 calls) | 6/6 read, 0 wrong of 975 key fields, 5/6 pass the gate (one leftover lint issue) |
| c9 | component-sum headers, Ind+Med totals (CSV) | 6 | Published from the 1st document (2 calls) | In the eval run: 1 learned, 5 replay-accepted, 100.0% delivered key fields (n = 1,062) |

**Two harness and replay defects found on this corpus, both fixed:**
- **The eval harness passed the construct carriers' key ("c6") as the submission's carrier name.** Every replay of a published construct spec was therefore refused as a carrier mismatch. The eval-run review rate on this corpus (75–83 % on the PDFs) measures that defect, not the constructs. The table's last column is the deterministic replay with the defect fixed.
- **A learned anchor that also matches the "(continued)" heading split each period at its page break**, and neither half tied to its subtotal (R-10). Replay now continues the open period when the anchor repeats its policy number and dates.

**Why first-document learns failed.** I re-verified the 9 saved best attempts with the final code, with no model calls: 3 now pass the gate and 5 tie their totals, with 1–3 leftover issues each. Most of the leftovers are ordinary model errors:
- document-field normalizers;
- a status enum;
- a total-row regex anchored with `$` before the amounts (now repaired automatically);
- `lineFields` in rows mode and an assumed `reportRunDate` constant (both now removed automatically).

**Offline coverage that does not depend on a model.** Hand specs per construct, replayed on recorded CU graphs and compared claim by claim with truth:
- `src/LossRun.Formats.Tests/ConstructTests.cs`: c6–c8, plus a no-records control and the continued-heading case;
- `src/LossRun.Evaluation.Tests/ConstructCsvTests.cs`: c9, the R-29 pass and fail cases, and R-20 failing without the declared components.

## 2. Corpus v3 regression (302 documents)

Final run (`eval-runs/run-v3-19c`, scored in `eval-runs/score-v3-19c`) against `../2026-10-23-corpus-v3/deck-table.md`:

| Input type | Delivered accuracy | Review rate | Pre-fill | Learn calls / doc | vs the committed deck table |
|---|---|---|---|---:|---|
| native PDF, recurring | 100.0 % (n=13386) | 3.1 % | 100.0 % | 0.000 | identical |
| native PDF, first-seen (n = 1) | — (not delivered) | insufficient sample | — | 5.000 | **was delivered with 3 calls; now the c2 learn fails** |
| XLSX / CSV | 100.0 % (n=14982) | 1.3 % | 100.0 % | 0.062 | identical except learn calls (0.088 → 0.062) |
| scan ≥ 300 dpi | 100.0 % (n=9942) | **6.2 %** | 90.0 % | 0.146 | was 4.2 %: one more c2 scan reviewed (no c2 spec yet when it ran) |
| scan 200 dpi rotated | 100.0 % (n=7272) | 27.1 % | **98.3 %** | 0.000 | pre-fill was 99.8 % (same c2 cause) |
| fax < 200 dpi | — | 100.0 % | **88.6 %** | 0.000 | pre-fill was 89.1 % (same cause) |
| narrative statement | — | 100.0 % | 0.0 % | 0.000 | identical |

**Unchanged:**
- no delivered value wrong;
- classification precision and recall 100.0 % (n = 302), bundle split 8/8;
- source-inconsistent documents kept out of straight-through: **9/9**.

That last figure dropped to 8/9 in the second run, a real safety regression. The published c4 workbook spec mapped two summary-sheet columns but not total incurred, and the summary-sheet auto-fix only ran when none were mapped. It now fills the gaps; the third run is back at 9/9.

**The c2 first-seen learn: root cause found after the last run.**
- **The failure.** In the third run, every attempt mapped the policy summary box ("Claims", "Tot. Inc") to `period.subtotals.*`, and the claim table's total-incurred column with it. No claim table then qualified.
- **Why the model never got the right feedback.** Brief 18's summary-only gate treated those claimless, totals-less periods as a summary run, so "no claim row was read" was replaced by "missing period subtotals".
- **Three fixes:**
  1. A summary-only period now needs printed totals (learn gate and router).
  2. Lint refuses money `period.subtotals` targets in a claims-shaped spec.
  3. The brief 18 prompt wording that invited the mapping is narrowed.
- **Verification.** The saved attempt now gets the right feedback ("claim rows (no claim row was read)", plus the lint issue). Whether the learn then succeeds needs a run, which the budget did not allow. **Not re-measured.**

**Earlier runs also showed the construct guidance over-applied.** The first v3 run had it in the shared prompt prefix: a status rule beside a status column, summary-box columns as subtotal counts, an inverted lookup. The guidance is now given only in the document part, when the document shows the construct's signal. The inverted lookup and the stray status rule are repaired deterministically.

## 3. Real samples (17 documents, 5 learn iterations)

"Before" is brief 18's final state (`real-samples-2`: run 3 with its Starr/Liberty and AXIS/okc/St Charles re-runs); "after" is run 4. Content Understanding came from the cache.

| File | Carrier (folder) | Route, before → after | Hard rules, before → after | Headers resolved, before → after | What changed |
|---|---|---|---|---|---|
| alvin-isd-loss-runs | Multi-carrier bundle | 7 review → 8 review | — → — | 125/157 → 134/168 | One more segment extracted. Still a 150 dpi scan, review by design |
| axis-cameron-county | AXIS Insurance | 1 review → 1 review | — → — | 16/26 → 20/26 | More headers resolve: the two-line headers now read by their key-line row. The learn did not choose records mode within 5 attempts |
| chubb-weslaco-isd | Chubb | 1 review → 1 review | — | 9/18 → 13/18 | Stacked or merged headers now resolve. 150 dpi intermediate, review-only |
| collin-county-wc | Self-insured county | **none → 1 review** | — | — → 60/100 | **Stacked headers** ("Claim Number Claim Type", "Injury Date / Closed") now resolve, so the segment is extracted. 150 dpi intermediate: review-only, not learned |
| liberty-mutual-mercedes-isd | Liberty Mutual | 2 review → 3 review | R-14, R-19 → **R-19** | 13/19 → 34/69 | **R-14 is cleared.** R-19 remains on the per-claim-table pages: the learn did not choose records mode lines |
| marin-exhibits | Self-insured county | none → none | — | — | Kept: multi-segment "Other", loss table on 1 of 20 pages. This is the tuning-run open item (Answers 4) |
| okc-ownrisk-wc-selfins | Self-insured city | none → none | — | — | Segment score 0.44, still under 0.5 (glossary gaps) |
| sf-hacp-wc-add1 | Housing authority | 1 review → 1 review | — → — | 42/247 → **129/247** | Stacked two-line headers resolve to both fields. The learn did not reach the gate |
| starr-cameron-county | Starr | 1 review → 1 review | — → — | 15/15 → 15/15 | The auto-fixes (valuation and insured by `labelNext`, period dates from their columns, `claimStatusRule`) fill everything but **claim.medical.paid: the run prints no medical column.** Brief 18's attempt had mapped another column to medical; this one maps honestly. See Questions |
| stcharles-wc | Self-insured city | 1 review → 1 review | — | 11/13 → 12/13 | Summary-only, 36 period rows. **The run prints no grand total anywhere** (classified: no printed total to verify against, so review is correct) |
| cccta-wc-addendum1 | RFP document | **none → 1 review** | — | — → 2/4 | Now reaches a summary-only segment and is learned (5 attempts, not published). Its one table is a form of stacked count and finance sections with values in its header cells: a layout class, not a classifier bug |
| point-isabel-isd | RFP document | 3 review → 3 review | R-19 → R-13, R-19 | 41/53 → 41/53 | R-19 remains (records mode lines not chosen). R-13 (counts) appears on one segment's attempt |
| laccd, travelers-loss-reports-manual | RFP / Travelers | none → none | — | — | Kept: "no loss table found", as in brief 18 |
| ontario-wc-rfp, oswego-tpa-rfp, sorm-wc-valuation | RFP / state agency | none → none (correct) | — | — | Unchanged |

**Totals:**
- **Files with an extracted loss run:** 8 → 10 (collin, cccta).
- **R-14:** 1 → 0.
- **Headers resolved across extracted documents:** 272/548 (50 %) → 460/713 (65 %).
- **Published:** 0 → 0.

## Cause list after brief 19

| # | Cause | State |
|---|---|---|
| P1 | Summary-only runs | In effect. St Charles: no printed total (correct review). cccta: a form-like section table (layout class) |
| P2 | Multi-line records | **Closed in the spec language (ADR 0018)**, proved on synthetic layouts (c6 rows, c7 lines; 0 wrong of 2,105 key fields). On the real samples the model did not choose the construct within 5 attempts (AXIS, Liberty, Point Isabel). Next lever: a deterministic records proposal from the same signals that trigger the prompt hint |
| P3 | Stacked cells | **Closed.** c8: 0 wrong of 975 key fields. Real: collin and hacp headers now resolve; collin stays review-only (150 dpi) |
| Q5 | Component-naming headers | **Closed.** c9 delivered 6/6. "Paid CLM + MED + EXP" is now the total |
| P4 / P6 / P8 | Header repair, classifier fallback, R-14 per period | Closed in brief 18; R-14 failures now 0 |
| Learn quality | Starr's 8 missing fields | 7 fixed by deterministic auto-fixes. **The 8th (medical paid) is a gate-policy question** |
| Open | Tuning run | 50 % fallback share (marin); `claimStatusRule` (both in `docs/operations/blind-run.md`) |

## 4. Follow-up run 5 (brief 19 Answers: AXIS, Liberty, Point Isabel, Starr)

This run uses the Answers changes: medical absent, `statusFrom`, and the construct instruction from ingestion evidence. It ran at 5 learn iterations, with Content Understanding from the cache (8 hits, 0 misses). The four RFP documents in the same folders were classified only (one call each), as before. Model spend was about $0.60.

| File | Evidence found at ingestion | Route, run 4 → run 5 | What the learn did / what blocks it |
|---|---|---|---|
| starr-cameron-county | none | 1 review → **1 published** (learned in 4 attempts) | Passes the `pdf-totals` gate. `absentComponents: ["medical"]` (the run prints no medical column) and `statusFrom: unknown` (no printed open/closed counts to verify the close date), so R-30 flags every claim's status |
| axis-cameron-county | `records-rows` | 1 review → 1 review | **Records mode rows chosen**, with line-1 mappings. It now fails on document fields: carrier name, valuation date, and the indemnity paid total row |
| liberty-mutual-mercedes-isd | `records-lines` | 3 review → 2 review | **Records mode lines chosen**, with `lineFields`. Its claim-number pattern matched none of the printed lines, so R-19 still fires; R-14 fires on one single-period segment with a "no losses" statement |
| point-isabel-isd | `records-lines` (after the window fix) | 3 review → 3 review | **Records mode lines chosen** on every segment. The same claim-number pattern miss leaves R-19 |

**What this shows:**
- The evidence instruction works: the model now chose the construct on all three real layouts, after not choosing it at all in run 4.
- What remains is the line-field regex for the claim number: a learn-quality fix for the next brief, not a missing construct.
- No document was delivered wrong.

**Cause list update:**
- **P2 (multi-line records):** the construct is now chosen on the real samples; claim-number `lineFields` remain.
- **Learn quality (Starr):** closed. It publishes, medical is recorded absent, and status is recorded unknown.
- **Tuning run:** the `claimStatusRule` item is replaced by `statusFrom` (verified per format).
