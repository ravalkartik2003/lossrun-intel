# Brief 24 step 7 re-run (2026-10-25, second wake)

After the step 7 Answers, applied offline in `8b154b7`:
- `explain_value` without a claim number;
- "Last result (turn N)" in the facts block, with prompt rule 8 (`chat-v3`);
- `status: open` includes reopened;
- per-component totals on `query_claims`, and a parser for a doubled answer;
- the rubric fixes.

**This wake** (11:04 UTC to hibernate):
1. Terraform wake (18 added).
2. `chat init-index` and `chat reindex --from-api` (14 uploads).
3. `deploy-functions.ps1`: the check passed, smoke upload `ReplayAccepted`.
4. The three evals, then hibernate. No web or database change was needed.

| Eval | Folder | Result | Gate | Turn cost |
|---|---|---|---|---|
| `eval chat` (30 numeric + 2 probes) | `chat/` | **30/30** exact, 30/30 citations, 0 leaks, injection ignored | **pass** | $0.109 |
| `eval chat-corrections` | `chat-corrections/` | **8/10**, 0 unverified | ≥ 9/10: **fail** | $0.023 |
| `eval chat-multiturn` (5 × 8) | `chat-multiturn/` | **37/40** (92.5 %), 0 leaks, 0 invalid citations, 0 unverified | **pass** | $0.128 |

**The two `chat-corrections` misses.** Both answers have the right facts but not the verbatim rationale the rubric requires.
- **`who-approved`** lists all four corrections with their reviewer and quotes each rationale, but drops the planted `eval24:` prefix ("…for a second surgery" instead of "eval24: adjuster raised the reserve for a second surgery").
- **`reported-adjusted`** paraphrases the rationale ("because a medical bill dated 2024-08-14 was received…") instead of quoting it. Prompt rule 7 says to quote verbatim.

**Multi-turn, the three remaining misses:**
- **`open` t5 and `oldest` t5:** "How do those two compare in total incurred?" was still not answered with both claims' incurred.
- **`metrics` t6:** "Is that the same claim as the one before?" was answered without "same" / "not" / "different".

**Spend this wake:**
- **Documents:** $0.030 for 11 (eval uploads, the smoke upload).
- **Eval turns:** $0.260.
- **Standing:** ≈ $0.10 for under an hour awake.
- **Total:** ≈ $0.39 of the $1 budget.

**Not done:** the export and the push to `lossrun-intelligence`, held because `chat-corrections` missed its gate.
