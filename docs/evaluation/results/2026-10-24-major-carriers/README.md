# Major-carrier formats from published samples (brief 22)

> Samples the carriers published themselves, for fictitious insureds. Nothing here is a client document or a real insured's run. The files live in `samples/public-carriers/` (gitignored, covered by the sample guard), with URL, retrieval date, SHA-256 and a licence note in `samples/provenance.json`. This page names files, report types, rule ids and field names only. It quotes no document values.

## Bottom line

- **Only one of the 15 carriers publishes sample loss runs: Travelers.** Two public agent guides contain 13 sample report pages in two generations (2004 and 2010). State Fund publishes definitions of loss-run terms but no sample run. The other 13 carriers publish nothing usable, so they are skipped, not substituted.
- **None of the 13 Travelers reports verifies within 5 learn iterations in the final run.** All 13 route to review. The validators catch the mis-reads: R-06, R-10, R-12–R-14, R-19, R-20, R-26. One earlier run published a wrong spec; that is fixed (see "One wrong publish" below).
- **One layout is not in the open-item list: measures as rows.** The Travelers Detail Loss Report prints three rows per claim (Inc, Pd, O/S), under the columns Total / Claim / Medical / Expense. That is the main reason its claim reports fail, and it is the first thing to fix for the largest national carrier.
- **Glossary 0.4.1 → 0.4.2.** Changes:
  - 10 header aliases;
  - 2 claim-type value aliases (Travelers' published file prefixes);
  - 5 ignored headers.
- **One wrong publish, caught here and fixed.** With glossary 0.4.2 the 2004 Frequency Analysis published (2 calls, `pdf-totals` gate). Its spec read two analysis columns, *Average Cost Per Claim* and *Largest Loss*, into recovery fields, and also listed both as ignored headers.
  - **Fix:** a new lint makes a header that is both mapped and ignored an issue (`SpecLint.ConstructIssues`), and both headers are now ignored in the glossary.
  - **Result:** on the re-run the report routes to review (R-26).
  - **Gate:** the lint changes the learn-prompt artefact (`01b56919bde3`), so corpus v4 was re-run under the gate.
- **Corpus v4 regression** (commit `0116174`, 326 documents): delivered accuracy and review rate are unchanged on every row of the deck table, and c7 improves from 5/6 to 6/6 delivered. See [Regression](#regression-corpus-v4-under-the-prompt-gate).

## Sourcing

| Carrier | Published sample found? | What was found, and why it was or was not used |
|---|---|---|
| **Travelers** | **Yes: two agent guides** | Two guides, both used:<br>• *CMS Loss Reports* manual (Sept 2004): sample reports on pp. 18, 20–24.<br>• *Loss Reports Quick Guide* (March 2010): report images on pp. 16–22, and a data-item glossary on pp. 26–36.<br>Both are public PDFs on `apps.travelers.com`, and their samples are for fictitious demo insureds. The whole guides are kept `reference-only`, because p. 7 of the 2004 guide shows an agency account-list screenshot. The report pages were excerpted one report per file (13 files, `use: local-testing`). |
| The Hartford | No | — |
| Liberty Mutual | No | Report names only: Loss History, Itemized Statement of Loss, Multi-Line Detail. No sample. |
| Zurich | No | The only Zurich run found is a real county's run in a public bid packet. **Excluded** by the rule against real insureds' runs. |
| AmTrust | No | Portal sell sheets only. |
| Zenith | No | — |
| ICW Group | No | No sample. Its loss-run field names were already in the glossary as `research:B1-guides`. |
| Employers | No | — |
| Berkshire Hathaway Homestate (incl. biBERK) | No | — |
| Chubb | No | The only Chubb run found is a school district's run in a bid packet. **Excluded**. |
| AIG | No | IntelliRisk "Standard Loss Run" is described, but no sample is published. |
| CNA | No | — |
| Sentry | No | — |
| Republic Indemnity | No | — |
| State Compensation Insurance Fund | **Terms only** | The broker page "Loss Run Information and Terms" defines the terms (indemnity, medical-only, total incurred losses, total incurred expenses), but publishes no sample run. Its definition of *Total Incurred Losses* was used as a glossary source. |

Searches: carrier sites (domain-restricted), agent/broker guide and "how to read your loss run" queries, and regulator/court exhibits. A "—" means no candidate document turned up at all.

## Run

- **Command:** `lossrun eval import` of the 13 report files, all with carrier `Travelers`. Then `lossrun eval run --mode local`.
- **Learn settings:** 5 learn iterations (`LOSSRUN_LEARN_MAX_ITERATIONS=5`). Learn cap 1: the local runner serialises learns through a one-slot gate.
- **Infrastructure:** Content Understanding results cached under `eval-runs/public-carriers/cu-cache`. The run is at `eval-runs/public-carriers/run2` (local, not committed).
- **Prompt and model:** `promptVersion` learn-v9; prompt artefact `d50df32a630c…`, which is unchanged by this brief. Learn model `gpt-5.4-mini` (configuration).
- **Spend:**
  - $0.30 for the first pass on the two whole excerpts (`run1`);
  - $0.90 for the 13 reports (`run2`);
  - $0.88 for the re-run with glossary 0.4.2 (`run3`);
  - $0.91 for the re-run with the new lint (`run4`).

  The carrier runs total **$2.99 of the $5 budget**. The two corpus v4 regressions meter $3.41 and $3.35 apart from that. Cached Content Understanding pages still meter in the run report, so those figures overstate what was paid.

Each report is one page. The 2004 reports are native text. The 2010 reports are low-resolution screenshots placed on a guide page, with the guide's letter callouts ("A Line of Insurance", "B Policy Number", …) printed into the header cells. The 2010 files import as native PDF because the guide page around the image is text.

### Per-report table (Travelers)

Routes: every report is a first-seen format, so it goes `Learn`, then `Review` after 5 attempts (`learn exhausted 5 iteration(s) without a verified spec`). No report was kept-not-processed. The line-of-business step reads every report as workers' comp, or as unknown on one 2010 retention report. Rules are those fired on the best attempt. "Missing" lists field names only.

| Report (file `travelers-…`) | Kind | Rows read (claim / subtotal / summary) | Best attempt | Rules fired | Open items exhibited |
|---|---|---|---|---|---|
| `2004-detail-loss-report` | claims | 4 / 2 / 0 | Missing: period policy number and dates, `claim.indemnity.paid` and `claim.medical.paid`. 9 issues: the claim-type values (file prefix) and status not normalised; the accident-cause code taken as the class code. | R-06 ×4 (accident after the valuation date: the valuation was read from the wrong label), R-20 (a measure row read into the wrong measure) | **measures as rows** (new); two-line records (layout evidence `records-rows`); period totals with claim rows; claim-number pattern |
| `2010-detail-loss-report` | claims | 18 / 1 / 0 (totals tie, 3 checks) | Claim number missing on 15 of 18 rows; period dates unparsed. 14 issues. | — | **measures as rows**; two-line records (`records-rows`); period totals with claim rows; claim-number pattern |
| `2004-detail-claim-listing` | claims | 11 / 0 / 0 | Missing: carrier, valuation, period fields, `claim.indemnity.paid` and `claim.medical.paid`. 13 issues, 11 of them claim type (file prefix). | — | component headers (`Incurred/Paid/Outstanding CLM+MED+EXP`, resolved as totals); no-medical-column (only totals are printed) |
| `2004-detail-changes-only` | claims (changes since the last report) | 12 / 0 / 0 | Claim number missing on 6 of 12 rows; valuation and period fields missing. | R-14 (a "no losses" reading beside 12 claims), R-19 (blank claim number), R-20 | stacked cells (claim number and claimant in one cell); claim-number pattern; measures as rows |
| `2010-detail-changes` | claims (changes) | 24 / 1 / 0 | Claim number missing on 19 of 24 rows; `claim.total.incurred` missing. 32 issues. | R-20 ×2 | stacked cells (`Claim Number Claimant`); claim-number pattern; measures as rows |
| `2010-insured-retention-detail` | claims (retention split) | 0 / 0 / 0 | No table qualified. The guide's letter callouts sit inside the header cells. | — | "Loss" composite (`Paid Loss`, `Reserve Loss` beside Expense, no medical column); no-medical-column |
| `2004-financial-summary` | summary by line of insurance | 0 / 0 / 7 | Clean: 0 hard, 0 missing, 0 issues, coverage 100 %. **Not publishable**: the PDF gate needs a printed total that reconciles, and none was checked (0 checks). | — | summary-only; component headers |
| `2010-financial-summary` | summary by line of insurance | 0 / 0 / 9 | Carrier missing. 5 issues: two columns both mapped to the period label, and counts printed with decimals. | — | summary-only; component headers |
| `2004-frequency-analysis` | summary by region | 0 / 1 / 11 | Paid and reserve were mapped to columns that are not paid or reserve (percent, average, largest loss). The report prints neither. | **R-26** (report total ≠ sum of periods): this caught the mis-mapping | summary-only; component headers |
| `2010-policy-summary` | summary by policy | 0 / 0 / 0 (3 periods) | Period dates unparsed: the years are printed as "Policy Years From … To …" inside a header row. A constant valuation date was linted. | — | summary-only; account and policy-year text inside the header rows |
| `2010-insured-retention-summary` | summary by policy (retention) | 0 / 0 / 0 | No table qualified (letter callouts in the headers). | — | summary-only; "Loss" composite; no-medical-column |
| `2004-claim-lag-analysis` | analysis (lag buckets) | 0 / 0 / 9 | The lag buckets were read as periods. | — | not a loss run (an analysis report); see Questions in the report |
| `2010-claim-lag-analysis` | analysis (lag buckets) | 0 / 1 / 5 (totals tie, 1 check) | The same, plus a `derivedRules` path lint. | — | not a loss run |

**Re-runs.**

`run3`, with glossary 0.4.2: 12 reports route to review, and `2004-frequency-analysis` was **published wrongly** (see Bottom line).

`run4`, with the mapped-and-ignored lint (commit `0116174`): all 13 route to review, and the Frequency Analysis now fails R-26. Changes against `run2`:

- **`2004-detail-claim-listing`:** issues fall from 13 to 0, and missing fields from 7 to 2. The `CB`/`CM` file prefixes normalise and the carrier and valuation are found. What is left is that the report prints no indemnity or medical column.
- **`2004-detail-loss-report`:** the accident cause no longer lands in the class code. The measure rows are still mis-read: 8 rows were read as claims (R-20). This is the measures-as-rows item.
- **Detail changes reports:** they now fire R-10, R-12, R-13, R-14 and R-26 as well. The learner reads more rows, so more mis-reads are caught. They are still review.

A learn at 5 iterations varies from run to run: rows read and fields missing move by a few between runs. The routes did not change, except the one wrong publish. The table above is `run2`.

### Which open layout items the major carriers exhibit ("what matters first")

Ranking: the number of the 15 brief carriers whose layouts show the item. Travelers comes from its published samples (this brief). Liberty Mutual and Chubb come from their runs already held from briefs 17/19, which were not re-sourced here and are counted only to rank. Ties are ordered by how many of the 13 Travelers report types show the item.

| Rank | Open item | Carriers | Travelers report types | Status, and what to do first |
|---:|---|---|---:|---|
| 1 | **Claim-number pattern** (claim number on a line other than the amounts line, or in a stacked cell) | Travelers, Liberty Mutual | 4 | **Open; first fix.** Travelers loses the claim number on most rows of the detail reports: 15/18, 19/24, 6/12. Liberty's `lineFields` pattern matched none of its lines (brief 19 follow-up). |
| 2 | **Measures as rows** (Inc / Pd / O/S rows per claim under Total / Claim / Medical / Expense) | Travelers | 4 | **New, not in the open-item list.** It is Travelers' standard claim layout in both generations. The glossary reads the columns wrongly here: `Total` → claim count and `Claim` → claim number. In this layout `Claim` is the indemnity component, as in Travelers' `CLM+MED+EXP`. It needs a construct like ADR 0018's: the measure from the row label, the component from the column. |
| 3 | **Two-line records** | Travelers, Liberty Mutual | 2 | Construct exists (ADR 0018). Layout evidence `records-rows` fires on both Travelers detail loss reports. The learner did not reach the gate within 5 attempts; it is held up by item 2. |
| 4 | **Stacked cells** | Travelers, Chubb | 2 | Construct exists. Travelers puts the claim number and claimant together in one cell on the changes reports; the claim number is then lost (item 1). |
| 5 | **Summary-only** | Travelers | 6 | Supported (ADR 0017). Five Travelers summary reports read cleanly or nearly so. **But a native PDF summary publishes only if a printed total reconciles** (ADR 0007 gate). The 2004 Financial Summary is otherwise clean and still routes to review. |
| 6 | **Component headers** (`CLM+MED+EXP`) | Travelers | 6 | **Closed** (brief 19 Q5): `Incurred/Paid/Outstanding CLM+MED+EXP` resolve as totals on every Travelers report. |
| 7 | **No medical column** | Travelers, Starr (not on the brief's list) | 3 | Handled by `absentComponents` (brief 19 follow-up). Travelers' claim listing and retention reports print totals or loss + expense only. |
| 8 | **"Loss" composite** | Travelers, State Fund (definition) | 2 | **Conflict found.** Travelers' retention reports print `Paid Loss` / `Reserve Loss` beside Expense, with no medical column, so the reading is ambiguous and routes to review (correct). State Fund defines *Total Incurred Losses* as the total including expenses, while the conditional rule would read it as indemnity. Fixed with an explicit alias (glossary 0.4.2): `Total Incurred Losses` → `claim.total.incurred`. |
| 9 | **Period totals with claim rows** | Travelers | 2 | Construct exists (ADR 0018 amendment 3). Policy subtotals and the report total were read on both detail loss reports; the 2010 one ties 3 checks. |

Two layout features come from the guides, not from loss runs: letter callouts inside header cells, and screenshots at low resolution. A carrier would not print them; they make the 2010 retention reports unreadable here and say nothing about real Travelers output.

## Glossary seeds (0.4.1 → 0.4.2)

Sources `public sample Travelers` (headers printed in the sample reports, and the guide's data-item glossary) and `public sample State Compensation Insurance Fund` (the broker terms page).

| Field | Header / value | Source |
|---|---|---|
| `document.lineOfBusiness` (read by `claim.lineOfBusiness`) | `Line of Insurance` | Travelers |
| `document.reportTotals.claimCount` | `# of Claims` | Travelers |
| `period.effectiveDate` | `Policy Eff Date` | Travelers |
| `claim.dateReported` | `Notice Date` | Travelers |
| `claim.status` | `O/C` | Travelers |
| `claim.causeOfInjury` | `Accident Cause` | Travelers. On `2004-detail-loss-report` this column had been read as the class code. |
| `claim.accidentDescription` | `Description of Accident` | Travelers |
| `claim.jurisdictionState` | `Benefit State` | Travelers ("the state whose laws are controlling the benefit level") |
| `claim.claimType` | header `File Prefix`; values `CB` → indemnity, `CM` → medical only | Travelers (guide data items) |
| `claim.total.incurred` | `Total Incurred Losses` | State Fund: paid + outstanding medical and indemnity plus expenses. The explicit alias overrides the conditional "Loss" rule. |
| ignored | `Adj Off`, `Percent Paid/Incurred`, `Percent To Total Claims`, `Average Cost Per Claim`, `Largest Loss` | Travelers |

**Learned aliases:** none. ADR 0019 learns aliases only from a verified document, and no Travelers report verified.

## Regression: corpus v4 under the prompt gate

- **The run:** `regression/` holds the deck table, the scoring report and `run.json`. The run is `gitSha` `0116174…`, an ancestor of the commit that adds this folder. It used learn-prompt artefact `01b56919bde3…` over 326 documents, which is what `tools/prompt-gate.py --pr` requires.
- **Comparison** with the last gated run (`2026-10-24-learn-loop-quality/follow-up-3`):
  - Delivered key-field accuracy and review rate are identical on every input-type row.
  - **c7, one table per claim, improves:** 5/6 → 6/6 delivered (100.0 %, n = 858), and the review rate 16.7 % → 0 %.
  - Classification precision and recall are 100.0 % (n = 326).
  - The learn calls per document move within run-to-run variance.
- **An earlier regression** run before the lint, on glossary 0.4.2 alone (`eval-runs/run-v4-22`, not committed), gave the same deck table.

## Follow-up (brief 22 Answers): corpus v5 under the gate

`follow-up/` holds the deck table, scoring report and `run.json` of the corpus v5 run.
- **The run:** commit `09eef63`, learn-prompt artefact `228649bbbe36` (learn-v10), 332 documents.
- **New construct row, measures as rows (c10):** 5/6 delivered at 100.0 % (n = 990). The sixth document was split by the classifier into three segments and is review.
- **Every other row** keeps its delivered accuracy and review rate.
- **Travelers re-run:**
  - **Kept, not processed (ADR 0021):** five reports, the two lag analyses, the frequency analysis and both changes reports.
  - **Measures mode:** both detail loss reports now learn in records mode `measures`.

Details, with routes before and after, are in the follow-up section of the major-carrier formats report.
