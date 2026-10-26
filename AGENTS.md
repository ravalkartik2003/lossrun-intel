# AGENTS.md — conventions for any implementer (the full guide is CLAUDE.md)

1. Read `CLAUDE.md` first: what this repo is, which document to read for which task, and the working rules.
2. Hard constraints: Azure only; no labelled training data; learn a carrier's format once, then replay it deterministically with zero model calls (the model is the fallback); every extracted value carries cell-level lineage; every change is an append-only audit event.
3. Model names, prices and infrastructure are configuration (`config/`, Terraform in `infra/`), never literals in code. SQL changes are rerunnable changesets in `db/changelog/`, every table in `dbo`.
4. Never commit client documents, real loss runs, credentials or anything with PII. Synthetic samples only.
5. Running the blind evaluation on a real archive: `docs/operations/blind-run.md` is the whole procedure, in order, with copy-pasteable commands.
6. With an assistant doing the routine steps, use the playbook (`docs/operations/playbook.md`): the five command files in `.cursor/commands/` (`import`, `seal-handoff`, `tune`, `blind-prep`, `score`). In Cursor they are slash commands; in Copilot, Codex or another assistant, open the same file in `docs/operations/playbook/` (a generated copy) and follow it. Each file carries its rules: never run the seal, never open `blind/`, budgets, the gates on every change, a pull request for every design change, no personal data in reports.
