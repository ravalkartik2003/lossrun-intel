### Learn reliability (`docs/evaluation/scoring-protocol.md`)

40 fresh learns (empty registry, `gpt-5.4-mini`, prompt `learn-v5`), one per native document, up to 5 iterations each. "Published at k" = the spec verified (learn gate) after k model calls.

| Carrier | File kind | Trials | Published | at 1 | at 2 | at 3 | at 4 | at 5 | Mean cost |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| c1 | pdf | 10 | 4 | 1 | 1 | 1 | 1 | 0 | $0.0575 |
| c2 | pdf | 10 | 4 | 1 | 3 | 0 | 0 | 0 | $0.0490 |
| c4 | spreadsheet | 10 | 7 | 0 | 1 | 3 | 2 | 1 | $0.0503 |
| c5 | spreadsheet | 10 | 5 | 0 | 0 | 2 | 1 | 2 | $0.0575 |

Cumulative success by iteration budget (what `MaxIterations` = k would have published):

| File kind | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 |
|---|---:|---:|---:|---:|---:|
| pdf | 2/20 | 6/20 | 7/20 | 8/20 | 8/20 |
| spreadsheet | 0/20 | 1/20 | 6/20 | 9/20 | 12/20 |

Failed trials: 20. Top lint findings on failed trials (share of failed trials where the finding appears in any attempt):

- 9/20: `lint: document.lineOfBusiness is declared as the constant "…" but "…" is printed on page 1`
- 6/20: `lint: document.carrierName is declared as the constant "…" but "…" is printed on page 1`
- 4/20: `lint: columnMap "…" → claim.carrierName is not a canonical path`

Last attempt of each failed trial:

- c1-001-native-pdf: 12 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals tie (27 checks); required coverage 100 %; gate none
- c1-002-native-pdf: 43 claim rows; 8 hard / 0 soft failures; 0 unmapped; 1 missing; 2 issues; totals do not tie (32 checks); required coverage 91 %; gate none
- c1-006-native-pdf: 41 claim rows; 8 hard / 0 soft failures; 0 unmapped; 1 missing; 2 issues; totals do not tie (32 checks); required coverage 91 %; gate none
- c1-007-native-pdf: 14 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 2 issues; totals tie (32 checks); required coverage 91 %; gate none
- c1-008-native-pdf: 47 claim rows; 9 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals do not tie (36 checks); required coverage 100 %; gate none
- c1-009-native-pdf: 10 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals tie (32 checks); required coverage 100 %; gate none
- c2-001-native-pdf: 32 claim rows; 0 hard / 0 soft failures; 0 unmapped; 3 missing; 0 issues; totals tie (10 checks); required coverage 73 %; gate none
- c2-003-native-pdf: 8 claim rows; 0 hard / 0 soft failures; 0 unmapped; 3 missing; 1 issues; totals tie (9 checks); required coverage 73 %; gate none
- c2-012-native-pdf: 18 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals tie (28 checks); required coverage 100 %; gate none
- c2-015-native-pdf: 8 claim rows; 0 hard / 0 soft failures; 0 unmapped; 4 missing; 1 issues; totals tie (10 checks); required coverage 64 %; gate none
- c2-016-native-pdf: 21 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals tie (28 checks); required coverage 100 %; gate none
- c2-017-native-pdf: 17 claim rows; 4 hard / 0 soft failures; 0 unmapped; 0 missing; 0 issues; totals do not tie (34 checks); required coverage 100 %; gate none
- c4-000-xlsx: 25 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 2 issues; totals tie (3 checks); required coverage 91 %; gate none
- c4-002-xlsx: 27 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 3 issues; totals tie (3 checks); required coverage 100 %; gate none
- c4-005-xlsx: 43 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals tie (3 checks); required coverage 100 %; gate none
- c5-001-csv: replay failed: The JSON value could not be converted to System.Nullable`1[LossRun.Contracts.ClaimType]. Path: $.policyPeriods[1].claims[15].claimType | LineNumber: 0 | BytePositionInLine: 21468.
- c5-002-csv: 43 claim rows; 86 hard / 0 soft failures; 0 unmapped; 0 missing; 0 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-004-csv: 9 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals n/a (0 checks); required coverage 91 %; gate none
- c5-006-csv: 52 claim rows; 0 hard / 0 soft failures; 0 unmapped; 8 missing; 2 issues; totals n/a (0 checks); required coverage 64 %; gate none
- c5-007-csv: 36 claim rows; 0 hard / 0 soft failures; 0 unmapped; 7 missing; 1 issues; totals n/a (0 checks); required coverage 73 %; gate none

**Decision:** pdf: MaxIterations 4 (8/20 published at k ≤ 4); spreadsheet: MaxIterations 5 (12/20 published at k ≤ 5).
