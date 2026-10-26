### First-seen native PDF templates (`docs/poc-plan/closeout/README.md`)

5 native PDFs of the unseen carriers (c2, c3, c4, c5), each learned from an empty registry as if it were the first document of its format (`gpt-5.4-mini`, prompt `learn-v5`). Delivered = the spec published and the replay has no hard rule failure; otherwise Review.

| Document | Carrier | Path | Model calls | Cost | Key fields correct |
|---|---|---|---:|---:|---:|
| c2-000-native-pdf | c2 | delivered | 1 | $0.0141 | 48/48 |
| c2-006-native-pdf | c2 | delivered | 2 | $0.0441 | 60/60 |
| c2-007-native-pdf | c2 | delivered | 2 | $0.0383 | 84/84 |
| c2-014-native-pdf | c2 | delivered | 2 | $0.0410 | 108/108 |
| c2-015-native-pdf | c2 | delivered | 5 | $0.0769 | 84/84 |

**Result:** first-seen native PDF: delivered key-field accuracy 100.0% [98.8%–100.0%] (n=384, docs=5); review rate 0/5; mean 2.4 model calls, $0.0429 per learn.
