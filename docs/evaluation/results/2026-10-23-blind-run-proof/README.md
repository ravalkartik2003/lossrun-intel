# Blind-run procedure, proven end to end on the fixture (`docs/operations/blind-run.md`)

`docs/operations/blind-run.md`, run as written, on the 10-file import fixture `src/TestShared/fixtures/import/`. The fixture holds synthetic files only, in carrier folders: 3 native PDFs (one bundle, one password-protected), a fax, a 200 dpi scan, an XLSX, a CSV, a ZIP of two workbooks, a 150 dpi PNG and a `.txt` to be skipped. Only the sizes are changed from the procedure: `--tuning 3`, and `run --mode local --limit 3`.

Reproduce from the repository root (needs `eval-runs/corpus-v3`, from `lossrun eval generate --out eval-runs/corpus-v3 --seed 8`, and `az login`):

```bash
bash docs/evaluation/results/2026-10-23-blind-run-proof/prove.sh
```

**Brief 30 item 26.** `prove.sh` and `adjudicate.py` now use the one blind layout (`$WORK/blind/results|align|report/build`). The CLI requires it: `eval run --only-blind` exits 2 without a `blind` path segment. `eval truth` now passes `--accept-defaults`, because the proof leaves every decision blank on purpose. `prove.log` is from the original run under the old paths; a re-run is pending once the brief-30 CLI changes are merged.

| Step | Result (`prove.log`) |
|---|---|
| import | 10 files, 15 pages; `readme.txt` skipped with its reason; carriers from folders and `import-carriers.csv`; strata `bundle` (ZIP) and `password-protected` |
| truth request (`truth-request.csv`) | 12 policies with their periods, read from native-PDF text, the workbook (inside the ZIP too) and the CSV; plus 2 carrier rows for the 4 files that need OCR first (fax, scan, PNG, protected PDF) |
| seal (`--tuning 3 --seed 14`) | 2 tuning, 8 blind; blind manifest sha256 `6ab82cfe…cf4c` |
| verify | 8 blind files match |
| run (local, blind only, limit 3) | c2 native PDF learned (2–3 calls), c2 scan replayed (0 extraction calls), ZIP: workbook 1 learned and workbook 2 replayed. About $0.09 |
| align | 181 matched · 174 archive-only (documents not run) · 0 extracted-only · 0 disagreements |
| align → `adjudication.xlsx` | 181 matched (all agree, so none listed); 174 rows "in the archive, not extracted": claims of the documents `--limit 3` did not run |
| adjudicate (`adjudicate.py` fills the workbook per section 8) + truth | No run document had a missed claim, so the 174 rows keep their blank default `exclude`. Truth for 3 documents, 181 claims, all with comparable amounts |
| score | 1,459 field values. Key fields 100.0 % on all three documents (native PDF n=294, 200 dpi scan n=186, ZIP n=606) (`report.md`, `documents.csv`) |
| seal-break check | `verify` refuses a moved blind set (exit 1) |

**Offline re-run (brief 30, 2026-10-25): `prove-stub.log`.** No Azure: `MODE=stub CU_CACHE=<corpus-v3-cu-cache> CORPUS_V3=<corpus-v3> bash …/prove.sh` (both may point at another checkout's `eval-runs/`). The script now follows the one layout (`$WORK/blind/results|align|report/build`) and proves the brief-30 guards on the fixture:
- `verify --published-hash` recomputes the blind list hash and compares it with the manifest field and the published value;
- `eval run --only-blind` refuses an `--out` without a `blind` segment (exit 2);
- `eval truth` refuses the 275 archive-only rows left at their default (exit 3) until `--accept-defaults`;
- an edited blind list whose recorded hash was rewritten to match passes the manifest check and is caught by `--published-hash` (exit 1).

In stub mode the learns never verify, so the three run documents end in review: the accuracy rows above stay those of the local run (`prove.log`).

**Offline re-run (brief 35 Answers 1, 2026-10-26): `prove-stub-b35.log`.** This is the same stub run after the alignment key began comparing the carrier with `CarrierNames.Normalize` (corporate words dropped, the function the replay carrier check uses). Its run folder was `eval-runs/blind-proof-b35`.
- Every step matches `prove-stub.log` apart from paths and timing: `aligned 0 · archive-only 355`, the adjudication defaults, the truth (2 documents, 80 claims) and the score (647 field values), plus the four seal guards.
- One line is new: seal's "no --policies map" warning (brief 31 item 3).
- The fixture's archive names each carrier as the page prints it, so the key change cannot move this proof. The new matching is tested in `Brief35AlignmentTests`, on the names of the brief 35 five-upload test.

`make_truth.py` stands in for the data team's export: it writes the truth CSV from the corpus-v3 truth of the documents the fixture was copied from. `adjudicate.py` plays the evaluator in `adjudication.xlsx`: the generator knows which document printed each claim. It picks the Document for any claim a run document printed, and leaves decisions blank so the stated defaults apply. When openpyxl saves the workbook it drops the drop-down validations (a library limitation); Excel keeps them. `ImportTests` covers each decision on the workbook directly.

**Found on the way, and fixed**
- `score` crashed on any partial run: latency rows for input types with no document computed a percentile of an empty list (NaN). Empty rows are now left out.
- A ZIP with two loss runs was scored on its first document only, and its claims were paired by number alone. The two synthetic workbooks reuse claim numbers. The package is now scored as one document, and claims pair on number plus date of injury first.
