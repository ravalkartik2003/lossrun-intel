# Learn-loop quality: results (2026-10-24, brief 20)

The corpus is corpus v4 at generator **corpus-gen-0.6.0**, seed 8, 326 synthetic documents (`lossrun eval generate --seed 8 --v4`). Since 0.5.0 only c7 changed, printing claim numbers in three shapes. The learn prompt is learn-v8. The real-sample section names files, carriers' folder labels, rule ids, field names and counts only.

**In one line:**
- c7 now delivers 5 of 6 documents (was 3 of 6) and c8 6 of 6 (was 1 of 6), with every repair layout-generic.
- The v3 rows of the deck table reproduce (`scoring-protocol.md` §8).
- On the real samples, blank claim numbers (R-19) are cleared on Liberty and Point Isabel.
- No delivered value is wrong anywhere.

## Files

| Path | What it is |
|---|---|
| `deck-table.md`, `report.md`, `report.json`, `documents.csv`, `field-by-carrier-input.csv`, `run.json` | The final corpus v4 run (`run.json` records the build: the brief 20 commit plus the fixes listed under "Run 2") |
| `first-run-deck-table.md` | The first corpus v4 run, before the three fixes it surfaced |

## 1. Why c7 and c8 did not learn first time (brief 19 v4 run)

Every saved attempt of the 9 unverified c7/c8 learns was replayed offline. That costs nothing: no model call, deterministic. None failed on the constructs themselves. They failed on generic spec mistakes, which the gate rightly refused:

| Cause | Seen on | Repair (`SpecLint.Quality.cs`, ADR 0018 amendment 2) |
|---|---|---|
| The document-field regex copied the prompt's markup (`^#`, the paragraph role "pageFooter:") | c8 | The markup is removed from the regex |
| The regex anchored on text CU had merged into one paragraph ("Workers' Compensation Loss Run Insured: …"), so the next document of the layout did not match | c8 | Regexes also run on the printed lines rebuilt from the OCR words, parts first. A regex is re-anchored on the value's own label |
| The capture was this document's literal value ("(?<value>Kappa Foods \(synthetic\))") | c8 | The literal is generalized when the generalized regex reads the same value |
| An optional field the run does not print (report run date, line of business) blocked the gate | c7, c8 | The field is removed when nothing it reads normalizes. Required fields are kept |
| A text normalizer on an enum target ("Medical Only" into claim type) | c7 | The normalizer is fitted to the target's type |
| `^Policy Total:\b`: a `\b` after a colon never matches before a space, so total rows were read as two claims with no number | c7 | The `\b` after punctuation is removed |
| A stacked status part mapped to `status` and `rawStatus`; rejected by the lint, the model moved `rawStatus` to a part 2 that does not exist | c8 | `rawStatus` may share the status part |

With these repairs every one of the 10 c7/c8 learns verifies offline, with 0 wrong key fields against truth. Each repaired spec also replays every other document of its carrier and passes the gate (a 10 × 6 matrix, all pass).

## 2. Corpus v4 runs

**Run 1** (the brief 20 commit):
- c8 delivered 6 of 6; c7 2 of 6.
- It surfaced three more generic causes, each fixed and verified offline before run 2:
  - **c7.** The c7 records whose lines CU drew inside the claim table were read as single-part lines, so "claim number | file number" never split. Table-drawn lines now keep their parts. The run-1 c7 spec then replays all 6 documents with 0 wrong.
  - **c4 XLSX.** Two learns wrote a regex over a label cell whose value sits in the next cell. This followed the learn-v8 schema text, which mentioned only paragraphs and lines. Two changes:
    - the schema text now says "a label and its value in two cells is labelRight";
    - a `labelRight` repair checks that one label cell locates only one field.
  - **c6.** Five attempts put every line-1 amount under the line-0 column to its left. Line-n entries are now set under the column their composed header names ("Loss State: Ind Paid:").

**Run 2** (final; the brief 20 commit plus the run-1 fixes): the deck table, with the v3 rows checked against `../2026-10-23-corpus-v3/deck-table.md`:

| Row | Committed (v3) | Run 2 | Reproduces (§8) |
|---|---|---|---|
| native PDF, recurring template | 100.0 % delivered, 3.1 % review | 100.0 %, 3.1 % | yes |
| XLSX / CSV | 100.0 %, 1.3 % review (the planted inconsistent workbook) | 100.0 %, 1.3 % | yes |
| scan ≥ 300 dpi | 100.0 %, 4.2 % review | 100.0 %, 4.2 % | yes |
| scan 200 dpi rotated | 100.0 % delivered, 27.1 % review, pre-fill 99.8 % | same | yes |
| fax < 200 dpi | review by design, pre-fill 89.1 % | 89.0 % [86.1–91.4] | yes (within CI) |
| narrative statement | review by design | same | yes |
| first-seen (n = 1) | — | not delivered, 5 calls | excluded by §8 (first-seen comes from the five-trial study) |

| Construct row | Brief 19 v4 | Run 2 |
|---|---|---|
| two-line records (c6) | 6 / 6 delivered | 6 / 6 (100.0 %, n = 1,278) |
| one table per claim (c7) | 3 / 6 | **5 / 6** (100.0 %, n = 744); c7-000 did not verify within 5 attempts, and c7-001 learned in 2 |
| stacked cells (c8) | 1 / 6 | **6 / 6** (100.0 %, n = 1,026) |
| component-sum headers (c9) | 6 / 6 | 6 / 6 |

- All 9 source-inconsistent (planted) documents were kept out of straight-through; classification was 100 / 100 (n = 326).
- No delivered value is wrong.
- Learn calls per document fell on every construct row, and on XLSX/CSV from 0.088 to 0.062.

## 3. Real samples (local runner; AXIS, Liberty, Point Isabel)

Run 6 against run 5 (brief 19 follow-up). Content Understanding came from the cache.

| File / segment | Run 5 | Run 6 | What still blocks |
|---|---|---|---|
| liberty-mutual-mercedes-isd pp. 72–73 | review: R-14, R-19; coverage 55 %; no totals checked | review: **no hard failure**; totals tie (14 checks); coverage 82 % | Claim type is a property value, not in the WC claim-type vocabulary. Period dates do not parse. The run is another line of business |
| point-isabel-isd pp. 22–24 | review: R-19; coverage 91 % | review: **no hard failure, nothing missing**; totals tie; coverage 100 % | One issue: a property claim type (see Questions) |
| point-isabel-isd pp. 25–27 | review: 5 missing | review: R-09 on one attempt; 5 missing | A different (scanned) layout |
| axis-cameron-county | review: R-08, R-14; coverage 73 % | review: the best attempt read no claim rows; coverage 67 % | The model alternates between reading period rows and claim rows. A property loss run ("Building" claim types) |

**What the real samples show:**
- The claim-number repairs are confirmed: printed lines with parts, evidence in the prompt, and the lint showing unmatched records.
- The remaining blockers are another line of business, not layout: property claim types do not normalize into the WC claim-type vocabulary, so these runs correctly stay in review.
- On AXIS, document fields (carrier, valuation date) are now located from the printed lines (offline check of the run-5 attempt: required coverage 73 % → 91 %). Its learn is unstable between two readings, so run 6's best attempt is worse.

## Spend

About **$2.7** against the $4 budget:

| Item | Cost |
|---|---:|
| Model, v4 run 1 | $1.01 |
| Model, v4 run 2 | $0.74 |
| Model, real run 6 | $0.42 |
| Content Understanding, 26 uncached documents (6 regenerated c7 PDFs, and 10 password-locked PDFs per run, whose encryption salt changes) | under $0.6 |
