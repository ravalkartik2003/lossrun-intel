# Blind-run checklist (2026-10-25)
- [x] seal verified: every blind file matches its hash
- [x] learned-alias snapshot: blind-prep/glossary.learned.json (from eval-runs/playbook-proof/tune/batch-02)
- [ ] learned formats reset (record the audit id / ledger tx in the bake-off log)
- [ ] deploy check: infra/scripts/deploy-functions.ps1 ran its database check and smoke upload on the release being used
- [ ] tuning verdict recorded (last batch-report.md) and every tier-2 pull request merged or closed
- [ ] the operator, not the assistant, runs: "$LR" eval run --corpus "$WORK/corpus" --out "$WORK/blind/results/build" --mode api --concurrency 8 --only-blind "$WORK/seal/seal-manifest.json"
