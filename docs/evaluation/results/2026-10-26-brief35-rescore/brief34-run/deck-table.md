Run 2026-10-26 14:34–14:47 UTC · git `de21d8486c` · mode local (CU async) · learn `gpt-5.4-mini@2026-03-17` · prompt `learn-v11` · gate `learn-gate-0.3.0` · catalog `0.11.0` · prices `0.1.0` · corpus `corpus-gen-0.9.0` seed 8 · truth `corpus-gen-0.9.0 seed 8`

> Synthetic corpus: generated documents from five fictional carriers, not client documents. Real carrier documents will be messier; the sealed blind set on the client's archive is the number that counts. Rows with fewer than 5 documents are an insufficient sample, not a result.

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| native PDF, recurring template | 65 | 100.0% [100.0%–100.0%] (n=13386, docs=63) | 3.1% [0.2%–11.5%] (n=65, docs=65) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 99% delivered — met |
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | — (none reviewed) | 2.000 | ≥ 98% delivered — insufficient sample (n = 1 document): no verdict |
| XLSX / CSV | 80 | 100.0% [100.0%–100.0%] (n=14778, docs=79) | 1.3% [0.0%–7.4%] (n=80, docs=80) | 100.0% [98.6%–100.0%] (n=336, docs=1) | 0.088 | ≥ 98% delivered — met |
| scan ≥ 300 dpi | 48 | 100.0% [100.0%–100.0%] (n=9990, docs=46) | 4.2% [0.4%–14.8%] (n=48, docs=48) | 100.0% [98.9%–100.0%] (n=432, docs=2) | 0.000 | ≥ 95% delivered — met |
| scan 200 dpi rotated | 48 | 100.0% [99.9%–100.0%] (n=6948, docs=34) | 29.2% [18.2%–43.3%] (n=48, docs=48) | 99.8% [99.6%–99.9%] (n=3474, docs=14) | 0.000 | measured, not committed; review rate 29.2%, pre-fill 99.8% |
| fax < 200 dpi | 48 | — (none delivered) | 100.0% [91.2%–100.0%] (n=48, docs=48) | 88.9% [86.0%–91.3%] (n=10422, docs=48) | 0.021 | routed to review by design; pre-fill 88.9% |
| narrative statement | 12 | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0.0% [0.0%–0.2%] (n=1992, docs=12) | 0.000 | routed to review by design; pre-fill is the statement's paragraphs, no structured values |
| construct: two-line records (c6) | 6 | 100.0% [99.6%–100.0%] (n=1278, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.333 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: one table per claim (c7) | 6 | 100.0% [99.3%–100.0%] (n=660, docs=4) | 33.3% [9.2%–70.6%] (n=6, docs=6) | 91.9% [86.0%–95.6%] (n=198, docs=2) | 2.167 | measured, not committed (construct coverage: first document learned, then replayed); review rate 33.3% |
| construct: stacked cells (c8) | 6 | — (none delivered) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [99.5%–100.0%] (n=1026, docs=6) | 0.167 | measured, not committed (construct coverage: first document learned, then replayed); review rate 100.0% |
| construct: component-sum headers (c9) | 6 | 100.0% [99.6%–100.0%] (n=1098, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.333 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
| construct: measures as rows (c10) | 6 | 100.0% [99.6%–100.0%] (n=1314, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.167 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |

Data-quality first pass and quote success by input type (source-consistent documents; rates on the delivered ones):

| Input type | Docs | DQ first pass (delivered) | Quote success (delivered) | Review rate | n delivered |
|---|---:|---|---|---|---:|
| native PDF, recurring template | 63 | 100.0% [93.1%–100.0%] (n=63, docs=63) | 100.0% [93.1%–100.0%] (n=63, docs=63) | 0.0% [0.0%–6.9%] (n=63, docs=63) | 63 |
| native PDF, first-seen template | 1 | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | insufficient sample (n = 1 document) | 1 |
| XLSX / CSV | 79 | 100.0% [94.4%–100.0%] (n=79, docs=79) | 100.0% [94.4%–100.0%] (n=79, docs=79) | 0.0% [0.0%–5.6%] (n=79, docs=79) | 79 |
| scan ≥ 300 dpi | 46 | 100.0% [90.8%–100.0%] (n=46, docs=46) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0.0% [0.0%–9.2%] (n=46, docs=46) | 46 |
| scan 200 dpi rotated | 46 | 100.0% [87.9%–100.0%] (n=34, docs=34) | 100.0% [87.9%–100.0%] (n=34, docs=34) | 26.1% [15.5%–40.4%] (n=46, docs=46) | 34 |
| fax < 200 dpi | 46 | — (none delivered) | — (none delivered) | 100.0% [90.8%–100.0%] (n=46, docs=46) | 0 |
| narrative statement | 12 | — (none delivered) | — (none delivered) | 100.0% [71.8%–100.0%] (n=12, docs=12) | 0 |
| construct: two-line records (c6) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: one table per claim (c7) | 6 | 100.0% [45.4%–100.0%] (n=4, docs=4) | 100.0% [45.4%–100.0%] (n=4, docs=4) | 33.3% [9.2%–70.6%] (n=6, docs=6) | 4 |
| construct: stacked cells (c8) | 6 | — (none delivered) | — (none delivered) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0 |
| construct: component-sum headers (c9) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
| construct: measures as rows (c10) | 6 | 100.0% [55.7%–100.0%] (n=6, docs=6) | 100.0% [55.7%–100.0%] (n=6, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | 6 |
