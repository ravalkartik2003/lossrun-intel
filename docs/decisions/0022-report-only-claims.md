# ADR 0022 — Report-only claims: status open, plus a `reportOnly` flag

**Status:** accepted (brief 22 Answers 4) · **Date:** 2026-10-24

## Context

The data items in Travelers' published Loss Reports Quick Guide include two that mark a claim recorded without financials:

- the status `X`, "census only: a newly reported claim without financial information";
- an "incident indicator" (Y/N, the claim was established for reporting purposes only).

The canonical status is open, closed or reopened. Mapping `X` to open alone loses the fact that the claim carries no amounts. A separate status value would break every metric that counts open claims.

## Decision

**Two answers to two questions.** Status says whether the claim is open; the new flag says whether it carries financials.

**Status.** `X` is a status value alias of `open` (glossary 0.4.3), and `rawStatus` keeps the printed `X`.

**The flag.** Canonical `claim.reportOnly` (boolean, nullable; schema 0.8.0), set two ways:
- **From a status alias:** a glossary value alias may state flags it implies (`"sets": { "claim.reportOnly": true }`). Replay applies them with the status cell's lineage. This is the first use of `ValueAlias.Sets`.
- **From a printed column:** "Incident Indicator", "Incident Only", "Report Only" or "Record Only", with Y/N values.

**Metrics** report both totals:
- `claim_count` counts every included claim;
- `claim_count_with_financials` (analytics catalog 1.3.0, C# engine and TypeScript twin) leaves out report-only claims.

The flag is written into the analytics input only when printed, so feature snapshots of runs without it are byte-unchanged.

## Consequences

- Open-claim counts are unchanged: a census-only claim is open.
- **Severity and frequency still use every included claim.** A census claim has no amounts, so it lowers average severity. The underwriter sees both counts side by side.
- **Not yet done:** using `claim_count_with_financials` as the severity denominator would be a catalog change of its own, decided with underwriting.
- **Mock data:** the UI mock captures (`docs/ui/mock/capture/`) predate catalog 1.3.0 and show the new metric only after the next live capture (`scripts/capture-mock.mjs`).
- **Tests:**
  - `ConstructTests.A_status_value_that_states_report_only_sets_the_flag_with_the_status_lineage`;
  - the hand-computed account: claim G is flagged, so claims with financials = 6;
  - the golden parity cases, regenerated.
