## Tier 2 (design change): (b) layout not expressible — Notional Workers Comp Exchange · xlsx

**Label: `tier-2`.** Keep `[tier-2]` in the merge commit message: the next batch counts it as a grammar change (tuning-loop.md §6).

**Found by:** tuning batch 2 (`lossrun tune`, commit `092832e44656`), fix-list rank 2.

**What it would clear:** 1 tuning document(s): notional-workers-comp-exchange-claims-export-d4bb8bf9.

**Evidence (rule ids, scrubbed reasons):** learn exhausted # iteration(s) without a verified spec → review.

**Proposed change:** A spec-language construct or lint repair (design change: ADR, corpus v5 run, human merge); meanwhile these documents go to review.

**Checklist (tuning-loop.md §3–4):**
- [ ] ADR `docs/decisions/NNNN-title.md` (context, decision, consequences)
- [ ] Deck table reproduces on corpus v5 (`eval generate --v5`, `eval run`, `eval score`); results folder attached
- [ ] `tools/generic-guard.sh` and the sample guard pass; .NET and web tests pass
- [ ] A learn-prompt change: `python tools/prompt-gate.py --write` and the corpus v5 run whose run.json records the artefact
- [ ] The batch report attached; spend reported from report.md

The assistant opened this pull request and stops here: a human reviews and merges design changes.
