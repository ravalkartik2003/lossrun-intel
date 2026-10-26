# Document fields from the header block, row-level line of business, classifier over-splitting, mock re-capture (brief 23)

> Synthetic corpus v5 (generated documents, fictional carriers) and the Travelers sample reports published in public agent guides (fictitious insureds; local, never committed). This page names files, rule ids and field names only. It quotes no document values.

## Bottom line

- **Header block (ADR 0023).**
  - **How fields are read.** Document-level fields are read from the printed word stream ("Valued as of …", "Insured: …", "Policy Number: …", "Policy Period: … – …", "Eff … Exp …").
  - **Where they go.** At ingestion they are evidence (`headerFields`) and a "Header block" section of the learn prompt. Replay reads them with a new locator, `headerLabel`, with lineage.
  - **Repair.** `LocateHeaderFields` points an unread field at its label.
  - **Coverage.** The detector finds the valuation date and insured on every synthetic PDF layout. On the first corpus v5 c10 learn, the model itself used `headerLabel` for both.
  - **Document facts.** A required field the document does not print is stated as a fact in the review reason, not left as a gap.
- **Travelers detail reports: review, with document facts.** The reports print **no valuation date and no policy period**. They print a losses window ("Accident Date Range", "Losses From … To …"). The detector records it as `report.lossWindow`, never as a policy period, and review now says so:
  - "document fact: the run prints no valuation (as-of) date";
  - "… a losses window ("Accident Date Range"), not a policy period".

  Publishing them would need dates the documents do not contain. This is the brief's second target: review with a reason that is a document fact.
- **Row-level line of business (ADR 0020 amendment).** Rows whose file prefix or line column names another line are kept, not extracted, on the period's `keptRows`.
  - **Detail claim listing:** 4 rows in run6 (prefixes LR, PP, …).
  - **Detail loss report:** 2 rows in run6 and run7.
  - **Each kept row** carries a reason, appears on the manifest's row-level list, and never enters claims or totals.
  - **R-10 to R-13** tie the claims, or the claims plus the kept rows, to a printed total.
- **Over-splitting.** The cause on c10-005: pages 3 and 4 each open with a period heading and the repeated table header, and the classifier read each as a new document. Adjacent loss-run segments with the same table header and no header block of their own are now merged. Across all 206 cached classifier results this changes one, c10-005 (1-2 / 3-3 / 4-6 → 1-6), and it now replays accepted.
- **Corpus v5 under the gate** (corpus-gen 0.8.0: same 332 files; c5's other-line truth rows move to `keptRows`). The deck table reproduces on every row:
  - **XLSX / CSV:** still 100.0 %. n falls from 14,982 to 14,778 field values, because the kept rows are no longer claims.
  - **c7:** 6/6.
  - **c10:** 5/6 in the gated run. A learn declared skip row kinds on the measure labels; that is fixed in replay. On the re-run, **6/6 at 100.0 % (n = 1,314)**.
- **Live environment.**
  - **Database:** brief 21's changeset 0018 (`dbo.GlossaryAlias*`) had never been applied to the live database, and every upload's parse failed on it. It is applied now, with this brief's 0019 (`UploadItem.KeptRowsJson`).
  - **Deploys:** Function App and web app.
  - **Mock capture:** re-captured from the live API with a new synthetic fixture under quote Q-DEMO-23B. It now includes `claim_count_with_financials`, a `reportOnly` claim, kept rows on the document and on the manifest's row-level list, and catalog 1.3.0.
  - **Checks:** axe 0 violations in both themes, including the new kept-rows state; e2e 49 + 2.

## 1. Header-block fields: detector coverage (synthetic, recorded graphs of corpus v5)

| Layout | Fields found | Notes |
|---|---|---|
| c1 landscape blocks (native, 300 dpi, fax) | valuationDate, reportRunDate, insuredName | Policy-period labels head each period block (multi-period), so they are rightly skipped. The 200 dpi rotated scan loses no document field. |
| c2 portrait codes (all renderings) | valuationDate ("Evaluation Date"), reportRunDate ("Printed"), insuredName ("Named Insured") | Per-period "Eff … Exp …" skipped (multi-period). |
| c3 narrative | valuationDate ("valued as of"), insuredName | The name ends at the sentence end. |
| c6, c7, c8, c10 constructs | valuationDate, insuredName (c7: "Valuation Date", "Named Insured") | Period labels are per block. |
| c4 XLSX, c5 / c9 CSV | — | No word stream: spreadsheets print document fields in cells (`labelRight`), unchanged. |
| Travelers detail and summary reports (7 graphs) | `report.lossWindow` only ("Accident Date Range" / "Losses From") | No valuation date, policy period or policy number label. The 2010 policy number is printed as "Policv Number" (OCR). |

## 2. Row-level line of business

- **Vocabulary.** `line-of-business.json` lob-0.2.0 `rowSignals`: the Travelers file prefixes (CB / CM workers' comp; LR / ER general liability; PP / AB / AD auto; FN / FR / BR / DR property; FY crime; SR bond), each with `source: "public sample Travelers"`. Also the glossary line values (WC, EL, GL, AL). A claim-type column that prints a line code ("GL") counts too.
- **Kept rows.** A kept row keeps its values and lineage under `policyPeriods[i].keptRows[j]`. Its `keptReason` reads, for example, "line of business: general liability (claim-type code "LR")". The canonical schema is 0.9.0.
- **Verification** (rule catalog 0.8.0).
  - **When it passes:** R-10 to R-13 pass when the printed total equals the claims (a workers'-comp-only total) or the claims plus the kept rows (a total across every line).
  - **When it fails:** a total that ties neither way fails, so the document goes to review.
  - **No printed total:** the existing gate applies (see Questions).

## 3. Corpus v5 under the prompt gate (`regression/`)

**The run.**
- Commit `47c1f1c`, learn-prompt artefact `2d37673f4a1e` (learn-v11), corpus-gen 0.8.0, 332 documents.
- **Files:** all 332 are byte-identical to corpus v5 at corpus-gen 0.7.0.
- **Truths:** they differ only in the schema-version stamp, and on c5 in the kept rows.

| Row | Brief 22 follow-up | Brief 23 |
|---|---|---|
| Recurring native, ≥ 300 dpi, 200 dpi, fax, narrative, c6, c8, c9 | — | Identical: delivered accuracy, review rate, n |
| XLSX / CSV | 100.0 % (n = 14,982), review 1.3 % | **100.0 % (n = 14,778)**, review 1.3 %. The kept rows left the claims, in truth and in replay alike |
| c7, one table per claim | 5/6 | 6/6 (100.0 %, n = 858): the known variance |
| c10, measures as rows | 5/6 (n = 990; c10-005 split into 3 segments) | 5/6 (n = 1,158): c10-005 merged and replay-accepted; c10-000's learn lost its measure rows to skip row kinds. **c10 re-run after the replay fix (`b0ab3d8`): 6/6, 100.0 % (n = 1,314), review 0 %** |
| Classification precision / recall | 100.0 % | 100.0 % (n = 332); bundle split exact 8/8 |
| Source-inconsistent guardrail | 9/9 | 9/9 |

The replay fix (`RecordMeasures`: a labelled row is a measure row even when a skip kind matches it) is not in the learn-prompt artefact, so the gated run stands. The c10 re-run is in `c10-rerun-deck-row.md`.

## 4. Travelers detail reports, before → after

**Runs.**
- **`run5`:** brief 22 follow-up.
- **`run6`:** commit `e76955a`.
- **`run7`:** `b0ab3d8`, with the measures fix.

All three use 5 learn iterations and the same Content Understanding cache. The five lag, frequency and changes reports stay kept, not processed (ADR 0021), in all three runs.

| Report | run5 (before) | run6 | run7 |
|---|---|---|---|
| `2004-detail-claim-listing` | review: 11 claims (LR/PP rows read as claims), 4 claim-type issues | review: **7 claims, 4 kept rows** (other-line prefixes), 0 hard. **Facts:** no valuation date; a losses window ("Accident Date Range"), not a policy period | review: the best attempt read 0 claim rows (learn variance), no fact stated |
| `2004-detail-loss-report` | review: 4 claims, R-05/R-06/R-20 | review: **2 claims, 2 kept rows**, R-05/R-06/R-20 on the date fields. **Facts:** losses window; no policy number | review: **2 claims, 2 kept rows**. **Facts:** losses window; no policy number |
| `2010-detail-loss-report` | review: 3 claims, 4 missing | review: 17 claims, 18 missing. **Fact:** losses window ("Losses From") | review: 6 claims, 11 missing. **Fact:** losses window |
| financial summaries, policy summary, retention reports | review | review (policy summary: **fact** "no valuation date") | review (policy summary: **fact** "no valuation date") |

**Reading the result.**
- **The target state is reached for the two 2004 detail reports.** They go to review with a document fact in the reason, and their other-line rows are kept with the file prefix named. Nothing is published, because the reports do not print a valuation date or a policy period, and nothing in the system may invent one (Questions 2 in the report).
- **The 2010 report** is also limited by its source: a screenshot, with the policy number misread ("Policv Number") and claim numbers on a line of their own.
- **Run-to-run variance** of the 5-iteration learn moves the rows read (claim listing 7 → 0, 2010 detail 17 → 6). Routes are unchanged.

## 5. Live environment and mock re-capture

- **SQL.** `lossrun-db plan` showed 2 changesets pending (0018, brief 21) and 36 applied. Every live upload failed at `ParseFile` with `Invalid object name 'dbo.GlossaryAliasEvidence'`, found from the Application Insights exceptions. `lossrun-db deploy` applied 0018, then 0019.
- **Function App.** Deployed from `47c1f1c`, then `b0ab3d8`.
- **Upload.** `src/TestShared/fixtures/lossrun-mixed-lines.csv`, uploaded under Q-DEMO-23B, replays the seen `synthetic-mutual-csv@1` with 0 model calls:
  - 4 claims, one of them open and report-only (status X);
  - 1 general-liability row kept, listed on the manifest item.
- **Capture.** `capture-mock.mjs` (quotes Q-DEMO-11, Q-DEMO-11B, Q-SAMPLE-10, Q-DEMO-23B) skips page images for a spreadsheet source (the API answers 409) instead of failing.
- **Earlier test uploads.** Three uploads under Q-DEMO-23 and Q-DEMO-23-CHECK stay in the environment, for the audit trail. Two failed before the database fix; one processed before changeset 0019 and so has no manifest kept rows. They are not in the capture.
- **UI.**
  - **Upload:** a loss run's kept rows, as a disclosure table.
  - **Document:** a "Rows of other lines kept, not extracted" panel per policy.
  - **Claims ledger:** status "Open · report only".
  - **Analytics:** KPI "Claims with financials".
  - **Classification word:** "Loss analysis report".
- **Checks.** axe in both themes on the new Document state, 0 violations. The a11y, keyboard and golden e2e suites pass: 49, plus the 2 new cases.

## Spend

The brief 23 budget is $6.

| Run | Metered |
|---|---:|
| Corpus v5 gated run | $3.67 |
| c10 re-run | $0.18 |
| Travelers run6 | $0.61 |
| Travelers run7 | $0.64 |
| Live uploads (CSV replay, 0 model calls) | $0.00 |

Total **$5.10** of $6. Cached Content Understanding pages still meter, so metered figures overstate what was paid.
