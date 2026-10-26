# ADR 0004 — Learn-path model is gpt-5.4-mini until gpt-5.2 deployment is unblocked

**Status:** accepted 2026-10-21

**Context.** Data Zone quota for gpt-5.2 exists (1.1M TPM) but deployment is blocked by an Azure account risk check, which only a support request from the subscription owner can clear (the provisioning apply log; ADR 0003 records the same subscription's SQL restriction). The brief's fallback rule covers this case.

**Decision.** `learn_model = gpt-5.4-mini` (Data Zone Standard, 100K TPM); Content Understanding default completion aliases point to `gpt-5.4-mini`. gpt-5.2 stays in the Terraform behind `deploy_gpt52 = false`. The learn path's accuracy guard is the verify-against-printed-totals loop and the validation rule catalog, not model size; replay has no model dependency.

**Consequences.** Re-check before week 4 of the plan: if the block is cleared, set `deploy_gpt52 = true`, re-point the CU alias and `learn_model`, re-run the golden set. Expect the mini model to need more learn-loop iterations on dense scanned formats; measure iterations-per-format and tokens-per-learn so the gpt-5.2 request carries real numbers. Model names remain configuration (CLAUDE.md).

## Amendment 1 — brief 35: decide the learn model by evidence on the next wake (2026-10-26)

**Context.**
- The organisation's sandbox example already runs gpt-5.2 as the learn model (`infra/environments/sandbox.tfvars.example`), so the numbers handed over should come from it, with gpt-5.4-mini kept as the comparison.
- Brief 34's wake read the eastus2 usage list and reported the gpt-5.2 quota as "granted". **That was a misreading.** The same 1,100 K TPM Data Zone quota was already free on 2026-10-21 (this ADR's context). What blocked gpt-5.2 was the account risk check, and a usage list says nothing about it. Whether the block is cleared is known only when a deployment is tried.

**Decision.**
- `config/models.json` lists each model's deployed version (`modelVersions`; gpt-5.2 is `2025-12-11`). A run that sets only `LOSSRUN_LEARN_MODEL` records the right version on its specs and usage.
- **The default does not change here.** `learnModel` stays gpt-5.4-mini. gpt-5.4-mini also remains the delta and classification model.
- Brief 35's wake tries `deploy_gpt52 = true` (capacity 300). If the deployment is refused, the block stands and nothing else changes.
- If it deploys, the wake runs corpus v5 on both learn models with the same prompt artefact. **gpt-5.2 becomes the learn model from that wake** only if both of these hold (the acceptance table):
  - it is at least as good on delivered-wrong (all-fields);
  - it is no worse than one band on review rate per input type.
- If it becomes the learn model, the switch is `config/models.json`, CLAUDE.md, Terraform `learn_model` and this amendment's outcome line. Otherwise the default stays and the comparison is recorded.

**Outcome (brief 35 wake, 2026-10-26 17:44 UTC).** **Refused.** `deploy_gpt52 = true` (capacity 300) failed with `400 Bad Request` and the risk check's message, "Our system has detected this request as unusual activity for your account. If you are confident this is in error, please contact support." This is the same risk check as on 2026-10-21.
- `deploy_gpt52` is back to false, and the CU defaults and app settings were restored by a second apply. The study was not run.
- **The default learn model stays gpt-5.4-mini.** It is the measured one: corpus v5 at artefact `f913ce4e28a5` (`docs/evaluation/results/2026-10-26-brief35-wake/`) is what the governance pack quotes.
- The organisation's sandbox is subject to its own subscription's risk check (`docs/solution-overview/next-steps-sandbox.md`). The study runs on the first wake after a gpt-5.2 deployment succeeds; the support ticket is the subscription owner's.
