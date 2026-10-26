# Chat corrections eval (brief 24 step 5) — run 10251008

API `https://<poc-host>/api/v1` · quote `EVAL24-10251008-FIX` · document `c1-000-native-pdf` (synthetic corpus) · 2026-10-25 10:10 UTC

Rubric: a turn passes when the answer has every expected fact (printed and current values, the reviewer, the date, the rationale verbatim), the fact guard flagged nothing (no invented name, date or quotation) and it did not answer "not found".

| Measure | Result | Target |
|---|---|---|
| Rubric pass | 6/10 | ≥ 9/10 |
| Unverified names, dates or quotations | 0 | 0 |
| Cost per turn | mean $0.0030 | — |

| Question | Pass | Missing | Unverified | Tools | ms | Answer |
|---|---|---|---|---|---:|---|
| why-differs | ✓ |  |  | explain_value | 3661 | Claim WC-23-0001’s medical paid was printed as $17,815.08 [S1] but is currently $19,049.64 [C1]. It was changed by an underwriter on 2026-10-25T10:09Z with the rationale: "eval24: medical bill dated 2024-08-14 received a… |
| who-when | ✗ | current |  | explain_value | 3820 | The indemnity reserve on claim WC-23-0002 was changed by the underwriter, on 2026-10-25T10:09Z [C1]. The rationale was: “eval24: adjuster raised the reserve for a second surgery” [C1]. |
| what-written | ✓ |  |  | explain_value | 2979 | The reviewer wrote: “eval24: adjuster raised the reserve for a second surgery” [C1]. The corrected indemnity reserve is $2,500.00 [C1], from $0.00 on the loss run [S1]. |
| chain-latest | ✗ | rationale |  | explain_value | 3262 | Yes. WC-22-0003’s total incurred has been corrected to $98,300.52 [C1]. The loss run prints $96,550.52 [S1]. |
| chain-history | ✓ |  |  | explain_value | 5186 | WC-22-0003 total incurred was printed as $96,550.52 [S1]. It was corrected to $97,550.52 by the underwriter, Underwriter, at 2026-10-25T10:09Z: "eval24: first correction from the broker's spreadsheet" [C1]. It was then c… |
| unchanged | ✗ | as printed |  | explain_value | 4030 | No. WC-23-0001’s indemnity paid is unchanged from the loss run: $76,293.69 [S1], and its current value is also $76,293.69 with status approved [C1]. There are no corrections [C1]. |
| which-fields | ✓ |  |  | explain_value | 3275 | A reviewer corrected WC-23-0001’s medical paid from $17,815.08 [S1] to $19,049.64 [C1]. It was changed by an underwriter, role: Underwriter, on 2026-10-25T10:09Z, with rationale: "eval24: medical bill dated 2024-08-14 re… |
| original | ✓ |  |  | explain_value | 3925 | The original printed indemnity reserve for claim WC-23-0002 was $0.00 [S1]. |
| reported-adjusted | ✓ |  |  | explain_value | 3321 | On claim WC-23-0001, reported medical paid was $17,815.08 [S1] and the adjusted medical paid is $19,049.64 [C1], a difference of $1,234.56. The underwriter changed it on 2026-10-25T10:09Z because: "eval24: medical bill d… |
| who-approved | ✗ | reviewer, rationale a, rationale b |  | get_review_status, search_documents, search_documents, explain_value, query_claims | 15553 | The documents do not identify any reviewer by name, so I can’t say who corrected values. The quote is in review state “override,” with 259 unresolved fields and no approved final values yet [C1]. For example, WC-23-0001 … |
