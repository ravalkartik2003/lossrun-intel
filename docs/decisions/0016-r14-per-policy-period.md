# ADR 0016 — R-14 is scoped per policy period

**Status:** accepted (brief 18 Answers), amended by ADR 0018 (attribution, R-28) · **Date:** 2026-10-24

## Context

R-14 ("'No losses' statement means zero claim rows", `docs/evaluation/rule-catalog.json`) was document-scoped: when `document.noLossesStatement` was true, the document had to have no claim rows at all.

Real loss runs print the statement per policy period. They say "no claims for this policy year" under one term and list claims under the next. The brief 17 fitness run (`docs/evaluation/results/2026-10-24-real-samples/`, proposal P8) found 4 of the 9 learned documents failing R-14 for that reason:
- starr-cameron-county;
- liberty-mutual-mercedes-isd;
- point-isabel-isd;
- laccd.

Starr tied all 9 of its printed totals and failed **only** R-14. A rule that fails a correct document is not a check, so it routes every such run to review.

## Decision

1. **Canonical schema 0.5.0** adds `policyPeriod.noLossesStatement` (boolean, default false). It is set when the period prints a "no claims / no losses" statement.
2. **R-14 is period-scoped** (catalog 0.5.0): when `period.noLossesStatement` is true, `count(period.claims) == 0`. Severity stays hard.
3. **R-27 (new, flag)** covers a period with no claim rows, no printed subtotals and no statement. It is a routing signal, not an error: an empty term is usually a term without claims, but it is also what a missed table looks like.

   The three cases the brief names:

   | Period | Outcome |
   |---|---|
   | Statement, zero rows | R-14 passes |
   | Statement and rows | R-14 hard failure |
   | No statement, zero rows | R-27 flag |

   The document outcome is the worst period outcome: one failing period blocks the document, as any period-scope hard rule does.
4. **Replay sets the period flag** deterministically, with no model call:
   - **On a row.** The row matches the no-losses pattern and prints no amount ("2021-2022 No claims exist for this policy year"). It marks its period, still fills the period's dates, and is never read as a claim.
   - **In a paragraph.** The paragraph lies between a block anchor and the next anchor and marks that block's period.
   - **The pattern** is the glossary's multi-word `document.noLossesStatement` aliases plus the common phrasings. A spec can override it with `periods.noLossesPattern` (spec schema 0.3.0).
   - **A document-level statement** (the existing `documentFields` locator) that no period took for itself goes to the periods without claim rows. *Amended (brief 18 real-sample run; ADR 0018):* when every period has rows, it goes to the period only if there is exactly one (rows under it fail R-14). With several, it most likely belongs to a term the replay did not read as a period: it stays unattributed, `document.unattributedNoLossesStatement` is set, and **R-28** (flag, brief 18 Answers 3) shows it to the reviewer.
   - **`document.noLossesStatement`** becomes derived: true only when every period states no losses. A document with no periods keeps the located value.
5. **A period with the statement has `detailLevel: claims`** (a claims period with zero rows), not `summaryOnly`.
6. **Comparator and scorer.**
   - `noLossesStatement` is compared as a boolean.
   - Periods without a policy number are aligned by period label, then effective date (summary periods, ADR 0017).
   - The flag is scored only where a layout prints it, as `PrintedFields.PeriodFields` lists it.

## Consequences

- **Starr-like runs can publish.** Runs that tie every total and print per-period statements no longer fail R-14.
- **Wrong statements are still caught.** A statement over rows still fails, now naming the period.
- **More lineage.** Every period carries `noLossesStatement` in the canonical JSON, and lineage records where the statement was read. A false value has lineage too ("default false").
- **Corpus v3 is unaffected:** none of its layouts prints a per-period statement. The synthetic coverage is a separate c5 rendering with a no-losses year, `Renderers.CsvWideWithNoLossesYear`, kept out of the corpus plan so the deck table stays reproducible. Its tests are in `src/LossRun.Evaluation.Tests/PeriodLevelTests.cs`.
- **The rule ids stay provisional** until the client's R-01..R-15 catalog lands. R-27 is ours.
