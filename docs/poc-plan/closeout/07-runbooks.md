# 7. Runbooks

The operational runbook is `docs/operations/runbook.md`: format registry, model capacity, deploy, integration, load test, demo host, SQL region move, chat and workbench. This artefact adds the procedures an evaluator expects (bakeoff-strategy KQ8 item 7) and points to the steps already written there.

## 7.1 New-carrier onboarding

1. **Upload the carrier's loss run.** The router fingerprints it.
   - On a miss, the learn path runs: `gpt-5.4-mini`, 2–4 calls, about 50 s, about $0.05 measured on 1–2 page synthetic documents.
   - The learn is under the `LearnGate` concurrency cap.
2. **If the learn verifies,** the format is published in **shadow**. The document shows "Format learned · shadow 1/3".
3. **Shadow → active** after 3 clean replays, or when an underwriter approves the format in review (`format.approve`).
4. **If the learn does not verify,** the document goes to review with a pre-fill. The reviewer's decision stands. The next document of that format retries the learn.
5. **Check the result:** **Formats** shows the version, who created it (learn, delta, hand), its gate and its document stats.

**Spreadsheets without a printed carrier name** (CSV exports) need the carrier in the submission (`carrierHint`) to pass the spreadsheet-rules gate.

## 7.2 Format-change alert handling

| Signal | Meaning | Action |
|---|---|---|
| A document of a known carrier routes to **delta** | New or renamed headers | None if delta repair verifies (v(n+1) is published with `supersedes`). If it escalates to learn and then review, decide the review, and check the reasons for a real layout change vs an inconsistent source |
| The review rate for a carrier rises (registry stats) | Drift, or a bad learned version | Retire the version (`POST /formats/{id}/versions/{v}/retire`, with a reason). The next document re-learns |
| Several sibling versions of one format (e.g. `…-xlsx-2…9`) | Concurrent first documents learned in parallel (ADR 0014; fixed) | Retire the duplicates. The pre-bake-off reset does this (runbook §Format registry) |

## 7.3 Model retirement migration

The Azure OpenAI retirement schedule (`docs/architecture/azure-extraction-stack.md` §model lifecycle):
- **gpt-5.4 family:** 2 Sep 2027 (verify the mini variant's own date on the schedule);
- **gpt-5.2:** 8 Jun 2027;
- **text-embedding-3-large:** 9 Feb 2028.

Microsoft gives 60 days' notice. Standard deployments may auto-upgrade, so pin versions.

1. **Monthly:** check the schedule and the deployed versions (`model_deployments` in `infra/environments/poc.tfvars`).
2. **On notice:**
   - deploy the replacement next to the current model (Terraform `model_deployments`);
   - point a staging environment's `learn_model` / `delta_model` / `chat_model` at it.
3. **Run the golden set:**
   - `lossrun eval run` on the corpus, then `eval score` and `eval insights`;
   - `eval chat` and `eval chat-text` for chat.
4. **Compare** with the current deck table and chat reports. The replacement must meet every committed row.
5. **Switch** by configuration (Terraform apply). Then record an ADR and a `catalogVersion` / prompt-version note.
6. **Rollback:** point the configuration back. Learned formats need no migration, because replay has no model.

## 7.4 Exception-queue SLA breach

- **Queue:** **Review** → *Documents needing review*, or `GET /reviews?state=pending`.
- **Proposed SLA:** pending under 1 business day.
- **Expiry:** items past the expiry sweep become `review.expired` and stay listed.
- **On breach:**
  1. Sort by age.
  2. Faxes and narrative statements are expected review (by design). For a cluster from one carrier, check for format drift (7.2).
  3. Assign follow-ups (`quote.followup`) when a decision needs the broker.
  4. If the queue is driven by the learn path being unavailable (quota or risk check), see the runbook §Model capacity. Replay keeps working.

## 7.5 Disaster recovery

| Component | Recovery | RPO / RTO (POC) |
|---|---|---|
| Azure SQL (system of record) | Point-in-time restore, 7 days on GP serverless. The ledger digests in immutable Blob verify the restored history (`audit-proof`) | 5–10 min / about 1 h |
| Blob (uploads, extractions, formats) | Soft delete and versioning per the storage account settings. Production: geo-redundant storage | Minutes / about 1 h |
| Function App | Redeploy from git (`infra/scripts/deploy-functions.ps1`). Stateless. The script refuses to deploy with pending database changesets and ends with a smoke upload that must publish (§7.6) | — / 15 min |
| Durable Task Scheduler | In-flight orchestrations replay. A lost upload is re-submitted with the same `Idempotency-Key` | — / 15 min |
| Infrastructure | `terraform apply` from `infra/` (remote state in Azure Storage) | — / 1–2 h |
| AI Search index | Rebuild: `lossrun chat init-index`, then `lossrun chat reindex --from-api` | — / about 1 h |

**Production (proposed).**
- A paired-region plan for SQL (failover group) and storage (GRS).
- The SQL region move (runbook §SQL region move) first.

## 7.6 Deploy (brief 23 Answers 4: mandatory gates)

1. **Database first.** Run `lossrun-db plan`, then `lossrun-db deploy`, against the environment (runbook §Deploy). Every changeset is rerunnable.
2. **App deploy.** Run `infra/scripts/deploy-functions.ps1` and, for the workbench, `infra/scripts/deploy-web.ps1`. Both run `lossrun-db deploy --check` first and stop on pending or drifted changesets.
3. **Smoke upload.** Both scripts end with one synthetic fixture through the live API (`Q-SMOKE`, a retired quote). It must reach a published tier, or the deploy is reported failed.
4. **Record.** Log the deploy and the smoke upload id in the bake-off log.

**Why.** A changeset that never reached the live database stopped every upload at parse for about a day, unnoticed (brief 23).

## 7.7 Other procedures already in the runbook

- **Pre-bake-off format reset:** dry run, then the real reset the day before the blind set. Record `audit <id> / ledger tx <id>`.
- **Demo days:** `demo_mode` on and off; the offline fallback host.
- **Quote names and retirement:** the audited API.
- **Load test:** run and summarize.
- **Integration:** outbox health and dead letters.
- **Chat index:** backfill and the poison queue.
- **Cost reconciliation:** `lossrun eval reconcile` against the daily export. Monthly, artefact 3.
