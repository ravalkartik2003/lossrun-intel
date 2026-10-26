# Scoring protocol — loss run extraction bake-off

For evaluators. This page fixes how every entrant (the in-house build and each vendor) is scored, **before** any test runs. The protocol follows research A4 (`evaluation-and-wc-domain.md` §A4 Inferences) and the thresholds of §A2. The scorer is the code in `src/LossRun.Evaluation`, published to every entrant. `lossrun eval seal` copies this file next to the seal manifest, so a sealed set always carries the rules it was sealed under.

## 1. Test sets

| Set | Who holds it | Size | Use |
|---|---|---|---|
| **Tuning** | Released to every entrant in advance | ≈ 100 docs | Configure, learn formats, tune. Results on it are never reported as accuracy. |
| **Blind** | The insurer's actuarial/UW team, sealed | 300–500 docs | The only set the reported numbers come from. |

- **Stratification** (`lossrun eval seal`). Documents are allocated proportionally (largest remainder) within each carrier × input type × edge-case stratum, with at least one per stratum on each side when it has ≥ 2 documents.
- **Renderings stay together.** Every rendering of one logical document (native, scans, bundle) lands on the same side. A blind scan is never a re-rendering of a tuning native.
- **Sealing.** The seal records a SHA-256 for every blind file, and a SHA-256 over the sorted blind list (`blindManifestSha256`).
  - Before scoring, `Sealing.Verify` re-hashes the blind files; any mismatch voids the run.
  - Publish the blind list hash to all entrants when the set is sealed.
- **Input types reported separately:**
  - native PDF;
  - XLSX/CSV;
  - scans (≥ 300 dpi clean, ~200 dpi rotated/noisy, ~150 dpi fax);
  - password-protected and bundled files, as edge strata.
- **Edge strata** (from the requirement):
  - missing year;
  - partial run;
  - closed-only;
  - reopened;
  - catastrophe outlier;
  - multiple policy numbers;
  - non-CA;
  - non-WC rows;
  - a source whose printed totals do not reconcile.
- **Client data.** On the client's archive, truth comes from the keyed claims system, aligned by `lossrun eval align`. Amounts count as truth only when the archive's valuation date equals the loss run's (§A1). Every other disagreement goes to adjudication, not to the score.

## 2. What every entrant receives and returns

- **Receives:**
  - the same files;
  - the canonical schema (`canonical-schema.json`);
  - the code sets (`code-sets.json`);
  - the rule catalog (`rule-catalog.json`);
  - this protocol and the scorer.
- **Returns,** per input file:
  - the loss-run document(s) in canonical JSON, each with its page range in the file;
  - per-field confidence, if the entrant has it;
  - per-field source (page + box, or sheet + cell);
  - wall-clock time and cost.
- **No submission counts as all-missing.** A file that times out, errors or returns nothing scores as all-missing: zero, never excluded (§A4: failed runs score zero and dropped rows count against the score).

## 3. Field matching — the typed comparator

Values are compared after normalization by `CanonicalComparer` (`comparator.md`). The kinds:

| Kind | Fields | Match rule |
|---|---|---|
| Claim number | `claimNumber` | Equal after removing spaces, dashes, dots and leading zeros of segments, case-insensitive |
| Date | DOI, reported, closed, period dates, valuation | Same calendar date after parsing any printed format |
| Currency | paid / reserve / incurred, totals | Within $1 (rounding) |
| Integer | claim / open / closed counts | Exact |
| Enum | status, claim type | Same canonical code after glossary aliases (`glossary.json`) |
| Short text | names, policy numbers, carrier | Normalized text similarity ≥ the comparator's threshold |

**Claim alignment.**
- Truth claims are matched to extracted claims on the normalized claim number.
- On a keyed archive (blind run), alignment keys on carrier, policy number, claim number and date of injury. The carrier part is compared after corporate-suffix normalization (`CarrierNames.Normalize`, brief 35 Answers 1; `docs/operations/blind-run.md` §7).
- When the claim number is missing or unreadable, they are matched on DOI + claimant name. Each extracted claim matches at most once.
- An unmatched truth claim scores every one of its fields as **missing**.
- An extracted claim with no truth counterpart counts toward predicted claims (precision of rows), not toward field accuracy.

**Scope.** Only fields the document **prints** are scored (`TruthRecord.Printed`). A carrier that prints no close-date column is not penalized for a null close date.

**Delivered and pre-fill.** Every accuracy measure is reported twice:
- **Delivered (no human)** — the headline. A document the pipeline routes to Review delivers nothing: all its printed values score as **missing**, whatever the pipeline pre-filled. This is UC1's measure.
- **Pre-fill (Review documents)** — the same measure, computed only over documents routed to Review, against the values the reviewer starts from (the replay of the matched or recovered format, or the learn's best attempt). It has its own CI. This is UC2's measure: how much of the reviewer's work is already done.

Entrants who do not distinguish (every value delivered without review) report the delivered column only.

**By input type, never blended (`docs/evaluation/results/2026-10-22-corpus-v2/`).** The headline is a table with one row per input type, and every document falls in exactly one row:
- native PDF, recurring template;
- native PDF, first-seen template;
- XLSX / CSV;
- scan ≥ 300 dpi;
- scan 200 dpi rotated;
- fax < 200 dpi;
- narrative statement (any rendering).

Each row shows three numbers, each with its CI:
- **Delivered accuracy** — key fields on the documents that left without a human.
- **Review rate** — the share of the row's documents routed to a human, including documents not extracted.
- **Pre-fill accuracy** — key fields of what the reviewer starts from, on the reviewed documents.

Together these three separate "what we deliver is right" from "how much we deliver". The end-to-end delivered measure above, where Review values score as missing, conflates the two. It stays in the detailed report but is not a headline.

The proposal commits targets only to rows whose design intent is delivery:

| Row | Committed target (delivered) |
|---|---|
| Native recurring | ≥ 99 % |
| Native first-seen | ≥ 98 % |
| XLSX/CSV | ≥ 98 % |
| Scan ≥ 300 dpi | ≥ 95 % |

The other rows are reported, not committed:
- **Fax and narrative** carry their measured numbers with "routed to review by design; pre-fill X %".
- **Scan 200 dpi rotated** carries delivered accuracy, review rate and pre-fill.

No number may be quoted blended across rows. The scorer writes this table to `deck-table.md` beside `report.md`.

The same layout applies to **data-quality first pass** and **quote success**. Each row shows:
- the rate on the delivered source-consistent documents;
- the review rate;
- the n delivered.

The blended all-documents figure stays in the full report only, marked as mixing by-design review rows.

**Small rows.** A row with fewer than 5 documents is printed as "insufficient sample (n = …)" and gets no met/not-met verdict.

**Caveat.** `deck-table.md` opens with the synthetic-corpus caveat: the sealed blind set on the client's archive is the number that counts.

**Error types:**
- **missing** — the row or value was not extracted;
- **wrong** — the value is wrong and the document failed a hard rule, so the error was caught;
- **wrong-but-plausible** — the value is wrong but every hard rule passed, so the error was silent. These are the costly ones, and they are reported separately.

## 4. Metrics

Each metric is reported by input type, by template (seen / first-seen / unseen-repeat) and overall.

| Metric | Unit | Definition |
|---|---|---|
| Field accuracy | field value | Share of printed values matched (per field × carrier × input type). Key fields: claim #, DOI, indemnity/medical paid and reserve. |
| Claim-level joint pass | claim | Every printed field of the claim matched (§A4: joint pass, not averaged components). |
| Classification P/R | segment | Loss run vs other: a predicted loss-run segment that overlaps a truth loss-run segment is a TP; one that overlaps only a non-loss-run segment is an FP. Bundle split exactness is reported separately. |
| DQ first-pass | document | Extracted and zero hard-rule failures, without edits. Measured only on source-consistent documents: a document whose printed totals do not sum does not count against the extractor (§A2). |
| STP | document | Extracted with no human touch: replay accepted, learned or delta-repaired, not routed to Review. Strict STP also excludes "confirm new format" prompts. |
| Quote success | document | STP, zero hard failures and at least one policy period, i.e. the payload can go to the quote system unedited. |
| Latency | document | p50 / p95 wall-clock from upload to result, by path. |
| Cost | document | Metered Azure cost (`config/azure-prices.json`) per document and per path. Vendors report their per-document price. |
| Traceability | field value | Share of values with a source a reviewer can open (page + box or sheet + cell). Scored for every entrant. |

## 5. Confidence intervals

- **Method.** Proportions use **Agresti-Coull** 95 % intervals on an effective sample size n / deff.
- **Design effect.** deff comes from a **document-level cluster bootstrap** with 1,000 resamples (`Stats.Estimate`): field values from one document are correlated, so documents are the resampling unit.
- **Reporting.** Every reported rate is shown as `value [lower–upper] (n, clusters)`.
- **A threshold passes** when the point estimate meets it. It **robustly passes** when the CI lower bound meets it too. Both are reported, and decisions should rest on the robust column.
- **Sample size.** With ≈ 300 blind documents, a 95 % field rate on ~10,000 values has a CI half-width of roughly ±1 point before the design effect, and ±2–3 points after it. Do not split segments finer than ≈ 30 documents.

## 6. Thresholds (fixed before the test)

From `evaluation-and-wc-domain.md` §A2 (Inferences). The scorer prints a pass/fail table against them.

| Criterion | Native PDF / XLSX-CSV | Scans ≥ 300 dpi | Scans < 200 dpi / fax |
|---|---|---|---|
| Claim # and DOI | ≥ 98 % | ≥ 95 % | ≥ 90 % |
| Paid / reserve splits | ≥ 97 % | ≥ 93 % | ≥ 93 % |
| Claim-level completeness | ≥ 90 % | ≥ 80 % | ≥ 80 % |
| STP — recurring template | 80–90 % | | |
| STP — first-seen template | 65–80 % | | |
| Classification | precision ≥ 97 %, recall ≥ 95 % | | |
| DQ first-pass | ≥ 85 % (source-consistent docs) | | |

## 7. Procedure

1. Seal: `lossrun eval seal --corpus <dir> --out <seal dir> --tuning 100 --seed <published seed>`. Publish `blindManifestSha256`.
2. Release the tuning set. Entrants configure.
3. Each entrant runs the blind set once, on the insurer's infrastructure or under observation. Nothing learned from a blind document may be reused for another entrant.
4. Verify the seal, then score: `lossrun eval score --corpus <dir> --results <entrant results> --out <report dir>`.
   - The report records the git SHA, models, prompt version, rule catalog, price table and the format registry at the end of the run.
5. Adjudicate disagreements (keyed-archive mismatches, ambiguous printing) blind to the entrant. Re-score once.
6. Publish per-entrant reports side by side. No single "overall accuracy" headline without its input-type and template breakdown (§A2).

## 8. Reproducibility

- **Synthetic corpus.** `lossrun eval generate --seed <n>` (generator version in the manifest) is deterministic: same seed, same files and truth. There is one exception: the password-locked PDFs get a random encryption salt on every generation, so their bytes differ while their content stays the same. Corpus v4 (`--v4`, generator 0.5.0) is the v3 plan followed by the construct carriers c6–c9; v3 regenerates with the same seed and no flag.
- **Runs.** `lossrun eval run` writes every document's result, graph and learned spec. `eval score` is a pure function of corpus + results.
- **Regression.** Re-scoring an old run with a new scorer version is a regression check. Re-running is a new run.
- **All-fields study (brief 31).** Every corpus result folder is scored with `lossrun eval score --all-fields`, which also writes `studies/all-fields.md`.
  - The study counts every field the scorer compares on delivered documents (every field the layout prints, per the truth's `printed` list), not only the deck table's key fields.
  - It lists every wrong delivered value with its document, row, expected and delivered value.
  - **On a synthetic corpus it must be 0 wrong.** Any row is a defect in the extraction, the scorer or the truth, and is fixed or explained in the run's README before the deck table is used.
  - It is the comparison that found brief 30's 6,036 generator rows (`codeSystem: "WCIO"`), which the key-field deck table could not see.
- **Glossary ablation (brief 21, ADR 0019).** `LOSSRUN_GLOSSARY_ABLATE=<file>` removes hand aliases (one `field|header` per line) from the seed glossary for an experiment: are they re-learned through the loop? `run.json` records the list as `glossaryAblation`, and the learned aliases at the end of the run as `learnedAliasesAtEnd`. It is never set in a deployment, and a run with it set is not a deck-table run.
- **"The deck table reproduces"** (brief 19 Answers 4) means:
  - every deck-table row with n ≥ 5 documents reproduces within its 95 % confidence interval: delivered key-field accuracy, review rate and pre-fill accuracy each fall inside the committed row's interval, and the commitment verdict is unchanged;
  - no delivered value is wrong that was right before, and the source-inconsistent guardrail and the classification precision / recall do not drop;
  - learn calls per document are reported, not compared, because model calls vary from run to run;
  - **the first-seen stratum is reported from the five-trial study** (`eval study first-seen --trials 5`), never from the single first-seen document of a run. That n = 1 cell is shown as an insufficient sample and is not part of the check.
  - **any single-document first-seen cell is reported the same way** (brief 20 Answers 2). Use `eval study first-seen --doc <id> --trials 5 --concurrency 1`: five fresh learns of that document from an empty registry. Report it as a deck-table row, with the study's "why reviewed" column naming what blocked each reviewed trial's best attempt (hard rule ids, missing fields, the first issue). A trial that failed for an infrastructure reason, such as a 429 throttle, is listed and not counted. Learn variance is reported as a finding with its failure modes, never as a single run's pass or fail.
