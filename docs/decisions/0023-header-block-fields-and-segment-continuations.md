# ADR 0023 — Document fields from the header block; continuation segments merged

**Status:** accepted (brief 23 §1 and §3; Answers 2–4 amendment below) · **Date:** 2026-10-24

## Context

Brief 22 left the Travelers detail reports in review on period identity and valuation date. Two things were in the way:

- **No way to read the header.** The learn had no evidence of where a run prints its document-level fields. A regex the model wrote for "Valued as of" either missed or matched a whole paragraph.
- **The samples don't print the fields.** Read closely, the Travelers samples print no valuation date and no policy period at all. They print an "Accident Date Range" (2004) or "Losses From … To …" (2010): the dates of the claims listed, not a policy term.

The same work found that the classifier had split one six-page synthetic run (c10-005) into three loss-run segments. Pages 3 and 4 each opened with a period heading and the repeated table header, and the classifier read each as a new document.

## Decision

**Header fields (`LossRun.Contracts.HeaderFields`).** The fields are read from the printed word stream, clustered into lines, the same technique as the ADR 0020 labels.
- **Labels.** A label is a glossary alias of the field. It must be followed by a colon, or by the value's own shape for date labels ("Valued as of 06/30/2024").
- **Value shapes.**
  - **Dates:** document.valuationDate and document.reportRunDate.
  - **Names:** document.insuredName and document.carrierName, ending at the next "Label:" on the line, a sentence end or a wide gap.
  - **Policy number:** period.policyNumber.
  - **Policy-period windows:** "Policy Period: d – d", "Eff d Exp d".
- **Losses window.** A losses window ("Accident Date Range", "Losses From", …) is recorded as `report.lossWindow` and is never a policy period.
- **Per-period labels are not header fields.** A period label printed with different values on several lines heads each period block of a multi-period run, so it is skipped.
- **At ingestion.** The fields are `DocumentGraph.headerFields`. The learn prompt lists them under "Header block" with their printed labels.
- **In the spec.** A new locator, `headerLabel` (`label` = the printed label), reads the same value in replay, with lineage (page and the value's words). `period.policyNumber|effectiveDate|expirationDate` may be `documentFields` targets with this locator only. They fill a single-period run's period where it has none of its own; with several periods read, they are an issue and nothing is applied.
- **Repair.** `SpecLint.LocateHeaderFields` points a document field the spec does not read at its detected header label. A field the spec reads is never touched.
- **Document facts.** When a required field is missing because the run does not print it, verification states it (`SpecVerifier.DocumentFacts`), and the learn's exhaustion reasons carry it:
  - "the run prints no valuation (as-of) date";
  - "the run prints a losses window ("Accident Date Range"), not a policy period";
  - "… no policy number in its header".

  Review then gives a document fact, not a system gap.

**Continuations (`IngestionPipeline.MergeContinuations`).** Before routing, a loss-run segment is merged into the one before it when all three hold:
- it starts on the next page;
- its first table prints the same header signature as the previous segment's last table;
- its first page prints no document header block of its own (no valuation-date, insured or carrier label with a value).

A second run with its own header block, a different table, or a page gap stays apart.

## Consequences

- **Synthetic runs.** The header fields are found on every synthetic PDF layout (c1–c3, c6–c8, c10). On multi-period layouts the period labels are rightly skipped.
- **Classifier results.** Across the 206 cached classifier results, the merge changes one: c10-005, 1-2 / 3-3 / 4-6 becomes 1-6.
- **Travelers.** The detail reports now state why they are in review, as document facts. They print no valuation date or policy period, and a published spec cannot invent one.
- **Prompt version:** learn-v11.
- **Tests:** `HeaderFieldsAndRowLineTests`, `SegmentMergeTests`, `HeaderFieldRepairTests`.

## Amendment (brief 23 Answers 2–4)

- **Valuation date from the submission, never silently (Answers 2).**
  - **The rule.** When the run prints none, replay takes the submission's valuation date (`SubmissionContext.ValuationDate`, the upload form's optional "Valuation date" field). It sets `document.valuationDateSource = submission` (canonical 0.10.0; `printed` otherwise), with lineage extractor `context`.
  - **Where it shows.**
    - R-32 (flag) states it.
    - The quote payload carries `valuationDateSource`.
    - The pricing memo prints the date as "valuation date as stated by the submitter".
    - The Document screen shows "(as stated by the submitter)".
  - **When the date is in neither place,** the run still reviews, with the document fact.
- **Carrier condition on the merge (Answers 3).** A segment's printed carrier is its labelled carrier ("Carrier: …"), or else a title or page header naming an insurer.
  - **Different carriers:** when both segments print one and they differ, the segments never merge.
  - **One or neither prints a carrier:** the table signature alone decides.
  - **Effect:** across the 206 cached classifier results the merge still changes c10-005 only.
- **Deploy gates (Answers 4).** `infra/scripts/deploy-checks.ps1`, used by both deploy scripts:
  - **Before the deploy:** `lossrun-db deploy --check` (exit 3 for pending changesets, 1 for drifted) refuses to deploy.
  - **After the deploy:** a smoke upload of `lossrun-export.csv` under the retired quote `Q-SMOKE` must reach a published tier, or the deploy is reported failed.
  - **Documented in:** runbook §Deploy and close-out runbook §7.6.
