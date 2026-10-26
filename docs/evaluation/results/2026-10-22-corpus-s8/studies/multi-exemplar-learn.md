### Multi-exemplar learn (ADR 0007)

Each unseen carrier, two fresh learns from the same seed (the document the run learned the carrier from): **without** exemplars, and **with** `LearnRequest.Exemplars` = 2 other documents (other renderings first), publishing only a spec that verifies on all of them. Each published spec is then verified (replay only, learn gate) on every remaining document of the carrier; an unpublished learn scores 0 (its documents would go to Review).

| Carrier | Seed | Exemplars | Held-out docs | Gate passes without | Gate passes with | Published with | Calls with | Cost with | Without |
|---|---|---|---:|---:|---:|---|---:|---:|---|
| c2 | c2-001-native-pdf | c2-001-scan-150dpi-fax, c2-001-scan-200dpi-rotated-noisy | 37 | 26 | 0 | no | 3 | $0.0344 | without: published in 2 call(s), $0.0240 |
| c3 | - |  | 0 | 0 | 0 | no | 0 | $0.0000 | no learn in the run |
| c4 | c4-000-xlsx | c4-001-xlsx, c4-002-xlsx | 37 | 0 | 0 | no | 3 | $0.0363 | without: not published in 3 call(s), $0.0343 |
| c5 | c5-000-csv | c5-001-csv, c5-002-csv | 37 | 0 | 0 | no | 3 | $0.0363 | without: not published in 3 call(s), $0.0388 |

**Decision:** keep the multi-exemplar check optional (off by default): held-out gate passes 0/111 with vs 26/111 without.
