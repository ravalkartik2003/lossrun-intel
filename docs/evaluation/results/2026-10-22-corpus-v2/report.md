# Evaluation report — synthetic corpus

Run 2026-10-22 20:47–21:02 UTC · git `b066eaa060` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v5` · gate `learn-gate-0.1.0` · catalog `0.4.0` · prices `0.1.0` · corpus `corpus-gen-0.2.0` seed 8

302 documents · 150,233 scored field values · metered cost $2.92

Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).

## Deck table — by input type

Every document is in exactly one row; no blended headline. Delivered = key fields (Claim #, DOI, indemnity/medical paid and reserve) on documents that left without a human. Review rate = documents routed to a human. Pre-fill = key fields of what the reviewer starts from on those documents. Committed rows carry a proposal target; the others carry the measured numbers.

> Synthetic corpus: generated documents from five fictional carriers, not client documents. Real carrier documents will be messier; the sealed blind set on the client's archive is the number that counts. Rows with fewer than 5 documents are an insufficient sample, not a result.

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| native PDF, recurring template | 65 | 100.0% [100.0%–100.0%] (n=13386, docs=63) | 3.1% [0.2%–11.5%] (n=65, docs=65) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 99% delivered — met |
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | — (none reviewed) | 2.000 | ≥ 98% delivered — insufficient sample (n = 1 document): no verdict |
| XLSX / CSV | 80 | 100.0% [100.0%–100.0%] (n=15318, docs=80) | 0.0% [0.0%–5.5%] (n=80, docs=80) | — (none reviewed) | 0.062 | ≥ 98% delivered — met |
| scan ≥ 300 dpi | 48 | 100.0% [100.0%–100.0%] (n=9990, docs=46) | 4.2% [0.4%–14.8%] (n=48, docs=48) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 95% delivered — met |
| scan 200 dpi rotated | 48 | 100.0% [99.9%–100.0%] (n=7272, docs=35) | 27.1% [16.5%–41.1%] (n=48, docs=48) | 99.8% [99.6%–99.9%] (n=3150, docs=13) | 0.000 | measured, not committed; review rate 27.1%, pre-fill 99.8% |
| fax < 200 dpi | 48 | — (none delivered) | 100.0% [91.2%–100.0%] (n=48, docs=48) | 89.1% [86.2%–91.4%] (n=10422, docs=48) | 0.000 | routed to review by design; pre-fill 89.1% |
| narrative statement | 12 | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0.0% [0.0%–0.2%] (n=1992, docs=12) | 0.000 | routed to review by design; pre-fill is the statement's paragraphs, no structured values |

Data-quality first pass and quote success by input type (source-consistent documents; rates on the delivered ones):

| Input type | Docs | DQ first pass (delivered) | Quote success (delivered) | Review rate | n delivered |
|---|---:|---|---|---|---:|
| native PDF, recurring template | 63 | 100.0% [93.1%–100.0%] (n=63, docs=63) | 100.0% [93.1%–100.0%] (n=63, docs=63) | 0.0% [0.0%–6.9%] (n=63, docs=63) | 63 |
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 1 |
| XLSX / CSV | 79 | 100.0% [94.4%–100.0%] (n=79, docs=79) | 100.0% [94.4%–100.0%] (n=79, docs=79) | 0.0% [0.0%–5.6%] (n=79, docs=79) | 79 |
| scan ≥ 300 dpi | 46 | 100.0% [90.8%–100.0%] (n=46, docs=46) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0.0% [0.0%–9.2%] (n=46, docs=46) | 46 |
| scan 200 dpi rotated | 46 | 100.0% [88.2%–100.0%] (n=35, docs=35) | 100.0% [88.2%–100.0%] (n=35, docs=35) | 23.9% [13.8%–38.1%] (n=46, docs=46) | 35 |
| fax < 200 dpi | 46 | — (none delivered) | — (none delivered) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0 |
| narrative statement | 12 | — (none delivered) | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0 |

## Requirement acceptance criteria (placeholders filled)

Delivered = values that leave without a human (a Review document delivers nothing). Pre-fill = the same measure on Review documents' pre-fill, what the reviewer starts from (scoring-protocol.md §3).

| Criterion | Delivered (no human) | Pre-fill (Review documents) | Proposed target |
|---|---|---|---|
| Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) — by input type, see the deck table | native PDF, recurring template 100.0% [100.0%–100.0%] (n=13386, docs=63); native PDF, first-seen template 100.0% [91.2%–100.0%] (n=48, docs=1); XLSX / CSV 100.0% [100.0%–100.0%] (n=15318, docs=80); scan ≥ 300 dpi 100.0% [100.0%–100.0%] (n=9990, docs=46); scan 200 dpi rotated 100.0% [99.9%–100.0%] (n=7272, docs=35) | native PDF, recurring template 100.0% [98.9%–100.0%] (n=432, docs=2); scan ≥ 300 dpi 100.0% [98.9%–100.0%] (n=432, docs=2); scan 200 dpi rotated 99.8% [99.6%–99.9%] (n=3150, docs=13); fax < 200 dpi 89.1% [86.2%–91.4%] (n=10422, docs=48); narrative statement 0.0% [0.0%–0.2%] (n=1992, docs=12) | committed rows: ≥ 99% native recurring, ≥ 98% native first-seen and XLSX/CSV, ≥ 95% scans ≥ 300 dpi |
| End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU) | p50 4.8 s, p95 12.8 s over 302 documents (deployed API figures: the API work) | — | ≤ 60 s p95 (proposed) |
| Auto-classification precision / recall | 100.0% [98.5%–100.0%] (n=302, docs=302) / 100.0% [98.5%–100.0%] (n=302, docs=302); bundle split exact 8/8 | — | ≥ 97% / ≥ 95% (A2) |
| Data-quality validation pass rate, first pass | 76.5% [71.3%–81.0%] (n=293, docs=293) — blended, mixes the by-design review rows (fax, narrative); not for the deck: use the by-input-type table | — | ≥ 85% (A2) |
| Quote success rate (priceable submissions) | 76.5% [71.3%–81.0%] (n=293, docs=293) — blended, mixes the by-design review rows; not for the deck: use the by-input-type table | — | ≥ 90% of valid uploads (proposed) |

## the evaluation harness targets

| Target | Segment | Threshold | Measured (delivered) | Result |
|---|---|---|---|---|
| Recurring-template delivered key-field accuracy | native PDF | 99 % | 96.9% [86.3%–100.0%] (n=13818, docs=65) | **not met** |
| Key fields | scan ≥ 300 dpi | 95 % | 91.5% [80.2%–97.0%] (n=10920, docs=51) | **not met** |
| Key fields | scan 200 dpi rotated | 90 % | 66.6% [51.3%–79.1%] (n=10920, docs=51) | **not met** |
| Key fields (reported, not promised) | fax 150 dpi | — | 0.0% [0.0%–0.0%] (n=10920, docs=51) | reported |
| DQ first pass (source-consistent) | excluding narrative | 85 % | 79.7% [74.6%–84.0%] (n=281, docs=281) | **not met** |
| STP | recurring templates | 85 % | 79.5% [74.3%–83.8%] (n=278, docs=278) | **not met** |
| STP (context: fax is reported, not promised) | recurring templates excluding fax | 85 % | 95.3% [91.6%–97.4%] (n=232, docs=232) | met |
| DQ first pass (context) | excluding narrative and fax | 85 % | 95.3% [91.7%–97.5%] (n=235, docs=235) | met |
| Recurring-template delivered key-field accuracy (context) | native PDF, source-consistent | 99 % | 100.0% [100.0%–100.0%] (n=13386, docs=63) | met |
| Learn/repair model calls per document | c1 + c2 | 0.3 | 0.010 per document (2 calls / 210 documents); target < 0.3 | met |

## Model calls per document by carrier (learn / delta / repair)

| Carrier | Docs | Calls | Per document | Docs with 0 calls |
|---|---:|---:|---:|---:|
| c1 | 110 | 0 | 0.000 | 110 |
| c2 | 100 | 2 | 0.020 | 99 |
| c3 | 12 | 0 | 0.000 | 12 |
| c4 | 40 | 2 | 0.050 | 39 |
| c5 | 40 | 3 | 0.075 | 39 |

## Thresholds (research A2) — pass/fail

| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |
|---|---|---|---|---|---|
| Claim # and DOI, normalized exact | native (PDF + XLSX/CSV) | 98 % | 96.9% [92.2%–99.0%] (n=9894, docs=149) | **fail** | no |
| Claim # and DOI, normalized exact | scan ≥ 300 dpi | 95 % | 91.5% [80.2%–97.0%] (n=3640, docs=51) | **fail** | no |
| Claim # and DOI, normalized exact | scan < 200 dpi / fax | 90 % | 33.3% [24.2%–43.8%] (n=7280, docs=102) | **fail** | no |
| Indemnity/medical paid and reserve per claim | native (PDF + XLSX/CSV) | 97 % | 96.9% [92.2%–99.0%] (n=19788, docs=149) | **fail** | no |
| Indemnity/medical paid and reserve per claim | scans | 93 % | 52.7% [44.1%–61.1%] (n=21840, docs=153) | **fail** | no |
| Claim-level completeness (all printed fields right) | native (PDF + XLSX/CSV) | 90 % | 96.6% [92.1%–98.7%] (n=4947, docs=149) | pass | yes |
| Claim-level completeness (all printed fields right) | scans | 80 % | 52.7% [44.1%–61.1%] (n=5460, docs=153) | **fail** | no |
| Straight-through (no human touch) | recurring template (seen + unseen repeats) | 80 % | 79.5% [74.3%–83.8%] (n=278, docs=278) | **fail** | no |
| Straight-through (no human touch) | first-seen template | 65 % | 20.0% [6.3%–46.0%] (n=15, docs=15) | **fail** | no |
| Auto-classification precision (loss run vs other) | bundles + singles | 97 % | 100.0% [98.5%–100.0%] (n=302, docs=302) | pass | yes |
| Auto-classification recall (loss run vs other) | bundles + singles | 95 % | 100.0% [98.5%–100.0%] (n=302, docs=302) | pass | yes |
| Data-quality first-pass rate (hard rules, consistent docs) | all | 85 % | 76.5% [71.3%–81.0%] (n=293, docs=293) | **fail** | no |

## Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)

| Segment | Value |
|---|---|
| all | 73.7% [67.6%–79.0%] (n=62442, docs=302) |
| native-pdf | 93.5% [84.4%–97.8%] (n=14364, docs=69) |
| xlsx-csv | 100.0% [100.0%–100.0%] (n=15318, docs=80) |
| scan (all) | 52.7% [44.1%–61.1%] (n=32760, docs=153) |
| scan-300dpi-clean | 91.5% [80.2%–97.0%] (n=10920, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [51.3%–79.1%] (n=10920, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=10920, docs=51) |

## Key-field accuracy by template (seen / first-seen / unseen repeats)

| Segment | Value |
|---|---|
| seen · native-pdf | 98.8% [92.2%–100.0%] (n=7416, docs=38) |
| seen · scan | 61.7% [49.6%–72.6%] (n=14724, docs=72) |
| first-seen · native-pdf | 8.8% [0.0%–51.9%] (n=546, docs=4) |
| first-seen · xlsx-csv | 100.0% [98.8%–100.0%] (n=384, docs=2) |
| first-seen · scan | 0.0% [0.0%–0.3%] (n=1494, docs=9) |
| unseen-repeat · native-pdf | 94.7% [72.8%–100.0%] (n=6402, docs=27) |
| unseen-repeat · xlsx-csv | 100.0% [100.0%–100.0%] (n=14934, docs=78) |
| unseen-repeat · scan | 49.4% [37.2%–61.7%] (n=16542, docs=72) |

## Pre-fill key-field accuracy (Review documents: what the reviewer starts from)

| Segment | Value |
|---|---|
| all | 80.9% [73.4%–86.7%] (n=16428, docs=77) |
| native-pdf | 46.5% [13.5%–82.8%] (n=930, docs=5) |
| scan (all) | 83.0% [76.1%–88.2%] (n=15498, docs=72) |
| scan-300dpi-clean | 46.5% [13.5%–82.8%] (n=930, docs=5) |
| scan-200dpi-rotated-noisy | 86.2% [63.4%–96.6%] (n=3648, docs=16) |
| scan-150dpi-fax | 85.0% [79.2%–89.4%] (n=10920, docs=51) |

## Pre-fill, all printed fields (Review documents)

| Segment | Value |
|---|---|
| all | 80.6% [73.5%–86.1%] (n=37725, docs=77) |
| native-pdf | 47.9% [14.4%–83.5%] (n=2107, docs=5) |
| scan (all) | 82.5% [76.0%–87.5%] (n=35618, docs=72) |
| scan-300dpi-clean | 47.9% [14.4%–83.5%] (n=2107, docs=5) |
| scan-200dpi-rotated-noisy | 85.8% [64.6%–95.9%] (n=8400, docs=16) |
| scan-150dpi-fax | 84.3% [78.9%–88.5%] (n=25111, docs=51) |

## All printed fields

| Segment | Value |
|---|---|
| all | 74.4% [68.6%–79.5%] (n=150233, docs=302) |
| native-pdf | 93.3% [84.6%–97.5%] (n=33021, docs=69) |
| xlsx-csv | 99.1% [98.9%–99.3%] (n=41879, docs=80) |
| scan (all) | 52.4% [44.0%–60.7%] (n=75333, docs=153) |
| scan-300dpi-clean | 91.2% [80.4%–96.6%] (n=25111, docs=51) |
| scan-200dpi-rotated-noisy | 66.2% [51.0%–78.6%] (n=25111, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.0%] (n=25111, docs=51) |

## Claim joint pass (every printed field of the claim right)

| Segment | Value |
|---|---|
| all | 73.5% [67.5%–78.8%] (n=10407, docs=302) |
| native-pdf | 93.5% [84.4%–97.8%] (n=2394, docs=69) |
| xlsx-csv | 99.4% [98.5%–99.8%] (n=2553, docs=80) |
| scan (all) | 52.7% [44.1%–61.1%] (n=5460, docs=153) |
| scan-300dpi-clean | 91.5% [80.2%–97.0%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [51.3%–79.1%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Document: every printed field right

| Segment | Value |
|---|---|
| all | 36.1% [30.9%–41.7%] (n=302, docs=302) |
| native-pdf | 53.6% [41.7%–65.1%] (n=69, docs=69) |
| xlsx-csv | 45.0% [34.5%–56.0%] (n=80, docs=80) |
| scan (all) | 23.5% [17.5%–30.9%] (n=153, docs=153) |
| scan-300dpi-clean | 41.2% [28.7%–54.8%] (n=51, docs=51) |
| scan-200dpi-rotated-noisy | 29.4% [18.6%–43.1%] (n=51, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–8.4%] (n=51, docs=51) |

## Claim rows: recall (truth rows found)

| Segment | Value |
|---|---|
| all | 73.7% [67.4%–79.2%] (n=10407, docs=302) |
| native-pdf | 93.5% [84.5%–97.8%] (n=2394, docs=69) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2553, docs=80) |
| scan (all) | 52.7% [44.3%–60.9%] (n=5460, docs=153) |
| scan-300dpi-clean | 91.5% [81.5%–96.6%] (n=1820, docs=51) |
| scan-200dpi-rotated-noisy | 66.6% [50.7%–79.4%] (n=1820, docs=51) |
| scan-150dpi-fax | 0.0% [0.0%–0.3%] (n=1820, docs=51) |

## Claim rows: precision (extracted rows that exist)

| Segment | Value |
|---|---|
| all | 100.0% [99.9%–100.0%] (n=7669, docs=225) |
| native-pdf | 100.0% [99.8%–100.0%] (n=2239, docs=64) |
| xlsx-csv | 100.0% [99.8%–100.0%] (n=2553, docs=80) |
| scan (all) | 100.0% [99.8%–100.0%] (n=2877, docs=81) |
| scan-300dpi-clean | 100.0% [99.7%–100.0%] (n=1665, docs=46) |
| scan-200dpi-rotated-noisy | 100.0% [99.6%–100.0%] (n=1212, docs=35) |

## Data-quality first pass (0 hard failures; source-consistent documents)

| Segment | Value |
|---|---|
| all | 76.5% [71.3%–81.0%] (n=293, docs=293) |
| native-pdf | 95.5% [87.1%–99.0%] (n=67, docs=67) |
| xlsx-csv | 100.0% [94.4%–100.0%] (n=79, docs=79) |
| scan (all) | 55.1% [47.0%–62.9%] (n=147, docs=147) |
| scan-300dpi-clean | 93.9% [82.9%–98.5%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 71.4% [57.3%–82.4%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Quote success (priceable: replay/learn/delta, 0 hard failures)

| Segment | Value |
|---|---|
| all | 76.5% [71.3%–81.0%] (n=293, docs=293) |
| native-pdf | 95.5% [87.1%–99.0%] (n=67, docs=67) |
| xlsx-csv | 100.0% [94.4%–100.0%] (n=79, docs=79) |
| scan (all) | 55.1% [47.0%–62.9%] (n=147, docs=147) |
| scan-300dpi-clean | 93.9% [82.9%–98.5%] (n=49, docs=49) |
| scan-200dpi-rotated-noisy | 71.4% [57.3%–82.4%] (n=49, docs=49) |
| scan-150dpi-fax | 0.0% [0.0%–8.7%] (n=49, docs=49) |

## Straight-through (no document review)

| Segment | Value |
|---|---|
| seen | 75.5% [66.4%–82.7%] (n=106, docs=106) |
| first-seen | 20.0% [6.3%–46.0%] (n=15, docs=15) |
| unseen-repeat | 82.0% [75.3%–87.2%] (n=172, docs=172) |

## Straight-through, strict (also no confirm-format item)

| Segment | Value |
|---|---|
| seen | 75.5% [66.4%–82.7%] (n=106, docs=106) |
| first-seen | 0.0% [0.0%–23.9%] (n=15, docs=15) |
| unseen-repeat | 76.2% [69.0%–82.1%] (n=172, docs=172) |

Classification precision 100.0% [98.5%–100.0%] (n=302, docs=302), recall 100.0% [98.5%–100.0%] (n=302, docs=302); bundle loss-run split exact 8/8. Guardrail: source-inconsistent documents kept out of straight-through 88.9% [54.3%–100.0%] (n=9, docs=9).

## Routing by carrier and input type

| Carrier | Input | Docs | Final tiers |
|---|---|---|---|
| c1 | native-pdf | 38 | ReplayAccepted 37, Review 1 |
| c1 | scan-150dpi-fax | 24 | Review 24 |
| c1 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 20, Review 4 |
| c1 | scan-300dpi-clean | 24 | ReplayAccepted 23, Review 1 |
| c2 | native-pdf | 28 | ReplayAccepted 26, Learned 1, Review 1 |
| c2 | scan-150dpi-fax | 24 | Review 24 |
| c2 | scan-200dpi-rotated-noisy | 24 | ReplayAccepted 15, Review 9 |
| c2 | scan-300dpi-clean | 24 | ReplayAccepted 23, Review 1 |
| c3 | native-pdf | 3 | Review 3 |
| c3 | scan-150dpi-fax | 3 | Review 3 |
| c3 | scan-200dpi-rotated-noisy | 3 | Review 3 |
| c3 | scan-300dpi-clean | 3 | Review 3 |
| c4 | xlsx | 40 | ReplayAccepted 39, Learned 1 |
| c5 | csv | 40 | ReplayAccepted 39, Learned 1 |

## Latency and cost by input type (per document, in-process)

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| all | 302 | 4.8 | 12.8 | 0.0097 | 2.92 | 165 |
| native-pdf | 69 | 5.1 | 170.1 | 0.0138 | 0.95 | 37 |
| xlsx-csv | 80 | 0.1 | 0.2 | 0.0008 | 0.06 | 5 |
| scan (all) | 153 | 5.6 | 13.7 | 0.0125 | 1.90 | 123 |
| scan-300dpi-clean | 51 | 5.3 | 12.0 | 0.0123 | 0.63 | 41 |
| scan-200dpi-rotated-noisy | 51 | 5.4 | 15.4 | 0.0124 | 0.63 | 41 |
| scan-150dpi-fax | 51 | 6.0 | 8.8 | 0.0126 | 0.64 | 41 |

## Latency and cost by final path

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| Learned | 3 | 32.1 | 32.6 | 0.0338 | 0.10 | 7 |
| ReplayAccepted | 222 | 4.2 | 10.9 | 0.0084 | 1.87 | 96 |
| Review | 77 | 5.7 | 104.1 | 0.0123 | 0.95 | 62 |

## Error types (field values)

| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |
|---|---|---|---|---|
| all | 111,817 | 38,394 | 0 | 22 |
| native-pdf | 30,799 | 2,222 | 0 | 0 |
| xlsx-csv | 41,507 | 357 | 0 | 15 |
| scan (all) | 39,511 | 35,815 | 0 | 7 |
| scan-300dpi-clean | 22,899 | 2,208 | 0 | 4 |
| scan-200dpi-rotated-noisy | 16,612 | 8,496 | 0 | 3 |
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
| source-inconsistent | 9 | 11.1% [0.0%–45.7%] (n=9, docs=9) | 16.3% [1.3%–56.1%] (n=2064, docs=9) | 8 |
| password-protected | 10 | 100.0% [67.9%–100.0%] (n=10, docs=10) | 100.0% [99.8%–100.0%] (n=1854, docs=10) | 0 |
| bundle | 8 | 100.0% [62.8%–100.0%] (n=8, docs=8) | 100.0% [99.7%–100.0%] (n=1590, docs=8) | 0 |
| non-wc-rows | 5 | 100.0% [51.1%–100.0%] (n=5, docs=5) | 100.0% [99.7%–100.0%] (n=1578, docs=5) | 0 |
