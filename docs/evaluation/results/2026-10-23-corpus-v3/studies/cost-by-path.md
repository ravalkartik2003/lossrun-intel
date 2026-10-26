### Cost by path

Per corpus file (bundles and archives count once), by the path the loss-run document took. CU = layout + classifier; LLM = learn/delta/repair calls; other = embeddings and DI.

| Path | Docs | Mean cost | Mean CU | Mean LLM | Mean other | LLM calls / doc | p50 s | p95 s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ReplayAccepted | 221 | $0.0085 | $0.0078 | $0.0007 | $0.0000 | 0.43 | 3.5 | 7.4 |
| Review | 78 | $0.0122 | $0.0109 | $0.0013 | $0.0001 | 0.81 | 5.0 | 8.5 |
| Learned | 3 | $0.0539 | $0.0017 | $0.0522 | $0.0000 | 3.33 | 59.4 | 62.2 |

Learn-vs-replay break-even per carrier (layout cost is the same on both paths and left out):

| Carrier | Docs | LLM spend (all docs) | Docs with 0 LLM calls | Per-call cost | Model-per-doc lower bound | Break-even at doc # |
|---|---:|---:|---:|---:|---:|---:|
| c1 | 110 | $0.1161 | 39 | $0.0016 | $0.1798 | 72 |
| c2 | 100 | $0.1718 | 24 | $0.0022 | $0.2175 | 80 |
| c3 | 12 | $0.0139 | 0 | $0.0012 | $0.0139 | 12 |
| c4 | 40 | $0.0580 | 39 | $0.0145 | $0.5801 | 4 |
| c5 | 40 | $0.0435 | 39 | $0.0145 | $0.5806 | 3 |

**Result:** 47% of documents ran with 0 LLM calls; corpus total $2.99 (CU $2.58, LLM $0.40).
