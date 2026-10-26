### Learn reliability (`docs/evaluation/scoring-protocol.md`)

40 fresh learns (empty registry, `gpt-5.4-mini`, prompt `learn-v5`), one per native document, up to 5 iterations each. "Published at k" = the spec verified (learn gate) after k model calls.

| Carrier | File kind | Trials | Published | at 1 | at 2 | at 3 | at 4 | at 5 | Mean cost |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| c1 | pdf | 10 | 6 | 2 | 0 | 0 | 3 | 1 | $0.0565 |
| c2 | pdf | 10 | 9 | 0 | 6 | 2 | 0 | 1 | $0.0384 |
| c4 | spreadsheet | 10 | 7 | 0 | 1 | 3 | 3 | 0 | $0.0499 |
| c5 | spreadsheet | 10 | 2 | 0 | 0 | 0 | 2 | 0 | $0.0661 |

Cumulative success by iteration budget (what `MaxIterations` = k would have published):

| File kind | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 |
|---|---:|---:|---:|---:|---:|
| pdf | 2/20 | 8/20 | 10/20 | 13/20 | 15/20 |
| spreadsheet | 0/20 | 1/20 | 4/20 | 9/20 | 9/20 |

Failed trials: 16. Top lint findings on failed trials (share of failed trials where the finding appears in any attempt):

- 6/16: `lint: columnMap "…" → claim.policyNumber is not a canonical path`
- 6/16: `lint: columnMap "…" → claim.valuationDate is not a canonical path`
- 5/16: `lint: columnMap "…" → claim.carrierName is not a canonical path`

Last attempt of each failed trial:

- c1-002-native-pdf: 43 claim rows; 10 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals do not tie (36 checks); required coverage 100 %; gate none
- c1-006-native-pdf: 41 claim rows; 9 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals do not tie (32 checks); required coverage 91 %; gate none
- c1-008-native-pdf: 47 claim rows; 8 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals do not tie (32 checks); required coverage 100 %; gate none
- c1-011-native-pdf: 22 claim rows; 10 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals do not tie (44 checks); required coverage 100 %; gate none
- c2-017-native-pdf: 17 claim rows; 3 hard / 0 soft failures; 0 unmapped; 4 missing; 3 issues; totals do not tie (10 checks); required coverage 64 %; gate none
- c4-005-xlsx: 43 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals tie (3 checks); required coverage 91 %; gate none
- c4-008-xlsx: 55 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals tie (3 checks); required coverage 91 %; gate none
- c4-009-xlsx: 48 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals tie (3 checks); required coverage 91 %; gate none
- c5-000-csv: 21 claim rows; 42 hard / 0 soft failures; 0 unmapped; 0 missing; 0 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-001-csv: 60 claim rows; 120 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-003-csv: 53 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals n/a (0 checks); required coverage 91 %; gate none
- c5-004-csv: 9 claim rows; 18 hard / 0 soft failures; 0 unmapped; 0 missing; 9 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-005-csv: 52 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals n/a (0 checks); required coverage 91 %; gate none
- c5-007-csv: 36 claim rows; 72 hard / 0 soft failures; 0 unmapped; 0 missing; 0 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-008-csv: 10 claim rows; 20 hard / 0 soft failures; 0 unmapped; 0 missing; 0 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-009-csv: 23 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 23 issues; totals n/a (0 checks); required coverage 100 %; gate none

**Decision:** pdf: MaxIterations 5 (15/20 published at k ≤ 5); spreadsheet: MaxIterations 4 (9/20 published at k ≤ 4).
