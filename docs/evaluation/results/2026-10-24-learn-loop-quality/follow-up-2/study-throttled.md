### First-seen learn variance: c7-000-native-pdf, 5 trials

c7-000-native-pdf learned 5 times, each from an empty registry as the first document of its format (`gpt-5.4-mini`, prompt `learn-v9`). Delivered = the spec published and the replay has no hard rule failure; otherwise Review.

| Document | Carrier | Path | Model calls | Cost | Key fields correct |
|---|---|---|---:|---:|---:|
| c7-000-native-pdf | c7 | review | 5 | $0.0955 | — |
| c7-000-native-pdf | c7 | review | 5 | $0.0945 | — |
| c7-000-native-pdf | c7 | review | 0 | $0.0423 | — |
| c7-000-native-pdf | c7 | review | 5 | $0.0969 | — |
| c7-000-native-pdf | c7 | delivered | 3 | $0.0608 | 114/114 |

**Result:** first-seen native PDF: delivered key-field accuracy 100.0% [96.1%–100.0%] (n=114, docs=1); review rate 4/5; mean 3.6 model calls, $0.0780 per learn.

Deck-table row:

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| first-seen learn, c7-000-native-pdf (5 trials) | 5 | 100.0% [96.1%–100.0%] (n=114, docs=1) | 80.0% [36.0%–98.0%] (n=5, docs=1) | — (study: no pre-fill scored) | 3.600 | measured, not committed (learn variance on one document) |
