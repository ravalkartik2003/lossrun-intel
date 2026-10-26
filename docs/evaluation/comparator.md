# Typed comparator (evaluation)

How the evaluation harness decides whether an extracted value equals the gold value. Implemented as `LossRun.Contracts.CanonicalComparer`, with one test case per rule in `src/LossRun.Contracts.Tests/CanonicalComparerTests.cs`.

The basis is research A2 (`evaluation-and-wc-domain.md`). RealDocBench compares numbers after normalization ("$1,234" equals 1234), types dates as YYYY-MM-DD, uses exact normalized equality for short strings, and uses fuzzy matching (≥ 92) for long strings. ParseBench recommends scoring "semantic correctness rather than text similarity".

## General rules
- **Normalize both sides, then compare.** The comparator never reads the source document. It only normalizes the two printed or canonical strings it is given.
- **An absent value is not zero.** If both sides are blank or null, it's a match (`both-absent`). If only one side is absent, it's a mismatch (`missing-expected` / `missing-actual`). This follows lossrx: "NA ≠ 0".
- **Unparseable is a mismatch.** If either side can't be parsed for its field kind, the result is `unparseable` and does not match.
- Each result carries the normalized values and a reason code, so per-field error analysis needs no re-parsing.

## Field kinds

| Kind | Normalization | Equality |
|---|---|---|
| `Date` | ISO `yyyy-MM-dd`. Accepted inputs: ISO; US `M/d/yyyy`, `M/d/yy`, `M-d-yyyy`; `yyyyMMdd`; `yyyy/MM/dd`; `d-MMM-yyyy`, `d-MMM-yy`; `MMM d, yyyy`, `MMMM d, yyyy`, `d MMM yyyy`; Excel serials 20000–80000 (1900 date system); date-times (time dropped). Two-digit years: 00–49 → 20xx, 50–99 → 19xx. | Same calendar date |
| `Currency` | Strips `$`, `USD`, commas and spaces. Negative forms: `(1,234.00)`, trailing minus `1,234.00-`, `CR` suffix, leading minus. A lone dash (`-`, `–`, `—`) is 0. Blank is null. | Within $0.005 (exact to the cent) |
| `Integer` | Parsed like currency; must be a whole number | Exact |
| `ClaimNumber` | Trim, upper-case, remove whitespace and `- _ . / \ #`. **Leading zeros are kept** (00123 ≠ 123). | Exact |
| `Code` | Trim, upper-case, drop a trailing `.0` (spreadsheet numeric cells). A numeric code is left-zero padded to its code system's `codeWidth` from `code-sets.json`: WCIO 2, stat-plan status 1, other stat-plan codes 2, WCIRB class 4. | Exact, within the same code system |
| `CodedValue` (nature/cause/part of body) | If both sides have a code, the code systems must agree (a null system doesn't block) and the padded codes are compared. Otherwise it falls back to `FreeText` on the printed text. | As described |
| `Enum` | Mapped through the glossary's `valueAliases` for the canonical field (e.g. `Re-opened` → `reopened`, `WCMO` → `medicalOnly`). Matching ignores case and punctuation; unmapped values fall back to normalized text. | Exact canonical value |
| `Boolean` | `Y/Yes/True/T/X/1` → true; `N/No/False/F/0` → false | Exact |
| `ShortText` (names, policy numbers, carrier) | Lower-case, punctuation folded to spaces, whitespace collapsed | Exact after normalization |
| `FreeText` (descriptions, coded-value text) | Same as `ShortText` | Normalized Levenshtein similarity `1 − LD / max(|a|,|b|)` ≥ **0.92** (`ComparerOptions.FreeTextThreshold`) |

## What is *not* in the comparator
- **Period alignment (brief 18):** periods are aligned by policy number; a period without one (a summary-only run's fiscal or report year, ADR 0017) aligns by its printed `periodLabel`, then by effective date. `noLossesStatement` (ADR 0016) compares as a boolean and `subtotals.recoveries.*` as currency.
- **Format-specific normalizers belong to the carrier format** (`CarrierFormat.columnMap[].normalizer`, the preprocessing work and the format-registry work), not the comparator. Examples: splitting TJC's `ClaimNo/Sts` `-C`/`-O` suffix, splitting a "Policy Period" range into effective/expiration dates, routing parenthesized recoveries into `recoverySubrogation`.
- **WCIO mapping of free text** (e.g. "pulled back" → Nature 52) is scored separately from extraction (research B2 Inferences). The "truth" for free text is a judgment call, so mapping accuracy gets its own metric with an NOC/unmapped bucket.
- **Table/record alignment** (pairing gold rows with extracted rows) is part of the evaluation harness (`docs/evaluation/scoring-protocol.md`). It keys on normalized claim number, falling back to DOI plus claimant, in the manner of ParseBench's TableRecordMatch.

## Changing the comparator
Comparator behaviour is part of the evaluation contract. A change can move every score, so:
- bump `catalogVersion` in `rule-catalog.json` (the evaluation contract version);
- add a test case to `CanonicalComparerTests`;
- note the change in the brief's report.
