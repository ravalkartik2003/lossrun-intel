# Chat corrections eval (brief 24 step 5) — run 10251217

API `https://<poc-host>/api/v1` · quote `EVAL24-10251217-FIX` · document `c1-000-native-pdf` (synthetic corpus) · 2026-10-25 12:18 UTC

Rubric: a turn passes when the answer has every expected fact (printed and current values, the reviewer, the date, the rationale verbatim), the fact guard flagged nothing (no invented name, date or quotation) and it did not answer "not found".

| Measure | Result | Target |
|---|---|---|
| Rubric pass | 9/10 | ≥ 9/10 |
| Unverified names, dates or quotations | 0 | 0 |
| Cost per turn | mean $0.0030 | — |

| Question | Pass | Missing | Unverified | Tools | ms | Answer |
|---|---|---|---|---|---:|---|
| why-differs | ✓ |  |  | explain_value, last-result | 3902 | Claim WC-23-0001’s medical paid was overridden from $17,815.08 [S1] to $19,049.64 [C1]. The correction was made by an Underwriter on 2026-10-25T12:18Z, with the rationale: "eval24: medical bill dated 2024-08-14 received … |
| who-when | ✓ |  |  | explain_value, last-result | 2766 | The indemnity reserve on claim WC-23-0002 was changed by an underwriter [C1]. It was changed on 2026-10-25T12:18Z [C1]. The reviewer’s rationale was: "eval24: adjuster raised the reserve for a second surgery" [C1]. |
| what-written | ✓ |  |  | explain_value, last-result | 2821 | The reviewer wrote: "eval24: adjuster raised the reserve for a second surgery" [C1]. It was changed by an underwriter on 2026-10-25T12:18Z [C1]. |
| chain-latest | ✓ |  |  | explain_value, restate, last-result | 5054 | Yes — claim WC-22-0003’s total incurred was corrected. The loss run prints $96,550.52 [S1], and the current approved value is $98,300.52 [C1]. The underwriter’s rationale was: "eval24: first correction from the broker's … |
| chain-history | ✓ |  |  | explain_value, last-result | 3006 | WC-22-0003 total incurred was printed as $96,550.52 [S1]. It was corrected to $97,550.52 by the Underwriter on 2026-10-25T12:18Z; rationale: "eval24: first correction from the broker's spreadsheet" [C1]. It was then corr… |
| unchanged | ✓ |  |  | explain_value, last-result | 2527 | No. WC-23-0001’s indemnity paid was not corrected; the printed value is $76,293.69 [S1] and the current approved value is also $76,293.69 [C1]. |
| which-fields | ✓ |  |  | explain_value, last-result | 10184 | The reviewer corrected WC-23-0001’s medical paid value from $17,815.08 [S1] to $19,049.64 [C1]. The correction was made by an Underwriter on 2026-10-25T12:18Z, with rationale: "eval24: medical bill dated 2024-08-14 recei… |
| original | ✗ | printed |  | explain_value, restate, last-result | 4517 | It is not in these documents. [S1] |
| reported-adjusted | ✓ |  |  | explain_value, last-result | 2965 | On claim WC-23-0001, medical paid was reported as $17,815.08 [S1] and adjusted to $19,049.64 [C1], an increase of $1,234.56. The correction was made by the underwriter (Underwriter) on 2026-10-25T12:18Z with rationale: "… |
| who-approved | ✓ |  |  | explain_value, last-result | 3391 | The corrections were made by an underwriter [C1][C2][C3][C4]. Reasons given were: "eval24: carrier letter supersedes the broker's figure" for WC-22-0003 total incurred [C1]; "eval24: first correction from the broker's sp… |
