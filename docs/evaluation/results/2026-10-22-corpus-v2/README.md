# Corpus v2 results (2026-10-22)

Corpus `corpus-gen-0.2.0`, seed 8, 302 synthetic documents. Regenerate it with `lossrun eval generate --out <dir> --seed 8`.

## Folder layout

| Path | What it is |
|---|---|
| `deck-table.md` | **The deck table.** One row per input type; delivered accuracy, review rate and pre-fill accuracy, each with a CI. This is the headline (`scoring-protocol.md` §3, "By input type, never blended"). |
| `report.md`, `report.json`, `documents.csv`, `field-by-carrier-input.csv`, `run.json` | Current results: the registry-reset work. |
| `08c-run/` | The replay fixes' results, merged from slice runs. |
| `08b-run/` | The corpus v2 run's corpus-v2 run (git `bd0463e`). Its `deck-table.md` was scored afterwards with the replay fixes scorer, for before/after. |
| `baseline-run3/` | The evaluation harness' run 3 on corpus v1, rescored with the corpus v2 run definitions. It is the "before" of the corpus v2 run. |
| `studies/` | The corpus v2 run studies on v2: cost by path, scan robustness, learn reliability in four passes. The registry-reset work: `learn-cap-burst.md`, the live test of the learn concurrency cap. |

## How the registry-reset work results were produced

1. One full run, `lossrun eval run` on all 302 documents (git `b066eaa`, uncommitted the registry-reset work changes).
2. The full run showed the replay fixes missing-totals guard sending c4 workbooks to Review. The guard was corrected, and only the c4 slice was re-run (`--only c4`).
3. The c4 slice replaced the c4 documents of the full run.

No other carrier's routing can change under the corrected guard: the new test is strictly narrower for PDFs, and the c5 spec was not admitted by a totals gate. The merged `run.json` takes the c4 registry entry from the re-run.

## How the replay fixes results were produced (now in `08c-run/`)

They were assembled from three slice runs, each with `lossrun eval run --only <carriers>`, all from the same working tree (based on `c640f73`, uncommitted changes):

| Carriers | What the run covered |
|---|---|
| c1 | Hand spec `synthetic-mutual-pdf@3` |
| c2 | Learned in-run |
| c3, c4, c5 | Narrative, XLSX, CSV |

Each slice starts from the hand specs alone. The c2 format is learned inside its slice, exactly as in a full run. The slices were merged and then scored with `lossrun eval score`. The merged `run.json` sums the document counts and keeps the union of the end-of-run registries.

Per-field error lists (`field-errors.csv`) and the Foundry dataset are not committed because of their size. Regenerate them from a run with `lossrun eval score`.
