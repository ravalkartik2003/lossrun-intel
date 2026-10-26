# ADR 0009 — Classified loss-run segments without a table are reviewed, not dropped

**Status:** proposed 2026-10-22 (`docs/evaluation/scoring-protocol.md`)

**Context.** CU GA returns no segment confidence. The preprocessing work derives one from the header resolution of the segment's best table:
- ≥ 0.8: accept;
- 0.5–0.8: review;
- < 0.5: not a loss run (dropped from extraction, kept in the manifest).

A segment with **no table** got confidence 0 by default, so it was dropped. In the evaluation harness evaluation run this discarded every document of the narrative carrier (c3, a statement with no tables): 4 of 4 renderings of each logical document, although the CU classifier had labelled them `LossRun`. Scans on which CU reads no table would be dropped the same way. That contradicts the preprocessing work, under which CU tables are optional and the line fallback reads them.

**Decision.** For a classified `LossRun` segment, a missing table is not evidence against the classifier:
- **No table:** decision `review`, method `header-resolution (no table)`. The segment is extracted like any loss-run segment (line fallback, or learn). Verification and the rule gate decide whether a reviewer sees it, as for every document. The segment's `review` decision is shown in the manifest and UI.
- **With a table:** the preprocessing work thresholds stand. A table whose headers resolve below 0.5 is still evidence that the page is something else (payroll, X-Mod worksheet).

**Consequences.**
- Classification recall on table-less loss runs no longer depends on CU finding a table.
- The cost is a learn attempt on such segments, plus a reviewer's look when the learn does not verify. A non-loss-run page without tables that CU mislabels as `LossRun` is no longer filtered out here; the learn gate and Review catch it instead.
- Real narrative loss runs are rare, but scans of poor quality are not; this keeps them in the pipeline.
- `IngestionPipeline.SegmentDecision` holds the rule, and `ContentUnderstandingTests` covers it.
