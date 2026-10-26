# Evaluation report — imported archive

Run 2026-10-23 20:00–20:02 UTC · git `587a1d5102` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v5` · gate `learn-gate-0.1.0` · catalog `0.4.0` · prices `0.1.0` · corpus `import`

3 documents · 1,459 scored field values · metered cost $0.13

Intervals: 95% Agresti-Coull on the cluster-adjusted effective n (document-level bootstrap design effect).

## Deck table — by input type (`docs/evaluation/results/2026-10-22-corpus-v2/`)

Every document is in exactly one row; no blended headline. Delivered = key fields (Claim #, DOI, indemnity/medical paid and reserve) on documents that left without a human. Review rate = documents routed to a human. Pre-fill = key fields of what the reviewer starts from on those documents. Committed rows carry a proposal target; the others carry the measured numbers.

> Imported archive (`lossrun eval import`). Rows with fewer than 5 documents are an insufficient sample, not a result.

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | — (none reviewed) | 1.000 | ≥ 98% delivered — insufficient sample (n = 1 document): no verdict |
| scan 200 dpi rotated | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | — (none reviewed) | 0.000 | measured, not committed; review rate 0.0%, pre-fill — |
| ZIP package | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | — (none reviewed) | 5.000 | measured, not committed; review rate 0.0%, pre-fill — |

Data-quality first pass and quote success by input type (source-consistent documents; rates on the delivered ones):

| Input type | Docs | DQ first pass (delivered) | Quote success (delivered) | Review rate | n delivered |
|---|---:|---|---|---|---:|
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 1 |
| scan 200 dpi rotated | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 1 |
| ZIP package | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 1 |

## Requirement acceptance criteria (placeholders filled)

Delivered = values that leave without a human (a Review document delivers nothing). Pre-fill = the same measure on Review documents' pre-fill, what the reviewer starts from (scoring-protocol.md §3).

| Criterion | Delivered (no human) | Pre-fill (Review documents) | Proposed target |
|---|---|---|---|
| Extraction accuracy (key fields: Claim #, DOI, paid/reserve splits) — by input type, see the deck table | native PDF, first-seen template 100.0% [98.4%–100.0%] (n=294, docs=1); scan 200 dpi rotated 100.0% [97.6%–100.0%] (n=186, docs=1); ZIP package 100.0% [99.2%–100.0%] (n=606, docs=1) |  | committed rows: ≥ 99% native recurring, ≥ 98% native first-seen and XLSX/CSV, ≥ 95% scans ≥ 300 dpi |
| End-to-end latency, ≤ 15 MB upload (per document, in-process pipeline incl. CU) | p50 26.1 s, p95 71.3 s over 3 documents (deployed API figures: the API work) | — | ≤ 60 s p95 (proposed) |
| Auto-classification precision / recall | 100.0% [38.3%–100.0%] (n=3, docs=3) / 100.0% [38.3%–100.0%] (n=3, docs=3); bundle split exact 0/0 | — | ≥ 97% / ≥ 95% (A2) |
| Data-quality validation pass rate, first pass | 100.0% [38.3%–100.0%] (n=3, docs=3) — blended, mixes the by-design review rows (fax, narrative); not for the deck: use the by-input-type table | — | ≥ 85% (A2) |
| Quote success rate (priceable submissions) | 100.0% [38.3%–100.0%] (n=3, docs=3) — blended, mixes the by-design review rows; not for the deck: use the by-input-type table | — | ≥ 90% of valid uploads (proposed) |

## the evaluation harness targets

| Target | Segment | Threshold | Measured (delivered) | Result |
|---|---|---|---|---|
| Recurring-template delivered key-field accuracy | native PDF | 99 % | n/a | **not met** |
| Key fields | scan ≥ 300 dpi | 95 % | n/a | **not met** |
| Key fields | scan 200 dpi rotated | 90 % | 100.0% [97.6%–100.0%] (n=186, docs=1) | met |
| Key fields (reported, not promised) | fax 150 dpi | — | n/a | reported |
| DQ first pass (source-consistent) | excluding narrative | 85 % | 100.0% [38.3%–100.0%] (n=3, docs=3) | met (CI lower bound below target) |
| STP | recurring templates | 85 % | 100.0% [16.7%–100.0%] (n=1, docs=1) | met (CI lower bound below target) |
| STP (context: fax is reported, not promised) | recurring templates excluding fax | 85 % | 100.0% [16.7%–100.0%] (n=1, docs=1) | met (CI lower bound below target) |
| DQ first pass (context) | excluding narrative and fax | 85 % | 100.0% [38.3%–100.0%] (n=3, docs=3) | met (CI lower bound below target) |
| Recurring-template delivered key-field accuracy (context) | native PDF, source-consistent | 99 % | n/a | **not met** |
| Learn/repair model calls per document | c1 + c2 | 0.3 | 0.000 per document (0 calls / 0 documents); target < 0.3 | met |

## Model calls per document by carrier (learn / delta / repair)

| Carrier | Docs | Calls | Per document | Docs with 0 calls |
|---|---:|---:|---:|---:|
| Fictional Casualty Group | 2 | 1 | 0.500 | 1 |
| Notional Workers Comp Exchange | 1 | 5 | 5.000 | 0 |

## Thresholds (research A2) — pass/fail

| Criterion | Segment | Threshold | Measured | Pass | CI lower ≥ threshold |
|---|---|---|---|---|---|
| Claim # and DOI, normalized exact | native (PDF + XLSX/CSV) | 98 % | 100.0% [95.5%–100.0%] (n=98, docs=1) | pass | no |
| Claim # and DOI, normalized exact | scan ≥ 300 dpi | 95 % | n/a | **fail** | no |
| Claim # and DOI, normalized exact | scan < 200 dpi / fax | 90 % | 100.0% [93.0%–100.0%] (n=62, docs=1) | pass | yes |
| Indemnity/medical paid and reserve per claim | native (PDF + XLSX/CSV) | 97 % | 100.0% [97.7%–100.0%] (n=196, docs=1) | pass | yes |
| Indemnity/medical paid and reserve per claim | scans | 93 % | 100.0% [96.4%–100.0%] (n=124, docs=1) | pass | yes |
| Claim-level completeness (all printed fields right) | native (PDF + XLSX/CSV) | 90 % | 100.0% [91.3%–100.0%] (n=49, docs=1) | pass | yes |
| Claim-level completeness (all printed fields right) | scans | 80 % | 100.0% [86.9%–100.0%] (n=31, docs=1) | pass | yes |
| Straight-through (no human touch) | recurring template (seen + unseen repeats) | 80 % | 100.0% [16.7%–100.0%] (n=1, docs=1) | pass | no |
| Straight-through (no human touch) | first-seen template | 65 % | 100.0% [29.0%–100.0%] (n=2, docs=2) | pass | no |
| Auto-classification precision (loss run vs other) | bundles + singles | 97 % | 100.0% [38.3%–100.0%] (n=3, docs=3) | pass | no |
| Auto-classification recall (loss run vs other) | bundles + singles | 95 % | 100.0% [38.3%–100.0%] (n=3, docs=3) | pass | no |
| Data-quality first-pass rate (hard rules, consistent docs) | all | 85 % | 100.0% [38.3%–100.0%] (n=3, docs=3) | pass | no |

## Key-field accuracy (Claim #, DOI, indemnity/medical paid and reserve)

| Segment | Value |
|---|---|
| all | 100.0% [99.6%–100.0%] (n=1086, docs=3) |
| native-pdf | 100.0% [98.4%–100.0%] (n=294, docs=1) |
| scan (all) | 100.0% [97.6%–100.0%] (n=186, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [97.6%–100.0%] (n=186, docs=1) |

## Key-field accuracy by template (seen / first-seen / unseen repeats)

| Segment | Value |
|---|---|
| first-seen · native-pdf | 100.0% [98.4%–100.0%] (n=294, docs=1) |
| unseen-repeat · scan | 100.0% [97.6%–100.0%] (n=186, docs=1) |

## Pre-fill key-field accuracy (Review documents: what the reviewer starts from)

| Segment | Value |
|---|---|

## Pre-fill, all printed fields (Review documents)

| Segment | Value |
|---|---|

## All printed fields

| Segment | Value |
|---|---|
| all | 100.0% [99.7%–100.0%] (n=1459, docs=3) |
| native-pdf | 100.0% [98.8%–100.0%] (n=395, docs=1) |
| scan (all) | 100.0% [98.2%–100.0%] (n=252, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [98.2%–100.0%] (n=252, docs=1) |

## Claim joint pass (every printed field of the claim right)

| Segment | Value |
|---|---|
| all | 100.0% [97.4%–100.0%] (n=171, docs=3) |
| native-pdf | 100.0% [91.3%–100.0%] (n=49, docs=1) |
| scan (all) | 100.0% [86.9%–100.0%] (n=31, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [86.9%–100.0%] (n=31, docs=1) |

## Document: every printed field right

| Segment | Value |
|---|---|
| all | 100.0% [38.3%–100.0%] (n=3, docs=3) |
| native-pdf | 100.0% [16.7%–100.0%] (n=1, docs=1) |
| scan (all) | 100.0% [16.7%–100.0%] (n=1, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [16.7%–100.0%] (n=1, docs=1) |

## Claim rows: recall (truth rows found)

| Segment | Value |
|---|---|
| all | 100.0% [97.5%–100.0%] (n=181, docs=3) |
| native-pdf | 100.0% [91.3%–100.0%] (n=49, docs=1) |
| scan (all) | 100.0% [86.9%–100.0%] (n=31, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [86.9%–100.0%] (n=31, docs=1) |

## Claim rows: precision (extracted rows that exist)

| Segment | Value |
|---|---|
| all | 100.0% [97.5%–100.0%] (n=181, docs=3) |
| native-pdf | 100.0% [91.3%–100.0%] (n=49, docs=1) |
| scan (all) | 100.0% [86.9%–100.0%] (n=31, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [86.9%–100.0%] (n=31, docs=1) |

## Data-quality first pass (0 hard failures; source-consistent documents)

| Segment | Value |
|---|---|
| all | 100.0% [38.3%–100.0%] (n=3, docs=3) |
| native-pdf | 100.0% [16.7%–100.0%] (n=1, docs=1) |
| scan (all) | 100.0% [16.7%–100.0%] (n=1, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [16.7%–100.0%] (n=1, docs=1) |

## Quote success (priceable: replay/learn/delta, 0 hard failures)

| Segment | Value |
|---|---|
| all | 100.0% [38.3%–100.0%] (n=3, docs=3) |
| native-pdf | 100.0% [16.7%–100.0%] (n=1, docs=1) |
| scan (all) | 100.0% [16.7%–100.0%] (n=1, docs=1) |
| scan-200dpi-rotated-noisy | 100.0% [16.7%–100.0%] (n=1, docs=1) |

## Straight-through (no document review)

| Segment | Value |
|---|---|
| first-seen | 100.0% [29.0%–100.0%] (n=2, docs=2) |
| unseen-repeat | 100.0% [16.7%–100.0%] (n=1, docs=1) |

## Straight-through, strict (also no confirm-format item)

| Segment | Value |
|---|---|
| first-seen | 0.0% [0.0%–71.0%] (n=2, docs=2) |
| unseen-repeat | 0.0% [0.0%–83.3%] (n=1, docs=1) |

Classification precision 100.0% [38.3%–100.0%] (n=3, docs=3), recall 100.0% [38.3%–100.0%] (n=3, docs=3); bundle loss-run split exact 0/0. Guardrail: source-inconsistent documents kept out of straight-through n/a.

## Routing by carrier and input type

| Carrier | Input | Docs | Final tiers |
|---|---|---|---|
| Fictional Casualty Group | native-pdf | 1 | Learned 1 |
| Fictional Casualty Group | scan-200dpi-rotated-noisy | 1 | ReplayAccepted 1 |
| Notional Workers Comp Exchange | zip | 1 | Learned 1 |

## Latency and cost by input type (per document, in-process)

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| all | 3 | 26.1 | 71.3 | 0.0438 | 0.13 | 8 |
| native-pdf | 1 | 26.1 | 26.1 | 0.0374 | 0.04 | 2 |
| scan (all) | 1 | 3.8 | 3.8 | 0.0134 | 0.01 | 1 |
| scan-200dpi-rotated-noisy | 1 | 3.8 | 3.8 | 0.0134 | 0.01 | 1 |

## Latency and cost by final path

| Segment | Docs | p50 s | p95 s | Mean cost $ | Total cost $ | LLM calls |
|---|---|---|---|---|---|---|
| Learned | 2 | 26.1 | 71.3 | 0.0590 | 0.12 | 7 |
| ReplayAccepted | 1 | 3.8 | 3.8 | 0.0134 | 0.01 | 1 |

## Error types (field values)

| Segment | ok | missing | wrong (caught by a hard rule) | wrong but plausible (silent) |
|---|---|---|---|---|
| all | 1,459 | 0 | 0 | 0 |
| native-pdf | 395 | 0 | 0 | 0 |
| xlsx-csv | 0 | 0 | 0 | 0 |
| scan (all) | 252 | 0 | 0 | 0 |
| scan-300dpi-clean | 0 | 0 | 0 | 0 |
| scan-200dpi-rotated-noisy | 252 | 0 | 0 | 0 |
| scan-150dpi-fax | 0 | 0 | 0 | 0 |

## Edge-case strata

| Stratum | Docs | Quote success | Key-field accuracy | Review |
|---|---|---|---|---|
| bundle | 1 | 100.0% [16.7%–100.0%] (n=1, docs=1) | 100.0% [99.2%–100.0%] (n=606, docs=1) | 0 |
