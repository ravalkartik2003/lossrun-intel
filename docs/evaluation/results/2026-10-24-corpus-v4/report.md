# Evaluation report — synthetic corpus

Run 2026-10-24 18:40–19:01 UTC · git `8636b72f3c` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v7` · gate `learn-gate-0.2.0` · catalog `0.7.0` · prices `0.1.0` · corpus `corpus-gen-0.5.0` seed 8

326 documents · 176,235 scored field values · metered cost $4.29

Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).

## Deck table — by input type (`docs/evaluation/results/2026-10-22-corpus-v2/`)

Every document is in exactly one row; no blended headline. Delivered = key fields (Claim #, DOI, indemnity/medical paid and reserve) on documents that left without a human. Review rate = documents routed to a human. Pre-fill = key fields of what the reviewer starts from on those documents. Committed rows carry a proposal target; the others carry the measured numbers.

> Synthetic corpus: generated documents from five fictional carriers, not client documents. Real carrier documents will be messier; the sealed blind set on the client's archive is the number that counts. Rows with fewer than 5 documents are an insufficient sample, not a result.

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| native PDF, recurring template | 65 | 100.0% [100.0%–100.0%] (n=13386, docs=63) | 3.1% [0.2%–11.5%] (n=65, docs=65) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 99% delivered — met |
| native PDF, first-seen template | 1 | — (none delivered) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 5.000 | ≥ 98% delivered — insufficient sample (n = 1 document): no verdict |
| XLSX / CSV | 80 | 100.0% [100.0%–100.0%] (n=14982, docs=79) | 1.3% [0.0%–7.4%] (n=80, docs=80) | 100.0% [98.6%–100.0%] (n=336, docs=1) | 0.088 | ≥ 98% delivered — met |
| scan ≥ 300 dpi | 48 | 100.0% [100.0%–100.0%] (n=9990, docs=46) | 4.2% [0.4%–14.8%] (n=48, docs=48) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.083 | ≥ 95% delivered — met |
| scan 200 dpi rotated | 48 | 100.0% [99.9%–100.0%] (n=7272, docs=35) | 27.1% [16.5%–41.1%] (n=48, docs=48) | 99.8% [99.6%–99.9%] (n=3150, docs=13) | 0.000 | measured, not committed; review rate 27.1%, pre-fill 99.8% |
| fax < 200 dpi | 48 | — (none delivered) | 100.0% [91.2%–100.0%] (n=48, docs=48) | 89.1% [86.2%–91.4%] (n=10422, docs=48) | 0.021 | routed to review by design; pre-fill 89.1% |
| narrative statement | 12 | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0.0% [0.0%–0.2%] (n=1992, docs=12) | 0.000 | routed to review by design; pre-fill is the statement's paragraphs, no structured values |
| construct: two-line records (c6) | 6 | 100.0% [99.6%–100.0%] (n=1278, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.333 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: one table per claim (c7) | 6 | 100.0% [99.1%–100.0%] (n=516, docs=3) | 50.0% [18.4%–81.6%] (n=6, docs=6) | 100.0% [98.7%–100.0%] (n=342, docs=3) | 3.167 | measured, not committed (construct coverage: first document learned, then replayed); review rate 50.0% |
| construct: stacked cells (c8) | 6 | 100.0% [98.5%–100.0%] (n=312, docs=1) | 83.3% [41.8%–98.9%] (n=6, docs=6) | 100.0% [99.4%–100.0%] (n=714, docs=5) | 5.000 | measured, not committed (construct coverage: first document learned, then replayed); review rate 83.3% |
| construct: component-sum headers (c9) | 6 | 100.0% [99.6%–100.0%] (n=1098, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.333 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |

Data-quality first pass and quote success by input type (source-consistent documents; rates on the delivered ones):

| Input type | Docs | DQ first pass (delivered) | Quote success (delivered) | Review rate | n delivered |
|---|---:|---|---|---|---:|
| native PDF, recurring template | 63 | 100.0% [93.1%–100.0%] (n=63, docs=63) | 100.0% [93.1%–100.0%] (n=63, docs=63) | 0.0% [0.0%–6.9%] (n=63, docs=63) | 63 |
| native PDF, first-seen template | 1 | — (none delivered) | — (none delivered) | insufficient sample (n = 1 document) | 0 |
| XLSX / CSV | 79 | 100.0% [94.4%–100.0%] (n=79, docs=79) | 100.0% [94.4%–100.0%] (n=79, docs=79) | 0.0% [0.0%–5.6%] (n=79, docs=79) | 79 |
| scan ≥ 300 dpi | 46 | 100.0% [90.8%–100.0%] (n=46, docs=46) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0.0% [0.0%–9.2%] (n=46, docs=46) | 46 |
| scan 200 dpi rotated | 46 | 100.0% [88.2%–100.0%] (n=35, docs=35) | 100.0% [88.2%–100.0%] (n=35, docs=35) | 23.9% [13.8%–38.1%] (n=46, docs=46) | 35 |
| fax < 200 dpi | 46 | — (none delivered) | — (none delivered) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0 |
| narrative statement | 12 | — (none delivered) | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0 |
| construct: two-line records (c6) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: one table per claim (c7) | 6 | 100.0% [38.3%–100.0%] (n=3, docs=3) | 100.0% [38.3%–100.0%] (n=3, docs=3) | 50.0% [18.4%–81.6%] (n=6, docs=6) | 3 |
| construct: stacked cells (c8) | 6 | 100.0% [16.7%–100.0%] (n=1, docs=1) | 100.0% [16.7%–100.0%] (n=1, docs=1) | 83.3% [41.8%–98.9%] (n=6, docs=6) | 1 |
| construct: component-sum headers (c9) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |

## Requirement acceptance criteria (placeholders filled)

Delivered = values that leave without a human (a Review document delivers nothing). Pre-fill = the same measure on Review documents' pre-fill, what the reviewer starts from (scoring-protocol.md §3).

| Criterion | Delivered (no human) | Pre-fill (Review documents) | Proposed target |
|---|---|---|---|
| Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) — by input type, see the deck table | native PDF, recurring template 100.0% [100.0%–100.0%] (n=13386, docs=63); XLSX / CSV 100.0% [100.0%–100.0%] (n=14982, docs=79); scan ≥ 300 dpi 100.0% [100.0%–100.0%] (n=9990, docs=46); scan 200 dpi rotated 100.0% [99.9%–100.0%] (n=7272, docs=35); construct: two-line records (c6) 100.0% [99.6%–100.0%] (n=1278, docs=6); construct: one table per claim (c7) 100.0% [99.1%–100.0%] (n=516, docs=3); construct: stacked cells (c8) 100.0% [98.5%–100.0%] (n=312, docs=1); construct: component-sum headers (c9) 100.0% [99.6%–100.0%] (n=1098, docs=6) | native PDF, recurring template 100.0% [98.9%–100.0%] (n=432, docs=2); native PDF, first-seen template 100.0% [91.2%–100.0%] (n=48, docs=1); XLSX / CSV 100.0% [98.6%–100.0%] (n=336, docs=1); scan ≥ 300 dpi 100.0% [98.9%–100.0%] (n=432, docs=2); scan 200 dpi rotated 99.8% [99.6%–99.9%] (n=3150, docs=13); fax < 200 dpi 89.1% [86.2%–91.4%] (n=10422, docs=48); narrative statement 0.0% [0.0%–0.2%] (n=1992, docs=12); construct: one table per claim (c7) 100.0% [98.7%–100.0%] (n=342, docs=3); construct: stacked cells (c8) 100.0% [99.4%–100.0%] (n=714, docs=5) | committed rows: ≥ 99% native recurring, ≥ 98% native first-seen and XLSX/CSV, ≥ 95% scans ≥ 300 dpi |
| End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU) | p50 0.3 s, p95 20.2 s over 326 documents (deployed API figures: the API work) | — | ≤ 60 s p95 (proposed) |
| Auto-classification precision / recall | 100.0% [98.6%–100.0%] (n=326, docs=326) / 100.0% [98.6%–100.0%] (n=326, docs=326); bundle split exact 8/8 | — | ≥ 97% / ≥ 95% (A2) |
| Data-quality validation pass rate, first pass | 75.4% [70.1%–80.0%] (n=317, docs=317) — blended, mixes the by-design review rows (fax, narrative); not for the deck: use the by-input-type table | — | ≥ 85% (A2) |
| Quote success rate (priceable submissions) | 75.4% [70.1%–80.0%] (n=317, docs=317) — blended, mixes the by-design review rows; not for the deck: use the by-input-type table | — | ≥ 90% of valid uploads (proposed) |

## The evaluation harness targets

| Target | Segment | Threshold | Measured (delivered) | Result |
|---|---|---|---|---|
| Recurring-template delivered key-field accuracy | native PDF | 99 % | 97.2% [88.4%–100.0%] (n=15396, docs=72) | **not met** |
| Key fields | scan ≥ 300 dpi | 95 % | 91.5% [80.2%–97.0%] (n=10920, docs=51) | **not met** |
| Key fields | scan 200 dpi rotated | 90 % | 66.6% [51.3%–79.1%] (n=10920, docs=51) | **not met** |
| Key fields (reported, not promised) | fax 150 dpi | — | 0.0% [0.0%–0.0%] (n=10920, docs=51) | reported |
| DQ first pass (source-consistent) | excluding narrative | 85 % | 78.4% [73.4%–82.6%] (n=305, docs=305) | **not met** |
| STP | recurring templates | 85 % | 80.3% [75.3%–84.5%] (n=289, docs=289) | **not met** |
| STP (context: fax is reported, not promised) | recurring templates excluding fax | 85 % | 95.5% [92.0%–97.5%] (n=243, docs=243) | met |
| DQ first pass (context) | excluding narrative and fax | 85 % | 92.3% [88.3%–95.0%] (n=259, docs=259) | met |
| Recurring-template delivered key-field accuracy (context) | native PDF, source-consistent | 99 % | 100.0% [100.0%–100.0%] (n=14964, docs=70) | met |
| Learn/repair model calls per document | c1 + c2 | 0.3 | 0.048 per document (10 calls / 210 documents); target < 0.3 | met |

## Model calls per document by carrier (learn / delta / repair)

| Carrier | Docs | Calls | Per document | Docs with 0 calls |
|---|---:|---:|---:|---:|
| c1 | 110 | 0 | 0.000 | 110 |
| c2 | 100 | 10 | 0.100 | 97 |
| c3 | 12 | 0 | 0.000 | 12 |
| c4 | 40 | 4 | 0.100 | 39 |
| c5 | 40 | 3 | 0.075 | 39 |
| c6 | 6 | 2 | 0.333 | 5 |
| c7 | 6 | 19 | 3.167 | 2 |
| c8 | 6 | 30 | 5.000 | 0 |
| c9 | 6 | 2 | 0.333 | 5 |

## Thresholds (research A2) — pass/fail

| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |
|---|---|---|---|---|---|
| Claim # and DOI, normalized exact | native (PDF + XLSX/CSV) | 98 % | 93.0% [87.9%–96.1%] (n=11314, docs=173) | **fail** | no |
| Claim # and DOI, normalized exact | scan ≥ 300 dpi | 95 % | 91.5% [80.2%–97.0%] (n=3640, docs=51) | **fail** | no |
| Claim # and DOI, normalized exact | scan < 200 dpi / fax | 90 % | 33.3% [24.2%–43.8%] (n=7280, docs=102) | **fail** | no |
| Indemnity/medical paid and reserve per claim | native (PDF + XLSX/CSV) | 97 % | 93.0% [87.9%–96.1%] (n=22628, docs=173) | **fail** | no |
| Indemnity/medical paid and reserve per claim | scans | 93 % | 52.7% [44.1%–61.1%] (n=21840, docs=153) | **fail** | no |
| Claim-level completeness (all printed fields right) | native (PDF + XLSX/CSV) | 90 % | 48.9% [40.8%–57.1%] (n=5657, docs=173) | **fail** | no |
| Claim-level completeness (all printed fields right) | scans | 80 % | 52.7% [44.1%–61.1%] (n=5460, docs=153) | **fail** | no |
| Straight-through (no human touch) | recurring template (seen + unseen repeats) | 80 % | 80.3% [75.3%–84.5%] (n=289, docs=289) | pass | no |
| Straight-through (no human touch) | first-seen template | 65 % | 25.0% [12.4%–43.6%] (n=28, docs=28) | **fail** | no |
| Auto-classification precision (loss run vs other) | bundles + singles | 97 % | 100.0% [98.6%–100.0%] (n=326, docs=326) | pass | yes |
| Auto-classification recall (loss run vs other) | bundles + singles | 95 % | 100.0% [98.6%–100.0%] (n=326, docs=326) | pass | yes |
| Data-quality first-pass rate (hard rules, consistent docs) | all | 85 % | 75.4% [70.1%–80.0%] (n=317, docs=317) | **fail** | no |

## Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)

| Segment | Value |
|---|---|
| all | 73.2% [67.4%–78.4%] (n=66702, docs=326) |
| native-pdf | 88.4% [79.7%–93.8%] (n=17526, docs=87) |
| xlsx-csv | 98.0% [87.5%–100.0%] (n=16416, docs=86) |
| scan (all) | 52.7% [44.1%–61.1%] (n=32760, docs=153) |
| scan-300dpi-clean | 91.5% [80.2%–97.0%] (n=10920, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [51.3%–79.1%] (n=10920, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=10920, docs=51) |

## Key-field accuracy by template (seen / first-seen / unseen repeats)

| Segment | Value |
|---|---|
| seen · native-pdf | 98.8% [92.2%–100.0%] (n=7416, docs=38) |
| seen · scan | 61.7% [49.6%–72.6%] (n=14724, docs=72) |
| first-seen · native-pdf | 24.8% [7.6%–55.3%] (n=2130, docs=15) |
| first-seen · xlsx-csv | 100.0% [99.4%–100.0%] (n=720, docs=3) |
| first-seen · scan | 3.1% [0.0%–20.4%] (n=1542, docs=10) |
| unseen-repeat · native-pdf | 95.7% [78.8%–100.0%] (n=7980, docs=34) |
| unseen-repeat · xlsx-csv | 97.9% [86.5%–100.0%] (n=15696, docs=83) |
| unseen-repeat · scan | 49.3% [37.1%–61.5%] (n=16494, docs=71) |

## Pre-fill key-field accuracy (Review documents: what the reviewer starts from)

| Segment | Value |
|---|---|
| all | 82.4% [75.4%–87.8%] (n=17868, docs=87) |
| native-pdf | 75.5% [46.4%–92.1%] (n=2034, docs=14) |
| xlsx-csv | 100.0% [98.6%–100.0%] (n=336, docs=1) |
| scan (all) | 83.0% [76.1%–88.2%] (n=15498, docs=72) |
| scan-300dpi-clean | 46.5% [13.5%–82.8%] (n=930, docs=5) |
| scan-200dpi-rotated-noisy | 86.2% [63.4%–96.6%] (n=3648, docs=16) |
| scan-150dpi-fax | 85.0% [79.2%–89.4%] (n=10920, docs=51) |

## Pre-fill, all printed fields (Review documents)

| Segment | Value |
|---|---|
| all | 81.5% [75.1%–86.7%] (n=41602, docs=87) |
| native-pdf | 73.8% [46.6%–90.4%] (n=4619, docs=14) |
| xlsx-csv | 87.7% [85.8%–89.3%] (n=1365, docs=1) |
| scan (all) | 82.3% [75.8%–87.4%] (n=35618, docs=72) |
| scan-300dpi-clean | 48.2% [14.4%–83.7%] (n=2107, docs=5) |
| scan-200dpi-rotated-noisy | 86.2% [64.6%–96.2%] (n=8400, docs=16) |
| scan-150dpi-fax | 83.9% [78.5%–88.2%] (n=25111, docs=51) |

## All printed fields

| Segment | Value |
|---|---|
| all | 72.9% [67.8%–77.5%] (n=176235, docs=326) |
| native-pdf | 88.4% [80.0%–93.7%] (n=40111, docs=87) |
| xlsx-csv | 87.7% [82.9%–91.3%] (n=60791, docs=86) |
| scan (all) | 52.7% [44.2%–61.0%] (n=75333, docs=153) |
| scan-300dpi-clean | 91.6% [80.5%–97.0%] (n=25111, docs=51) |
| scan-200dpi-rotated-noisy | 66.4% [51.2%–78.8%] (n=25111, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=25111, docs=51) |

## Claim joint pass (every printed field of the claim right)

| Segment | Value |
|---|---|
| all | 50.8% [44.8%–56.6%] (n=11117, docs=326) |
| native-pdf | 88.4% [79.7%–93.8%] (n=2921, docs=87) |
| xlsx-csv | 6.7% [2.5%–15.2%] (n=2736, docs=86) |
| scan (all) | 52.7% [44.1%–61.1%] (n=5460, docs=153) |
| scan-300dpi-clean | 91.5% [80.2%–97.0%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [51.3%–79.1%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Document: every printed field right

| Segment | Value |
|---|---|
| all | 43.9% [38.5%–49.4%] (n=326, docs=326) |
| native-pdf | 75.9% [65.8%–83.7%] (n=87, docs=87) |
| xlsx-csv | 7.0% [3.0%–14.7%] (n=86, docs=86) |
| scan (all) | 46.4% [38.7%–54.3%] (n=153, docs=153) |
| scan-300dpi-clean | 82.4% [69.5%–90.7%] (n=51, docs=51) |
| scan-200dpi-rotated-noisy | 56.9% [43.0%–69.8%] (n=51, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–8.4%] (n=51, docs=51) |

## Claim rows: recall (truth rows found)

| Segment | Value |
|---|---|
| all | 73.2% [67.2%–78.5%] (n=11117, docs=326) |
| native-pdf | 88.4% [80.2%–93.6%] (n=2921, docs=87) |
| xlsx-csv | 98.0% [87.6%–100.0%] (n=2736, docs=86) |
| scan (all) | 52.7% [44.3%–60.9%] (n=5460, docs=153) |
| scan-300dpi-clean | 91.5% [81.5%–96.6%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [50.7%–79.4%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Claim rows: precision (extracted rows that exist)

| Segment | Value |
|---|---|
| all | 100.0% [99.9%–100.0%] (n=8139, docs=239) |
| native-pdf | 100.0% [99.8%–100.0%] (n=2582, docs=73) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2680, docs=85) |
| scan (all) | 100.0% [99.8%–100.0%] (n=2877, docs=81) |
| scan-300dpi-clean | 100.0% [99.7%–100.0%] (n=1665, docs=46) |
| scan-200dpi-rotated-noisy | 100.0% [99.6%–100.0%] (n=1212, docs=35) |

## Data-quality first pass (0 hard failures; source-consistent documents)

| Segment | Value |
|---|---|
| all | 75.4% [70.1%–80.0%] (n=317, docs=317) |
| native-pdf | 85.9% [76.8%–91.9%] (n=85, docs=85) |
| xlsx-csv | 100.0% [94.8%–100.0%] (n=85, docs=85) |
| scan (all) | 55.1% [47.0%–62.9%] (n=147, docs=147) |
| scan-300dpi-clean | 93.9% [82.9%–98.5%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 71.4% [57.3%–82.4%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Quote success (priceable: replay/learn/delta, 0 hard failures)

| Segment | Value |
|---|---|
| all | 75.4% [70.1%–80.0%] (n=317, docs=317) |
| native-pdf | 85.9% [76.8%–91.9%] (n=85, docs=85) |
| xlsx-csv | 100.0% [94.8%–100.0%] (n=85, docs=85) |
| scan (all) | 55.1% [47.0%–62.9%] (n=147, docs=147) |
| scan-300dpi-clean | 93.9% [82.9%–98.5%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 71.4% [57.3%–82.4%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Straight-through (no document review)

| Segment | Value |
|---|---|
| seen | 75.5% [66.4%–82.7%] (n=106, docs=106) |
| first-seen | 25.0% [12.4%–43.6%] (n=28, docs=28) |
| unseen-repeat | 83.1% [76.8%–87.9%] (n=183, docs=183) |

## Straight-through, strict (also no confirm-format item)

| Segment | Value |
|---|---|
| seen | 75.5% [66.4%–82.7%] (n=106, docs=106) |
| first-seen | 0.0% [0.0%–14.3%] (n=28, docs=28) |
| unseen-repeat | 73.8% [66.9%–79.7%] (n=183, docs=183) |

Classification precision 100.0% [98.6%–100.0%] (n=326, docs=326), recall 100.0% [98.6%–100.0%] (n=326, docs=326); bundle loss-run split exact 8/8. Guardrail: source-inconsistent documents kept out of straight-through 100.0% [65.5%–100.0%] (n=9, docs=9).

## Routing by carrier and input type

| Carrier | Input | Docs | Final tiers |
|---|---|---|---|
| c1 | native-pdf | 38 | ReplayAccepted 37, Review 1 |
| c1 | scan-150dpi-fax | 24 | Review 24 |
| c1 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 20, Review 4 |
| c1 | scan-300dpi-clean | 24 | ReplayAccepted 23, Review 1 |
| c2 | native-pdf | 28 | ReplayAccepted 26, Review 2 |
| c2 | scan-150dpi-fax | 24 | Review 24 |
| c2 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 15, Review 9 |
| c2 | scan-300dpi-clean | 24 | ReplayAccepted 22, Learned 1, Review 1 |
| c3 | native-pdf | 3 | Review 3 |
| c3 | scan-150dpi-fax | 3 | Review 3 |
| c3 | scan-200dpi-rotated-noisy | 3 | Review 3 |
| c3 | scan-300dpi-clean | 3 | Review 3 |
| c4 | xlsx | 40 | ReplayAccepted 38, Learned 1, Review 1 |
| c5 | csv | 40 | ReplayAccepted 39, Learned 1 |
| c6 | native-pdf | 6 | ReplayAccepted 5, Learned 1 |
| c7 | native-pdf | 6 | Review 3, ReplayAccepted 2, Learned 1 |
| c8 | native-pdf | 6 | Review 5, Learned 1 |
| c9 | csv | 6 | ReplayAccepted 5, Learned 1 |

## Latency and cost by input type (per document, in-process)

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| all | 326 | 0.3 | 20.2 | 0.0132 | 4.29 | 228 |
| native-pdf | 87 | 1.8 | 65.8 | 0.0243 | 2.11 | 91 |
| xlsx-csv | 86 | 0.1 | 0.2 | 0.0023 | 0.20 | 9 |
| scan (all) | 153 | 0.3 | 3.0 | 0.0130 | 1.98 | 128 |
| scan-300dpi-clean | 51 | 0.4 | 2.8 | 0.0139 | 0.71 | 45 |
| scan-200dpi-rotated-noisy | 51 | 0.3 | 3.0 | 0.0123 | 0.63 | 41 |
| scan-150dpi-fax | 51 | 1.6 | 3.3 | 0.0128 | 0.65 | 42 |

## Latency and cost by final path

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| Learned | 7 | 53.0 | 81.0 | 0.0709 | 0.50 | 24 |
| ReplayAccepted | 232 | 0.2 | 3.7 | 0.0086 | 1.99 | 96 |
| Review | 87 | 1.7 | 65.8 | 0.0207 | 1.80 | 108 |

## Error types (field values)

| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |
|---|---|---|---|---|
| all | 128,451 | 41,666 | 0 | 6,118 |
| native-pdf | 35,464 | 4,647 | 0 | 0 |
| xlsx-csv | 53,315 | 1,365 | 0 | 6,111 |
| scan (all) | 39,672 | 35,654 | 0 | 7 |
| scan-300dpi-clean | 23,000 | 2,107 | 0 | 4 |
| scan-200dpi-rotated-noisy | 16,672 | 8,436 | 0 | 3 |
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
| non-wc-rows | 5 | 100.0% [51.1%–100.0%] (n=5, docs=5) | 100.0% [99.7%–100.0%] (n=1578, docs=5) | 0 |
