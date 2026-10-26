# ADR 0026 — Where carrier knowledge lives: meaning is global, layout is per format

**Status:** accepted (brief 29 §5; proposed in `docs/architecture/flow-diagrams.md` §"Decision records") · **Date:** 2026-10-25

## Context

Carrier knowledge has four possible homes:
- the hand-curated glossary (`docs/evaluation/glossary.json`);
- learned glossary aliases (ADR 0019);
- a carrier format spec (`src/TestShared/formats/`, learned specs in the registry);
- the code.

The flow-diagram verification found the rule implied everywhere but written nowhere: ADRs 0018, 0019 and 0023, the generic guard (brief 20 §4) and `carrier-format-memory.md` each state a part of it. Without one sentence the team can carry, each new layout invites a different answer. The answers then drift: a mapping true of one carrier's table ends up in the glossary, or a code branch tests a carrier's name.

## Decision

**Meaning is global; layout is per format.**

1. **A header-to-field mapping belongs to the glossary** when it is true of the words regardless of who printed them ("Claimant" is `claim.claimantName` on every run). It belongs to the format spec when it is true of one layout only (the third unlabelled column of one carrier's table).
2. **A mapping learned for one format becomes global only with evidence across formats, or a reviewer's confirmation.** That is ADR 0019's lifecycle: a learned mapping is a candidate alias, and it becomes active on a reviewer's confirmation or on verification across independent learned formats. The thresholds are in `config/alias-policy.json`: 5 documents across 3 learned formats, and a hand-spec replay never counts as a format (brief 29 §1).
3. **A mapping whose meaning depends on the table is a conditional alias with its condition attached**, never an unconditional one. Examples are the glossary's `compositeHeaders.conditionalComponents` (ADR 0018) and a header that means paid in one table shape and incurred in another. If no condition can be stated, the mapping stays in the spec.
4. **No code branches on a carrier's identity.** Carrier knowledge is data (specs, glossary, learned aliases). `tools/generic-guard.sh` blocks carrier names and carrier conditions in `src/`. The only exceptions are lines marked `generic-guard: allow (<reason>)` that scan for the names themselves or carry a placeholder value.

## Consequences

- **Every layout question has one answer:** first ask whether the mapping is true of the words or of the table.
- **The glossary changes only by pull request.** Promotion of long-lived learned aliases (`lossrun glossary export --promote`, brief 29 §2) proposes a diff and a PR description, and never writes the file. The glossary is rendered into the learn prompt, so a change carries a corpus v5 run (`tuning-loop.md` §3).
- **Specs stay small and replaceable.** Retiring a format removes its layout knowledge only; the meanings it relied on stay in the glossary.
- **Reviews are where a local mapping is promoted** (the confirm-format review's alias decisions, ADR 0019), not code changes.
- **The cost:** some mappings stay per format longer than a human would keep them. A reviewer's confirmation is the fast path.
