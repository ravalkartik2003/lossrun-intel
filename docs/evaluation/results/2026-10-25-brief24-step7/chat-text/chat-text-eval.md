# Chat text-answer eval (`docs/poc-plan/closeout/README.md`) — run 10251006

API `https://<poc-host>/api/v1` · corpus v3 · CT quote: c4-002-xlsx, c5-010-csv · bundle quote: c1-030-native-pdf-bundle · 2026-10-25 10:08 UTC

Rubric: a turn passes when every expected fact is in the answer (numbers within rounding: money ±$1, percentages ±0.5 pt, days ±0.5) and no number in it is unexplained by the system of record, the documents' printed text or the question (a fabrication).

| Measure | Result | Target |
|---|---|---|
| Rubric pass | 10/10 (100 %) | ≥ 90 % |
| Fabrications (unexplained numbers) | 0 | 0 |
| Full answer, these turns | p50 3,215 ms · p95 4,858 ms | — |
| Full answer, large account `Q-LOAD-1000` (10 claim questions) | p50 2,648 ms · p95 22,091 ms | p50 ≤ 4 s |
| Cost per turn | mean $0.0035 · max $0.0058 | — |

| Question | Pass | Missing facts | Unexplained numbers | Tools | ms | Answer |
|---|---|---|---|---|---:|---|
| ct-summary | ✓ |  |  | compute_metric, query_claims | 3645 | There are 14 cumulative-trauma claims on this account [C1–C14], and they represent 15.2% of all claims [M1]. |
| litigated | ✓ |  |  | query_claims | 3615 | The litigated claims are: N25-00038 [C1], PA21000018 [C2], N23-00030 [C3], PA22000007 [C4], PA23000039 [C5], N24-00022 [C6], PA22000029 [C7], PA22000036 [C8], N23-00014 [C9], N23-00011 [C10], PA22000004 [C11], PA23000040… |
| post-term-ct | ✓ |  |  | compute_metric, query_claims | 3215 | Yes — there are 4 post-termination cumulative trauma claims [M1]. |
| litigation-rate | ✓ |  |  | compute_metric | 4303 | The litigation rate on lost-time claims is 15.0% [M1]. |
| cause-driver | ✓ |  |  | compute_metric, search_documents | 4858 | Cause code 78 drives the most incurred loss at $1,196,798.90 [M1]. |
| subrogation | ✓ |  |  | compute_metric | 2285 | The subrogation recovery rate on this account is 8.1% [M1]. |
| reporting-lag | ✓ |  |  | compute_metric, compute_metric | 2976 | The median reporting lag is 7.0 days [M1]. The share of claims reported more than 30 days after injury is 10.9% [M2]. |
| payroll | ✓ |  |  | search_documents | 2813 | The payroll report shows $1,615,500 for class code 3632 [S1]. That class is Machine Shop [S1]. |
| application | ✓ |  |  | search_documents | 3086 | According to the application, the insured has 43 employees and the nature of business is light manufacturing [S1]. |
| totals | ✓ |  |  | query_claims, compute_metric | 4544 | Total incurred is $2,367,092.89 [M1]. There are 16 open or reopened claims [C1]. |
