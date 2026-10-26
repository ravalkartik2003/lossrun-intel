# Corpus v4 results (2026-10-24, brief 19 follow-up)

Corpus `corpus-gen-0.5.0`, seed 8, 326 synthetic documents: the 302 corpus v3 documents, then 24 construct-carrier documents (c6–c9, 6 each; ADR 0018). Regenerate it with `lossrun eval generate --out <dir> --seed 8 --v4`. Corpus v3 stays reproducible with the same seed and no flag.

**Reproducibility.** On regeneration, 292 of the 302 v3 files are byte-identical. The other 10 are the password-locked PDFs: their encryption salt is random on every generation (also true for a plain v3 regeneration), and their content is unchanged. Truth files differ only in serialization (schema 0.7.0 and its new default fields); the values are the same.

## Files

| Path | What it is |
|---|---|
| `deck-table.md` | The one deck table (brief 19 Answers 5), with the construct carriers as their own rows. |
| `v3-rerun-deck-table.md` | Corpus v3 re-run after the brief 19 fixes (run first, Answers condition). |
| `report.md`, `report.json`, `documents.csv`, `field-by-carrier-input.csv`, `run.json` | Scorer output (`lossrun eval score`) for v4. |

## Corpus v3 re-run: the deck table reproduces

The definition is in `docs/evaluation/scoring-protocol.md` §8: rows with n ≥ 5 reproduce within CI, and the first-seen stratum comes from the five-trial study. Against the committed table (`../2026-10-23-corpus-v3/deck-table.md`):

- every n ≥ 5 row is identical except one: fax pre-fill is 89.0 % [86.1–91.4], against 89.1 % [86.2–91.4], which is within CI;
- learn calls per document moved on the fax row (0.021 → 0.000);
- the n = 1 first-seen c2 document is delivered (3 → 4 learn calls), so the brief 19 defect 6 is fixed;
- guardrail 9 / 9; classification 100 / 100.

Spend: $0.45 of model calls plus about $0.05 of Content Understanding.

## Corpus v4

**The v3 rows match the committed table.** The exception is the n = 1 first-seen cell, which is not delivered this time (5 learn calls). That cell is excluded by the §8 definition, and it is why the definition exists. Scan ≥ 300 dpi reviews 4.2 %, rotated pre-fill is 99.8 %, fax pre-fill 89.1 %, and XLSX/CSV reviews 1.3 % (the planted inconsistent workbook).

| Construct row | Docs | Delivered key-field accuracy | Review rate | Learn calls / doc |
|---|---:|---|---|---:|
| two-line records (c6) | 6 | 100.0 % (n = 1,278, 6 docs) | 0.0 % | 0.333 |
| one table per claim (c7) | 6 | 100.0 % (n = 516, 3 docs) | 50.0 % | 3.167 |
| stacked cells (c8) | 6 | 100.0 % (n = 312, 1 doc) | 83.3 % | 5.000 |
| component-sum headers (c9) | 6 | 100.0 % (n = 1,098, 6 docs) | 0.0 % | 0.333 |

- **No construct document was delivered with a wrong value.** The 16 delivered documents have no wrong or missing key field. On each delivered c6 document and on c8-005, the 4 missing fields are printed claim counts on subtotal and grand-total rows; these are not key fields.
- **The reviewed c7 and c8 documents carry no claim pre-fill.** No spec verified, so the reviewer starts from the document. Their 100 % pre-fill figure covers document-level fields only, so read it as "nothing wrong pre-filled", not as coverage.
- **c7 and c8 review because the learn did not verify a spec within 5 attempts**, not because of wrong values. Each run learns per document until one spec publishes, then replays it:
  - c7: two attempts ended with 2 hard failures and 2–3 required fields missing; one tied every total but carried 10 lint issues. Then c7-002 published, and c7-004/005 replayed.
  - c8: five attempts tied every printed total at 91–100 % required coverage but still carried 1–4 lint issues (stacked parts), so the gate held them. The last document published.
- The constructs are therefore expressible and replay exactly (c6, c9, and every replay hit). Learning c7 and c8 first time is the remaining weakness, which these rows report as "measured, not committed".
- Guardrail 9 / 9; classification 100 / 100 (n = 326).

Spend: $1.456 of model calls plus Content Understanding for the 28 cache misses (the construct documents and the locked PDFs).
