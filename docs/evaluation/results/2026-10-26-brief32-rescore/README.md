# Brief 32 item 2 — corpus v5 offline re-score (three replay drops routed)

**No Azure, no model.** The stored graphs and specs of the attached corpus v5 run (`eval-runs/run-v5-w31`, `docs/evaluation/results/2026-10-26-corpus-v5-wake/`) were replayed with the brief 30 re-score tool (`docs/evaluation/results/2026-10-25-brief30-rescore/tool`), built twice:
- **before:** against `46dd547`, the commit before brief 32;
- **after:** against the brief 32 tree.

The delta is item 2 alone, on identical graphs and specs. Items 1 and 3–7 change no replay.

| | before | after |
|---|---:|---:|
| Delivered (of 253 delivered in the run) | 253 | **253** |
| Re-gated to Review | 0 | **0** |
| All-fields study: delivered values wrong (caught / silent) | 0 / 0 | **0 / 0** (of 179,409) |
| Deck table | — | identical to before (`deck-table-before.md`, `deck-table-after.md`) |

**What changed in the code.** Four replay sites that left a printed value absent wrote an issue only, and `Decide` reads `Unnormalized`. They now route through it, with the cell:
- a period date that does not parse (`SetPeriodDate`);
- a value dropped on the type check (`Drop`);
- a merged cell not split;
- a period-lookup table not found.

**Why nothing moved.** No delivered corpus v5 document hits any of the four sites. The fixtures in `src/LossRun.Formats.Tests/Brief32Tests.cs` show each one routing to Review.

**"Values changed on 7 still delivered"** (the tool's own line) is the same before and after. It counts the seven scanned insured names that now carry `lineage.repair` (brief 31 Answers 1). It is a lineage-only change: the values are identical, and the all-fields study is 0 wrong.

```bash
dotnet build <tool copy> -p:LossRunSrc=<tree>/src
dotnet <tool copy>/bin/Debug/net10.0/Rescore.dll eval-runs/corpus-v5-w31 eval-runs/run-v5-w31 eval-runs/rescore32-<tree> src/TestShared/formats
tools/lossrun/lossrun eval score --corpus eval-runs/corpus-v5-w31 --results eval-runs/rescore32-<tree> --out eval-runs/score-rescore32-<tree> --all-fields
```
