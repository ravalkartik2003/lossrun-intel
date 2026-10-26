# Evaluation report — synthetic corpus

Run 2026-10-22 18:51–19:03 UTC · git `bd0463ec09` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v5` · gate `learn-gate-0.1.0` · catalog `0.4.0` · prices `0.1.0` · corpus `corpus-gen-0.2.0` seed 8

302 documents · 150,233 scored field values · metered cost $3.36

Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).

## Requirement acceptance criteria (placeholders filled)

Delivered = values that leave without a human (a Review document delivers nothing). Pre-fill = the same measure on Review documents' pre-fill, what the reviewer starts from (scoring-protocol.md §3).

| Criterion | Delivered (no human) | Pre-fill (Review documents) | Proposed target |
|---|---|---|---|
| Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) | native PDF 89.1% [78.2%–95.2%] (n=14364, docs=69); XLSX/CSV 97.8% [88.1%–100.0%] (n=15318, docs=80); scans 51.1% [42.8%–59.5%] (n=32760, docs=153) | native PDF 65.7% [32.1%–88.8%] (n=1560, docs=8); XLSX/CSV 100.0% [98.6%–100.0%] (n=336, docs=1); scans 83.2% [76.3%–88.4%] (n=15780, docs=74) | ≥ 98% native, ≥ 95% scans ≥ 300 dpi, ≥ 90% scans < 200 dpi/fax (A2) |
| End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU) | p50 4.7 s, p95 13.5 s over 302 documents (deployed API figures: the API work) | — | ≤ 60 s p95 (proposed) |
| Auto-classification precision / recall | 100.0% [98.5%–100.0%] (n=301, docs=301) / 99.7% [98.0%–100.0%] (n=302, docs=302); bundle split exact 8/8 | — | ≥ 97% / ≥ 95% (A2) |
| Data-quality validation pass rate, first pass | 74.4% [69.1%–79.1%] (n=293, docs=293) | — | ≥ 85% (A2) |
| Quote success rate (priceable submissions) | 74.4% [69.1%–79.1%] (n=293, docs=293) | — | ≥ 90% of valid uploads (proposed) |

## the evaluation harness targets

| Target | Segment | Threshold | Measured (delivered) | Result |
|---|---|---|---|---|
| Recurring-template delivered key-field accuracy | native PDF | 99 % | 92.5% [80.9%–97.8%] (n=13524, docs=64) | **not met** |
| Key fields | scan ≥ 300 dpi | 95 % | 86.8% [74.0%–94.1%] (n=10920, docs=51) | **not met** |
| Key fields | scan 200 dpi rotated | 90 % | 66.6% [51.3%–79.1%] (n=10920, docs=51) | **not met** |
| Key fields (reported, not promised) | fax 150 dpi | — | 0.0% [0.0%–0.0%] (n=10920, docs=51) | reported |
| DQ first pass (source-consistent) | excluding narrative | 85 % | 77.6% [72.3%–82.1%] (n=281, docs=281) | **not met** |
| STP | recurring templates | 85 % | 78.8% [73.5%–83.2%] (n=273, docs=273) | **not met** |
| STP (context: fax is reported, not promised) | recurring templates excluding fax | 85 % | 93.9% [89.9%–96.4%] (n=229, docs=229) | met |
| DQ first pass (context) | excluding narrative and fax | 85 % | 92.8% [88.7%–95.5%] (n=235, docs=235) | met |
| Recurring-template delivered key-field accuracy (context) | native PDF, source-consistent | 99 % | 95.6% [84.1%–99.6%] (n=13092, docs=62) | **not met** |
| Learn/repair model calls per document | c1 + c2 | 0.3 | 0.162 per document (34 calls / 210 documents); target < 0.3 | met |

## Model calls per document by carrier (learn / delta / repair)

| Carrier | Docs | Calls | Per document | Docs with 0 calls |
|---|---:|---:|---:|---:|
| c1 | 110 | 6 | 0.055 | 109 |
| c2 | 100 | 28 | 0.280 | 94 |
| c3 | 12 | 0 | 0.000 | 12 |
| c4 | 40 | 3 | 0.075 | 39 |
| c5 | 40 | 3 | 0.075 | 39 |

## Thresholds (research A2) — pass/fail

| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |
|---|---|---|---|---|---|
| Claim # and DOI, normalized exact | native (PDF + XLSX/CSV) | 98 % | 93.6% [87.5%–97.0%] (n=9894, docs=149) | **fail** | no |
| Claim # and DOI, normalized exact | scan ≥ 300 dpi | 95 % | 86.8% [74.0%–94.1%] (n=3640, docs=51) | **fail** | no |
| Claim # and DOI, normalized exact | scan < 200 dpi / fax | 90 % | 33.3% [24.2%–43.8%] (n=7280, docs=102) | **fail** | no |
| Indemnity/medical paid and reserve per claim | native (PDF + XLSX/CSV) | 97 % | 93.6% [87.5%–97.0%] (n=19788, docs=149) | **fail** | no |
| Indemnity/medical paid and reserve per claim | scans | 93 % | 51.1% [42.8%–59.5%] (n=21840, docs=153) | **fail** | no |
| Claim-level completeness (all printed fields right) | native (PDF + XLSX/CSV) | 90 % | 93.3% [87.3%–96.7%] (n=4947, docs=149) | pass | no |
| Claim-level completeness (all printed fields right) | scans | 80 % | 51.1% [42.8%–59.5%] (n=5460, docs=153) | **fail** | no |
| Straight-through (no human touch) | recurring template (seen + unseen repeats) | 80 % | 78.8% [73.5%–83.2%] (n=273, docs=273) | **fail** | no |
| Straight-through (no human touch) | first-seen template | 65 % | 15.0% [4.4%–36.9%] (n=20, docs=20) | **fail** | no |
| Auto-classification precision (loss run vs other) | bundles + singles | 97 % | 100.0% [98.5%–100.0%] (n=301, docs=301) | pass | yes |
| Auto-classification recall (loss run vs other) | bundles + singles | 95 % | 99.7% [98.0%–100.0%] (n=302, docs=302) | pass | yes |
| Data-quality first-pass rate (hard rules, consistent docs) | all | 85 % | 74.4% [69.1%–79.1%] (n=293, docs=293) | **fail** | no |

## Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)

| Segment | Value |
|---|---|
| all | 71.3% [65.2%–76.7%] (n=62442, docs=302) |
| native-pdf | 89.1% [78.2%–95.2%] (n=14364, docs=69) |
| xlsx-csv | 97.8% [88.1%–100.0%] (n=15318, docs=80) |
| scan (all) | 51.1% [42.8%–59.5%] (n=32760, docs=153) |
| scan-300dpi-clean | 86.8% [74.0%–94.1%] (n=10920, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [51.3%–79.1%] (n=10920, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=10920, docs=51) |

## Key-field accuracy by template (seen / first-seen / unseen repeats)

| Segment | Value |
|---|---|
| seen · native-pdf | 98.8% [92.2%–100.0%] (n=7416, docs=38) |
| seen · scan | 59.9% [47.7%–71.0%] (n=14724, docs=72) |
| first-seen · native-pdf | 35.0% [7.1%–78.6%] (n=840, docs=5) |
| first-seen · xlsx-csv | 100.0% [98.8%–100.0%] (n=384, docs=2) |
| first-seen · scan | 0.0% [0.0%–0.2%] (n=1866, docs=13) |
| unseen-repeat · native-pdf | 84.9% [62.7%–95.6%] (n=6108, docs=26) |
| unseen-repeat · xlsx-csv | 97.8% [87.4%–100.0%] (n=14934, docs=78) |
| unseen-repeat · scan | 49.1% [36.9%–61.3%] (n=16170, docs=68) |

## Pre-fill key-field accuracy (Review documents: what the reviewer starts from)

| Segment | Value |
|---|---|
| all | 82.0% [75.0%–87.3%] (n=17676, docs=83) |
| native-pdf | 65.7% [32.1%–88.8%] (n=1560, docs=8) |
| xlsx-csv | 100.0% [98.6%–100.0%] (n=336, docs=1) |
| scan (all) | 83.2% [76.3%–88.4%] (n=15780, docs=74) |
| scan-300dpi-clean | 64.9% [30.2%–88.9%] (n=1440, docs=8) |
| scan-200dpi-rotated-noisy | 84.9% [62.5%–95.7%] (n=3648, docs=16) |
| scan-150dpi-fax | 85.1% [79.2%–89.5%] (n=10692, docs=50) |

## Pre-fill, all printed fields (Review documents)

| Segment | Value |
|---|---|
| all | 81.6% [75.0%–86.8%] (n=40858, docs=83) |
| native-pdf | 68.5% [34.0%–90.4%] (n=3607, docs=8) |
| xlsx-csv | 100.0% [99.5%–100.0%] (n=973, docs=1) |
| scan (all) | 82.4% [75.9%–87.4%] (n=36278, docs=74) |
| scan-300dpi-clean | 64.9% [31.4%–88.4%] (n=3306, docs=8) |
| scan-200dpi-rotated-noisy | 84.6% [63.4%–95.2%] (n=8400, docs=16) |
| scan-150dpi-fax | 84.0% [78.5%–88.3%] (n=24572, docs=50) |

## All printed fields

| Segment | Value |
|---|---|
| all | 72.4% [66.5%–77.6%] (n=150233, docs=302) |
| native-pdf | 88.9% [78.2%–95.0%] (n=33021, docs=69) |
| xlsx-csv | 97.6% [87.5%–100.0%] (n=41879, docs=80) |
| scan (all) | 51.0% [42.7%–59.3%] (n=75333, docs=153) |
| scan-300dpi-clean | 86.7% [74.0%–93.9%] (n=25111, docs=51) |
| scan-200dpi-rotated-noisy | 66.4% [51.2%–78.8%] (n=25111, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=25111, docs=51) |

## Claim joint pass (every printed field of the claim right)

| Segment | Value |
|---|---|
| all | 71.2% [65.1%–76.6%] (n=10407, docs=302) |
| native-pdf | 89.1% [78.2%–95.2%] (n=2394, docs=69) |
| xlsx-csv | 97.2% [88.9%–100.0%] (n=2553, docs=80) |
| scan (all) | 51.1% [42.8%–59.5%] (n=5460, docs=153) |
| scan-300dpi-clean | 86.8% [74.0%–94.1%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [51.3%–79.1%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Document: every printed field right

| Segment | Value |
|---|---|
| all | 55.6% [49.9%–61.2%] (n=302, docs=302) |
| native-pdf | 60.9% [49.1%–71.5%] (n=69, docs=69) |
| xlsx-csv | 93.8% [85.9%–97.6%] (n=80, docs=80) |
| scan (all) | 33.3% [26.3%–41.1%] (n=153, docs=153) |
| scan-300dpi-clean | 45.1% [32.0%–58.9%] (n=51, docs=51) |
| scan-200dpi-rotated-noisy | 54.9% [41.1%–68.0%] (n=51, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–8.4%] (n=51, docs=51) |

## Claim rows: recall (truth rows found)

| Segment | Value |
|---|---|
| all | 71.3% [65.0%–76.9%] (n=10407, docs=302) |
| native-pdf | 89.1% [79.0%–94.9%] (n=2394, docs=69) |
| xlsx-csv | 97.8% [87.3%–100.0%] (n=2553, docs=80) |
| scan (all) | 51.1% [43.0%–59.2%] (n=5460, docs=153) |
| scan-300dpi-clean | 86.8% [73.8%–94.1%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [50.7%–79.4%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Claim rows: precision (extracted rows that exist)

| Segment | Value |
|---|---|
| all | 100.0% [99.9%–100.0%] (n=7423, docs=218) |
| native-pdf | 100.0% [99.8%–100.0%] (n=2134, docs=61) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2497, docs=79) |
| scan (all) | 100.0% [99.8%–100.0%] (n=2792, docs=78) |
| scan-300dpi-clean | 100.0% [99.7%–100.0%] (n=1580, docs=43) |
| scan-200dpi-rotated-noisy | 100.0% [99.6%–100.0%] (n=1212, docs=35) |

## Data-quality first pass (0 hard failures; source-consistent documents)

| Segment | Value |
|---|---|
| all | 74.4% [69.1%–79.1%] (n=293, docs=293) |
| native-pdf | 91.0% [81.5%–96.2%] (n=67, docs=67) |
| xlsx-csv | 100.0% [94.4%–100.0%] (n=79, docs=79) |
| scan (all) | 53.1% [45.0%–60.9%] (n=147, docs=147) |
| scan-300dpi-clean | 87.8% [75.4%–94.6%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 71.4% [57.3%–82.4%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Quote success (priceable: replay/learn/delta, 0 hard failures)

| Segment | Value |
|---|---|
| all | 74.4% [69.1%–79.1%] (n=293, docs=293) |
| native-pdf | 91.0% [81.5%–96.2%] (n=67, docs=67) |
| xlsx-csv | 100.0% [94.4%–100.0%] (n=79, docs=79) |
| scan (all) | 53.1% [45.0%–60.9%] (n=147, docs=147) |
| scan-300dpi-clean | 87.8% [75.4%–94.6%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 71.4% [57.3%–82.4%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Straight-through (no document review)

| Segment | Value |
|---|---|
| seen | 74.5% [65.4%–81.9%] (n=106, docs=106) |
| first-seen | 15.0% [4.4%–36.9%] (n=20, docs=20) |
| unseen-repeat | 81.4% [74.8%–86.6%] (n=167, docs=167) |

## Straight-through, strict (also no confirm-format item)

| Segment | Value |
|---|---|
| seen | 74.5% [65.4%–81.9%] (n=106, docs=106) |
| first-seen | 0.0% [0.0%–19.0%] (n=20, docs=20) |
| unseen-repeat | 74.9% [67.7%–80.9%] (n=167, docs=167) |

Classification precision 100.0% [98.5%–100.0%] (n=301, docs=301), recall 99.7% [98.0%–100.0%] (n=302, docs=302); bundle loss-run split exact 8/8. Guardrail: source-inconsistent documents kept out of straight-through 100.0% [65.5%–100.0%] (n=9, docs=9).

## Routing by carrier and input type

| Carrier | Input | Docs | Final tiers |
|---|---|---|---|
| c1 | native-pdf | 38 | ReplayAccepted 37, Review 1 |
| c1 | scan-150dpi-fax | 24 | Review 24 |
| c1 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 20, Review 4 |
| c1 | scan-300dpi-clean | 24 | ReplayAccepted 22, Review 2 |
| c2 | native-pdf | 28 | ReplayAccepted 23, Review 4, Learned 1 |
| c2 | scan-150dpi-fax | 24 | Review 23, NotFound 1 |
| c2 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 15, Review 9 |
| c2 | scan-300dpi-clean | 24 | ReplayAccepted 21, Review 3 |
| c3 | native-pdf | 3 | Review 3 |
| c3 | scan-150dpi-fax | 3 | Review 3 |
| c3 | scan-200dpi-rotated-noisy | 3 | Review 3 |
| c3 | scan-300dpi-clean | 3 | Review 3 |
| c4 | xlsx | 40 | ReplayAccepted 38, Learned 1, Review 1 |
| c5 | csv | 40 | ReplayAccepted 39, Learned 1 |

## Latency and cost by input type (per document, in-process)

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| all | 302 | 4.7 | 13.5 | 0.0111 | 3.36 | 198 |
| native-pdf | 69 | 5.6 | 18.0 | 0.0146 | 1.01 | 42 |
| xlsx-csv | 80 | 0.1 | 0.2 | 0.0010 | 0.08 | 6 |
| scan (all) | 153 | 5.3 | 13.8 | 0.0149 | 2.27 | 150 |
| scan-300dpi-clean | 51 | 5.6 | 18.5 | 0.0152 | 0.77 | 52 |
| scan-200dpi-rotated-noisy | 51 | 4.9 | 9.8 | 0.0136 | 0.70 | 46 |
| scan-150dpi-fax | 51 | 5.7 | 13.5 | 0.0157 | 0.80 | 52 |

## Latency and cost by final path

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| Learned | 3 | 28.1 | 31.3 | 0.0404 | 0.12 | 9 |
| NotFound | 1 | 3.7 | 3.7 | 0.0135 | 0.01 | 1 |
| ReplayAccepted | 215 | 4.1 | 10.7 | 0.0083 | 1.79 | 91 |
| Review | 83 | 5.7 | 58.6 | 0.0172 | 1.43 | 97 |

## Error types (field values)

| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |
|---|---|---|---|---|
| all | 108,699 | 41,512 | 0 | 22 |
| native-pdf | 29,371 | 3,650 | 0 | 0 |
| xlsx-csv | 40,891 | 973 | 0 | 15 |
| scan (all) | 38,437 | 36,889 | 0 | 7 |
| scan-300dpi-clean | 21,768 | 3,339 | 0 | 4 |
| scan-200dpi-rotated-noisy | 16,669 | 8,439 | 0 | 3 |
| scan-150dpi-fax | 0 | 25,111 | 0 | 0 |

## Edge-case strata

| Stratum | Docs | Quote success | Key-field accuracy | Review |
|---|---|---|---|---|
| missing-year | 11 | 72.7% [42.9%–90.8%] (n=11, docs=11) | 76.5% [41.8%–94.4%] (n=1866, docs=11) | 3 |
| partial-run | 10 | 80.0% [47.9%–95.4%] (n=10, docs=10) | 80.0% [47.6%–95.5%] (n=2676, docs=10) | 2 |
| closed-only | 10 | 80.0% [47.9%–95.4%] (n=10, docs=10) | 78.4% [46.1%–94.6%] (n=804, docs=10) | 2 |
| reopened | 15 | 60.0% [35.7%–80.2%] (n=15, docs=15) | 63.6% [37.7%–83.5%] (n=2802, docs=15) | 6 |
| cat-outlier | 10 | 70.0% [39.2%–89.7%] (n=10, docs=10) | 69.8% [38.7%–89.7%] (n=2502, docs=10) | 2 |
| multi-policy | 10 | 80.0% [47.9%–95.4%] (n=10, docs=10) | 78.4% [45.9%–94.7%] (n=2610, docs=10) | 2 |
| non-ca | 5 | 100.0% [51.1%–100.0%] (n=5, docs=5) | 100.0% [99.6%–100.0%] (n=1104, docs=5) | 0 |
| summary-only-period | 9 | 77.8% [44.3%–94.7%] (n=9, docs=9) | 76.2% [40.7%–94.5%] (n=2196, docs=9) | 2 |
| source-inconsistent | 9 | 0.0% [0.0%–34.5%] (n=9, docs=9) | 0.0% [0.0%–0.2%] (n=2064, docs=9) | 9 |
| password-protected | 10 | 100.0% [67.9%–100.0%] (n=10, docs=10) | 100.0% [99.8%–100.0%] (n=1854, docs=10) | 0 |
| bundle | 8 | 100.0% [62.8%–100.0%] (n=8, docs=8) | 100.0% [99.7%–100.0%] (n=1590, docs=8) | 0 |
| non-wc-rows | 5 | 100.0% [51.1%–100.0%] (n=5, docs=5) | 100.0% [99.7%–100.0%] (n=1578, docs=5) | 0 |
