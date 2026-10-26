# Reference set — actuary-computed metrics for insight accuracy

The insight-accuracy check (ADR 0011; `lossrun eval insights`) compares the feature engine's metrics with an independent answer. On corpus v2 that answer is the generator's truth. In the formal POC it is a **reference set**: metrics the client's actuarial or underwriting-ops team computes by hand from the same loss runs (ADR 0011).

## Who, when, how many

| | |
|---|---|
| **Who fills it** | The client's actuarial or underwriting-ops team. |
| **When** | Weeks 0–1 of the formal POC, before any engine numbers are shown to them. |
| **Accounts** | 10–20, drawn by the sealed-set sampler (`scoring-protocol.md` §2) from the accounts in the sealed test set. |
| **Metrics** | Any catalog metric (`docs/analytics/catalog.yaml`). Suggested core: `claim_count`, `open_claim_count`, `incurred_total`, `paid_total`, `large_loss_count` (at $100K and $250K), `severity_all`, `paid_to_incurred`, `lost_time_share`, `frequency_per_100_payroll` (where payroll is supplied), `reporting_lag_median_days`. |
| **Until then** | The corpus truth stands in (`docs/evaluation/results/2026-10-22-corpus-v2/insights/`). |

The preparers compute from the printed loss runs, not from the engine's output. Disagreements are resolved by reading the source page: the lineage view shows the cell each engine value came from.

## File format

CSV, UTF-8, comma-separated, one header line, exactly these columns. The template is `docs/analytics/reference-set-template.csv`.

| Column | Required | Meaning |
|---|---|---|
| `account_id` | yes | The quote id, or the corpus document id on synthetic data. |
| `metric` | yes | A catalog metric `name`, e.g. `incurred_total`. |
| `breakdown_key` | no | Blank for the metric's value. For a breakdown entry, the catalog's key: a policy year (`2023`), a threshold or cap in dollars (`250000`), a code (`52`), or a bucket (`1-2y`). |
| `expected_value` | yes, to be scored | The preparer's number. Blank means "not provided": the row is not scored. |
| `unit` | yes | `count`, `money`, `ratio`, `rate` or `days`, the same as the catalog. |
| `valuation_date` | yes | `yyyy-mm-dd`, the loss run's "valued as of" date the preparer used. |
| `prepared_by` | yes | Initials or a team name (no personal data beyond that). |
| `notes` | no | Free text, no commas, e.g. how a printed-only total was treated. |

How to write the numbers:
- **Money:** dollars with up to 2 decimals, no thousands separators or currency sign, e.g. `2802851.20`. Negatives take a leading `-`.
- **Ratio and rate:** a fraction from 0 to 1, e.g. `0.734` (not `73.4 %`).
- **Days:** a decimal number of days, e.g. `12.5`.
- **Counts:** whole numbers.

Tolerances are the catalog's, the same as for corpus truth:
- counts: exact;
- money: within $1 or 0.1 %, whichever is larger;
- ratios and rates: within 1 % relative;
- days: within 0.5.

### Example (2 rows)

```csv
account_id,metric,breakdown_key,expected_value,unit,valuation_date,prepared_by,notes
Q-2026-018342,incurred_total,,319606.05,money,2026-08-31,ACT-1,three policy years; expense separate
Q-2026-018342,large_loss_count,100000,1,count,2026-08-31,ACT-1,WC-25-0430 at 176600.00
```

## Scoring

```bash
lossrun eval insights --corpus <corpus> --results <run> --out <dir> --reference reference-set.csv
```

The result is a "Reference set" section in `insights.md` with:
- rows scored, rows within tolerance, rows skipped (blank expected value);
- the first misses, each with account, metric, key, expected and actual.

A miss is investigated in the workbench's Document screen (trace the value to its source cell) before anyone calls it an engine error. Two outcomes are possible:
- the preparer misread the page;
- the extraction is wrong, which becomes a review override.

Both outcomes are recorded.

Do not commit a filled reference set that contains client data. Keep it with the sealed set, under the same access rules.
