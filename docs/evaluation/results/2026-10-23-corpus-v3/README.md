# Corpus v3 results (2026-10-23, the close-out work)

Corpus `corpus-gen-0.3.0`, seed 8, 302 synthetic documents. Regenerate it with `lossrun eval generate --out <dir> --seed 8`.

## What v3 adds

The v3 fields come from a separate random stream, so v2 documents keep their bytes. The one exception is the 10 password-locked PDFs: their encryption salt changes, their content does not.

**New truth fields**
- WCIO nature, cause and body-part codes.
- Cumulative trauma on about 12 % of claims, with CT nature codes 78/80, cause codes 97/98 and "cumulative trauma" descriptions.
- Litigation flag on about 10 % of claims.
- Subrogation recoveries on about 6 % of indemnity claims.
- Last day worked, including post-termination CT (date of injury after the last day worked).
- A payroll and FTE context per submission.

**Where it is printed**
- **c4 xlsx** adds these columns: Nature, Cause, Body Part, Litigated (Y/N), Last Day Worked, Claim Type, and Subrogation after the grand total.
- **c5 csv** adds these columns: Cause of Loss, Nature of Injury, Loss Description, Litigation (Yes/No), Recoveries, LDW.
- **Payroll and FTE** are submission context. The insights evaluation passes them to both sides.

## Files

| Path | What it is |
|---|---|
| `deck-table.md` | The deck table, with the first-seen row from the study appended. Against v2: XLSX/CSV now reviews 1 of 80, the planted source-inconsistent workbook, which v2 delivered (step 5 below). Learn calls per document moved on the first-seen and XLSX/CSV rows, as did the fax row's model calls. Delivered accuracy is unchanged. |
| `report.md`, `report.json`, `documents.csv`, `field-by-carrier-input.csv`, `run.json` | Scorer output (`lossrun eval score`). |
| `insights/insights-delivered.md` | Insight accuracy: **41 of 41 metrics measurable and within tolerance on 224 delivered documents.** On v2, 11 were not measurable. |
| `studies/first-seen.md` | Five fresh-registry learns of c2 native PDFs. This is the first-seen stratum at n = 5. |

## How it was produced

1. **First run.** The first `lossrun eval run` was stopped at 238 of 302 documents: every c4 and c5 learn exhausted its 5 iterations.
   - **Root cause.** The model mapped the new WCIO code columns to `claim.natureOfInjury` with an `integer` normalizer. The canonical field is a {code, text, codeSystem} object, so every row failed the type check. The replay's fix-hint did not mention codes, so all five iterations kept failing.
   - **Fix.** `SpecLint.RetargetCodedColumns` now retargets such a mapping to `.code`, with the `code` normalizer and the WCIO code system, without a model call. The hint now names codes too.
2. **Resumed run.** The failed documents were removed and the run resumed: c4 learned in 2 calls and c5 in 3, and every other document replayed.
3. **Claim Type column.** Insights then showed `litigation_rate` still unmeasurable. Its denominator is lost-time claims, and neither c4 nor c5 printed a claim type. A Claim Type column was added to c4, the corpus was regenerated (only c4 and the locked PDFs changed) and the c4 slice re-run: 3 learn calls, then 39 replays.
4. **The live run found two more learn defects.** The chat eval's workbook upload went to review on the live system, and that led back to the local spec. Two lint auto-fixes now apply without a model call, and the c4 slice was re-run with them (4 learn calls, then 39 replays):
   - **The planted workbook was delivered.** The learned workbook spec read its Policy Summary sheet for dates only (`subtotals: {}`), so it passed the weaker spreadsheet-rules gate. The planted source-inconsistent workbook c4-022 (a policy total $100 off its claim rows) was delivered.
     - `SpecLint.MapLookupSubtotals` now maps a summary sheet's count and total-incurred columns. R-10/R-11 then tie them, and the spreadsheet-totals gate applies. c4-022 now goes to review.
   - **A valuation date was located by its value.** One learn declared the valuation date as a constant, and the corpus v2 run auto-fix turned that into a regex for the literal date. On other workbooks it matched a claim's date, and R-06/R-21 failed.
     - `LocatePrintedConstants` now locates a date constant by the label printed left of it (`labelRight` "Loss experience valued"), or leaves it to the model.
   - **The delta repair path** now applies the coded-column fix too.
5. **Scoring.** `lossrun eval score`, `eval insights`, `eval study first-seen --trials 5` and `eval study cost-by-path`.

**Spend**
- Delivered run: $2.99, the run as it stands.
- Discarded attempts: about $1.23 of c4/c5 learn failures, plus two c4 re-runs at $0.05–0.07 each.
- First-seen study: $0.21.

`field-errors.csv` and `foundry-dataset.jsonl` are not committed because of their size. Regenerate them with `lossrun eval score`.
