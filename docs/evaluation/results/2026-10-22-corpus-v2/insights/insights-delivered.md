# Insight accuracy — feature engine vs truth (ADR 0011)

Catalog `1.0.0` · results `run-v2-08d-final` · 302 documents, 225 delivered scored · truth reduced to printed fields.

Tolerances: counts exact; money within $1 or 0.1 % (the larger); ratios and rates within 1 % relative; days within 0.5. A metric value and every breakdown entry must be within tolerance. "Not measurable" = the corpus prints none of the metric's inputs (both sides null on every document).

| Metric | Unit | Tolerance | Documents compared | Within tolerance | Rate | Both null | Null on one side | Status |
|---|---|---|---:|---:|---:|---:|---:|---|
| claim_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| open_claim_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| closed_claim_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| reopened_claim_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| medical_only_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| lost_time_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| lost_time_share | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| paid_total | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| reserve_total | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| incurred_total | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| frequency_per_100_payroll | rate | 1 % rel. | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| frequency_per_100_fte | rate | 1 % rel. | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| claim_count_by_policy_year | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| severity_all | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| severity_lost_time | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 145 | 0 | within tolerance |
| paid_to_incurred | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| large_loss_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| large_loss_share | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| capped_incurred | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| loss_drivers_cause_incurred | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| loss_drivers_cause_count | count | exact | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| loss_drivers_body_part_incurred | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| loss_drivers_body_part_count | count | exact | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| loss_drivers_nature_incurred | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| loss_drivers_nature_count | count | exact | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| litigation_rate | ratio | 1 % rel. | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| ct_share | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| ct_indemnity_share | ratio | 1 % rel. | 225 | 225 | 100.0 % | 145 | 0 | within tolerance |
| post_termination_ct_count | count | exact | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| subrogation_recovery_rate | ratio | 1 % rel. | 225 | 225 | 100.0 % | 225 | 0 | not measurable on corpus v2 |
| closure_median_days | days | ±0.5 | 225 | 225 | 100.0 % | 80 | 0 | within tolerance |
| open_claim_age | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| reporting_lag_median_days | days | ±0.5 | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| reporting_lag_p90_days | days | ±0.5 | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| reporting_lag_over_30_share | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| incurred_by_policy_year | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| paid_by_policy_year | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| severity_by_policy_year | money | $1 / 0.1 % | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| paid_to_incurred_by_policy_year | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| non_ca_share | ratio | 1 % rel. | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |
| non_wc_excluded_count | count | exact | 225 | 225 | 100.0 % | 0 | 0 | within tolerance |

## Reference set

4/4 reference values within tolerance (1 row(s) left blank, not scored).
