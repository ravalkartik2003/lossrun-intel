# ADR 0019 — Learned glossary aliases: candidate, active, retired

**Status:** proposed (brief 21) · **Date:** 2026-10-24

## Context

The glossary (`docs/evaluation/glossary.json`) is hand-curated. Each brief that met a new layout added aliases by hand: brief 17 added the headers the real samples print, and briefs 18–19 added more. Carrier formats grow without code changes: a learn is verified once and then replayed. Vocabulary did not grow that way.

Since the learn-path work the learn path has recorded the headers a published spec maps and the seed does not resolve (`LearnedMapping`, blob sidecar `glossary-learned.json`). Those mappings had no lifecycle:
- every recorded mapping was used by the next learn, verified or not;
- none was used by ingestion or replay, so fingerprinting and header resolution never saw them;
- a reviewer could neither confirm nor reject one;
- a correction of a value the mapping produced changed nothing.

## Decision

A learned header mapping is a **learned alias**. It is keyed by its normalized header and canonical field, and has a lifecycle.

1. **Candidate.** When a learn verifies and publishes a spec that maps a header the seed glossary does not resolve to that field, the alias is recorded as a candidate. Its provenance is:
   - the format (id @ version) and the document;
   - the date;
   - the learn model, model version and prompt version;
   - the verification evidence: whether the replay tied to printed totals, and the gate that published it.

   A candidate is not used for resolution anywhere.
2. **Evidence.** Every later document that verifies with a spec mapping the alias adds a verification row (document, format, route, tie, time): a replay accepted or a learn published.
3. **Active.** A candidate becomes active either way:
   - **(a)** a reviewer confirms the format that introduced it (the confirm-format review accepts it, or edits it to another canonical field);
   - **(b)** it has verified on **at least 3 documents across at least 2 formats**.

   An active alias takes part in header resolution exactly like a hand alias, after the seed's own aliases and composites. That covers ingestion (the graph's header fields, hence fingerprinting and the construct evidence), replay, the learn prompt and lint.
4. **Retired.**
   - **Correction.** A reviewer corrects a value whose lineage traces to the alias: the corrected field is read through a column whose header is the alias. The retirement links that audit event.
   - **Rejection.** A reviewer rejects the alias in the confirm-format review.

   A retired alias is never used, and the same header and field is not re-learned into a candidate. A reviewer can confirm it again.
5. **Storage.**
   - **Hand-curated base:** `glossary.json`, unchanged.
   - **SQL:** learned aliases live in `dbo.GlossaryAlias`, system-versioned with history in `dbo.GlossaryAliasHistory`, and evidence in the append-only `dbo.GlossaryAliasEvidence`.
   - **Local and Blob registries:** the same records in the sidecar, version glossary-learned-0.2.0; 0.1.0 sidecars load as candidates.
   - **Review:** `lossrun glossary export` writes a reviewable `glossary.learned.json`.
   - **Promotion to the base:** an alias moves to the base only by a pull request (tuning-loop tier 1).
6. **UI.**
   - The confirm-format review lists the aliases the format introduces, with accept, edit (canonical field) and reject.
   - The Formats screen lists the learned aliases per format with status and evidence counts.
   - Decisions are audited (`glossary.alias.*` events).

## Configuration and tooling

- **Thresholds:** `config/alias-policy.json` (brief 29 §1; brief 21 Answer 4): **5 documents across 3 learned formats**, `countHandFormats: false`. Read by `AliasPolicy.Default` (→ `LearnOptions.AliasPolicy`, `FormatMemory`); `LOSSRUN_ALIAS_MIN_DOCUMENTS` / `LOSSRUN_ALIAS_MIN_FORMATS` override per run. A replay of a hand-written spec is recorded as route `hand-replay`: it counts toward documents, never toward formats (brief 21 Answer 2). Before brief 29 it did count, so a hand spec could supply an alias's second or third format. Since evidence rows are append-only, history is corrected in the reading (brief 29 Answers 4): the format count excludes any format the registry knows as `createdBy: "hand"` (`AliasPolicy.WithRegistryAsync`; `glossary export --promote` takes the hand formats from the registry, the run's `registryAtEnd` or `GET /formats`), whatever route its evidence carries.
- **Promotion:** `lossrun glossary export … --promote [--min-weeks 4] [--min-documents 10]` writes `glossary.promote.json`, a proposed diff to `glossary.json` with provenance per alias (active ≥ N weeks, never retired, ≥ M verified documents, not in the seed), and `glossary.promote.pr.md`. It never writes `glossary.json`.
- **Experiments:** `LOSSRUN_GLOSSARY_ABLATE` removes hand aliases for an ablation run (`scoring-protocol.md` §8).
- **Run snapshot:** `run.json` records `learnedAliasesAtEnd`.
- **Review:** `lossrun glossary export (--registry <dir> | --run <eval run> | --base <api>) --out glossary.learned.json`.

## Consequences

- **Vocabulary grows from verified learns.** A header learned on two carriers' formats becomes active without a code change. The thresholds (5 documents across 3 learned formats since brief 29; 3 and 2 before) are configuration, revisited on the tuning run. Re-scored offline on brief 21's corpus v4 run, two of the five removed hand aliases would still activate by evidence; the other three are printed by only two learned formats and would wait for a reviewer.
- **One wrong learn cannot spread.** A candidate is never used; activation needs a reviewer or repeated independent verification; one correction retires it.
- **Replay determinism holds per registry state.** An alias that becomes active can change how a later document's headers resolve, which can change its fingerprint and route. The alias set in force is part of the run's registry snapshot (`registryAtEnd` lists active aliases).
- **The hand-curated base stays the source of truth for the schema.** Learned aliases only map headers to existing canonical fields; they cannot add fields, value aliases or composite grammar.
