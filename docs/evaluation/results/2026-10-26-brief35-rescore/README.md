# Brief 35 §6: offline re-score with the carrier-name checks

> Synthetic corpus v5 (generated documents, fictional carriers). No client documents.

**No Azure, no model.** The tool replays the stored graphs and published specs of two corpus v5 runs on the brief 35 tree and re-gates every delivered document:
- **Replay hits** go through the router, which now has the brief 35 §3 carrier-on-the-page check.
- **Learned and delta documents** go through the learn gate's no-issues rule. That now includes `SpecLint.Check` as `SpecVerifier` runs it, with the brief 35 §1–2 label checks and the §3 learn-time carrier check.

The tool (`tool/`) is the brief 30 re-score tool plus that lint call.

| Run | Delivered in the run | Re-gated to Review | All-fields wrong, before → after |
|---|---:|---:|---|
| Brief 34 wake (`2026-10-26-brief34-wake/corpus-v5`, artefact `e0d9753d8f5b` + exemplar store) | 251 | **6**, all c8 | **6 → 0** (of 179,409) |
| Attached run (`2026-10-26-corpus-v5-wake`) | 253 | **0** | 0 → 0 |

- **Brief 34 run** (`brief34-run/rescore.tsv`):
  - **c8-000 (learned):** the learn gate refuses its spec with 3 issues:
    1. `document.carrierName` and `document.insuredName` both read the label "Insured";
    2. the glossary maps "Insured" to `document.insuredName`;
    3. the carrier read from the page is not the spec's carrier, "Illustrative Comp Assurance".
  - **c8-001 to c8-005 (replayed):** each goes to Review with "carrier name on the page differs from the format's". The insured name read as the carrier never reaches delivery.
  - Every other delivered document is unchanged. The deck table differs only in the c8 row: 6 delivered → 6 reviewed.
- **Attached run:** nothing moves. Its deck table is identical apart from the stamp line.
  - "Values changed on 7 still delivered" is the same line the brief 32 re-score reported. It is lineage only: seven scanned insured names carry `lineage.repair` (brief 31 Answers 1).
- **In a live run** the learn loop would get the three lint issues back as repair feedback, and the next attempt would read the carrier from its own place. A re-score cannot show that, because it makes no model call. Brief 35 Part B's corpus run does.

```bash
dotnet build docs/evaluation/results/2026-10-26-brief35-rescore/tool/Rescore.csproj -o eval-runs/rescore35-tool
dotnet eval-runs/rescore35-tool/Rescore.dll eval-runs/corpus-v5-<run> eval-runs/run-v5-<run> eval-runs/rescore35-<run> src/TestShared/formats
tools/lossrun/lossrun eval score --corpus eval-runs/corpus-v5-<run> --results eval-runs/rescore35-<run> --out eval-runs/score-rescore35-<run> --all-fields
```

## Second pass: carrier-name normalization and `carrierNames` (brief 35 Answers 2)

Re-run on the tree with the carrier comparison relaxed:
- names compare without corporate words (`CanonicalComparer.NormalizeCarrierName`, since moved to `CarrierNames.Normalize`);
- a format knows its learned name plus the names reviewers added (`carrierNames`).

The result is identical to the first pass, as Answers 2 requires:
- **Brief 34 run:** 6 re-gated (c8-000 at the learn gate, c8-001..005 on the carrier check), all-fields 0 wrong.
- **Attached run:** 0 re-gated, 0 wrong. Both deck tables are identical to the first pass.

The insured's name shares no core with "Illustrative Comp Assurance" ("illustrative comp"), so the relaxed comparison still refuses it.
