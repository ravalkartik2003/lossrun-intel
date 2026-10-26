### Classifier skip (ADR 0005)

The first-page check skips the CU classifier when a single loss-run table header resolves on page 1. Correct = a skipped document was a single loss run that was extracted; a classified bundle split exactly at the loss-run segment; a classified single document was found.

| Population | Docs | Skipped | Skip rate | Correct when skipped | Correct when classified | Mean classifier cost when run |
|---|---:|---:|---:|---:|---:|---:|
| single documents | 177 | 17 | 10 % | 17/17 | 158/160 | $0.0146 |
| bundles | 8 | 0 | 0 % | 0/0 | 8/8 | $0.0327 |
| — native-pdf | 59 | 17 | 29 % | 17/17 | 42/42 | $0.0183 |
| — scan | 126 | 0 | 0 % | 0/0 | 124/126 | $0.0146 |

**Decision:** keep the first-page skip: 9% of documents skip the classifier and no bundle was skipped.
