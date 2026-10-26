# Chat corrections eval (brief 24 step 5) — run 10251124

API `https://<poc-host>/api/v1` · quote `EVAL24-10251124-FIX` · document `c1-000-native-pdf` (synthetic corpus) · 2026-10-25 11:25 UTC

Rubric: a turn passes when the answer has every expected fact (printed and current values, the reviewer, the date, the rationale verbatim), the fact guard flagged nothing (no invented name, date or quotation) and it did not answer "not found".

| Measure | Result | Target |
|---|---|---|
| Rubric pass | 8/10 | ≥ 9/10 |
| Unverified names, dates or quotations | 0 | 0 |
| Cost per turn | mean $0.0023 | — |

| Question | Pass | Missing | Unverified | Tools | ms | Answer |
|---|---|---|---|---|---:|---|
| why-differs | ✓ |  |  | explain_value, last-result | 2895 | Claim WC-23-0001’s medical paid was printed as $17,815.08 [S1], but it is now $19,049.64 [C1]. The change was made by an Underwriter on 2026-10-25T11:24Z, with the rationale: "eval24: medical bill dated 2024-08-14 receiv… |
| who-when | ✓ |  |  | explain_value, last-result | 2909 | The indemnity reserve on claim WC-23-0002 was changed by an underwriter on 2026-10-25T11:24Z [C1]. Rationale: "eval24: adjuster raised the reserve for a second surgery" [C1]. The loss run printed it as $0.00 [S1], and th… |
| what-written | ✓ |  |  | explain_value, last-result | 2835 | The reviewer wrote: “eval24: adjuster raised the reserve for a second surgery” [C1]. The reserve was corrected from $0.00 [S1] to $2,500.00 [C1]. |
| chain-latest | ✓ |  |  | explain_value, query_claims, last-result | 3563 | Yes — claim WC-22-0003’s total incurred has been corrected to $98,300.52 [C1]. The loss run prints $96,550.52 [S1]. The underwriter rationale for the latest correction was: "eval24: carrier letter supersedes the broker's… |
| chain-history | ✓ |  |  | explain_value, last-result | 3425 | WC-22-0003 total incurred was printed as $96,550.52 [S1]. It was corrected to $97,550.52 by an Underwriter on 2026-10-25T11:24Z, rationale: "eval24: first correction from the broker's spreadsheet" [C1]. It was then corre… |
| unchanged | ✓ |  |  | explain_value, query_claims, last-result | 5892 | No. Claim WC-23-0001’s indemnity paid matches the loss run at $76,293.69 and has no corrections; its current value is approved [S1][C1]. The claim row is otherwise in override status with some other fields still unresolv… |
| which-fields | ✓ |  |  | explain_value, last-result | 5416 | A reviewer corrected WC-23-0001’s medical paid amount from $17,815.08 [S1] to $19,049.64 [C1]. The reviewer was an underwriter, and the rationale was: "eval24: medical bill dated 2024-08-14 received after the run was pri… |
| original | ✓ |  |  | explain_value, last-result | 3057 | The original printed indemnity reserve for claim WC-23-0002 was $0.00 [S1]. |
| reported-adjusted | ✗ | rationale |  | explain_value, last-result | 2867 | On claim WC-23-0001, the reported medical paid was $17,815.08 [S1], and the adjusted medical paid is $19,049.64 [C1]. The difference is $1,234.56 higher after an underwriter override because a medical bill dated 2024-08-… |
| who-approved | ✗ | rationale a, rationale b |  | explain_value, last-result | 3248 | An underwriter corrected the values on this quote [C1][C2][C3][C4]. They said: “carrier letter supersedes the broker's figure” for WC-22-0003 [C1]; “first correction from the broker's spreadsheet” for WC-22-0003 [C2]; “a… |
