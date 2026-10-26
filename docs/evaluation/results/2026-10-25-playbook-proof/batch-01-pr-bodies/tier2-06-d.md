## Tier 2 (design change): (d) scan quality — synthetic-mutual-pdf@5

**Label: `tier-2`.** Keep `[tier-2]` in the merge commit message: the next batch counts it as a grammar change (tuning-loop.md §6).

**Found by:** tuning batch 1 (`lossrun tune`, commit `092832e44656`), fix-list rank 6.

**What it would clear:** 1 tuning document(s): synthetic-mutual-insurance-co-faxed-loss-run-26d1f5dc.

**Evidence (rule ids, scrubbed reasons):** +# more → review with pre-fill; R-10; R-10: word confidence #; R-10: word confidence #) → review with pre-fill; R-12; no repair attempted.

**Proposed change:** Leave to review with pre-fill unless a gate threshold should move (design change); re-request a native copy where possible.

**Checklist (tuning-loop.md §3–4):**
- [ ] ADR `docs/decisions/NNNN-title.md` (context, decision, consequences)
- [ ] Deck table reproduces on corpus v5 (`eval generate --v5`, `eval run`, `eval score`); results folder attached
- [ ] `tools/generic-guard.sh` and the sample guard pass; .NET and web tests pass
- [ ] A learn-prompt change: `python tools/prompt-gate.py --write` and the corpus v5 run whose run.json records the artefact
- [ ] The batch report attached; spend reported from report.md

The assistant opened this pull request and stops here: a human reviews and merges design changes.
