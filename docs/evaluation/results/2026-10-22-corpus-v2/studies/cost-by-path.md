### Cost by path

Per corpus file (bundles and archives count once), by the path the loss-run document took. CU = layout + classifier; LLM = learn/delta/repair calls; other = embeddings and DI.

| Path | Docs | Mean cost | Mean CU | Mean LLM | Mean other | LLM calls / doc | p50 s | p95 s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ReplayAccepted | 215 | $0.0083 | $0.0077 | $0.0006 | $0.0000 | 0.42 | 4.1 | 10.7 |
| Review | 83 | $0.0172 | $0.0108 | $0.0064 | $0.0001 | 1.17 | 5.7 | 58.6 |
| Learned | 3 | $0.0404 | $0.0040 | $0.0363 | $0.0001 | 3.00 | 28.1 | 31.3 |
| not-found | 1 | $0.0135 | $0.0120 | $0.0015 | $0.0000 | 1.00 | 3.7 | 3.7 |

Learn-vs-replay break-even per carrier (layout cost is the same on both paths and left out):

| Carrier | Docs | LLM spend (all docs) | Docs with 0 LLM calls | Per-call cost | Model-per-doc lower bound | Break-even at doc # |
|---|---:|---:|---:|---:|---:|---:|
| c1 | 110 | $0.1909 | 39 | $0.0025 | $0.2728 | 77 |
| c2 | 100 | $0.4876 | 21 | $0.0047 | $0.4734 | 104 |
| c3 | 12 | $0.0131 | 0 | $0.0011 | $0.0131 | 12 |
| c4 | 40 | $0.0372 | 39 | $0.0124 | $0.4964 | 3 |
| c5 | 40 | $0.0411 | 39 | $0.0137 | $0.5482 | 3 |

**Result:** 46% of documents ran with 0 LLM calls; corpus total $3.36 (CU $2.58, LLM $0.77).
