### Cost by path

Per corpus file (bundles and archives count once), by the path the loss-run document took. CU = layout + classifier; LLM = learn/delta/repair calls; other = embeddings and DI.

| Path | Docs | Mean cost | Mean CU | Mean LLM | Mean other | LLM calls / doc | p50 s | p95 s |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| ReplayAccepted | 211 | $0.0074 | $0.0069 | $0.0005 | $0.0000 | 0.35 | 4.1 | 7.8 |
| Review | 115 | $0.0488 | $0.0120 | $0.0368 | $0.0001 | 3.27 | 42.7 | 54.1 |
| Learned | 10 | $0.0312 | $0.0068 | $0.0242 | $0.0002 | 2.00 | 19.5 | 40.0 |
| not-found | 2 | $0.0135 | $0.0120 | $0.0015 | $0.0000 | 1.00 | 4.6 | 6.0 |

Learn-vs-replay break-even per carrier (layout cost is the same on both paths and left out):

| Carrier | Docs | LLM spend (all docs) | Docs with 0 LLM calls | Per-call cost | Model-per-doc lower bound | Break-even at doc # |
|---|---:|---:|---:|---:|---:|---:|
| c1 | 110 | $1.0143 | 38 | $0.0082 | $0.8998 | 125 |
| c2 | 100 | $1.3684 | 30 | $0.0091 | $0.9123 | 150 |
| c3 | 48 | $2.1135 | 0 | $0.0110 | $0.5284 | 192 |
| c4 | 40 | $0.0432 | 39 | $0.0144 | $0.5756 | 3 |
| c5 | 40 | $0.0361 | 39 | $0.0120 | $0.4811 | 3 |

**Result:** 43% of documents ran with 0 LLM calls; corpus total $7.51 (CU $2.92, LLM $4.58).
