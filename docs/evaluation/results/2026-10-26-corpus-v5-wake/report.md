# Evaluation report — synthetic corpus

Run 2026-10-26 04:14–04:24 UTC · git `3228641349` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v11` · gate `learn-gate-0.3.0` · catalog `0.11.0` · prices `0.1.0` · corpus `corpus-gen-0.9.0` seed 8 · truth `corpus-gen-0.9.0 seed 8`

332 documents · 179,409 scored field values · metered cost $3.62

Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).

## Deck table — by input type (`docs/evaluation/results/2026-10-22-corpus-v2/`)

Every document is in exactly one row; no blended headline. Delivered = key fields (Claim #, DOI, indemnity/medical paid and reserve) on documents that left without a human. Review rate = documents routed to a human. Pre-fill = key fields of what the reviewer starts from on those documents. Committed rows carry a proposal target; the others carry the measured numbers.

Run 2026-10-26 04:14–04:24 UTC · git `3228641349` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v11` · gate `learn-gate-0.3.0` · catalog `0.11.0` · prices `0.1.0` · corpus `corpus-gen-0.9.0` seed 8 · truth `corpus-gen-0.9.0 seed 8`

> Synthetic corpus: generated documents from five fictional carriers, not client documents. Real carrier documents will be messier; the sealed blind set on the client's archive is the number that counts. Rows with fewer than 5 documents are an insufficient sample, not a result.

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| native PDF, recurring template | 65 | 100.0% [100.0%–100.0%] (n=13386, docs=63) | 3.1% [0.2%–11.5%] (n=65, docs=65) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 99% delivered — met |
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | — (none reviewed) | 5.000 | ≥ 98% delivered — insufficient sample (n = 1 document): no verdict |
| XLSX / CSV | 80 | 100.0% [100.0%–100.0%] (n=14778, docs=79) | 1.3% [0.0%–7.4%] (n=80, docs=80) | 100.0% [98.6%–100.0%] (n=336, docs=1) | 0.062 | ≥ 98% delivered — met |
| scan ≥ 300 dpi | 48 | 100.0% [100.0%–100.0%] (n=9990, docs=46) | 4.2% [0.4%–14.8%] (n=48, docs=48) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 95% delivered — met |
| scan 200 dpi rotated | 48 | 100.0% [99.9%–100.0%] (n=6948, docs=34) | 29.2% [18.2%–43.3%] (n=48, docs=48) | 99.8% [99.6%–99.9%] (n=3474, docs=14) | 0.000 | measured, not committed; review rate 29.2%, pre-fill 99.8% |
| fax < 200 dpi | 48 | — (none delivered) | 100.0% [91.2%–100.0%] (n=48, docs=48) | 88.9% [86.0%–91.3%] (n=10422, docs=48) | 0.021 | routed to review by design; pre-fill 88.9% |
| narrative statement | 12 | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0.0% [0.0%–0.2%] (n=1992, docs=12) | 0.000 | routed to review by design; pre-fill is the statement's paragraphs, no structured values |
| construct: two-line records (c6) | 6 | 100.0% [99.6%–100.0%] (n=1278, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.500 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: one table per claim (c7) | 6 | 100.0% [99.5%–100.0%] (n=858, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.333 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: stacked cells (c8) | 6 | 100.0% [99.5%–100.0%] (n=1026, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.500 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: component-sum headers (c9) | 6 | 100.0% [99.6%–100.0%] (n=1098, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.333 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: measures as rows (c10) | 6 | 100.0% [99.6%–100.0%] (n=1314, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.167 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |

Data-quality first pass and quote success by input type (source-consistent documents; rates on the delivered ones):

| Input type | Docs | DQ first pass (delivered) | Quote success (delivered) | Review rate | n delivered |
|---|---:|---|---|---|---:|
| native PDF, recurring template | 63 | 100.0% [93.1%–100.0%] (n=63, docs=63) | 100.0% [93.1%–100.0%] (n=63, docs=63) | 0.0% [0.0%–6.9%] (n=63, docs=63) | 63 |
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 1 |
| XLSX / CSV | 79 | 100.0% [94.4%–100.0%] (n=79, docs=79) | 100.0% [94.4%–100.0%] (n=79, docs=79) | 0.0% [0.0%–5.6%] (n=79, docs=79) | 79 |
| scan ≥ 300 dpi | 46 | 100.0% [90.8%–100.0%] (n=46, docs=46) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0.0% [0.0%–9.2%] (n=46, docs=46) | 46 |
| scan 200 dpi rotated | 46 | 100.0% [87.9%–100.0%] (n=34, docs=34) | 100.0% [87.9%–100.0%] (n=34, docs=34) | 26.1% [15.5%–40.4%] (n=46, docs=46) | 34 |
| fax < 200 dpi | 46 | — (none delivered) | — (none delivered) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0 |
| narrative statement | 12 | — (none delivered) | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0 |
| construct: two-line records (c6) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: one table per claim (c7) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: stacked cells (c8) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: component-sum headers (c9) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: measures as rows (c10) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |

## Requirement acceptance criteria (placeholders filled)

Delivered = values that leave without a human (a Review document delivers nothing). Pre-fill = the same measure on Review documents' pre-fill, what the reviewer starts from (scoring-protocol.md §3).

| Criterion | Delivered (no human) | Pre-fill (Review documents) | Proposed target |
|---|---|---|---|
| Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) — by input type, see the deck table | native PDF, recurring template 100.0% [100.0%–100.0%] (n=13386, docs=63); native PDF, first-seen template 100.0% [91.2%–100.0%] (n=48, docs=1); XLSX / CSV 100.0% [100.0%–100.0%] (n=14778, docs=79); scan ≥ 300 dpi 100.0% [100.0%–100.0%] (n=9990, docs=46); scan 200 dpi rotated 100.0% [99.9%–100.0%] (n=6948, docs=34); construct: two-line records (c6) 100.0% [99.6%–100.0%] (n=1278, docs=6); construct: one table per claim (c7) 100.0% [99.5%–100.0%] (n=858, docs=6); construct: stacked cells (c8) 100.0% [99.5%–100.0%] (n=1026, docs=6); construct: component-sum headers (c9) 100.0% [99.6%–100.0%] (n=1098, docs=6); construct: measures as rows (c10) 100.0% [99.6%–100.0%] (n=1314, docs=6) | native PDF, recurring template 100.0% [98.9%–100.0%] (n=432, docs=2); XLSX / CSV 100.0% [98.6%–100.0%] (n=336, docs=1); scan ≥ 300 dpi 100.0% [98.9%–100.0%] (n=432, docs=2); scan 200 dpi rotated 99.8% [99.6%–99.9%] (n=3474, docs=14); fax < 200 dpi 88.9% [86.0%–91.3%] (n=10422, docs=48); narrative statement 0.0% [0.0%–0.2%] (n=1992, docs=12) | committed rows: ≥ 99% native recurring, ≥ 98% native first-seen and XLSX/CSV, ≥ 95% scans ≥ 300 dpi |
| End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU) | p50 3.7 s, p95 8.8 s over 332 documents (deployed API figures: the API work) | — | ≤ 60 s p95 (proposed) |
| Auto-classification precision / recall | 100.0% [98.6%–100.0%] (n=332, docs=332) / 100.0% [98.6%–100.0%] (n=332, docs=332); bundle split exact 8/8 | — | ≥ 97% / ≥ 95% (A2) |
| Data-quality validation pass rate, first pass | 78.3% [73.5%–82.5%] (n=323, docs=323) — blended, mixes the by-design review rows (fax, narrative); not for the deck: use the by-input-type table | — | ≥ 85% (A2) |
| Quote success rate (priceable submissions) | 78.3% [73.5%–82.5%] (n=323, docs=323) — blended, mixes the by-design review rows; not for the deck: use the by-input-type table | — | ≥ 90% of valid uploads (proposed) |

## The evaluation harness targets

| Target | Segment | Threshold | Measured (delivered) | Result |
|---|---|---|---|---|
| Recurring-template delivered key-field accuracy | native PDF | 99 % | 97.6% [89.2%–100.0%] (n=17706, docs=85) | **not met** |
| Key fields | scan ≥ 300 dpi | 95 % | 91.5% [80.2%–97.0%] (n=10920, docs=51) | **not met** |
| Key fields | scan 200 dpi rotated | 90 % | 63.6% [48.6%–76.4%] (n=10920, docs=51) | **not met** |
| Key fields (reported, not promised) | fax 150 dpi | — | 0.0% [0.0%–0.0%] (n=10920, docs=51) | reported |
| DQ first pass (source-consistent) | excluding narrative | 85 % | 81.4% [76.6%–85.3%] (n=311, docs=311) | **not met** |
| STP | recurring templates | 85 % | 80.9% [76.0%–84.9%] (n=303, docs=303) | **not met** |
| STP (context: fax is reported, not promised) | recurring templates excluding fax | 85 % | 95.3% [91.9%–97.4%] (n=257, docs=257) | met |
| DQ first pass (context) | excluding narrative and fax | 85 % | 95.5% [92.2%–97.5%] (n=265, docs=265) | met |
| Recurring-template delivered key-field accuracy (context) | native PDF, source-consistent | 99 % | 100.0% [100.0%–100.0%] (n=17274, docs=83) | met |
| Learn/repair model calls per document | c1 + c2 | 0.3 | 0.029 per document (6 calls / 210 documents); target < 0.3 | met |

## Model calls per document by carrier (learn / delta / repair)

| Carrier | Docs | Calls | Per document | Docs with 0 calls |
|---|---:|---:|---:|---:|
| c1 | 110 | 0 | 0.000 | 110 |
| c10 | 6 | 1 | 0.167 | 5 |
| c2 | 100 | 6 | 0.060 | 98 |
| c3 | 12 | 0 | 0.000 | 12 |
| c4 | 40 | 3 | 0.075 | 39 |
| c5 | 40 | 2 | 0.050 | 39 |
| c6 | 6 | 3 | 0.500 | 5 |
| c7 | 6 | 2 | 0.333 | 5 |
| c8 | 6 | 3 | 0.500 | 5 |
| c9 | 6 | 2 | 0.333 | 5 |

## Thresholds (research A2) — pass/fail

| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |
|---|---|---|---|---|---|
| Claim # and DOI, normalized exact | native (PDF + XLSX/CSV) | 98 % | 96.4% [91.3%–98.7%] (n=11684, docs=179) | **fail** | no |
| Claim # and DOI, normalized exact | scan ≥ 300 dpi | 95 % | 91.5% [80.2%–97.0%] (n=3640, docs=51) | **fail** | no |
| Claim # and DOI, normalized exact | scan < 200 dpi / fax | 90 % | 31.8% [23.0%–42.2%] (n=7280, docs=102) | **fail** | no |
| Indemnity/medical paid and reserve per claim | native (PDF + XLSX/CSV) | 97 % | 96.4% [91.3%–98.7%] (n=23368, docs=179) | **fail** | no |
| Indemnity/medical paid and reserve per claim | scans | 93 % | 51.7% [43.1%–60.2%] (n=21840, docs=153) | **fail** | no |
| Claim-level completeness (all printed fields right) | native (PDF + XLSX/CSV) | 90 % | 96.4% [91.3%–98.7%] (n=5842, docs=179) | pass | yes |
| Claim-level completeness (all printed fields right) | scans | 80 % | 51.7% [43.1%–60.2%] (n=5460, docs=153) | **fail** | no |
| Straight-through (no human touch) | recurring template (seen + unseen repeats) | 80 % | 80.9% [76.0%–84.9%] (n=303, docs=303) | pass | no |
| Straight-through (no human touch) | first-seen template | 65 % | 40.0% [21.8%–61.4%] (n=20, docs=20) | **fail** | no |
| Auto-classification precision (loss run vs other) | bundles + singles | 97 % | 100.0% [98.6%–100.0%] (n=332, docs=332) | pass | yes |
| Auto-classification recall (loss run vs other) | bundles + singles | 95 % | 100.0% [98.6%–100.0%] (n=332, docs=332) | pass | yes |
| Data-quality first-pass rate (hard rules, consistent docs) | all | 85 % | 78.3% [73.5%–82.5%] (n=323, docs=323) | **fail** | no |

## Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)

| Segment | Value |
|---|---|
| all | 74.8% [69.1%–79.8%] (n=67812, docs=332) |
| native-pdf | 95.1% [87.9%–98.4%] (n=18840, docs=93) |
| xlsx-csv | 97.9% [87.3%–100.0%] (n=16212, docs=86) |
| scan (all) | 51.7% [43.1%–60.2%] (n=32760, docs=153) |
| scan-300dpi-clean | 91.5% [80.2%–97.0%] (n=10920, docs=51) |
| scan-200dpi-rotated-noisy | 63.6% [48.6%–76.4%] (n=10920, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=10920, docs=51) |

## Key-field accuracy by template (seen / first-seen / unseen repeats)

| Segment | Value |
|---|---|
| seen · native-pdf | 98.8% [92.2%–100.0%] (n=7416, docs=38) |
| seen · scan | 61.7% [49.6%–72.6%] (n=14724, docs=72) |
| first-seen · native-pdf | 56.1% [23.6%–84.1%] (n=1134, docs=8) |
| first-seen · xlsx-csv | 100.0% [99.4%–100.0%] (n=720, docs=3) |
| first-seen · scan | 0.0% [0.0%–0.3%] (n=1494, docs=9) |
| unseen-repeat · native-pdf | 96.7% [81.5%–100.0%] (n=10290, docs=47) |
| unseen-repeat · xlsx-csv | 97.8% [86.4%–100.0%] (n=15492, docs=83) |
| unseen-repeat · scan | 47.4% [35.3%–59.9%] (n=16542, docs=72) |

## Pre-fill key-field accuracy (Review documents: what the reviewer starts from)

| Segment | Value |
|---|---|
| all | 81.5% [74.4%–87.1%] (n=17088, docs=79) |
| native-pdf | 46.5% [13.5%–82.8%] (n=930, docs=5) |
| xlsx-csv | 100.0% [98.6%–100.0%] (n=336, docs=1) |
| scan (all) | 83.2% [76.5%–88.3%] (n=15822, docs=73) |
| scan-300dpi-clean | 46.5% [13.5%–82.8%] (n=930, docs=5) |
| scan-200dpi-rotated-noisy | 87.3% [66.3%–96.8%] (n=3972, docs=17) |
| scan-150dpi-fax | 84.9% [79.1%–89.3%] (n=10920, docs=51) |

## Pre-fill, all printed fields (Review documents)

| Segment | Value |
|---|---|
| all | 81.4% [74.5%–86.8%] (n=39837, docs=79) |
| native-pdf | 48.2% [14.4%–83.7%] (n=2107, docs=5) |
| xlsx-csv | 100.0% [99.7%–100.0%] (n=1365, docs=1) |
| scan (all) | 82.6% [76.3%–87.6%] (n=36365, docs=73) |
| scan-300dpi-clean | 48.2% [14.4%–83.7%] (n=2107, docs=5) |
| scan-200dpi-rotated-noisy | 87.3% [67.5%–96.5%] (n=9147, docs=17) |
| scan-150dpi-fax | 83.8% [78.5%–88.1%] (n=25111, docs=51) |

## All printed fields

| Segment | Value |
|---|---|
| all | 77.7% [72.4%–82.3%] (n=179409, docs=332) |
| native-pdf | 95.1% [88.2%–98.3%] (n=43999, docs=93) |
| xlsx-csv | 97.7% [86.3%–100.0%] (n=60077, docs=86) |
| scan (all) | 51.7% [43.2%–60.1%] (n=75333, docs=153) |
| scan-300dpi-clean | 91.6% [80.5%–97.0%] (n=25111, docs=51) |
| scan-200dpi-rotated-noisy | 63.4% [48.5%–76.2%] (n=25111, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=25111, docs=51) |

## Claim joint pass (every printed field of the claim right)

| Segment | Value |
|---|---|
| all | 74.8% [69.1%–79.8%] (n=11302, docs=332) |
| native-pdf | 95.1% [87.9%–98.4%] (n=3140, docs=93) |
| xlsx-csv | 97.9% [87.3%–100.0%] (n=2702, docs=86) |
| scan (all) | 51.7% [43.1%–60.2%] (n=5460, docs=153) |
| scan-300dpi-clean | 91.5% [80.2%–97.0%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 63.6% [48.6%–76.4%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Document: every printed field right

| Segment | Value |
|---|---|
| all | 70.5% [65.1%–75.3%] (n=332, docs=332) |
| native-pdf | 81.7% [72.5%–88.4%] (n=93, docs=93) |
| xlsx-csv | 98.8% [92.5%–100.0%] (n=86, docs=86) |
| scan (all) | 47.7% [39.9%–55.7%] (n=153, docs=153) |
| scan-300dpi-clean | 90.2% [78.6%–96.2%] (n=51, docs=51) |
| scan-200dpi-rotated-noisy | 52.9% [39.1%–66.3%] (n=51, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–8.4%] (n=51, docs=51) |

## Claim rows: recall (truth rows found)

| Segment | Value |
|---|---|
| all | 74.8% [69.1%–79.7%] (n=11302, docs=332) |
| native-pdf | 95.1% [88.0%–98.3%] (n=3140, docs=93) |
| xlsx-csv | 97.9% [87.5%–100.0%] (n=2702, docs=86) |
| scan (all) | 51.7% [43.3%–60.0%] (n=5460, docs=153) |
| scan-300dpi-clean | 91.5% [81.5%–96.6%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 63.6% [47.5%–77.2%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Claim rows: precision (extracted rows that exist)

| Segment | Value |
|---|---|
| all | 100.0% [99.9%–100.0%] (n=8454, docs=253) |
| native-pdf | 100.0% [99.8%–100.0%] (n=2985, docs=88) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2646, docs=85) |
| scan (all) | 100.0% [99.8%–100.0%] (n=2823, docs=80) |
| scan-300dpi-clean | 100.0% [99.7%–100.0%] (n=1665, docs=46) |
| scan-200dpi-rotated-noisy | 100.0% [99.6%–100.0%] (n=1158, docs=34) |

## Data-quality first pass (0 hard failures; source-consistent documents)

| Segment | Value |
|---|---|
| all | 78.3% [73.5%–82.5%] (n=323, docs=323) |
| native-pdf | 96.7% [90.3%–99.3%] (n=91, docs=91) |
| xlsx-csv | 100.0% [94.8%–100.0%] (n=85, docs=85) |
| scan (all) | 54.4% [46.3%–62.3%] (n=147, docs=147) |
| scan-300dpi-clean | 93.9% [82.9%–98.5%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 69.4% [55.2%–80.7%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Quote success (priceable: replay/learn/delta, 0 hard failures)

| Segment | Value |
|---|---|
| all | 78.3% [73.5%–82.5%] (n=323, docs=323) |
| native-pdf | 96.7% [90.3%–99.3%] (n=91, docs=91) |
| xlsx-csv | 100.0% [94.8%–100.0%] (n=85, docs=85) |
| scan (all) | 54.4% [46.3%–62.3%] (n=147, docs=147) |
| scan-300dpi-clean | 93.9% [82.9%–98.5%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 69.4% [55.2%–80.7%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Straight-through (no document review)

| Segment | Value |
|---|---|
| seen | 75.5% [66.4%–82.7%] (n=106, docs=106) |
| first-seen | 40.0% [21.8%–61.4%] (n=20, docs=20) |
| unseen-repeat | 83.8% [77.9%–88.3%] (n=197, docs=197) |

## Straight-through, strict (also no confirm-format item)

| Segment | Value |
|---|---|
| seen | 75.5% [66.4%–82.7%] (n=106, docs=106) |
| first-seen | 0.0% [0.0%–19.0%] (n=20, docs=20) |
| unseen-repeat | 71.1% [64.0%–77.2%] (n=197, docs=197) |

Classification precision 100.0% [98.6%–100.0%] (n=332, docs=332), recall 100.0% [98.6%–100.0%] (n=332, docs=332); bundle loss-run split exact 8/8. Guardrail: source-inconsistent documents kept out of straight-through 100.0% [65.5%–100.0%] (n=9, docs=9).

## Routing by carrier and input type

| Carrier | Input | Docs | Final tiers |
|---|---|---|---|
| c1 | native-pdf | 38 | ReplayAccepted 37, Review 1 |
| c1 | scan-150dpi-fax | 24 | Review 24 |
| c1 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 20, Review 4 |
| c1 | scan-300dpi-clean | 24 | ReplayAccepted 23, Review 1 |
| c10 | native-pdf | 6 | ReplayAccepted 5, Learned 1 |
| c2 | native-pdf | 28 | ReplayAccepted 26, Learned 1, Review 1 |
| c2 | scan-150dpi-fax | 24 | Review 24 |
| c2 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 14, Review 10 |
| c2 | scan-300dpi-clean | 24 | ReplayAccepted 23, Review 1 |
| c3 | native-pdf | 3 | Review 3 |
| c3 | scan-150dpi-fax | 3 | Review 3 |
| c3 | scan-200dpi-rotated-noisy | 3 | Review 3 |
| c3 | scan-300dpi-clean | 3 | Review 3 |
| c4 | xlsx | 40 | ReplayAccepted 38, Learned 1, Review 1 |
| c5 | csv | 40 | ReplayAccepted 39, Learned 1 |
| c6 | native-pdf | 6 | ReplayAccepted 5, Learned 1 |
| c7 | native-pdf | 6 | ReplayAccepted 5, Learned 1 |
| c8 | native-pdf | 6 | ReplayAccepted 5, Learned 1 |
| c9 | csv | 6 | ReplayAccepted 5, Learned 1 |

## Latency and cost by input type (per document, in-process)

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| all | 332 | 3.7 | 8.8 | 0.0109 | 3.62 | 186 |
| native-pdf | 93 | 3.8 | 17.7 | 0.0170 | 1.58 | 55 |
| xlsx-csv | 86 | 0.1 | 0.2 | 0.0014 | 0.12 | 7 |
| scan (all) | 153 | 4.2 | 8.3 | 0.0125 | 1.92 | 124 |
| scan-300dpi-clean | 51 | 4.0 | 9.1 | 0.0124 | 0.63 | 41 |
| scan-200dpi-rotated-noisy | 51 | 3.9 | 7.0 | 0.0124 | 0.63 | 41 |
| scan-150dpi-fax | 51 | 4.9 | 6.5 | 0.0128 | 0.65 | 42 |

## Latency and cost by final path

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| Learned | 8 | 37.5 | 65.8 | 0.0523 | 0.42 | 22 |
| ReplayAccepted | 245 | 3.6 | 6.9 | 0.0091 | 2.23 | 100 |
| Review | 79 | 4.9 | 8.8 | 0.0123 | 0.97 | 64 |

## Error types (field values)

| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |
|---|---|---|---|---|
| all | 139,485 | 39,924 | 0 | 0 |
| native-pdf | 41,844 | 2,155 | 0 | 0 |
| xlsx-csv | 58,712 | 1,365 | 0 | 0 |
| scan (all) | 38,929 | 36,404 | 0 | 0 |
| scan-300dpi-clean | 23,004 | 2,107 | 0 | 0 |
| scan-200dpi-rotated-noisy | 15,925 | 9,186 | 0 | 0 |
| scan-150dpi-fax | 0 | 25,111 | 0 | 0 |

## Edge-case strata

| Stratum | Docs | Quote success | Key-field accuracy | Review |
|---|---|---|---|---|
| missing-year | 11 | 72.7% [42.9%–90.8%] (n=11, docs=11) | 76.5% [41.8%–94.4%] (n=1866, docs=11) | 3 |
| partial-run | 10 | 80.0% [47.9%–95.4%] (n=10, docs=10) | 80.0% [47.6%–95.5%] (n=2676, docs=10) | 2 |
| closed-only | 10 | 80.0% [47.9%–95.4%] (n=10, docs=10) | 78.4% [46.1%–94.6%] (n=804, docs=10) | 2 |
| reopened | 15 | 60.0% [35.7%–80.2%] (n=15, docs=15) | 63.6% [37.7%–83.5%] (n=2802, docs=15) | 6 |
| cat-outlier | 10 | 70.0% [39.2%–89.7%] (n=10, docs=10) | 69.8% [38.7%–89.7%] (n=2502, docs=10) | 3 |
| multi-policy | 10 | 80.0% [47.9%–95.4%] (n=10, docs=10) | 78.4% [45.9%–94.7%] (n=2610, docs=10) | 2 |
| non-ca | 5 | 100.0% [51.1%–100.0%] (n=5, docs=5) | 100.0% [99.6%–100.0%] (n=1104, docs=5) | 0 |
| summary-only-period | 9 | 77.8% [44.3%–94.7%] (n=9, docs=9) | 76.2% [40.7%–94.5%] (n=2196, docs=9) | 2 |
| source-inconsistent | 9 | 0.0% [0.0%–34.5%] (n=9, docs=9) | 0.0% [0.0%–0.2%] (n=2064, docs=9) | 9 |
| password-protected | 10 | 100.0% [67.9%–100.0%] (n=10, docs=10) | 100.0% [99.8%–100.0%] (n=1854, docs=10) | 0 |
| bundle | 8 | 100.0% [62.8%–100.0%] (n=8, docs=8) | 100.0% [99.7%–100.0%] (n=1590, docs=8) | 0 |
| non-wc-rows | 5 | 100.0% [51.1%–100.0%] (n=5, docs=5) | 100.0% [99.7%–100.0%] (n=1374, docs=5) | 0 |
