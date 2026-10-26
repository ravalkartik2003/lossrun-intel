# ADR 0018 — Spec language: multi-line claim records, stacked cells, component-naming headers

**Status:** accepted (brief 19 Answers), amended below · **Date:** 2026-10-24

## Context

The brief 17 and 18 real-sample runs left three layout families the carrier-format spec (`docs/evaluation/carrier-format.schema.json`) could not express. Brief 17 named them P2 and P3; brief 18 Answers 5 named the third.

| Family | Seen in | What the spec could not say |
|---|---|---|
| **Multi-line claim records** (P2) | axis-cameron-county; liberty-mutual-mercedes-isd pp. 72–73; point-isabel-isd pp. 22–24 | **AXIS:** a claim prints a key line (number, state, dates, status, claimant) and an amounts line in the same columns, under two header rows; one page prints the second header row as a merged caption row.<br>**Liberty and Point Isabel:** one small table per claim, then loose lines ("Claim Type: …", "Location: …", the claim number on a line of its own) and a "Policy Total:" line whose amounts sit under the table's columns. Content Understanding returns those amounts as separate paragraphs, merges consecutive printed lines into one paragraph, and on some pages draws the claim table over the loose lines below it |
| **Stacked two-field cells** (P3) | collin-county-wc | A header cell printing two labels ("Claim Number" over "Claim Type"), each body cell printing two values. CU joins the two printed lines with a space, both in the header and in the body ("IC2100001 Medical Only") |
| **Component-naming headers** (Q5) | travelers-loss-reports-manual ("Paid CLM + MED + EXP") | The composite grammar read "Incurred CLM + MED + EXP" as medical incurred. A total of indemnity and medical only ("Total Incurred (Ind+Med)") beside separate expense columns would fail R-20, which expects indemnity + medical + expense |

The brief also asked for learn-quality fixes: Starr's best attempt missed 8 required fields, St Charles read no grand total, and cccta and marin were blocked before summary support.

## Decision

Spec schema **carrier-format-0.4.0**, canonical schema **0.6.0**, rule catalog **0.6.0**, glossary **0.4.0**, learn prompt **learn-v7**. Older specs load unchanged.

1. **Records (`tableLocator.records`).**
   - **`mode: rows`.** A row with a value under `keySource` (default: the claim-number column) opens a record. The following rows without one are its lines 1, 2, … A record ends at the next key row, a row-kind row, or after `maxLines` rows.
     - A column-map entry with `line: n` reads record line n under `under` (the line-0 header it is printed beneath), or under its own header on header row n.
     - Rows of captions only ("Loss Paid: / Expenses Paid:") are skipped.
     - With a records spec, a column whose composed header ("Loss State: Loss Paid:") matches no source is read by its line-0 header.
     - A total row prints a record's line-n amounts on one line, so total rows read line-n entries from their `under` column.
   - **`mode: lines`.** Each claim row owns the **loose lines** printed after it, up to the next claim row, a total line or `maxLines`. These are lines outside the used tables, rebuilt from the OCR words, because CU merges printed lines into paragraphs; page headers and footers are excluded.
     - `lineFields` read them: a regex with a `value` group, or a relative `offset` (0-based, negative from the end).
     - A loose line a subtotal / grand-total row kind matches is a total row. Its amounts are assigned to the preceding claim table's columns by x position.
     - A table row that fills too few columns to be a claim row and is not a total row is split into its printed lines, which become loose lines: CU drew the claim table over them.
   - The record's lines are part of the replay. There are no model calls.
2. **Stacked cells.** Two column-map entries share a `source`, with `part: 0` and `part: 1`.
   - Each body cell gives its printed lines in order, recovered from the OCR words' heights; the cell's own line breaks are used only without word geometry, since CU's do not follow the printed lines. `partPattern` (groups p0, p1, …) covers a cell genuinely printed on one line.
   - Lineage records the cell once, with **`part`** (canonical `LineageRecord.part`).
   - Ingestion resolves a header that divides into exactly two known headers to both fields (`GraphTable.stackedHeaders`), so the segment's header resolution counts it. "Injury Date" was added as an alias (collin).
3. **Component-naming headers.** In the glossary grammar, a header that names two or more components joined by `+` or `&` resolves to `claim.total.<measure>` with that component set (method `component-sum`). Within such a sum, CLM / Claim / Loss mean indemnity, because medical and expense are named beside them; this replaces the brief 17 ambiguous-composite review reason where the components are explicit.
   - A spec declares the set on the total column: `components: ["indemnity", "medical"]`.
   - Indemnity + medical sets the new **`document.totalComposition: indemnityMedical`**.
   - **R-20** is then not applicable, and the new **R-29** checks total = indemnity + medical (±$1; hard when expense is printed separately). All three components keep R-20.
4. **Learn quality: deterministic auto-fixes.** These run on every learn attempt, with no model call, from what the Starr diagnosis showed:
   - **`labelNext`** is a new document-field locator: a label paragraph and its value in the next paragraph ("Valuation Date" / "9/30/2024"). It is applied when the spec's regex finds nothing and a glossary alias of the field is printed with a value after it.
   - **Period dates from the claim table:** when periods come from a column and the table also prints inception and expiration columns (glossary aliases, confidence ≥ 0.8), they are mapped as row-level period dates.
   - **`claimStatusRule: dateClosed`:** when a run prints a close date but no status column, a claim is closed when a close date is printed and open otherwise (lineage extractor `derived`, confidence 0.9).

   On Starr's saved best attempt these three fill all 8 missing fields, and the spec passes the `pdf-totals` gate.
5. **Lint** names each construct used inconsistently:
   - `line` without `records.mode: rows`, or `under` not a line-0 header;
   - `lineFields` outside lines mode, or a regex that does not compile;
   - a stacked part mapped twice, or a `partPattern` without its group;
   - `components` that differ from what the header names, or set on a non-total column;
   - a `claimStatusRule` beside a mapped status.

   The learn prompt explains each construct with a JSON example.
6. **Brief 18 follow-ups in the same versions:**
   - **R-28** (flag): a "no losses" statement no period could take, set as `document.unattributedNoLossesStatement` (Answers 3);
   - the 50 % fallback page share stays a configuration value, and is listed as a tuning-run open item in `docs/operations/blind-run.md` (Answers 4).
7. **Generator 0.4.0 (`eval generate --constructs`).** One fictitious carrier per construct, generated apart from the corpus v3 plan, so corpus v3 and its deck table stay reproducible:
   - c6: two-line records;
   - c7: one table per claim with loose lines;
   - c8: stacked cells;
   - c9: component-sum CSV.

   Offline tests replay hand specs on recorded CU graphs of c6–c8 and compare every claim with truth (`src/LossRun.Formats.Tests/ConstructTests.cs`); c9 is in `ConstructCsvTests`. The constructs corpus is also run end to end (learn, then replay) and scored: see `docs/evaluation/results/2026-10-24-real-samples-3/`.

## Consequences

- **The three families are expressible and replay deterministically**, and the real layouts that motivated them can be learned.
- **Coverage is at two levels.** The constructs are covered by offline tests on synthetic carriers. Their real-sample effect depends on the model choosing them during learn; the prompt teaches them, and lint feeds back misuse.
- **Two-column, one-target maps are still a lint issue** (brief 18), except for stacked parts, which have distinct targets.
- **`claimStatusRule` is an inference.** A claim closed and then reopened, with its close date still printed, would read closed. R-04 (closed ⇒ zero reserves) flags such cases. The rule is opt-in per format, and the tuning run must confirm it (open items).
- **Lineage gains `part`.** The UI shows the same cell for both values of a stacked cell.

## Amendment (brief 19 Answers, follow-up)

Spec schema **carrier-format-0.5.0**, canonical schema **0.7.0**, rule catalog **0.7.0**.

1. **Components the run does not print** (Answers 1).
   - The spec's `absentComponents` (e.g. `["medical"]`) records the canonical `document.componentPresence.medical = { present: false }`: not zero, not null.
   - A deterministic auto-fix declares it only when no header of the claim tables resolves to a medical field.
   - The learn gate does not require an absent component's fields. The run still passes only when every printed component ties to the printed totals.
   - The quote payload lists, per absent component, the documents and the catalog metrics computed without it (`sourceAvailability`, "not available from source").
2. **`statusFrom` replaces `claimStatusRule`** (Answers 2). Deriving status from the close date is a carrier convention, so:
   - **`closeDate`** is kept only when the learn loop verified it: replay with it reconciles to printed open/closed counts (R-11 or R-13 pass, none fails);
   - **otherwise `unknown`**, a new canonical status, neither open nor closed, flagged by **R-30**;
   - a status beside a printed status column is removed.
3. **Construct instruction from ingestion evidence** (Answers 3).
   - `LayoutEvidenceDetector` records in the graph (`layoutEvidence`):
     - `records-rows`: claim-number rows each followed by an amounts row without a number;
     - `records-lines`: a claim table without a claim-number column, followed by a printed line carrying a claim number;
     - `stacked`: one header cell, two claim fields, one header row.
   - The learn prompt then states the construct as an INSTRUCTION, quoting the evidence; header-signal hints remain for the other constructs.
   - Replay never proposes a spec.
   - On corpus v3 the detector fires only on the one c1 scan whose header cell is genuinely stacked.


## Amendment 3 (brief 20 Answers 2: claim rows with period totals)

The layout detector states a fourth fact from evidence: `claims-with-period-totals`. It fires on claim rows (a table with a claim-number column) printed together with period totals, in either form:
- a separate period table (a period label, counts and totals, no claim number);
- per-period total rows inside the claim table ("Policy Total …").

The learn prompt (learn-v9) then instructs: claim rows are the records, and the period rows are totals to tie against (rowKinds subtotal / grandTotal, or periods.lookup.subtotals), never `rowShape: periodSummary`. A `periodSummary` row shape on such a document is a lint issue. This keeps the learn from alternating between the two readings, as on AXIS in brief 20. It also makes the period totals the reconciliation target, rather than records to deliver.

## Amendment 2 (brief 20: learn-loop quality)

The spec schema stays at carrier-format-0.5.0 and the learn prompt moves to **learn-v8**. What a spec reads widens; no spec that read a value before reads a different one.

1. **Printed lines.**
   - **Document fields.** `regex` and `regexPresent` locators run over each paragraph and then over each printed line rebuilt from the OCR words, trying a line's parts before the whole line. CU merges printed lines into paragraphs differently from one document of a layout to the next ("Workers' Compensation Loss Run Insured: …" on one, two paragraphs on the next). A label-anchored regex ("^Insured:") therefore reads every document of the layout. Paragraphs come first, so a value matched before is matched the same way.
   - **Line fields.** `lineFields` are tried on the whole loose line and then on each part. The parts are values printed apart on one line, such as a claim number and a file number, which CU returned as two paragraphs.
2. **No value group.** A document-field `regex` without a `value` group yields its whole match, as `lineFields` already did. A blank capture is not a value.
3. **Deterministic repairs on every learn attempt** (`SpecLint.Quality.cs`). Each is kept only when the replay on the learning document confirms it.
   - **Document-field regexes:**
     - prompt markup is removed (a markdown `#`, or a paragraph role such as "pageFooter:");
     - a regex anchored on merged text is re-anchored on the value's own label;
     - a capture of this document's literal value is generalized (carrier name and line of business keep theirs);
     - an optional field that reads nothing that normalizes is removed. Required fields stay, so the gate still names them.
   - **Normalizers:** a text normalizer on an enum, date or amount target becomes the type the target needs.
   - **Patterns:** a `\b` right after literal punctuation ("^Policy Total:\b", which never matches before a space) is removed.
   - **Stacked cells:** `claim.rawStatus` reads the same stacked part as `claim.status` (the lint allows the pair), and a component listed absent that a column maps is not absent.
4. **Claim-number evidence.**
   - **Prompt.** The records-lines instruction quotes the printed lines below the evidenced table as replay reads them, with parts.
   - **Lint.** It names a claim-number line field that is one literal claim number. For records the pattern matched on no line, it shows their printed lines.
   - **Generator.** Corpus-gen 0.6.0 prints c7 claim numbers in three shapes, with a file number on every other claim-number line.

## Amendment 4 (brief 22 Answers 1: measures as rows)

**Context.** Travelers' standard claim report, in both published generations (brief 22), prints three rows per claim: Inc, Pd and O/S. Each row is labelled in a cell of its own, under the component columns Total | Claim | Medical | Expense, and "Claim" is the indemnity component. The policy totals and the report total print the same three rows. None of the three earlier constructs reads this layout. Records rows needs a header per line, which this layout does not print.

**Decision.** This is the fourth construct: `tableLocator.records.mode: "measures"`.

- **Opening a record.** A key row (a value under `keySource`, default the claim-number column) or a total row opens a record.
- **Measure rows.** A row whose cell matches a measure label is that measure's row, and its cells are filed on the open record. The labels are `measureLabels.paid|reserve|incurred`, .NET regexes over a whole cell. The defaults (`LossRun.Contracts.MeasureLabels`) are Pd / Paid, O/S / Outstanding / Reserve and Inc / Incurred, each with an optional colon.
- **Unlabelled rows.** A row with no key and no label, such as an accident-description line, is skipped while a record is open.
- **Column map.** A component column maps to the component without a measure: `claim.indemnity`, `claim.medical`, `claim.expense`, `claim.total`. Replay expands it on a copy of the spec into one entry per measure, and each entry reads that measure's row. The published spec is unchanged. Lineage cites each value's own printed row.
- **Lint.**
  - A measure-less component outside measures mode is an issue.
  - Measures mode with no component entry is an issue.
  - `line` is not allowed in measures mode.
  - Every label regex must compile.
- **Repair** (`SpecLint.RepairMeasureColumns`). Per-measure entries of one column become the component; a single measure beside measure-less siblings does too ("Total" read as total incurred only).
  - **Why:** both mistakes were seen on the first c10 learns. Each read the key row only, so R-02 failed on every claim, or total paid and reserve were never read.
- **Ingestion evidence.** `LayoutEvidenceDetector` emits `measures-as-rows`: rows labelled with at least two measures, each on at least two rows. The learn prompt states it as an instruction, which replaces the records-rows instruction, because those amounts rows are the measure rows. The instruction also says a "Claim" or "Loss" column beside Medical and Expense is indemnity in this mode.

**Coverage.**
- **Construct c10:** corpus-gen 0.7.0, `--v5`. Six native PDFs from the fictional carrier "Exemplar Casualty Mutual", appended after corpus v4, so v4's documents are unchanged by seed.
- **Corpus v5:** corpus v4 plus c10, 332 documents. It is the prompt-gate corpus from this amendment on (`tools/prompt-gate.py`, `MIN_DOCUMENTS = 332`).
- **Test:** `ConstructTests.Measures_as_rows_read_each_component_on_the_row_its_measure_label_names`, on a recorded Content Understanding graph, against the generator's truth.
