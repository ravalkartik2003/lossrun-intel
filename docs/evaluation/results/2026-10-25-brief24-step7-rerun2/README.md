# Brief 24 step 7, second re-run of chat-corrections (2026-10-25, third wake)

After `a13b907`:
- the rubric strips the eval's own `eval24:` tag;
- prompt rule 7 (`chat-v4`) requires rationales verbatim;
- the fact guard enforces exact-substring quotes and every returned rationale (up to 5), with one restate pass.

**This wake** (about 12:03–12:22 UTC; `sql_auto_pause_minutes` lowered from 60 to 15 in the same apply):
1. Terraform wake.
2. `chat init-index`. No reindex: the eval uses a fresh quote.
3. `deploy-functions.ps1`: the check passed, smoke upload `ReplayAccepted`.
4. `eval chat-corrections`, then hibernate.

| Eval | Result | Gate | Turn cost |
|---|---|---|---|
| `eval chat-corrections` | **9/10**, 0 unverified, 2 of 10 turns restated | ≥ 9/10: **pass** | $0.030 |

**The miss: `original`** ("What was the original printed indemnity reserve of claim WC-23-0002?", printed $0.00).
- `explain_value` returned the correction and its rationale. The first answer did not quote the rationale, so the restate pass ran.
- The restated answer was "It is not in these documents." **The restate pass made this answer worse.**
- A known issue. The rationale check should apply only when the answer mentions a correction, and a restate should never turn an answered question into not-found.
