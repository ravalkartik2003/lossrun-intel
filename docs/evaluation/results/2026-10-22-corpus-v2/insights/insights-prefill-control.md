# Insight accuracy — feature engine vs truth (ADR 0011)

Catalog `1.0.0` · results `run-v2-08d-final` · 302 documents, 65 Review pre-fills (control) scored · truth reduced to printed fields.

Tolerances: counts exact; money within $1 or 0.1 % (the larger); ratios and rates within 1 % relative; days within 0.5. A metric value and every breakdown entry must be within tolerance. "Not measurable" = the corpus prints none of the metric's inputs (both sides null on every document).

| Metric | Unit | Tolerance | Documents compared | Within tolerance | Rate | Both null | Null on one side | Status |
|---|---|---|---:|---:|---:|---:|---:|---|
| claim_count | count | exact | 65 | 59 | 90.8 % | 0 | 0 | **6 miss(es)** |
| open_claim_count | count | exact | 65 | 63 | 96.9 % | 0 | 0 | **2 miss(es)** |
| closed_claim_count | count | exact | 65 | 61 | 93.8 % | 0 | 0 | **4 miss(es)** |
| reopened_claim_count | count | exact | 65 | 64 | 98.5 % | 0 | 0 | **1 miss(es)** |
| medical_only_count | count | exact | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |
| lost_time_count | count | exact | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |
| lost_time_share | ratio | 1 % rel. | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |
| paid_total | money | $1 / 0.1 % | 65 | 14 | 21.5 % | 0 | 0 | **51 miss(es)** |
| reserve_total | money | $1 / 0.1 % | 65 | 23 | 35.4 % | 0 | 0 | **42 miss(es)** |
| incurred_total | money | $1 / 0.1 % | 65 | 13 | 20.0 % | 0 | 0 | **52 miss(es)** |
| frequency_per_100_payroll | rate | 1 % rel. | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| frequency_per_100_fte | rate | 1 % rel. | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| claim_count_by_policy_year | count | exact | 65 | 39 | 60.0 % | 0 | 0 | **26 miss(es)** |
| severity_all | money | $1 / 0.1 % | 65 | 13 | 20.0 % | 0 | 0 | **52 miss(es)** |
| severity_lost_time | money | $1 / 0.1 % | 65 | 43 | 66.2 % | 35 | 0 | **22 miss(es)** |
| paid_to_incurred | ratio | 1 % rel. | 65 | 19 | 29.2 % | 0 | 0 | **46 miss(es)** |
| large_loss_count | count | exact | 65 | 22 | 33.8 % | 0 | 0 | **43 miss(es)** |
| large_loss_share | ratio | 1 % rel. | 65 | 16 | 24.6 % | 0 | 0 | **49 miss(es)** |
| capped_incurred | money | $1 / 0.1 % | 65 | 14 | 21.5 % | 0 | 0 | **51 miss(es)** |
| loss_drivers_cause_incurred | money | $1 / 0.1 % | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| loss_drivers_cause_count | count | exact | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| loss_drivers_body_part_incurred | money | $1 / 0.1 % | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| loss_drivers_body_part_count | count | exact | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| loss_drivers_nature_incurred | money | $1 / 0.1 % | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| loss_drivers_nature_count | count | exact | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| litigation_rate | ratio | 1 % rel. | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| ct_share | ratio | 1 % rel. | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |
| ct_indemnity_share | ratio | 1 % rel. | 65 | 65 | 100.0 % | 35 | 0 | within tolerance |
| post_termination_ct_count | count | exact | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| subrogation_recovery_rate | ratio | 1 % rel. | 65 | 65 | 100.0 % | 65 | 0 | not measurable on corpus v2 |
| closure_median_days | days | ±0.5 | 65 | 60 | 92.3 % | 30 | 0 | **5 miss(es)** |
| open_claim_age | count | exact | 65 | 62 | 95.4 % | 0 | 0 | **3 miss(es)** |
| reporting_lag_median_days | days | ±0.5 | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |
| reporting_lag_p90_days | days | ±0.5 | 65 | 64 | 98.5 % | 0 | 0 | **1 miss(es)** |
| reporting_lag_over_30_share | ratio | 1 % rel. | 65 | 61 | 93.8 % | 0 | 0 | **4 miss(es)** |
| incurred_by_policy_year | money | $1 / 0.1 % | 65 | 9 | 13.8 % | 0 | 0 | **56 miss(es)** |
| paid_by_policy_year | money | $1 / 0.1 % | 65 | 10 | 15.4 % | 0 | 0 | **55 miss(es)** |
| severity_by_policy_year | money | $1 / 0.1 % | 65 | 9 | 13.8 % | 0 | 0 | **56 miss(es)** |
| paid_to_incurred_by_policy_year | ratio | 1 % rel. | 65 | 12 | 18.5 % | 0 | 0 | **53 miss(es)** |
| non_ca_share | ratio | 1 % rel. | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |
| non_wc_excluded_count | count | exact | 65 | 65 | 100.0 % | 0 | 0 | within tolerance |

## Misses (first 5 per metric)

- **claim_count**: c2-002-scan-150dpi-fax: truth 31 vs extracted 32; c2-005-scan-150dpi-fax: truth 34 vs extracted 35; c2-013-scan-150dpi-fax: truth 38 vs extracted 40; c2-014-scan-150dpi-fax: truth 18 vs extracted 19; c2-021-scan-150dpi-fax: truth 58 vs extracted 59
- **open_claim_count**: c1-014-scan-150dpi-fax: truth 16 vs extracted 15; c2-009-scan-150dpi-fax: truth 10 vs extracted 6
- **closed_claim_count**: c1-016-scan-150dpi-fax: truth 33 vs extracted 32; c2-003-scan-150dpi-fax: truth 29 vs extracted 28; c2-008-scan-150dpi-fax: truth 30 vs extracted 28; c2-023-scan-150dpi-fax: truth 13 vs extracted 12
- **reopened_claim_count**: c1-014-scan-150dpi-fax: truth 3 vs extracted 2
- **paid_total**: c1-000-scan-150dpi-fax: truth 2129817.85 vs extracted 5301011.73; c1-001-scan-150dpi-fax: truth 575423.01 vs extracted 467926.39; c1-002-scan-150dpi-fax: truth 1767653 vs extracted 3201017.53; c1-003-scan-150dpi-fax: truth 1795263.34 vs extracted 1569086.08; c1-004-scan-150dpi-fax: truth 2130945.35 vs extracted 5744814.5
- **reserve_total**: c1-000-scan-150dpi-fax: truth 673033.35 vs extracted 585709.98; c1-001-scan-150dpi-fax: truth 10889.25 vs extracted 0; c1-002-scan-150dpi-fax: truth 402367.85 vs extracted 392508.41; c1-003-scan-150dpi-fax: truth 521284.19 vs extracted 1368425.56; c1-004-scan-150dpi-fax: truth 368251.68 vs extracted 4607102.01
- **incurred_total**: c1-000-scan-150dpi-fax: truth 2802851.2 vs extracted 5886721.71; c1-001-scan-150dpi-fax: truth 586312.26 vs extracted 467926.39; c1-002-scan-150dpi-fax: truth 2170020.85 vs extracted 3593525.94; c1-003-scan-150dpi-fax: truth 2316547.53 vs extracted 2937511.64; c1-004-scan-150dpi-fax: truth 2499197.03 vs extracted 10351916.51
- **claim_count_by_policy_year**: c1-000-scan-150dpi-fax: truth 3 vs extracted 2; c1-001-scan-200dpi-rotated-noisy: truth 2 vs extracted 1; c1-008-scan-200dpi-rotated-noisy: truth 3 vs extracted 2; c1-012-scan-150dpi-fax: truth 3 vs extracted 2; c1-020-scan-150dpi-fax: truth 3 vs extracted 2
- **severity_all**: c1-000-scan-150dpi-fax: truth 56057.02 vs extracted 117734.43; c1-001-scan-150dpi-fax: truth 48859.36 vs extracted 38993.87; c1-002-scan-150dpi-fax: truth 50465.6 vs extracted 83570.37; c1-003-scan-150dpi-fax: truth 42119.05 vs extracted 53409.3; c1-004-scan-150dpi-fax: truth 44628.52 vs extracted 184855.65
- **severity_lost_time**: c1-000-scan-150dpi-fax: truth 87018.31 vs extracted 75699.2; c1-001-scan-150dpi-fax: truth 91567.02 vs extracted 81584.83; c1-002-scan-150dpi-fax: truth 79175.61 vs extracted 156240.7; c1-003-scan-150dpi-fax: truth 86684.37 vs extracted 73252.91; c1-004-scan-150dpi-fax: truth 79060.03 vs extracted 309404.81
- **paid_to_incurred**: c1-000-scan-150dpi-fax: truth 0.759875 vs extracted 0.900503; c1-001-scan-150dpi-fax: truth 0.981428 vs extracted 1; c1-002-scan-150dpi-fax: truth 0.814579 vs extracted 0.890773; c1-003-scan-150dpi-fax: truth 0.774974 vs extracted 0.534155; c1-004-scan-150dpi-fax: truth 0.852652 vs extracted 0.554952
- **large_loss_count**: c1-000-scan-150dpi-fax: truth 6 vs extracted 6; c1-002-scan-150dpi-fax: truth 6 vs extracted 6; c1-003-scan-150dpi-fax: truth 4 vs extracted 4; c1-004-scan-150dpi-fax: truth 4 vs extracted 6; c1-006-scan-150dpi-fax: truth 7 vs extracted 10
- **large_loss_share**: c1-000-scan-150dpi-fax: truth 0.285653 vs extracted 0.683144; c1-001-scan-150dpi-fax: truth 0.194688 vs extracted 0.243945; c1-002-scan-150dpi-fax: truth 0.313082 vs extracted 0.584666; c1-003-scan-150dpi-fax: truth 0.233105 vs extracted 0.441133; c1-004-scan-150dpi-fax: truth 0.171479 vs extracted 0.818174
- **capped_incurred**: c1-000-scan-150dpi-fax: truth 2802851.2 vs extracted 2712723.71; c1-001-scan-150dpi-fax: truth 586312.26 vs extracted 467926.39; c1-002-scan-150dpi-fax: truth 2170020.85 vs extracted 2306311.49; c1-003-scan-150dpi-fax: truth 2316547.53 vs extracted 2288966.81; c1-004-scan-150dpi-fax: truth 2499197.03 vs extracted 2957804.19
- **closure_median_days**: c2-003-scan-150dpi-fax: truth 227 vs extracted 218.5; c2-008-scan-150dpi-fax: truth 250 vs extracted 232; c2-020-scan-150dpi-fax: truth 321 vs extracted 330; c2-022-scan-150dpi-fax: truth 495 vs extracted 514; c2-023-scan-150dpi-fax: truth 213 vs extracted 207
- **open_claim_age**: c1-014-scan-150dpi-fax: truth 16 vs extracted 15; c2-009-scan-150dpi-fax: truth 10 vs extracted 6; c2-020-scan-150dpi-fax: truth 22 vs extracted 21
- **reporting_lag_p90_days**: c2-022-scan-150dpi-fax: truth 51 vs extracted 56
- **reporting_lag_over_30_share**: c1-007-scan-150dpi-fax: truth 0.2 vs extracted 0.214286; c2-020-scan-150dpi-fax: truth 0.035088 vs extracted 0.036364; c2-021-scan-150dpi-fax: truth 0.068966 vs extracted 0.070175; c2-022-scan-150dpi-fax: truth 0.133333 vs extracted 0.135593
- **incurred_by_policy_year**: c1-000-scan-150dpi-fax: truth 2802851.2 vs extracted 5886721.71; c1-001-scan-200dpi-rotated-noisy: truth 586312.26 vs extracted 586312.26; c1-001-scan-150dpi-fax: truth 586312.26 vs extracted 467926.39; c1-002-scan-150dpi-fax: truth 2170020.85 vs extracted 3593525.94; c1-003-scan-150dpi-fax: truth 2316547.53 vs extracted 2937511.64
- **paid_by_policy_year**: c1-000-scan-150dpi-fax: truth 2129817.85 vs extracted 5301011.73; c1-001-scan-200dpi-rotated-noisy: truth 575423.01 vs extracted 575423.01; c1-001-scan-150dpi-fax: truth 575423.01 vs extracted 467926.39; c1-002-scan-150dpi-fax: truth 1767653 vs extracted 3201017.53; c1-003-scan-150dpi-fax: truth 1795263.34 vs extracted 1569086.08
- **severity_by_policy_year**: c1-000-scan-150dpi-fax: truth 56057.02 vs extracted 117734.43; c1-001-scan-200dpi-rotated-noisy: truth 48859.36 vs extracted 48859.36; c1-001-scan-150dpi-fax: truth 48859.36 vs extracted 38993.87; c1-002-scan-150dpi-fax: truth 50465.6 vs extracted 83570.37; c1-003-scan-150dpi-fax: truth 42119.05 vs extracted 53409.3
- **paid_to_incurred_by_policy_year**: c1-000-scan-150dpi-fax: truth 0.759875 vs extracted 0.900503; c1-001-scan-200dpi-rotated-noisy: truth 0.981428 vs extracted 0.981428; c1-001-scan-150dpi-fax: truth 0.981428 vs extracted 1; c1-002-scan-150dpi-fax: truth 0.814579 vs extracted 0.890773; c1-003-scan-150dpi-fax: truth 0.774974 vs extracted 0.534155
