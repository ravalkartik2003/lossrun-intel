### Learn reliability (`docs/evaluation/scoring-protocol.md`)

20 fresh learns (empty registry, `gpt-5.4-mini`, prompt `learn-v5`), one per native document, up to 5 iterations each. "Published at k" = the spec verified (learn gate) after k model calls.

| Carrier | File kind | Trials | Published | at 1 | at 2 | at 3 | at 4 | at 5 | Mean cost |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| c4 | spreadsheet | 10 | 10 | 0 | 4 | 3 | 2 | 1 | $0.0400 |
| c5 | spreadsheet | 10 | 3 | 0 | 1 | 1 | 0 | 1 | $0.0600 |

Cumulative success by iteration budget (what `MaxIterations` = k would have published):

| File kind | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 |
|---|---:|---:|---:|---:|---:|
| spreadsheet | 0/20 | 5/20 | 9/20 | 11/20 | 13/20 |

Failed trials: 7. Top lint findings on failed trials (share of failed trials where the finding appears in any attempt):

- 4/7: `lint: columnMap "…" → document.lineOfBusiness: replay reads only claim.* canonical paths (unknown path)`
- 2/7: `lint: columnMap "…" → claim.periodEffectiveDate: replay reads only claim.* canonical paths (unknown path)`
- 2/7: `lint: columnMap "…" → claim.periodExpirationDate: replay reads only claim.* canonical paths (unknown path)`

Last attempt of each failed trial:

- c5-002-csv: 43 claim rows; 86 hard / 0 soft failures; 0 unmapped; 0 missing; 0 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-003-csv: 53 claim rows; 106 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-004-csv: 9 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 9 issues; totals n/a (0 checks); required coverage 91 %; gate none
- c5-005-csv: 52 claim rows; 104 hard / 0 soft failures; 0 unmapped; 6 missing; 1 issues; totals n/a (0 checks); required coverage 82 %; gate none
- c5-006-csv: 52 claim rows; 0 hard / 0 soft failures; 0 unmapped; 0 missing; 1 issues; totals n/a (0 checks); required coverage 100 %; gate none
- c5-008-csv: 10 claim rows; 0 hard / 0 soft failures; 0 unmapped; 1 missing; 1 issues; totals n/a (0 checks); required coverage 91 %; gate none
- c5-009-csv: 23 claim rows; 0 hard / 0 soft failures; 1 unmapped; 0 missing; 0 issues; totals n/a (0 checks); required coverage 100 %; gate none

**Decision:** spreadsheet: MaxIterations 5 (13/20 published at k ≤ 5).
