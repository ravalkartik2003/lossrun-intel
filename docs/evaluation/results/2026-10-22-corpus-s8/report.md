# Evaluation report — synthetic corpus

Run 2026-10-22 14:45–15:52 UTC · git `6522509497` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v5` · gate `learn-gate-0.1.0` · catalog `0.4.0` · prices `0.1.0` · corpus `corpus-gen-0.1.0` seed 8

338 documents · 154,783 scored field values · metered cost $7.51

Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).

## Requirement acceptance criteria (placeholders filled)

| Criterion | Measured | Proposed target |
|---|---|---|
| Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) | native PDF 76.1% [64.3%–84.9%] (n=16026, docs=78); XLSX/CSV 100.0% [100.0%–100.0%] (n=13158, docs=80); scans 49.8% [42.1%–57.6%] (n=35910, docs=180) | ≥ 98% native, ≥ 95% scans ≥ 300 dpi, ≥ 90% scans < 200 dpi/fax (A2) |
| End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU) | p50 4.9 s, p95 50.2 s over 338 documents (deployed API figures: the API work) | ≤ 60 s p95 (proposed) |
| Auto-classification precision / recall | 100.0% [98.6%–100.0%] (n=336, docs=336) / 99.4% [97.7%–100.0%] (n=338, docs=338); bundle split exact 8/8 | ≥ 97% / ≥ 95% (A2) |
| Data-quality validation pass rate, first pass | 65.6% [60.4%–70.5%] (n=337, docs=337) | ≥ 85% (A2) |
| Quote success rate (priceable submissions) | 65.6% [60.4%–70.5%] (n=337, docs=337) | ≥ 90% of valid uploads (proposed) |

## Thresholds (research A2) — pass/fail

| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |
|---|---|---|---|---|---|
| Claim # and DOI, normalized exact | native (PDF + XLSX/CSV) | 98 % | 86.9% [79.2%–92.1%] (n=9728, docs=158) | **fail** | no |
| Claim # and DOI, normalized exact | scan ≥ 300 dpi | 95 % | 66.1% [51.7%–78.0%] (n=3990, docs=60) | **fail** | no |
| Claim # and DOI, normalized exact | scan < 200 dpi / fax | 90 % | 42.2% [32.9%–52.0%] (n=7980, docs=120) | **fail** | no |
| Indemnity/medical paid and reserve per claim | native (PDF + XLSX/CSV) | 97 % | 86.8% [79.1%–92.0%] (n=19456, docs=158) | **fail** | no |
| Indemnity/medical paid and reserve per claim | scans | 93 % | 49.7% [42.0%–57.4%] (n=23940, docs=180) | **fail** | no |
| Claim-level completeness (all printed fields right) | native (PDF + XLSX/CSV) | 90 % | 63.5% [54.7%–71.5%] (n=4864, docs=158) | **fail** | no |
| Claim-level completeness (all printed fields right) | scans | 80 % | 48.1% [40.6%–55.8%] (n=5985, docs=180) | **fail** | no |
| Straight-through (no human touch) | recurring template (seen + unseen repeats) | 80 % | 82.3% [77.1%–86.5%] (n=260, docs=260) | pass | no |
| Straight-through (no human touch) | first-seen template | 65 % | 9.1% [4.2%–17.9%] (n=77, docs=77) | **fail** | no |
| Auto-classification precision (loss run vs other) | bundles + singles | 97 % | 100.0% [98.6%–100.0%] (n=336, docs=336) | pass | yes |
| Auto-classification recall (loss run vs other) | bundles + singles | 95 % | 99.4% [97.7%–100.0%] (n=338, docs=338) | pass | yes |
| Data-quality first-pass rate (hard rules, consistent docs) | all | 85 % | 65.6% [60.4%–70.5%] (n=337, docs=337) | **fail** | no |

## Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)

| Segment | Value |
|---|---|
| all | 66.4% [60.5%–71.9%] (n=65094, docs=338) |
| native-pdf | 76.1% [64.3%–84.9%] (n=16026, docs=78) |
| xlsx-csv | 100.0% [100.0%–100.0%] (n=13158, docs=80) |
| scan (all) | 49.8% [42.1%–57.6%] (n=35910, docs=180) |
| scan-300dpi-clean | 66.1% [51.7%–78.0%] (n=11970, docs=60) |
| scan-200dpi-rotated-noisy | 64.7% [50.6%–76.6%] (n=11970, docs=60) |
| scan-150dpi-fax | 18.8% [10.6%–31.0%] (n=11970, docs=60) |

## Key-field accuracy by template (seen / first-seen / unseen repeats)

| Segment | Value |
|---|---|
| seen · native-pdf | 94.8% [84.0%–99.1%] (n=6804, docs=38) |
| seen · scan | 77.0% [65.3%–85.7%] (n=12078, docs=72) |
| first-seen · native-pdf | 23.3% [8.8%–47.7%] (n=4506, docs=16) |
| first-seen · xlsx-csv | 100.0% [98.3%–100.0%] (n=276, docs=2) |
| first-seen · scan | 1.7% [0.0%–9.4%] (n=14826, docs=59) |
| unseen-repeat · native-pdf | 99.6% [98.4%–100.0%] (n=4716, docs=24) |
| unseen-repeat · xlsx-csv | 100.0% [100.0%–100.0%] (n=12882, docs=78) |
| unseen-repeat · scan | 92.5% [80.1%–98.0%] (n=9006, docs=49) |

## All printed fields

| Segment | Value |
|---|---|
| all | 68.1% [62.5%–73.2%] (n=154783, docs=338) |
| native-pdf | 77.6% [66.3%–85.9%] (n=36511, docs=78) |
| xlsx-csv | 96.9% [96.0%–97.6%] (n=36312, docs=80) |
| scan (all) | 51.1% [43.5%–58.6%] (n=81960, docs=180) |
| scan-300dpi-clean | 67.9% [54.0%–79.3%] (n=27320, docs=60) |
| scan-200dpi-rotated-noisy | 66.5% [52.8%–77.8%] (n=27320, docs=60) |
| scan-150dpi-fax | 18.8% [10.8%–30.7%] (n=27320, docs=60) |

## Claim joint pass (every printed field of the claim right)

| Segment | Value |
|---|---|
| all | 55.0% [49.1%–60.8%] (n=10849, docs=338) |
| native-pdf | 75.5% [63.8%–84.4%] (n=2671, docs=78) |
| xlsx-csv | 48.9% [37.1%–60.9%] (n=2193, docs=80) |
| scan (all) | 48.1% [40.6%–55.8%] (n=5985, docs=180) |
| scan-300dpi-clean | 66.1% [51.7%–78.0%] (n=1995, docs=60) |
| scan-200dpi-rotated-noisy | 64.3% [50.3%–76.2%] (n=1995, docs=60) |
| scan-150dpi-fax | 14.1% [7.7%–24.0%] (n=1995, docs=60) |

## Document: every printed field right

| Segment | Value |
|---|---|
| all | 42.0% [36.8%–47.4%] (n=338, docs=338) |
| native-pdf | 65.4% [54.3%–75.0%] (n=78, docs=78) |
| xlsx-csv | 50.0% [39.3%–60.7%] (n=80, docs=80) |
| scan (all) | 28.3% [22.2%–35.3%] (n=180, docs=180) |
| scan-300dpi-clean | 50.0% [37.7%–62.3%] (n=60, docs=60) |
| scan-200dpi-rotated-noisy | 35.0% [24.1%–47.7%] (n=60, docs=60) |
| scan-150dpi-fax | 0.0% [0.0%–7.2%] (n=60, docs=60) |

## Claim rows: recall (truth rows found)

| Segment | Value |
|---|---|
| all | 66.6% [60.6%–72.2%] (n=10849, docs=338) |
| native-pdf | 76.2% [65.6%–84.4%] (n=2671, docs=78) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2193, docs=80) |
| scan (all) | 50.1% [42.6%–57.6%] (n=5985, docs=180) |
| scan-300dpi-clean | 66.1% [51.9%–77.8%] (n=1995, docs=60) |
| scan-200dpi-rotated-noisy | 64.7% [50.6%–76.7%] (n=1995, docs=60) |
| scan-150dpi-fax | 19.6% [10.7%–33.1%] (n=1995, docs=60) |

## Claim rows: precision (extracted rows that exist)

| Segment | Value |
|---|---|
| all | 100.0% [99.9%–100.0%] (n=7230, docs=246) |
| native-pdf | 100.0% [99.8%–100.0%] (n=2036, docs=63) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2193, docs=80) |
| scan (all) | 100.0% [99.8%–100.0%] (n=3001, docs=103) |
| scan-300dpi-clean | 100.0% [99.6%–100.0%] (n=1318, docs=45) |
| scan-200dpi-rotated-noisy | 100.0% [99.6%–100.0%] (n=1291, docs=43) |
| scan-150dpi-fax | 100.0% [98.8%–100.0%] (n=392, docs=15) |

## Data-quality first pass (0 hard failures; source-consistent documents)

| Segment | Value |
|---|---|
| all | 65.6% [60.4%–70.5%] (n=337, docs=337) |
| native-pdf | 78.2% [67.7%–86.0%] (n=78, docs=78) |
| xlsx-csv | 100.0% [94.4%–100.0%] (n=79, docs=79) |
| scan (all) | 45.0% [37.9%–52.3%] (n=180, docs=180) |
| scan-300dpi-clean | 75.0% [62.7%–84.3%] (n=60, docs=60) |
| scan-200dpi-rotated-noisy | 58.3% [45.7%–69.9%] (n=60, docs=60) |
| scan-150dpi-fax | 1.7% [0.0%–9.7%] (n=60, docs=60) |

## Quote success (priceable: replay/learn/delta, 0 hard failures)

| Segment | Value |
|---|---|
| all | 65.6% [60.4%–70.5%] (n=337, docs=337) |
| native-pdf | 78.2% [67.7%–86.0%] (n=78, docs=78) |
| xlsx-csv | 100.0% [94.4%–100.0%] (n=79, docs=79) |
| scan (all) | 45.0% [37.9%–52.3%] (n=180, docs=180) |
| scan-300dpi-clean | 75.0% [62.7%–84.3%] (n=60, docs=60) |
| scan-200dpi-rotated-noisy | 58.3% [45.7%–69.9%] (n=60, docs=60) |
| scan-150dpi-fax | 1.7% [0.0%–9.7%] (n=60, docs=60) |

## Straight-through (no document review)

| Segment | Value |
|---|---|
| seen | 69.1% [59.9%–77.0%] (n=110, docs=110) |
| first-seen | 9.1% [4.2%–17.9%] (n=77, docs=77) |
| unseen-repeat | 92.0% [86.4%–95.5%] (n=150, docs=150) |

## Straight-through, strict (also no confirm-format item)

| Segment | Value |
|---|---|
| seen | 58.2% [48.8%–67.0%] (n=110, docs=110) |
| first-seen | 0.0% [0.0%–5.7%] (n=77, docs=77) |
| unseen-repeat | 79.3% [72.1%–85.1%] (n=150, docs=150) |

Classification precision 100.0% [98.6%–100.0%] (n=336, docs=336), recall 99.4% [97.7%–100.0%] (n=338, docs=338); bundle loss-run split exact 8/8. Guardrail: source-inconsistent documents kept out of straight-through 100.0% [16.7%–100.0%] (n=1, docs=1).

## Routing by carrier and input type

| Carrier | Input | Docs | Final tiers |
|---|---|---|---|
| c1 | native-pdf | 38 | ReplayAccepted 33, Review 3, Learned 2 |
| c1 | scan-150dpi-fax | 24 | Review 23, ReplayAccepted 1 |
| c1 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 16, Review 7, Learned 1 |
| c1 | scan-300dpi-clean | 24 | ReplayAccepted 23, Review 1 |
| c2 | native-pdf | 28 | ReplayAccepted 22, Learned 4, Review 2 |
| c2 | scan-150dpi-fax | 24 | Review 22, NotFound 2 |
| c2 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 18, Review 6 |
| c2 | scan-300dpi-clean | 24 | ReplayAccepted 21, Review 2, Learned 1 |
| c3 | native-pdf | 12 | Review 12 |
| c3 | scan-150dpi-fax | 12 | Review 12 |
| c3 | scan-200dpi-rotated-noisy | 12 | Review 12 |
| c3 | scan-300dpi-clean | 12 | Review 12 |
| c4 | xlsx | 40 | ReplayAccepted 38, Learned 1, Review 1 |
| c5 | csv | 40 | ReplayAccepted 39, Learned 1 |

## Latency and cost by input type (per document, in-process)

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| all | 338 | 4.9 | 50.2 | 0.0222 | 7.51 | 472 |
| native-pdf | 78 | 5.1 | 46.3 | 0.0231 | 1.80 | 94 |
| xlsx-csv | 80 | 0.1 | 0.1 | 0.0010 | 0.08 | 6 |
| scan (all) | 180 | 7.6 | 52.8 | 0.0313 | 5.63 | 372 |
| scan-300dpi-clean | 60 | 5.5 | 46.7 | 0.0226 | 1.36 | 89 |
| scan-200dpi-rotated-noisy | 60 | 5.4 | 46.5 | 0.0238 | 1.43 | 94 |
| scan-150dpi-fax | 60 | 42.7 | 54.1 | 0.0474 | 2.84 | 189 |

## Latency and cost by final path

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| Learned | 10 | 19.5 | 40.0 | 0.0312 | 0.31 | 20 |
| NotFound | 2 | 4.6 | 6.0 | 0.0135 | 0.03 | 2 |
| ReplayAccepted | 211 | 4.1 | 7.8 | 0.0074 | 1.56 | 74 |
| Review | 115 | 42.7 | 54.1 | 0.0488 | 5.62 | 376 |

## Error types (field values)

| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |
|---|---|---|---|---|
| all | 105,377 | 49,298 | 96 | 12 |
| native-pdf | 28,317 | 8,194 | 0 | 0 |
| xlsx-csv | 35,192 | 1,120 | 0 | 0 |
| scan (all) | 41,868 | 39,984 | 96 | 12 |
| scan-300dpi-clean | 18,557 | 8,757 | 0 | 6 |
| scan-200dpi-rotated-noisy | 18,162 | 9,137 | 15 | 6 |
| scan-150dpi-fax | 5,149 | 22,090 | 81 | 0 |

## Edge-case strata

| Stratum | Docs | Quote success | Key-field accuracy | Review |
|---|---|---|---|---|
| missing-year | 16 | 50.0% [28.0%–72.0%] (n=16, docs=16) | 50.8% [27.6%–73.7%] (n=2784, docs=16) | 8 |
| partial-run | 12 | 75.0% [46.2%–91.7%] (n=12, docs=12) | 80.3% [51.7%–94.7%] (n=2652, docs=12) | 3 |
| closed-only | 10 | 70.0% [39.2%–89.7%] (n=10, docs=10) | 70.8% [35.9%–91.7%] (n=1602, docs=10) | 3 |
| reopened | 14 | 50.0% [26.8%–73.2%] (n=14, docs=14) | 26.9% [10.2%–53.5%] (n=1896, docs=14) | 7 |
| cat-outlier | 14 | 42.9% [20.9%–68.0%] (n=14, docs=14) | 36.6% [15.9%–63.7%] (n=2496, docs=14) | 8 |
| multi-policy | 10 | 70.0% [39.2%–89.7%] (n=10, docs=10) | 85.8% [55.4%–98.0%] (n=1932, docs=10) | 3 |
| non-ca | 2 | 100.0% [29.0%–100.0%] (n=2, docs=2) | 100.0% [99.2%–100.0%] (n=552, docs=2) | 0 |
| source-inconsistent | 1 | 0.0% [0.0%–83.3%] (n=1, docs=1) | 100.0% [94.8%–100.0%] (n=84, docs=1) | 1 |
| password-protected | 10 | 100.0% [67.9%–100.0%] (n=10, docs=10) | 100.0% [99.8%–100.0%] (n=2358, docs=10) | 0 |
| bundle | 8 | 100.0% [62.8%–100.0%] (n=8, docs=8) | 100.0% [99.7%–100.0%] (n=1698, docs=8) | 0 |
| non-wc-rows | 1 | 100.0% [16.7%–100.0%] (n=1, docs=1) | 100.0% [96.1%–100.0%] (n=114, docs=1) | 0 |
