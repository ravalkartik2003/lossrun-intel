# POC close-out pack

This is the governance and acceptance pack for the build side of the bake-off. The POC is presented as independent work on its author's own subscription: nothing here is shared with the client until the project is green-lit (`handover.md`). It covers the nine artefacts listed in `docs/poc-plan/bakeoff-strategy.md` KQ8, plus the final acceptance table and the sealed-set protocol for evaluators.

Everything here describes the POC as built and measured on synthetic data. No client document was used. Statements about production are marked as such.

**Every corpus figure in the pack comes from the run the prompt gate currently holds:** the deck table, review and pre-fill rates, all-fields and insight accuracy. Today that is `docs/evaluation/results/2026-10-26-brief35-wake/corpus-v5-gpt-5.4-mini/` (brief 35 wake), artefact `f913ce4e28a5`; it superseded `2026-10-26-corpus-v5-wake/` (artefact `e0d9753d8f5b`). A new attached run means the pack is updated with it. Studies (first-seen learns, learn reliability) and the chat evals name their own folders.

| # | Artefact | File | Framework it answers |
|---|---|---|---|
| 1 | AI-system inventory entry | `01-ai-system-inventory.md` | Colorado 10-1-1 inventory fields, NAIC Bulletin §4 |
| 2 | Responsible AI impact assessment | `02-rai-impact-assessment.md` | Microsoft RAI Impact Assessment template (sections 1–5) |
| 3 | Audit & governance model | `03-audit-and-governance-model.md` | NAIC §4, RAI Standard A1/A5/RS3 |
| 4 | Model-risk documentation | `04-model-risk.md` | RAI Standard RS1/RS2, golden set, failure modes |
| 5 | Lineage & audit spec | `05-lineage-and-audit-spec.md` | NAIC 1.3b data lineage |
| 6 | RBAC configuration | `06-rbac-configuration.md` | Azure CAF AI governance, least privilege |
| 7 | Runbooks | `07-runbooks.md` | RAI Standard RS2/RS3, Azure WAF operational excellence |
| 8 | Transparency note | `08-transparency-note.md` | RAI Standard T2 |
| 9 | Third-party section | `09-third-party.md` | NAIC §4.3 vendor due diligence |
| — | Final acceptance table | `acceptance-table.md` | The requirement document's acceptance criteria |
| — | Sealed-set protocol, evaluator text | `sealed-set-protocol.md` | `docs/evaluation/scoring-protocol.md` |
| — | Handover plan (one page) | `handover.md` | The close-out work |

**Related**
- The TCO: `docs/poc-plan/tco.md`.
- The demo: `docs/poc-plan/demo-script.md`.
- The operations runbook: `docs/operations/runbook.md`.

**Review cadence (proposed).**
- Artefacts 1, 2, 4 and 8 are reviewed annually.
- They are also reviewed after any material change: a model, the prompt version, the rule catalog, or the learn gate.
- Each review is recorded as an audit event (artefact 3).
