# Real-sample fitness run (2026-10-24)

The first contact with loss-run layouts nobody generated: 17 public sample documents, redacted copies only, run on the **local runner**. This is a defect-finding run, not an accuracy measurement: none of the samples has keyed truth.

**What it shows:**
- **The pipeline is safe on unknown layouts.** No document was published, and no wrong value went through silently: everything unknown went to review.
- **It is not yet *useful* on them.** The main causes are glossary gaps and header-composition bugs, which are fixed here, and layouts and one rule scope that need design decisions, which are proposed below.

## How it was run

| | |
|---|---|
| Documents | 17 of the 27 redacted sample copies (`samples/`, local only). **Eligible:** a recorded or named source in `samples/provenance.json` (9), or a public origin evident in the document itself (8: RFPs and addenda, a state workers' comp court filing, a state agency valuation, a carrier's published manual). The other 10 have an unknown source and wait for the provenance file |
| Pages | 532 (46 MB); 3 files scanned at 150 dpi (one of them a multi-carrier bundle), 1 mixed scan |
| Runner | `lossrun eval run --mode local --concurrency 2`, learn cap 1. Content Understanding and Azure OpenAI are called directly. The format registry, raw results and cost meter are in memory: **nothing went to Blob, SQL, AI Search, the quote list or the demo host**. Learned specs, graphs and results stay in `eval-runs/` (ignored) |
| Runs | **Run 1:** before the fixes, 5 learn iterations.<br>**Run 2:** after the fixes, from the new `--cu-cache` (Content Understanding not called again), with `LOSSRUN_LEARN_MAX_ITERATIONS=3` to stay inside the $5 cap. Routes and header resolution compare directly; learn attempts are 3 against 5 |
| Spend | **$4.47.** Run 1: $3.94 ($3.19 Content Understanding, $0.75 models). Run 2: $0.53 (models only) |
| Privacy | The documents are the redacted copies. Azure OpenAI abuse monitoring cannot be switched off on this subscription, so prompts may be kept by Microsoft for up to 30 days. Nothing else left the machine |

## Results

| File | Carrier (folder) | Input | Pages | Route, run 1 → run 2 | Hard rules (run 2) | Headers resolved, run 1 → run 2 | Cost: run 1 / run 2 (models) |
|---|---|---|---:|---|---|---|---|
| alvin-isd-loss-runs | Multi-carrier bundle | scan 150 dpi | 22 | 7 review → 7 review | — | 104/162 → 128/157 | $0.139 / $0.007 |
| axis-cameron-county | AXIS Insurance | native | 4 | 1 review → 1 review | — | 12/26 → 18/26 | $0.094 / $0.045 |
| chubb-weslaco-isd | Chubb | scan 150 dpi | 6 | **none → 1 review** | — | 0/0 → 9/18 | $0.038 / $0.002 |
| collin-county-wc | Self-insured county | scan 150 dpi | 49 | none → none | — | — | $0.310 / $0.016 |
| liberty-mutual-mercedes-isd | Liberty Mutual | native | 73 | 2 review → 2 review | R-14, R-19 | 13/19 → **19/19** | $0.509 / $0.053 |
| marin-exhibits | Self-insured county | native | 20 | none → none | — | — | $0.125 / $0.005 |
| starr-cameron-county | Starr | native | 1 | 1 review → 1 review | R-14 | 10/15 → **15/15** | $0.065 / $0.034 |
| stcharles-wc | Self-insured city | native | 2 | 1 review → 1 review | — | 4/13 → 8/13 | $0.073 / $0.037 |
| okc-ownrisk-wc-selfins | Self-insured city | native | 13 | none → none | — | — | $0.082 / $0.004 |
| sf-hacp-wc-add1 | Housing authority | native | 33 | 1 review → 1 review | — | 37/251 → 42/247 | $0.291 / $0.067 |
| cccta-wc-addendum1 | RFP document | native | 7 | none → none | — | — | $0.045 / $0.003 |
| point-isabel-isd | RFP document | native (mixed scan) | 77 | 3 review → 3 review | R-14, R-19 | 40/53 → 46/53 | $0.655 / $0.117 |
| laccd | RFP document | native | 64 | 1 review → 1 review | R-14 | 2/9 → 3/9 | $0.440 / $0.053 |
| ontario-wc-rfp | RFP document | native | 18 | none → none (correct) | — | — | $0.112 / $0.004 |
| oswego-tpa-rfp | RFP document | scan 300 dpi | 38 | none → none (correct) | — | — | $0.237 / $0.009 |
| sorm-wc-valuation | State risk management | native | 80 | none → none (correct) | — | — | $0.511 / $0.031 |
| travelers-loss-reports-manual | Travelers | native | 25 | 1 review → 1 review | — | 18/51 → 25/50 | $0.215 / $0.047 |

**Totals:**
- **Extracted loss-run documents:** 18 → 19.
- **Files with no loss-run document:** 8 → 7. Four of the 7 are correct (three non-loss-run documents and an actuarial valuation).
- **Headers resolved across extracted documents:** 240/599 (40%) → 313/607 (52%).
- **Routes:** every extracted document ends in **review**; none replays, none publishes a learned spec.

## Causes, by category

Every document that went to review or produced nothing, with its cause in one line.

**(a) Glossary gap: fixed.** 22 aliases and 4 composite component words were added to `docs/evaluation/glossary.json` (0.1.2 → 0.2.0), each with `source: "real-sample <file>"`:
- dates: Close Date, Incident Date, Date Received, Inception Date, Policy Eff/Exp Dt;
- descriptions and causes: Description of Loss/Claim, Loss Cause;
- identity: Claim Reference, Claimant(s), Claim State;
- recoveries meaning subrogation: Subrogation Recovery, Recovery, Recovered, Recovered Loss, Total Recoveries;
- totals and counts: Total Gross Incurred, Total Nbr Open/Closed;
- expense: ExpensePaid, Expenses / Fees Paid;
- component words: **Loss** and **Losses** as indemnity (the P&C convention: loss payments, expense apart), and **LAE** and **Expenses** as expense, so that "Loss Paid", "Loss Reserve", "LAE Paid" and "Expenses Paid" resolve by the composite grammar.

Effect: headers resolved 40% → 52%; Liberty's and Starr's tables now resolve completely.

**(a) Glossary gap: not added, on purpose.**
- "Handler" and "Examiner" columns: staff, not a canonical field, and should stay unextracted.
- Bare "Medical" or "Expense" without a measure: ambiguous between paid and incurred.
- Deductible, reinsurance, SIF and "other" recoveries: the canonical schema has only subrogation; see proposal P5.

**(g) Tool bug: fixed.** Header composition, in `CuResultMapper.ComposeHeaderTexts`:
- **Report titles in headers.** A banner header cell spanning the whole table (a report title or date range) was prepended to every column's header, so known headers stopped resolving. It is now dropped.
- **Totals rows as headers.** Header cells holding values (an amount, a date, a figure), from a totals row Content Understanding read as a header row, are dropped too.

Group headers over some columns stay. There are 3 tests. Effect: Chubb's loss-run segment now classifies; part of the 40% → 52%.

**(g, upstream) Content Understanding table structure: proposal P4.**
- `sf-hacp-wc-add1`: adjacent column headers merged into one header cell spanning several columns; 0 claim rows after 3 attempts.
- `okc-ownrisk-wc-selfins`: the form's labels above a 4-row claim table merged into its header row, so the segment is rejected (`notLossRun`).

**(b) Layout the spec language cannot express: proposals P1–P3.**
- `stcharles-wc`, `cccta-wc-addendum1`, `marin-exhibits`: **summary-only loss runs** (rows by fiscal or report year, counts and totals, no claims). St Charles's 36 year rows are extracted as "claims"; cccta and Marin produce no document.
- `axis-cameron-county`: claim records printed on two physical lines, summary-then-detail per policy term; 0 claim rows.
- `liberty-mutual-mercedes-isd` (pages 72–73), `point-isabel-isd` (pages 22–24): nested records where the claim number sits on its own line, so **R-19** fires on rows whose claim number was not captured.
- `collin-county-wc`: each printed column stacks two fields (claim number over claim type, claimant over status), so composite headers do not resolve and the segment is rejected.

**(c) Classifier miss: proposal P6.**
- `laccd` (64 pages) and `travelers-loss-reports-manual` (25): Content Understanding says "Other", but the fallback ("loss-run headers and text density") sends the **whole document** to learn. That is two RFP or manual documents learned as loss runs: 6 model calls each in run 1, without result.
- `okc` and `collin`: real loss tables rejected (see (g, upstream) and (b)).

**(d) OCR / scan quality: by design, plus one process fix.**
- `alvin-isd-loss-runs`: a genuine 150 dpi scan. The learn-from-scan policy (≥ 280 dpi) sends it to review with pre-fill; page 12 also has low OCR confidence. This is correct behaviour.
- `chubb-weslaco-isd`, `collin-county-wc`: 150 dpi as well, but **these scans are our own rasterized intermediates** (`samples/_intermediate/`), made before redaction. P7: re-rasterize at 300 dpi.

**(e) Rule too strict: proposal P8.**
- **R-14** ("'No losses' statement means zero claim rows") is document-scoped. Real loss runs print "no claims for this policy year" for some periods and claim rows for others.
- `starr-cameron-county` ties all 9 printed totals with 5 claim rows and fails **only** R-14. Liberty, Point Isabel and laccd fail it too.

**(f) Genuine source inconsistency:** none confirmed. One learn attempt on `sf-hacp-wc-add1` reported that totals do not tie; it was not reproducible across attempts.

**Correct outcomes:** `sorm-wc-valuation` (actuarial triangles, rejected as not a loss run), `ontario-wc-rfp` and `oswego-tpa-rfp` (RFP text, no loss table).

## Proposals (no code change without an ADR where marked)

| # | Proposal | Files | ADR |
|---|---|---|---|
| P1 | **Summary-only loss runs:** a period-summary record (year, counts by status, paid / reserve / incurred) instead of forcing claim rows | stcharles-wc, cccta-wc-addendum1, marin-exhibits | Yes: spec language and canonical schema |
| P2 | **Multi-line claim records:** a record that spans two printed lines, keyed by the claim-number line | axis-cameron-county, liberty-mutual-mercedes-isd, point-isabel-isd | Yes: spec language |
| P3 | **Stacked two-field columns:** resolve "A over B" headers to two fields, and read the two values per cell in order | collin-county-wc | Yes: spec language |
| P4 | **Header repair for merged Content Understanding cells:** split a merged header cell by known aliases; strip "Label:" fragments carried in from a form above the table | sf-hacp-wc-add1, okc-ownrisk-wc-selfins | No: ingestion code, like the composition fix |
| P5 | **Non-subrogation recoveries** (deductible, reinsurance, SIF, other) as canonical fields, or an explicit "ignored" list | stcharles-wc, alvin-isd-loss-runs | Yes: canonical schema |
| P6 | **Classifier fallback:** require at least one table at or above the review threshold, and limit the fallback to the pages that carry it, before sending an "Other" document to learn | laccd, travelers-loss-reports-manual | No (ingestion option), but measured on the synthetic corpus first |
| P7 | **Re-rasterize intermediates at 300 dpi** before redaction, and record dpi in `provenance.json` | chubb-weslaco-isd, collin-county-wc | No: sample-preparation process |
| P8 | **R-14 per policy period** (`period.noLossesStatement` → that period has zero claims) | starr-cameron-county, liberty-mutual-mercedes-isd, point-isabel-isd, laccd | Yes: rule catalog |

## Tool changes made for this run

- **`eval run --cu-cache <dir>`** (local mode) keeps each Content Understanding result on disk, keyed by analyzer, content type and the file's SHA-256. A re-run replays the whole pipeline, glossary included, without paying for Content Understanding again. 1 test.
- **`LOSSRUN_LEARN_MAX_ITERATIONS`** overrides the PDF learn budget for one run.
- **`CuResultMapper.ComposeHeaderTexts`:** the header composition fix above.
