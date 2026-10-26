# ADR 0007 — Learn-path verification gate (what "verified" means before a learned spec is published)

**Status:** accepted 2026-10-22 (accepted in the learn-path review)

**Context.** The learn path publishes model-written `CarrierFormat` specs that then replay on every future document of that carrier with no model call (carrier-format-memory.md, "LLM-synthesized extractors"; CACHE-ED2-style verify-before-store). The brief's gate was "0 hard failures, required fields mapped, totals tie". On the first live learn of the second synthetic carrier (prompt learn-v1), a spec passed that gate with 4 claim rows, 0 hard failures and every printed total reconciled, yet it declared `jurisdictionState = "California"` as a constant (not printed anywhere), set `expenseTreatment = mergedIntoMedical` although an expense column is mapped (which softens R-20), captured `insuredName` as "Beta Tooling (synthetic) Evaluation Date: 08/31/2024" (two printed lines merged into one paragraph), mapped summary-box headers to `period.*` targets the replay never reads, and wrote derived rules with a `claim.` prefix the replay cannot apply. Totals and rule checks are blind to all of these. The spec existed only in a local scratch registry.

**Decision.** A spec is publishable only when, replayed on the document it was learned from:
1. no hard rule fails;
2. no printed header of a used table is unmapped (or deliberately ignored);
3. carrier name, valuation date, every period's policy number and dates, and every claim's claim number are present;
4. the replay reports no normalization issue; and
5. at least one printed subtotal or grand total reconciles (R-10 or R-12 pass, none fail). This is `LearnOptions.RequireTotals`, default on; a run that prints no totals goes to Review.
6. `SpecLint` finds nothing:
   - column-map targets are canonical `claim.*` paths;
   - derived-rule paths are `component.measure`;
   - constants only for layout facts (`expenseTreatment`, `carrierName`, `lineOfBusiness`, `noLossesStatement`);
   - `expenseTreatment = separate` whenever an expense column is mapped;
   - no name capture runs into another printed label or date.

Lint findings are fed back to the model like rule failures. They are never auto-fixed. After lint was added (prompt learn-v2), the same document produced a clean spec, in 1 iteration (CLI) or 2 (Azure test run, the first attempt rejected by lint).

**Consequences.**
- Learning costs slightly more on average: one extra iteration in some runs, about $0.013.
- A learned spec can no longer assert facts the document does not print.
- Lint rules are code (`LossRun.Learning/SpecLint.cs`), versioned with the prompt (`PromptBuilder.Version`).
- The constant allow-list and the RequireTotals policy are open questions in the brief-06 report. Spreadsheet exports without totals will route to Review until they are answered.

**Amendment (from the learn-path review).**
- **Gate per file kind.** The gate the spec passed is recorded as `learning.verificationGate`.
  - `pdf-totals`: PDFs need a reconciling printed total.
  - `spreadsheet-totals`: the same check for spreadsheets that print totals.
  - `spreadsheet-rules`: a spreadsheet without totals passes when all of these hold:
    - 0 hard failures;
    - no printed total contradicts the rows;
    - required-field coverage is at least 80 % (`SpecVerifier.GateFields`, 11 fields);
    - the submission context names the same carrier.
- **Printed constants.** Lint now rejects a constant whose value, or a glossary value alias of it, is printed on page 1. Phrases shorter than 4 characters are not searched.
- **Lifecycle.** The first learned version of a new carrier is published as `status: shadow`.
  - Its first 3 ReplayAccepted documents are processed normally and also flagged `ConfirmNewFormat`.
  - Either 3 clean replays or `FormatLifecycle.ApproveAsync` makes it `active`.
  - Drift and delta versions inherit the live status of the version they supersede.
  - Live status sits in the stats sidecar (`lifecycle` per version), because published specs never change.
