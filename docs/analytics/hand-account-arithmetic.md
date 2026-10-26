# Hand arithmetic for `hand-account.json` (ADR 0011)

The expected values in `src/web/packages/metrics/test/hand-account.json` were worked out by hand from `docs/analytics/catalog.yaml`; both the C# engine and the TypeScript twin assert them. The arithmetic:

Included claims are A–G (H is GL, so it is excluded). Policy years: A–D in 2022, E–G in 2023.

**Counts:** 7 claims; G is flagged `reportOnly` (catalog 1.3.0, brief 22 Answers 4), so **claims with financials** = 6 (A–F). The flag changes no other metric.

**Money (claim A–G incurred):**
- A $56,000 · B $150,000 · C $2,000 · D $480,000 · E $1,000 · F $60,000 · G $3,500.
- **Total** = $752,500. Paid = $512,500; reserves = $240,000.

**Ratios:**
- **Lost-time share** = 4/7 = 0.571429. **CT share** = B (cause 97), D (text "Cumulative"), F (nature 60) → 3/7 = 0.428571. **CT share of lost-time** = 3/4.
- **Paid-to-incurred** = 512,500 / 752,500 = 0.681063.
- **Large losses** ≥ $100K = B and D. Share = 630,000 / 752,500 = 0.837209. At $250K: D alone, 0.637874.
- **Capped at $250K** = 752,500 − 230,000 = $522,500.

**Frequency:**
- **Per $100 payroll** = 7 / (3,000,000 / 100) = 0.000233. By year: 4 / 10,000 = 0.0004; 3 / 20,000 = 0.00015.
- **Per 100 FTE** = 7 / 0.6 = 11.666667. By year: 20 and 7.5.

**Dates:**
- **Reporting lags** (days) = A 4, B 52, C 1, D 61, E 10, F 34. Median (10 + 34) / 2 = 22; p90 nearest-rank = 61; share over 30 days = 3/6.
- **Closure** = A 210, C 60, E 30 → median 60.
- **Open-claim age** at 2025-06-30 = B 1,116 days (3y+), D 927 (2–3y), F 501 (1–2y).

**Severity:**
- **All claims** = 752,500 / 7 = $107,500.
- **Lost-time** = 746,000 / 4 = $186,500.
- **By year** = 688,000 / 4 = $172,000 and 64,500 / 3 = $21,500. Paid-to-incurred by year: 0.71657 and 0.302326.

**Lineage:**
- Override o1 (B indemnity reserve) explains reserve_total, incurred_total, severity and paid-to-incurred.
- Override o2 (E status) explains closed_claim_count.

**Scenario 1** (exclude G and non-CA D; trend 2023 +10 %; reserves +10 %; cap $300K):
- Included: A, B, C, E, F.
- E becomes $1,100 and F becomes $70,950; B's reserves go from $65,000 to $71,500.
- **Incurred** $286,550; **severity** $57,310.

**Scenario 2** (cap $100K):
- D becomes $100,000, all of it paid. B becomes $100,000: $85,000 paid plus $15,000 of reserves.
- **Incurred** $322,500; paid $262,500; reserves $60,000.

