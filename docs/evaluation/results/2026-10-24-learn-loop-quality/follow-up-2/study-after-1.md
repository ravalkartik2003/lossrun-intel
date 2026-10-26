### First-seen learn variance: c7-000-native-pdf, 5 trials

c7-000-native-pdf learned 5 times, each from an empty registry as the first document of its format (`gpt-5.4-mini`, prompt `learn-v9`). Delivered = the spec published and the replay has no hard rule failure; otherwise Review.

| Document | Carrier | Path | Model calls | Cost | Key fields correct |
|---|---|---|---:|---:|---:|
| c7-000-native-pdf | c7 | review | 5 | $0.0788 | — |
| c7-000-native-pdf | c7 | delivered | 2 | $0.0262 | 114/114 |
| c7-000-native-pdf | c7 | review | 5 | $0.0925 | — |
| c7-000-native-pdf | c7 | delivered | 5 | $0.1026 | 114/114 |
| c7-000-native-pdf | c7 | review | 5 | $0.0745 | — |

**Result:** first-seen native PDF: delivered key-field accuracy 100.0% [98.0%–100.0%] (n=228, docs=1); review rate 3/5; mean 4.4 model calls, $0.0749 per learn.

Deck-table row:

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| first-seen learn, c7-000-native-pdf (5 trials) | 5 | 100.0% [98.0%–100.0%] (n=228, docs=1) | 60.0% [22.9%–88.4%] (n=5, docs=1) | — (study: no pre-fill scored) | 4.400 | measured, not committed (learn variance on one document) |
