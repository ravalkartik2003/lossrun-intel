### Scan robustness (ADR 0007)

24 scans (c1 12, c2 12) across scan-150dpi-fax, scan-200dpi-rotated-noisy, scan-300dpi-clean; each replayed with the carrier's spec four ways. Claim-field accuracy (all printed claim, period and total fields) and key-field accuracy (claim #, DOI, indemnity/medical paid and reserve):

| Rendering | Variant | Docs | Field accuracy | Key-field accuracy | Fully correct docs | Claims read / truth |
|---|---|---:|---:|---:|---:|---:|
| scan-150dpi-fax | cu+fallback | 8 | 87.7 % | 92.8 % | 0 | 102/102 |
| scan-150dpi-fax | cu-tables-only | 8 | 87.7 % | 92.8 % | 0 | 102/102 |
| scan-150dpi-fax | line-fallback-only | 8 | 64.8 % | 63.7 % | 0 | 99/102 |
| scan-150dpi-fax | di-second-opinion | 8 | 86.0 % | 91.3 % | 0 | 102/102 |
| scan-200dpi-rotated-noisy | cu+fallback | 8 | 98.2 % | 100.0 % | 4 | 102/102 |
| scan-200dpi-rotated-noisy | cu-tables-only | 8 | 98.2 % | 100.0 % | 4 | 102/102 |
| scan-200dpi-rotated-noisy | line-fallback-only | 8 | 95.3 % | 95.1 % | 0 | 109/102 |
| scan-200dpi-rotated-noisy | di-second-opinion | 8 | 95.8 % | 99.8 % | 2 | 102/102 |
| scan-300dpi-clean | cu+fallback | 8 | 99.6 % | 100.0 % | 4 | 102/102 |
| scan-300dpi-clean | cu-tables-only | 8 | 99.6 % | 100.0 % | 4 | 102/102 |
| scan-300dpi-clean | line-fallback-only | 8 | 99.6 % | 100.0 % | 0 | 114/102 |
| scan-300dpi-clean | di-second-opinion | 8 | 99.8 % | 100.0 % | 5 | 102/102 |

Second-opinion trigger policies (DI replaces CU tables on the documents where the signal fires):

| Trigger | Docs fired | Field accuracy | Fully correct docs | DI cost |
|---|---:|---:|---:|---:|
| never | 0 | 95.2 % | 8/24 | $0.00 |
| always | 24 | 93.9 % | 7/24 | $0.24 |
| zero-rows | 0 | 95.2 % | 8/24 | $0.00 |
| hard-failure | 12 | 94.3 % | 8/24 | $0.12 |
| fallback-rows | 0 | 95.2 % | 8/24 | $0.00 |
| ocr-confidence<0.9 | 2 | 95.0 % | 8/24 | $0.02 |

**Decision (ADR 0007):** second opinion stays off: no trigger raised fully-correct documents above 8/24.

**Decision (ADR 0007):** page image: not measured.
