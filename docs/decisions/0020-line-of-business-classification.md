# ADR 0020 — Line of business is a classification step

**Status:** accepted (brief 20 Answers 1) · **Date:** 2026-10-24

## Context

The POC's requirement is workers' compensation loss runs (`docs/requirements/poc-requirements.md`). Three real samples (AXIS, and the Liberty and Point Isabel segments) are property runs. Brief 20's repairs read their layouts, and what still blocked them was a non-WC claim type ("Building", "Property Claim") that does not normalize into the WC claim-type vocabulary. They stayed in review, but only by that accident: each spent a full learn (5 attempts) first.

Two options were weighed: an `other` bucket on WC claim type, or a classification step. An `other` bucket would let a non-WC run verify, and so one day be delivered. That was rejected in the Answers.

## Decision

1. **Line of business is decided at ingestion, before routing and learn, with no model call.** `LineOfBusinessDetector` counts each loss-run graph's printed evidence:
   - values after a line or coverage label ("Claim Type:", "Coverage:", "Line of Business:", "Policy Type:", "LOB:"), read from the OCR word stream because CU often splits the label and its value into two paragraphs;
   - the values of a column headed by such a label;
   - headings that name a line ("Property Loss Run", "Workers' Compensation Loss Run").
2. **Vocabulary is data.**
   - **Workers' comp evidence:** the glossary's value aliases of `claim.claimType` (3 characters or more) and of `document.lineOfBusiness` WC.
   - **Other lines' terms:** `docs/evaluation/line-of-business.json`, version lob-0.1.0, covering property, auto, general liability, professional liability, crime, bond, cyber and umbrella. Ambiguous terms such as "property & casualty" and "liability" are not evidence.
   - **Labels:** cause-like labels ("loss type") are not used, because a WC cause ("fire") would read as property.
3. **The verdict.** A graph is out of scope when either:
   - it prints another line's evidence and no workers' comp evidence; or
   - it prints at least two pieces of another line's evidence (or one line-of-business label), and they are at least 80 % of all the evidence.

   An all-lines run that prints workers' comp claims stays in scope, as does corpus v3's non-WC-rows stratum, whose CSVs mix GL or AUTO rows into WC runs. A graph with no evidence is processed as before.
4. **An out-of-scope graph is kept, not processed.**
   - **Bundle segment:** it loses its child graph.
   - **Whole document:** it gets one kept segment.
   - **Manifest reason:** "line of business: property (evidence …); out of the workers' comp scope, kept, not processed".
   - **After that:** it is listed to the user and indexed for chat like every kept segment, and it is never routed, replayed or learned.
5. `DocumentGraph.lineOfBusiness` records the verdict for every loss-run graph (workersCompensation, another line, or null).

## Consequences

- **No model call on a property run.** On the real samples, AXIS, Liberty pp. 72–73 and Point Isabel pp. 22–24 are kept with that reason, and so is a property-policy table in one public exhibit. The synthetic corpus has no out-of-scope document.
- **Scope is a data change.** A line enters scope by editing `line-of-business.json` and `inScope`; no code changes.
- **Detection depends on what is printed.** A non-WC run that prints no line, coverage or claim-type evidence is processed as before. If its claim types are not WC values it still cannot verify, so it reaches review rather than delivery. The tuning run should look for such runs in the review queue.

## Amendment (brief 23 §2: row-level line of business)

**Context.** A loss run can mix lines in one table. Travelers' detail reports print a file prefix per claim: CB / CM for workers' comp, and LR, PP, AB … for other lines. c5's synthetic CSV prints a line column with WC, GL and AUTO. The document-level step below keeps a run only when most of its evidence is another line, so a mostly-WC run went on with its other-line rows read as claims, or with "could not normalize" issues on their claim-type codes.

**Decision.** Classify rows, not the document, when the rows carry the signal.
- **Signals** (`line-of-business.json` lob-0.2.0, `rowSignals`, each with provenance):
  - a claim-type code pattern (the published Travelers file prefixes);
  - the glossary's line-of-business value aliases (WC, EL, GL, AL);
  - the file's line terms.

  A code the glossary resolves to a workers' comp claim type is in scope before any pattern is tried. Employers liability (EL) is in scope.
- **Replay** (`RowLineOfBusiness`). A claim row whose `claim.lineOfBusiness` or `claim.claimType` column names another line goes to the period's `keptRows`, not to its claims.
  - It keeps its values, with lineage under `policyPeriods[i].keptRows[j]`.
  - Its `keptReason` reads "line of business: <line> (<evidence>)".
  - It is counted (`ReplayStats.KeptRows`) and never enters claims, metrics or the quote payload.
- **Schema.** Canonical 0.9.0 adds `PolicyPeriod.keptRows` and `Claim.keptReason`.
- **Manifest.** The manifest item lists the kept rows (`ManifestItem.keptRows`: claim number, policy, reason, page, total incurred). The API documents it.
- **Verification** (rule catalog 0.8.0). R-10 to R-13 pass when the printed total equals the claims, or the claims plus the kept rows. That covers a total printed for workers' comp only and a total printed across every line. A printed total that ties neither way fails, so the document goes to review.
- **No printed total.** Then nothing changes: a spreadsheet's rules gate still applies to the workers' comp rows. See brief 23 report, Questions.
- **Corpus.** corpus-gen 0.8.0 moves c5's other-line rows to `keptRows` in the truth after rendering, so every file is unchanged by seed.

**R-31 (brief 23 Answers 1).** Kept rows and no printed total to reconcile the workers' comp rows against is a flag, not a review trigger. The rules gate still applies. R-31 shows in the review panel, and the manifest item's reason states it. Revisit if the tuning set shows kept rows hiding real errors.

**Consequences.** A Travelers detail report's LR rows are kept, with the reason naming the code, instead of failing claim-type normalisation. A run whose kept rows have no printed amounts leaves the "claims plus kept rows" comparison unknown, not failed.
