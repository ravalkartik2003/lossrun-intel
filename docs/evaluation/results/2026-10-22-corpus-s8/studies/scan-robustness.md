### Scan robustness (ADR 0007)

24 scans (c1 12, c2 12) across scan-150dpi-fax, scan-200dpi-rotated-noisy, scan-300dpi-clean; each replayed with the carrier's spec four ways. Claim-field accuracy (all printed claim, period and total fields) and key-field accuracy (claim #, DOI, indemnity/medical paid and reserve):

| Rendering | Variant | Docs | Field accuracy | Key-field accuracy | Fully correct docs | Claims read / truth |
|---|---|---:|---:|---:|---:|---:|
| scan-150dpi-fax | cu+fallback | 8 | 85.6 % | 85.6 % | 1 | 104/104 |
| scan-150dpi-fax | cu-tables-only | 8 | 85.6 % | 85.6 % | 1 | 104/104 |
| scan-150dpi-fax | line-fallback-only | 8 | 86.0 % | 85.7 % | 1 | 113/104 |
| scan-150dpi-fax | di-second-opinion | 8 | 82.3 % | 80.4 % | 0 | 101/104 |
| scan-200dpi-rotated-noisy | cu+fallback | 8 | 97.8 % | 99.8 % | 4 | 104/104 |
| scan-200dpi-rotated-noisy | cu-tables-only | 8 | 97.8 % | 99.8 % | 4 | 104/104 |
| scan-200dpi-rotated-noisy | line-fallback-only | 8 | 95.1 % | 95.0 % | 0 | 111/104 |
| scan-200dpi-rotated-noisy | di-second-opinion | 8 | 95.5 % | 99.7 % | 3 | 104/104 |
| scan-300dpi-clean | cu+fallback | 8 | 99.6 % | 100.0 % | 5 | 104/104 |
| scan-300dpi-clean | cu-tables-only | 8 | 99.6 % | 100.0 % | 5 | 104/104 |
| scan-300dpi-clean | line-fallback-only | 8 | 99.6 % | 100.0 % | 0 | 116/104 |
| scan-300dpi-clean | di-second-opinion | 8 | 99.8 % | 100.0 % | 5 | 104/104 |

Second-opinion trigger policies (DI replaces CU tables on the documents where the signal fires):

| Trigger | Docs fired | Field accuracy | Fully correct docs | DI cost |
|---|---:|---:|---:|---:|
| never | 0 | 94.3 % | 10/24 | $0.00 |
| always | 24 | 92.5 % | 8/24 | $0.24 |
| zero-rows | 0 | 94.3 % | 10/24 | $0.00 |
| hard-failure | 9 | 93.0 % | 10/24 | $0.09 |
| fallback-rows | 0 | 94.3 % | 10/24 | $0.00 |
| ocr-confidence<0.9 | 2 | 93.9 % | 10/24 | $0.02 |

Page image on vs off (fresh registry, same scan learned twice):

| Doc | Image | Published | Calls | Cost | Field accuracy of the spec |
|---|---|---|---:|---:|---:|
| c2-001-scan-150dpi-fax | off | no | 3 | $0.0599 | 2.0 % |
| c2-001-scan-150dpi-fax | on | no | 3 | $0.0405 | 2.0 % |
| c2-003-scan-150dpi-fax | off | no | 3 | $0.0425 | 80.5 % |
| c2-003-scan-150dpi-fax | on | no | 3 | $0.0534 | 65.8 % |
| c2-001-scan-200dpi-rotated-noisy | off | no | 3 | $0.0429 | 94.7 % |
| c2-001-scan-200dpi-rotated-noisy | on | no | 3 | $0.0439 | 92.4 % |
| c2-003-scan-200dpi-rotated-noisy | off | yes | 2 | $0.0250 | 100.0 % |
| c2-003-scan-200dpi-rotated-noisy | on | yes | 2 | $0.0291 | 100.0 % |
| c2-001-scan-300dpi-clean | off | yes | 1 | $0.0154 | 99.8 % |
| c2-001-scan-300dpi-clean | on | yes | 2 | $0.0307 | 99.8 % |
| c2-003-scan-300dpi-clean | off | yes | 2 | $0.0255 | 100.0 % |
| c2-003-scan-300dpi-clean | on | yes | 2 | $0.0258 | 99.3 % |

**Decision (ADR 0007):** second opinion stays off: no trigger raised fully-correct documents above 10/24.

**Decision (ADR 0007):** drop the page image: published 3/6 with vs 3/6 without, at 1.1× the learn cost.
