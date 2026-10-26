### Learn reliability (`docs/evaluation/scoring-protocol.md`)

10 fresh learns (empty registry, `gpt-5.4-mini`, prompt `learn-v5`), one per native document, up to 5 iterations each. "Published at k" = the spec verified (learn gate) after k model calls.

| Carrier | File kind | Trials | Published | at 1 | at 2 | at 3 | at 4 | at 5 | Mean cost |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| c5 | spreadsheet | 10 | 10 | 0 | 5 | 2 | 3 | 0 | $0.0353 |

Cumulative success by iteration budget (what `MaxIterations` = k would have published):

| File kind | k = 1 | k = 2 | k = 3 | k = 4 | k = 5 |
|---|---:|---:|---:|---:|---:|
| spreadsheet | 0/10 | 5/10 | 7/10 | 10/10 | 10/10 |

Failed trials: 0. Top lint findings on failed trials (share of failed trials where the finding appears in any attempt):


Last attempt of each failed trial:


**Decision:** spreadsheet: MaxIterations 4 (10/10 published at k ≤ 4).
