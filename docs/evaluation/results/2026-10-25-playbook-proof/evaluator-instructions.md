# Seal — for the evaluator (run it yourself; the build side never sees the seed first)

1. Take the seed (any integer) from your sealed envelope and publish it to every entrant before sealing. Do not set it
   in the operator's shared shell or any shell an assistant can read.
2. In your own bash shell at the repository root, with LR=tools/lossrun/lossrun and WORK=eval-runs/playbook-proof:

       "$LR" eval seal --corpus "eval-runs/playbook-proof/corpus" --out "eval-runs/playbook-proof/seal" --tuning 100 --seed <SEED>

   (--tuning: the size of the tuning set; 100 for a 400–600-file pool, blind-run.md §1.)
3. Send the printed "blind manifest sha256" to every entrant the same day. Keep seal-manifest.json.
4. Release the tuning files listed under tuning[] (paths relative to the archive) with docs/evaluation/canonical-schema.json,
   code-sets.json, rule-catalog.json and $WORK/seal/scoring-protocol.md. Never release a blind file.
5. The blind run's results, alignment, adjudication and score go under eval-runs/playbook-proof/blind/ (results/build, align/build,
   report/build; a vendor's under results/<vendor>, align/<vendor>, report/<vendor>). Verify later runs against the
   published hash: eval verify ... --published-hash <sha256>. The assistant never opens eval-runs/playbook-proof/blind/ (only /score
   prints the deck table it writes there).
