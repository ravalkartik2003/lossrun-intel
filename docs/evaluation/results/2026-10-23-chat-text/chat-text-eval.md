# Chat text-answer eval (`docs/poc-plan/closeout/README.md`) — run 10231623

API `https://<poc-host>/api/v1` · corpus v3 · CT quote: c4-002-xlsx, c5-010-csv · bundle quote: c1-030-native-pdf-bundle · 2026-10-23 16:26 UTC

Rubric: a turn passes when every expected fact is in the answer (numbers within rounding: money ±$1, percentages ±0.5 pt, days ±0.5) and no number in it is unexplained by the system of record, the documents' printed text or the question (a fabrication).

| Measure | Result | Target |
|---|---|---|
| Rubric pass | 10/10 (100 %) | ≥ 90 % |
| Fabrications (unexplained numbers) | 0 | 0 |
| Full answer, these turns | p50 3,146 ms · p95 7,101 ms | — |
| Full answer, large account `Q-LOAD-1000` (10 claim questions) | p50 2,604 ms · p95 6,155 ms | p50 ≤ 4 s |
| Cost per turn | mean $0.0046 · max $0.0161 | — |

| Question | Pass | Missing facts | Unexplained numbers | Tools | ms | Answer |
|---|---|---|---|---|---:|---|
| ct-summary | ✓ |  |  | compute_metric, query_claims | 4697 | There are 14 cumulative-trauma claims on this account [C1]. They make up 15.2% of all claims [M1]. |
| litigated | ✓ |  |  | query_claims | 3146 | The litigated claims are: N25-00038, PA21000018, N23-00030, PA22000007, PA23000039, N24-00022, PA22000029, PA22000036, N23-00014, N23-00011, PA22000004, PA23000040, N25-00034, N24-00027, N23-00003, and N23-00015 [C1][C2]… |
| post-term-ct | ✓ |  |  | compute_metric | 2318 | Yes — there are 4 post-termination cumulative trauma claims [M1]. |
| litigation-rate | ✓ |  |  | compute_metric | 2604 | The litigation rate on lost-time claims is 15.0% [M1]. |
| cause-driver | ✓ |  |  | compute_metric, search_documents, search_documents, search_documents | 7101 | Cause code 78 drives the most incurred loss, at $1,196,798.90 [M1]. The loss-run tables identify the cause field by code, but do not provide a text description for code 78 in these documents [S6]. |
| subrogation | ✓ |  |  | compute_metric | 2517 | The subrogation recovery rate on this account is 8.1% [M1]. It is based on 92 claims, with 2 subrogation contributors [M1]. |
| reporting-lag | ✓ |  |  | compute_metric, compute_metric | 3234 | The median reporting lag is 7.0 days [M1], and 10.9% of claims were reported more than 30 days after injury [M2]. |
| payroll | ✓ |  |  | search_documents | 3027 | The payroll report shows class code 3632 with annual payroll of $1,615,500, and the class is Machine Shop [S1]. |
| application | ✓ |  |  | search_documents | 3492 | According to the application, the insured has 43 employees and the nature of business is light manufacturing [S1]. |
| totals | ✓ |  |  | compute_metric, compute_metric, query_claims | 4462 | Total incurred on the loss run is $2,367,092.89 [M1]. There are 16 open or reopened claims [C1]. |
