# The tuning loop: changing the system on the tuning set

This page is the procedure for improving extraction on the organisation's archive before the blind run. `lossrun tune` runs one batch of it (run, align, score, classify failures into causes a–g, rank the fixes, apply the stopping criteria in `config/tuning-config.json`), and `docs/operations/playbook.md` drives the batches with an assistant (`/tune`). A developer runs it, and an AI assistant may do the routine work. It covers what may change, how each change is checked, and when to stop. The blind run itself is `docs/operations/blind-run.md`, and the protocol behind both is `docs/evaluation/scoring-protocol.md`.

## 1. Tuning set only, never blind

- **The tuning set only.** Work only on the files the seal lists under `tuning[]` (`$WORK/seal/seal-manifest.json`; `eval run --only-tuning`).
- **No blind file is opened, run, aligned or inspected** before the blind run, by the developer or the assistant. Not for a quick check, and not "only its headers".
- **If a blind file is touched by mistake, stop.** Report it to the evaluator, who re-seals with a new published seed (`blind-run.md` §3).
- **Tuning numbers are never accuracy.** They show what changed, not how good the system is.
- **The residual without a policy map (brief 31).** `tune --seal` cuts the archive to the tuning documents' policies. If the seal was made without `--policies`, a policy can have documents on both sides; its blind claims then still appear as archive-only rows under `$WORK/tune/`. The developer treats those rows as blind: they are not investigated or fixed against. With the data team's map (`blind-run.md` §3), no policy spans the split and nothing leaks.

## 2. Two tiers of change

| Tier | Examples | Who may make it | What it needs |
|---|---|---|---|
| **Data and routine fixes** | glossary aliases and value aliases (`docs/evaluation/glossary.json`; long-lived learned aliases are proposed by `lossrun glossary export --promote`, which writes `glossary.promote.json` and a PR description and never edits the glossary; the glossary is rendered into the learn prompt, so the PR carries a corpus v5 run); accepting or rejecting learned aliases (ADR 0019); the ignored-headers list; a deterministic lint repair; a bug fix with a test that reproduces it | the assistant or the developer | tests that cover the change; the regression gate (§3) |
| **Design changes** | the canonical schema; rule semantics (rule catalog R-01..R-30); the spec language and its constructs (ADR 0018); classifier and gate thresholds; the learn gate's required fields; prices | the developer, with a human reviewer | an ADR (`docs/decisions/NNNN-title.md`) with context, decision and consequences, reviewed before merge; the regression gate |

**When in doubt, it is a design change.** A data change that alters what a rule means, such as an alias that maps a column to a different component, is a design change.

## 3. The regression gate, on every change

Every change, of either tier, passes all of these before it merges:
1. **The deck table reproduces** on the synthetic corpus, by the definition in `scoring-protocol.md` §8:
   - rows with n ≥ 5 stay within their 95 % confidence intervals;
   - no delivered value is wrong that was right before;
   - the source-inconsistent guardrail and the classification precision and recall do not drop.

   Run it with `lossrun eval generate --v5` (corpus v5: corpus v4 plus the measures-as-rows construct c10, brief 22), then `eval run`, then `eval score`.
2. **The carrier-generic guard** (`tools/generic-guard.sh`) passes: no carrier name or carrier condition in `src/`. Carrier knowledge lives in specs and the glossary (`docs/architecture/carrier-format-memory.md`).
3. **No archive or sample document is in the repository.** Use the organisation's check for documents in the repository.
4. **The .NET and web test suites** pass.
5. **A learn-prompt change carries its corpus run.** A change to any of these changes `docs/evaluation/learn-prompt-artefact.json` (`python tools/prompt-gate.py --write`; the files are listed in `config/learn-prompt-artefact.paths`):
   - the learn prompt;
   - the schema descriptions it carries (`carrier-format.schema.json`);
   - the model-facing schema;
   - the lint rules and repairs;
   - the glossary (`docs/evaluation/glossary.json`), which the learn prompt renders (brief 29 Answers 1).

   CI (`prompt-gate`) then fails the pull request unless it attaches a corpus v5 results folder (332 documents; corpus v4 until brief 22) whose `run.json` records the same `learnPromptArtefact` and was made from a clean commit of the branch. A run whose `gitSha` ends in `+dirty` was made from uncommitted changes, so it is refused. The same check runs on every push to `main`, against the commit before the push, so a change that skipped the pull request is caught too. CI also fails on a stale artefact, so the change cannot be missed. The rule was set after brief 20, when one sentence in a schema description made two XLSX learns fail and only the corpus run caught it.

## 4. Everything through a pull request

- **One change, one pull request.** The description says which tier the change is, what it fixes (rule ids, field names, counts) and what it cost.
- **The results folder is attached:** `docs/evaluation/results/<date>-<name>/`, with the regression `deck-table.md`, the tuning-run route counts before and after, and a README.
- **Design changes link their ADR.**
- **Nobody merges their own design change.** The assistant never merges.

## 5. Budget and iteration caps per run

- **Each tuning run has a model-spend cap,** agreed before it starts, and stops when the cap is reached. Content Understanding is cached between runs (`--cu-cache`), so a re-run pays only for model calls.
- **The learn loop keeps its configured caps:** iterations per document and the learn cap per run (`docs/operations/runbook.md`).
- **The assistant does not raise a cap.** The developer may raise it once per batch and records it in the pull request.
- **Spend is reported per run** from `report.md` (*Latency and cost*), never estimated.

## 6. The stopping rule

The loop exists to fix layout classes, not to chase single documents. The tuning set is worked in batches.
- **Record the grammar changes per batch.** These are spec-language constructs, new lint repairs and new glossary grammar (composites, component words). Aliases and bug fixes are not counted.
- **If the second batch produces grammar changes at the first batch's rate or higher, stop.** That means the layouts are not converging. Report it with the batches' counts and the layout classes involved.
- **Then fall back to the delta path for that carrier class:** learn per document, review, no new grammar. Leave the redesign to a design change after the blind run.

## 7. No personal data in reports or commits

- **Reports and pull requests name categories only:** files, carriers' folder labels, rule ids, field names and counts. They never contain a claimant name, a claim number, an amount or a quoted line from an archive document.
- **Nothing from the archive is committed:** no document, extraction dump, graph or learned spec built from it. A sample guard blocks the files it can recognise; the rest is the operator's responsibility.
- **Code refers to a real carrier by brief and category, never by name** (`tools/generic-guard.sh`).
