# Sealed-set protocol — text for evaluators

*To be handed to the State Fund evaluation panel and to every entrant (the internal build, Nomad Data, Bevaya, SortSpoke, Convr) before the blind run. The full technical version, with the comparator and the metric definitions, is `docs/evaluation/scoring-protocol.md`. The scorer is open: `lossrun eval score`.*

## 1. Who holds what

- **State Fund's actuarial and underwriting team holds the truth.** Truth is the keyed claims record of State Fund's archive. An amount counts as truth only where the archive's valuation date equals the loss run's. Every other disagreement is adjudicated, not scored.
- **Two sets are drawn from the archive before any entrant sees a document:**

| Set | Size | Who sees it | Use |
|---|---|---|---|
| Tuning | about 100 documents | Every entrant, in advance | Configure, learn formats, tune. Never reported as accuracy |
| **Blind** | 300–500 documents | Nobody until the run | The only source of reported numbers |

## 2. How the sets are drawn

- **Stratified.** Documents are allocated by carrier × input type × edge case: native PDF, XLSX/CSV, clean / rotated / fax scans, password-protected, bundled. The edge cases are:
  - missing year, partial run, closed-only, reopened;
  - catastrophe outlier, multiple policy numbers, non-CA, non-WC rows;
  - a source whose printed totals do not reconcile.

  Each stratum with 2 or more documents has at least one on each side.
- **Renderings stay together.** A blind scan is never a re-scan of a tuning document.
- **The seed is published before the draw.** The draw itself is `lossrun eval seal --tuning 100 --seed <published seed>`.

## 3. Sealing

- **What is published.** The seal records a SHA-256 for every blind file, and a SHA-256 over the sorted list (`blindManifestSha256`). That list hash is sent to every entrant the day the set is sealed.
- **Before scoring,** every blind file is re-hashed. Any mismatch voids the run.
- **The internal build gets no head start.** Its learned formats are reset the day before the blind run (`formats.reset`, recorded as `audit <id> / ledger tx <id>` in the bake-off log). It starts from the hand specs and the tuning set, like everyone else.

## 4. The run

- **Same files for everyone.** Each entrant runs the blind set **once**, on State Fund infrastructure or under observation. Nothing learned from a blind document may be reused for another entrant.
- **What each entrant returns, per file:**
  - the loss-run document(s) in the canonical JSON schema (`canonical-schema.json`), with page ranges;
  - per-field source: page and box, or sheet and cell;
  - per-field confidence, if the entrant has it;
  - wall-clock time and cost per document.
- **Failures score zero.** A file that errors, times out or returns nothing scores as all-missing. It is never excluded.

## 5. Scoring

- **One comparator for all.** The typed comparator handles dates, money to the cent, codes padded to their code set, status through the glossary, and claims matched by number.
- **Reported by input type and by template,** seen / first-seen / unseen-repeat. **Never one blended number.**
- **Every rate is shown as `value [95 % CI] (n, documents)`.** The intervals are Agresti-Coull with a document-level bootstrap design effect.
  - A threshold **passes** on the point estimate.
  - It **robustly passes** when the lower bound also meets it. Decisions should rest on the robust column.
- **Thresholds are fixed before the run** (scoring-protocol §6, to be confirmed by State Fund):

| Criterion | Native PDF / XLSX-CSV | Scans ≥ 300 dpi | Scans < 200 dpi |
|---|---|---|---|
| Claim # and DOI | ≥ 98 % | ≥ 95 % | ≥ 90 % |
| Paid / reserve splits | ≥ 97 % | ≥ 93 % | ≥ 93 % |
| Claim-level completeness | ≥ 90 % | ≥ 80 % | ≥ 80 % |

  - Classification: precision ≥ 97 %, recall ≥ 95 %.
  - DQ first pass: ≥ 85 % on source-consistent documents.

- **Also reported for every entrant:**
  - the review rate and the accuracy of what a reviewer starts from;
  - traceability: the share of values with an openable source;
  - latency p50 / p95;
  - cost per document.
- **Adjudication.** Disagreements are adjudicated blind to the entrant, then re-scored once.

## 6. Publication

- Per-entrant reports go side by side, each stamped with:
  - the scorer version;
  - the entrant's model and prompt versions, where disclosed;
  - the run date.
- The tuning-set numbers are never published as accuracy.
