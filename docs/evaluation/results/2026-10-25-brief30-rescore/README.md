# Brief 30 — corpus v5 offline re-score (items 4, 5, 14, 20)

**No Azure, no model.** Corpus v5 has no Content Understanding cache (only corpus v3 and the constructs are cached), so `eval run --mode stub` cannot re-read it. The last corpus v5 run (`eval-runs/run-v5-23`, brief 23) stored every document graph and every published spec. This re-score uses those, not a new extraction.

**What it does** (`tool/Program.cs`):
- every document that run delivered (ReplayAccepted, Learned, Delta) is replayed again with the spec it used;
- replay hits are re-gated through `FormatRouter.Decide`;
- learned and delta documents go through the learn gate's rule: any issue or hard failure sends them to Review.
- `lossrun eval score` then scores both result folders.

**Before and after.** The tool was built twice:
- **before:** against the tree at `1ba6c7b`, the commit before brief 30's code;
- **after:** against the brief 30 tree.

So the delta is brief 30's code alone, on identical graphs and specs.

```bash
dotnet build docs/evaluation/results/2026-10-25-brief30-rescore/tool -p:LossRunSrc=<tree>/src
dotnet <tool>/bin/Debug/net10.0/Rescore.dll eval-runs/corpus-v5 eval-runs/run-v5-23 eval-runs/rescore30-<tree> <tree>/src/TestShared/formats
tools/lossrun/lossrun eval score --corpus eval-runs/corpus-v5 --results eval-runs/rescore30-<tree> --out eval-runs/score-rescore30-<tree>
```

## Result

| | before | after |
|---|---:|---:|
| Delivered documents (of 253 delivered in run-v5-23) | 253 | **252** |
| Re-gated to Review | 0 | **1** (`regated-after.tsv`) |
| Delivered values wrong and caught by a hard rule | 0 | **0** |
| Delivered values wrong but plausible (silent) | 6,036 | **6,036** (identical rows; see below) |
| Delivered key-field accuracy, every input type | unchanged | unchanged |

**The one document** is `c2-008-scan-200dpi-rotated-noisy`. OCR printed a closed claim's indemnity reserve as `0,00`.
- **Before:** the comma was stripped and the value read as `000` = 0, right by coincidence.
- **After:** item 5 refuses a decimal comma, item 4 routes the unnormalized value, and the document goes to Review. The reason names the field and page.

**Deck table delta** (`deck-table.diff`). Only the scan 200 dpi rotated row moves:
- delivered docs 35 → 34;
- review rate 27.1 % → 29.2 %;
- pre-fill 99.8 % (now n = 14 docs);
- delivered accuracy 100.0 %, unchanged.

**Items 14 and 20 change nothing on corpus v5.**
- No delivered document had a descriptive value in a code column.
- No delivered document had a blank policy cell under a `column` period source.

**Not a regression.** The 6,036 silent rows below never count as delivered-wrong in a deck table: the deck's delivered accuracy compares key fields (scoring-protocol §8), and these are non-key coded fields wrong in the truth itself. Brief 31 fixed the generator; the section at the end shows the number at 0.

**The 6,036 silent rows** are the generator's coded values. `TruthFactory` writes `codeSystem: "WCIO"`, which is not a code-sets.json id (the schema says `WCIO-Nature` / `WCIO-Cause` / `WCIO-PartOfBody`), and the extraction never carried a code system. They are the same 6,036 rows before and after, and not key fields. Item 14 now writes the schema's ids, which is why the rows read `WCIO` vs `WCIO-Nature`. Aligning the generator is a question in the brief 30 report.

**Not covered:**
- **Documents already in Review in run-v5-23:** they are not re-routed, so an improvement from item 5's accounting zero on a reviewed document is not counted.
- **Item 11:** fingerprinting is not re-run; every document keeps its matched spec.
- **The real check:** a full corpus v5 run (`eval run --mode api`) on the next wake, which is also the prompt-gate run.

## Brief 31: regenerated truth and the all-fields study

**The generator fix.** `TruthFactory` writes each coded value with its code-sets table (`WCIO-Nature`, `WCIO-Cause`, `WCIO-PartOfBody`). Corpus v5 truth was regenerated as `corpus-gen-0.9.0`.
- Seed 8, same plan; every rendered file is unchanged except the 10 password-locked PDFs, whose encryption is not byte-stable.
- The re-score reads the stored graphs, not the files.
- The corpus previously on disk was **0.7.0**, so the regenerated truth also carries **0.8.0**'s change: rows of another line of business are on the period's `keptRows`, not its claims.

**The re-score, re-run on the brief 31 tree** (`brief31/`), scored with `lossrun eval score --all-fields`:

| | brief 30 tree, 0.7.0 truth | brief 31 tree, 0.9.0 truth |
|---|---:|---:|
| Delivered documents (of 253) | 252 | 252 |
| All-fields study: delivered values wrong (caught / silent) | 0 / 6,036 | **0 / 0** |
| XLSX / CSV delivered key-field accuracy | 98.6 % (n = 14,982) | 100.0 % (n = 14,778) |
| Every other deck row | — | unchanged |

**What moved, and why:**
- **The 6,036 → 0** is the generator fix (6,025 coded values) plus three fixes the all-fields study found.
  - The scorer compared `recoverySubrogation` as text (`747.90` ≠ `747.9`); it is money now, and `netIncurred` and the flags compare by value.
  - Replay delivered 8 scanned insured names wrong. Seven had OCR reading the leading capital `I` as `l` ("lota Staffing"): a Title-Case name on a scan now reads it as `I`, first word only, and lineage keeps the printed text. One rotated scan carried the next line's ": 03/04/2024": a header text value now stops at a colon.
- **The XLSX/CSV row** is the kept-rows truth (0.8.0). The 0.7.0 truth listed c5's other-line rows as claims, so replay, which keeps them aside correctly, was scored as missing 714 values. Scored on the old truth, the brief 31 tree gives the brief 30 deck table exactly: the code changes alone move nothing in it.
- **`brief31/studies/all-fields-before.md`** is the brief 30 baseline tree on the new truth (6,033 wrong), for comparison.
