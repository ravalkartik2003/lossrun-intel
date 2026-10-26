# /seal-handoff — hand the seal to the evaluator (never run it)

The seal splits the pool into a tuning set (released to every entrant) and a blind set (kept by the evaluator). **The assistant never runs the seal** and never sees the seed before it is published (`docs/operations/blind-run.md` §3–4, `docs/operations/playbook.md`).

## Rules (always)
- **Do not run `eval seal`.** Write the instructions; the evaluator runs the command with their seed.
- **Never open a `blind/` folder** or any file the seal lists under `blind[]` — not for a quick check, not "only its headers". If one is touched by mistake, stop and tell the operator: the evaluator re-seals with a new published seed (`tuning-loop.md` §1).
- `blind/` is in `.gitignore` and `.cursorignore`; keep it there. The blind run's results, alignment and adjudication go under `$WORK/blind/` (`blind/results/build`, `blind/align/build`, `blind/report/build`; `blind-run.md` *Folder layout*). Those two files cover only the repository workspace, not a `$WORK` outside it: the real guard is the CLI (`eval run --only-blind` exits 2 when its `--out` has no `blind` path segment; `tune` refuses any path with one).
- **Never ask for, set or echo the seal seed.** The evaluator keeps it in a sealed envelope and sets it in their own shell at the seal.
- No archive content in chat or commits (`tuning-loop.md` §7).

## Do
```bash run
mkdir -p "$WORK/handoff"
grep -qx 'blind/' .gitignore && grep -qx 'blind/' .cursorignore || { echo "blind/ must be in .gitignore and .cursorignore"; exit 1; }
cat > "$WORK/handoff/evaluator-instructions.md" <<EOF
# Seal — for the evaluator (run it yourself; the build side never sees the seed first)

1. Take the seed (any integer) from your sealed envelope and publish it to every entrant before sealing. Do not set it
   in the operator's shared shell or any shell an assistant can read.
2. In your own bash shell at the repository root, with LR=tools/lossrun/lossrun and WORK=$WORK:

       "\$LR" eval seal --corpus "$WORK/corpus" --out "$WORK/seal" --tuning 100 --seed <SEED> --policies "$WORK/policies.csv"

   (--tuning: the size of the tuning set; 100 for a 400–600-file pool, blind-run.md §1. --policies: the data team's
   document → policy map (file,policyNumber), so no policy spans tuning and blind; without it seal warns.)
3. Send the printed "blind manifest sha256" to every entrant the same day. Keep seal-manifest.json.
4. Release the tuning files listed under tuning[] (paths relative to the archive) with docs/evaluation/canonical-schema.json,
   code-sets.json, rule-catalog.json and \$WORK/seal/scoring-protocol.md. Never release a blind file.
5. The blind run's results, alignment, adjudication and score go under $WORK/blind/ (results/build, align/build,
   report/build; a vendor's under results/<vendor>, align/<vendor>, report/<vendor>). Verify later runs against the
   published hash: eval verify ... --published-hash <sha256>. The assistant never opens $WORK/blind/ (only /score
   prints the deck table it writes there).
EOF
echo "written: $WORK/handoff/evaluator-instructions.md (the seal is the evaluator's to run)"
```

## Then tell the operator, and stop
Send `evaluator-instructions.md` to the evaluator. When `$WORK/seal/seal-manifest.json` exists and `$WORK/truth.csv` has arrived, continue with `/tune`.
