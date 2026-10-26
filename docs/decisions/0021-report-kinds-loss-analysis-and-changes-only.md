# ADR 0021 — Loss-analysis pages and changes-only runs are kept, not processed

**Status:** accepted (brief 22 Answers 3 and 5) · **Date:** 2026-10-24

## Context

Brief 22 ran the published Travelers loss-report package. Two kinds of page in it are not complete loss runs, and the zero-shot classifier calls both `LossRun`:

- **Analysis pages.** Claim lag analysis (buckets by days to report) and frequency analysis (claims and incurred by region). These have no claims and no policy periods. The learner read their buckets as summary-only periods, and once published a wrong spec from one of them.
- **Changes-only runs** ("Detail Changes", "Changes Only"). These list only the claims whose values moved since the last report. Extracted as a run, they understate the period's counts and totals. R-14 fired on one.

The Answers chose:

- **For the analysis pages:** a classifier label, kept and indexed for chat but not extracted. The underwriter can still use them through chat; as claim rows they are useless.
- **For changes-only runs:** kept-not-processed with a manifest reason, not a `partial` flag. A flag would one day let an incomplete run into the counts.

## Decision

`ReportKindDetector` runs at ingestion after the line-of-business step (ADR 0020), before any learn, and makes no model call. It reads only what the page prints:

| Kind | When | Classification | Manifest reason |
|---|---|---|---|
| `loss-analysis` | No table with a claim-number column, and either a heading naming an analysis or a column naming an analysis measure. Headings: "Claim Lag Analysis", "Frequency Analysis", "Severity Analysis", cause-of-loss analysis, loss stratification. Columns: "Lag Period", "Days to Report", "Percent to/of Total". | `LossAnalysis` | `analysis report, not a loss run; kept, not processed (<evidence>)` |
| `changes-only` | A heading naming the changes since the last report: "Detail Changes", "Changes Only", "Changes since the last report". | `LossRun` | `changes since the last report, not a complete run; kept, not processed (<evidence>)` |

**What happens to a matching document:**
- **A whole document** becomes one kept segment with the reason.
- **A bundle segment** loses its child graph, as in ADR 0020.
- **Manifest and chat:** the manifest lists it as kept-not-processed, and chat indexes it.
- **Record:** `DocumentGraph.ReportKind` records the kind.

**Why the claim-table guard:** a page that prints claims is a loss run, whatever its title says. A summary-only run (ADR 0017) prints no analysis heading or column, so it stays a loss run.

**Why not a classifier category:** a new category in the Content Understanding analyzer would mean a new analyzer version (`lossrun_classifier_v3`) and a re-classification of every cached document. The deterministic step is cheaper, testable, and states its evidence in the reason. The zero-shot categories are unchanged.

## Consequences

- No learn is spent on these pages, and their rows never enter metrics or the quote payload.
- **A changes-only run is not delivered.** If a carrier sends only changes reports, the underwriter needs the full run. The manifest reason says so.
- **The patterns are layout words, not carrier identity** (generic guard). A real archive may name other analysis pages, such as "Large Loss Report" or "Loss Triangle". Each one found is added here, with a test.
- Tests: `ReportKindTests` (detection, guards, kept segment and bundle child).
