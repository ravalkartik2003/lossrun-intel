# Blind run: the procedure, in order

This page is self-contained: follow it top to bottom without reading the rest of the repository. With an AI assistant doing the routine steps, `docs/operations/playbook.md` drives the same procedure through five commands (`/import`, `/seal-handoff`, `/tune`, `/blind-prep`, `/score`). Run every command in a **bash** shell (Git Bash on Windows) from the **repository root**. The protocol behind it is `docs/evaluation/scoring-protocol.md`. The text evaluators receive is `docs/poc-plan/closeout/sealed-set-protocol.md`.

## 0. Before day one

| What | Who |
|---|---|
| .NET 10 SDK, Git, bash; read access to the archive share; Azure CLI signed in (`az login`) with access to the organisation's Content Understanding and Azure OpenAI resources | The operator |
| The deployed API in the organisation's tenant (`docs/poc-plan/closeout/handover.md`, steps 1–4), an Admin account for the format reset, and an Underwriter account for the run | The platform team |
| The **truth CSV** from the keyed claims system, for the policies `truth-request.csv` lists (section 2) | The data team |
| The **document → policy map** (`policies.csv`: columns `file,policyNumber`, one row per document and policy it prints; repeat the file for a multi-policy document), sent with the archive. The seal keeps every policy on one side (section 3) | The data team |
| A seal **seed** (any integer), chosen by the evaluator and kept in a sealed envelope until the seal (section 3), where it is published to every entrant before the draw | The State Fund evaluator |

Build the tool once:

```bash
dotnet build src/LossRun.Tool -c Release -o tools/lossrun
```

Set the variables for this shell. The paths are examples; keep them **outside** the repository, because archive files must never be committed.

```bash
export LR="$PWD/tools/lossrun/lossrun"
export ARCHIVE="/d/blind-run/archive"
export WORK="/d/blind-run/work"
mkdir -p "$WORK"
```

The seal seed is **not** set here: this shell is shared with the operator (and, with the playbook, an assistant). The evaluator sets it in their own shell at the seal (section 3).

## Folder layout

The same layout as `docs/operations/playbook.md` (*Folders*). Everything built from the blind set lives under **`$WORK/blind/`**; the tuning set's runs live under `$WORK/tune/`.

```
$ARCHIVE/                          the loss-run files as the archive holds them: one sub-folder per carrier (or a carrier map)
$WORK/carriers.csv                 optional: file,carrier (a row may name a file, a file name or a top-level folder)
$WORK/truth.csv                    the truth CSV from the data team
$WORK/corpus/manifest.json         written by import (files stay in $ARCHIVE; --copy puts them in corpus/files)
$WORK/corpus/truth-request.csv     written by import: the policies the data team exports claims for
$WORK/corpus/truth/                written by `eval truth` after adjudication
$WORK/seal/seal-manifest.json      tuning and blind lists with a SHA-256 per file (plus a copy of the scoring protocol)
$WORK/tune/dry-run/results/        the tuning run on the organisation's environment (section 2); /tune batches: $WORK/tune/batch-NN/
$WORK/tune/dry-run/align/          its alignment and practice workbook
$WORK/blind/results/<entrant>/docs/ one JSON per document from the blind run (canonical documents); the build is "build"
$WORK/blind/align/<entrant>/       aligned.csv, the alignment (machine file)
$WORK/blind/align/build/adjudication.xlsx the evaluator's workbook: one row per claim that needs a decision
$WORK/blind/report/<entrant>/      report.md, deck-table.md, report.json, CSVs
```

**What guards `$WORK/blind/`.** `blind/` is listed in `.gitignore` and `.cursorignore`, but those only cover folders inside the repository workspace; `$WORK` is outside it (it must be), so they do not hide it from an assistant. The real guard is in the CLI: `eval run --only-blind` exits 2 when its `--out` has no `blind` path segment, and `lossrun tune` refuses any path with one. Keep the layout above so both refusals apply.

## 1. Import the archive (operator, minutes)

```bash
"$LR" eval import --in "$ARCHIVE" --out "$WORK/corpus" --carriers "$WORK/carriers.csv"
```

- **Drop `--carriers`** when every file sits in a folder named after its carrier.
- **Add `--copy`** to copy the files into the corpus instead of referencing them in place.
- **Password-protected files:** set `LOSSRUN_PASSWORDS="pw1;pw2"` in the shell first (from Key Vault, never written down). Without it they are kept and flagged.

**Read the summary it prints:**
- **The carrier × input type table.**
- **Strata with fewer than 2 files.** Those cannot appear on both the tuning and the blind side.
- **Files with an unknown carrier.** Add them to `carriers.csv` and import again.
- **Skipped files, with the reason.** Import never changes `$ARCHIVE`.
- **The blind-set size check.** The protocol wants a 300–500-file blind set: about 400–600 files in total with a 100-file tuning set.
- **Packages (ZIP):** each stays one file, stratum `bundle`, and its member files and their types are listed in the manifest.

Import also writes `$WORK/corpus/truth-request.csv`: the claims to ask the data team for (section 2).

## 2. Export the truth CSV (data team)

Send the data team `$WORK/corpus/truth-request.csv`. It covers the **whole imported pool**, not the blind set, so the request does not reveal the draw. Its columns are `carrier, policy_number, period_start, period_end, documents, note`:
- **Policy rows** were read from the files themselves: native-PDF text, and workbook and CSV tables.
- **Carrier rows** have no policy number. Their `note` counts that carrier's files that print nothing readable before OCR (scans, images, protected files). For those, export every policy of that carrier for the accounts in the pool.

Export one row per claim of the listed policies. Use exactly these columns, in this order, with a header row:

```
carrier,policy_number,claim_number,date_of_injury,valuation_date,indemnity_paid,indemnity_reserve,medical_paid,medical_reserve,expense_paid,total_incurred
```

- **Dates:** `yyyy-mm-dd`.
- **Amounts:** plain numbers, no `$` and no thousands separators. Leave a cell empty when it is not known.
- **`valuation_date`:** the date the amounts were valued in the claims system. Amounts count as truth only where it equals the valuation date printed on the loss run (research A1).

Save the file as `$WORK/truth.csv`.

**Changing the system on the tuning set** (aliases, lint repairs, bug fixes, and design changes with an ADR) follows `docs/operations/tuning-loop.md`: tuning files only, the regression gate on every change, a pull request with the results folder, budget caps and a stopping rule.

**The tuning run is the dry run.** After sealing (section 3), the build side runs the tuning set on the organisation's environment:

```bash
"$LR" eval run --corpus "$WORK/corpus" --out "$WORK/tune/dry-run/results" --mode api --concurrency 8 --only-tuning "$WORK/seal/seal-manifest.json"
"$LR" eval align --archive "$WORK/truth.csv" --results "$WORK/tune/dry-run/results" --out "$WORK/tune/dry-run/align" --corpus "$WORK/corpus"
```

The evaluator then adjudicates `$WORK/tune/dry-run/align/adjudication.xlsx` once, as practice (section 8). Those numbers are never reported as accuracy. The same run re-derives the cost and time figures at the end of this page.

**Open items the tuning run must check** (settings chosen on synthetic and public samples, to be confirmed on the organisation's archive):
- **Classifier fallback page share** (`IngestionOptions.FallbackMinTablePageShare`, 0.5; brief 18 Answers 4). An upload CU calls "Other" goes on only from the pages with a loss table, and only when they are half the document. A real submission with a small loss run inside a large application (a 5-page run in a 60-page package) would be kept, not processed, under 0.5. Look for kept-not-processed items with reason "no loss table found: loss-run-like tables on k of n pages" in the tuning manifests; lower the share, or move the decision to the segment, if real loss runs appear there. The test that pins today's value is the Travelers manual case (`ClassifierFallbackTests`); the public `marin-exhibits` sample (one loss table in a 20-page exhibit) is the case it would drop (brief 19).
- **`statusFrom`** (ADR 0018 as amended, brief 19 Answers 2). A format that prints no status gets `statusFrom: closeDate` only when the learn loop verified it against printed open/closed counts; otherwise its claims are `unknown` and R-30 flags them. Check, on the tuning manifests, which formats ended with `unknown` and whether the archive's truth shows a usable convention.

## 3. Seal (State Fund evaluator)

In the evaluator's **own** shell (not the operator's shared one, and with no assistant attached), open the sealed envelope, publish the seed to every entrant, then seal:

```bash
export SEED=<the seed from the sealed envelope>
"$LR" eval seal --corpus "$WORK/corpus" --out "$WORK/seal" --tuning 100 --seed "$SEED" --policies "$WORK/policies.csv"
```

- **`--policies` (brief 31).** The split unit becomes the policy cluster: documents sharing a policy, transitively, all go to one side, still stratified by carrier × input type × edge case. Without the map, seal prints a warning and splits by logical document; then a policy can have documents on both sides (`tuning-loop.md` §1).

- **Publish the result.** Send the printed `blind manifest sha256` to every entrant the same day. Every later `eval verify` can check against it (`--published-hash`, section 6).
- **Keep `seal-manifest.json` with the evaluator.** It lists the tuning set and the blind set.

## 4. Release the tuning set (State Fund evaluator)

Give every entrant the **tuning** files listed in `seal-manifest.json` (`tuning[].file`, paths relative to `$ARCHIVE`), together with:
- `docs/evaluation/canonical-schema.json`, `code-sets.json` and `rule-catalog.json`;
- `$WORK/seal/scoring-protocol.md`.

Never release a blind file.

## 5. Reset learned formats (build side, the day before the blind run)

An Admin account runs this. Dry run first, then the reset:

```bash
export LOSSRUN_API_BASE="https://<the organisation's API host>/api/v1"
export LOSSRUN_API_TOKEN="<Admin bearer token>"
"$LR" formats reset --learned --reason "pre-blind-run reset $(date +%F)" --dry-run
"$LR" formats reset --learned --reason "pre-blind-run reset $(date +%F)"
```

Record the reset's audit event as `audit <id> / ledger tx <id>` in the bake-off log. `docs/operations/runbook.md` (Format registry) says how to find it.

## 6. Run the blind set (build side; about 1 hour at 500 documents, derived from synthetic measurements, replaced after the tuning run)

1. **Verify the seal first.** Every blind file must still match its hash:

   ```bash
   "$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json" --published-hash <the blind manifest sha256 published at the seal>
   ```

   `--published-hash` recomputes the blind manifest hash from the seal's blind list and fails if it differs from the published one, so an edited `seal-manifest.json` cannot pass. Without it, `verify` checks the files against the seal only.

   **If verification names files, stop.** A blind file changed, moved or was replaced after sealing. Do not run. The evaluator restores the files from the archive's original copy and verifies again. If they cannot be restored, the evaluator re-seals with a new published seed and re-releases the tuning set. The run is never "fixed" by editing the seal.

2. **Run** the blind set through the deployed API:

   ```bash
   export LOSSRUN_API_TOKEN="<Underwriter bearer token>"
   "$LR" eval run --corpus "$WORK/corpus" --out "$WORK/blind/results/build" --mode api --concurrency 8 --only-blind "$WORK/seal/seal-manifest.json"
   ```

   It writes one JSON per document. `--only-blind` exits 2 when `--out` has no `blind` folder in its path (*Folder layout*). It is resumable: run the same command again after an interruption and finished documents are skipped.

## 7. Align (operator, minutes)

```bash
"$LR" eval align --archive "$WORK/truth.csv" --results "$WORK/blind/results/build" --out "$WORK/blind/align/build" --corpus "$WORK/corpus"
```

This writes two files:
- **`aligned.csv`**, the machine file. Leave it as it is.
- **`adjudication.xlsx`**, the evaluator's workbook. It has one row per claim that needs a decision:
  - matched, but the amounts differ at the same valuation date;
  - in the archive, not extracted;
  - extracted, not in the archive.

  Matched claims that agree are not listed: they need no decision.

**How a claim is matched.** An archive row and an extracted claim match on four parts: carrier, policy number, claim number (normalized) and date of injury.
- **The carrier column is matched after corporate-suffix normalization** (brief 35 Answers 1: `CarrierNames.Normalize`, the same function the replay carrier check uses). Case, punctuation, spacing, the corporate words (Inc, Co, Company, Corp, Insurance, Assurance, Indemnity, Casualty, Mutual, Group, The) and a trailing "of America" are ignored.
  - So an archive keyed "X Insurance Company" matches documents printing "X Indemnity Co" or "The X Group, Inc.". **The data team does not hand-edit the carrier column.**
  - A distinguishing word still counts: "X Property Casualty Co of America" does not match "X Insurance Company". Its rows show as "in the archive, not extracted" and the claims as "extracted, not in the archive", and the evaluator decides them in step 8.
- The other three parts keep two carriers whose names share a core apart.

## 8. Adjudicate (State Fund evaluator, blind to the entrant)

Open `$WORK/blind/align/build/adjudication.xlsx` in Excel. Its *Instructions* sheet repeats the rules below.
- **What each row shows:** the raw claim number, carrier, policy and date of injury; both valuation dates; and the archive and extracted amounts side by side. No normalized key is shown.
- **What to fill in:** the **Decision** (a drop-down), the **Document** where asked (a drop-down of file names), and a free-text **Note**.

| Row | Decision |
|---|---|
| Matched, amounts differ | `archive-correct` (the default if left blank): the archive amounts are printed. `extracted-correct`: the document prints the extracted amounts. `not-comparable`: neither is the printed value, so the claim is kept and its amounts are not scored. `exclude` |
| In the archive, not extracted | If a document of the set prints it: choose the **Document**, then `archive-correct` (the default once a document is chosen) or `not-comparable`. If none does: `exclude` (the default) |
| Extracted, not in the archive | `extracted-correct`: the document prints this claim, and the archive is missing it. `not-comparable`. `exclude` (the default): it then counts against the entrant as an extra claim |

Save the workbook, keeping `.xlsx`, then build the truth files:

```bash
"$LR" eval truth --archive "$WORK/truth.csv" --aligned "$WORK/blind/align/build/aligned.csv" --adjudication "$WORK/blind/align/build/adjudication.xlsx" --corpus "$WORK/corpus"
```

- **The decision counts.** It always prints them, including how many were blanks that took their default. Review those before scoring.
- **Archive-only rows left at their default are refused.** A row "in the archive, not extracted" with no decision would silently become `exclude`, so `eval truth` exits 3 and lists them. Decide each one in the workbook. Only when the evaluator has reviewed the list and accepts the defaults, run it again with `--accept-defaults`, and note that in the bake-off log.
- **Bad decisions are refused.** A decision that cannot apply, for example `archive-correct` on a claim the archive does not have, is refused with its row number. Nothing is written until every row is valid.
- **Adjudicate once.** The truth belongs to the documents, not to an entrant, so the same truth serves every entrant.

## 9. Score (operator, minutes)

```bash
"$LR" eval score --corpus "$WORK/corpus" --results "$WORK/blind/results/build" --out "$WORK/blind/report/build"
```

- **`report.md` and `deck-table.md`** give accuracy by input type and template with 95 % intervals, plus review rate, latency, cost and model calls.
- **Documents without truth** are counted in a note at the top. Their path, review rate, latency and cost are scored; their accuracy is not.
- **Re-score** after any adjudication change. Scoring is a pure function of corpus, truth and results.

## 10. Publish (State Fund evaluator)

Put the per-entrant `report.md` and `deck-table.md` side by side. Each is stamped with the scorer version, models, prompt and run date. Never quote one blended accuracy number without its input-type breakdown, and never publish tuning-set numbers as accuracy.

## Vendor variant

Vendors run the same blind files on their own platform and return, per file, a canonical JSON document (`canonical-schema.json`) in the shape of `blind/results/<entrant>/docs/<id>.json`; put them in `$WORK/blind/results/<vendor>/docs/`. The `id` is the manifest id from `seal-manifest.json`. Then, for each vendor:

```bash
"$LR" eval verify --corpus "$WORK/corpus" --seal "$WORK/seal/seal-manifest.json" --published-hash <published blind manifest sha256>
"$LR" eval align --archive "$WORK/truth.csv" --results "$WORK/blind/results/<vendor>" --out "$WORK/blind/align/<vendor>"
"$LR" eval score --corpus "$WORK/corpus" --results "$WORK/blind/results/<vendor>" --out "$WORK/blind/report/<vendor>"
```

- **Adjudication happens once,** on the build's workbook (step 8). `eval truth` is shared; do not adjudicate per vendor. A vendor's extra claims show up as `extracted-only` in its own `aligned.csv`, and they count against it at scoring without a decision.
- **Failures score zero.** A file a vendor did not return scores as not extracted, never excluded.

## Expected durations and cost at 500 documents

| Step | Duration | Cost |
|---|---|---|
| Import, seal, verify | minutes | — |
| Run (API, concurrency 8; about 12 pages per document; about 40 carrier formats learned once each) | about 1 hour *(derived from synthetic measurements; replaced after the tuning run)* | about $45–60 *(derived from synthetic measurements; replaced after the tuning run)* |
| Align, truth, score | minutes | — |
| Adjudication | 1–3 days of evaluator time | — |

**How the run figures are derived** (synthetic measurements in `docs/poc-plan/tco.md` and `docs/evaluation/results/2026-10-23-batch50/`):
- **Cost:** layout at about $0.0063 per PDF page → 500 × 12 pages ≈ $38. Learns at $0.05–0.30 per new format ≈ $8. Spreadsheets cost $0.
- **Time:** a 72-file, 50 MB batch finished in 89 s. Learns run one at a time, at about 50–60 s each.

Real archive documents are longer and messier, so treat the figures as an order of magnitude.

**Replacing them after the tuning run.** The first tuning-set run on the organisation's environment re-derives pages per document, formats learned and cost from `report.md` (*Latency and cost*) and the Costs screen. Replace the two run figures above with those numbers before the blind date. Do not re-measure on the POC subscription: that would be another synthetic number.

## If something goes wrong

| Symptom | Do this |
|---|---|
| `verify` names files | Section 6, step 1. Never edit the seal |
| `run` stops part-way | Run the same command again. It resumes |
| A document's result has `error` set | It stays in the set and scores as not extracted. Note it in the bake-off log |
| `truth` says a key is not in the archive | The truth CSV was re-exported after `align`. Align again with the new CSV, then adjudicate again |
| `truth` lists rows of `adjudication.xlsx` | Fix each named row (a Document missing, or a decision that does not apply to that kind of row) and run it again |
| The report's top note says documents have no truth | Adjudication has not attributed any claim to them (section 8) |
