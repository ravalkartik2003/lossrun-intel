### First-seen learn variance: c7-000-native-pdf, 5 trials

c7-000-native-pdf learned 5 times, each from an empty registry as the first document of its format (`gpt-5.4-mini`, prompt `learn-v9`). Delivered = the spec published and the replay has no hard rule failure; otherwise Review.

| Document | Carrier | Path | Model calls | Cost | Key fields correct | Why reviewed (best attempt) |
|---|---|---|---:|---:|---:|---|
| c7-000-native-pdf | c7 | delivered | 4 | $0.0562 | 114/114 |  |
| c7-000-native-pdf | c7 | delivered | 2 | $0.0240 | 114/114 |  |
| c7-000-native-pdf | c7 | review | 5 | $0.0759 | — | hard R-29 |
| c7-000-native-pdf | c7 | review | 5 | $0.0788 | — | hard R-19,R-29; missing policyPeriods[0].claims[2].claimNumber,policyPeriods[0].claims[4].claimNumber,policyPeriods[0].claims[8].claimNumber; 1 issue(s): lint: lineFields claim.claimNumber pattern ^(?<value>(?:[A-Z]{2,4}-?\d{2}-?\d{4,5}[A-Z]?/[A-Z]{2,4}\d{6,7}/\d{ |
| c7-000-native-pdf | c7 | delivered | 2 | $0.0453 | 114/114 |  |

**Result:** first-seen native PDF: delivered key-field accuracy 100.0% [98.7%–100.0%] (n=342, docs=1); review rate 2/5; mean 3.6 model calls, $0.0561 per learn.

Deck-table row:

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| first-seen learn, c7-000-native-pdf (5 trials) | 5 | 100.0% [98.7%–100.0%] (n=342, docs=1) | 40.0% [11.6%–77.1%] (n=5, docs=1) | — (study: no pre-fill scored) | 3.600 | measured, not committed (learn variance on one document) |
