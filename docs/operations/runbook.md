# Operations runbook — POC environment

Environment:
- Function App `<function-app>`
- Azure SQL as the system of record (ADR 0010)
- Blob storage for formats and extractions
- Azure AI resource `<ai-resource>`

Credentials:
- Demo users live in Key Vault `<key-vault>`, secret `devauth-users`.
- Read them into the shell only. Never print them or commit them.

**The blind run** (the organisation's archive, any operator): `docs/operations/blind-run.md` is the whole procedure, in order. It covers the import, seal, verify, run, align, adjudicate, truth and score steps, and the vendor variant.

## Format registry

**Retire one format version.** `POST /api/v1/formats/{formatId}/versions/{version}/retire` with body `{ "reason": "…" }`.
- Allowed for Admin and UWManager.
- Audited (`format.retire`).
- The spec stays for lineage; it is only hidden from matching.
- **A live format test ends with a retire** (brief 35 §8). A test against the live API that publishes a version must retire it before the wake ends; examples are a fill-down decision, a confirm-format approval, or an upload that learns. Otherwise the next real or smoke upload of that format replays on the test version: a shadow version raises a confirm-format review, and the deploy's smoke gate expects a delivered tier. Brief 34 step 4 left `synthetic-mutual-csv` v2 in shadow; brief 35's wake retires it first.

**Un-retire a version (brief 30 item 17).** An approval or a confirm-format review never brings a retired version back.
- Only `lossrun formats unretire <formatId> <version> --reason "<why>"` does, as Engineer or Admin. It calls `POST /api/v1/formats/{id}/versions/{v}/unretire` and is audited (`format.unretire`).
- A retired learned alias likewise stays retired: a review that accepts or edits it is refused (409 `alias-retired`).

**Pre-bake-off reset.** Run it **one day before the blind-set run**, not earlier. It retires every learned version, so the bake-off starts from the hand specs and an empty learned set.

1. Dry run first. It lists what would be retired and changes nothing:
   ```bash
   lossrun formats reset --learned --reason "pre-bake-off reset <date>" --dry-run
   ```
2. Then the real reset:
   ```bash
   lossrun formats reset --learned --reason "pre-bake-off reset <date>"
   ```
- It calls `POST /api/v1/formats/reset` as an Admin. Set `LOSSRUN_API_USER` / `LOSSRUN_API_PASSWORD` from Key Vault, or `LOSSRUN_API_TOKEN`.
- It writes one `formats.reset` audit event naming every retired ref, plus each version's lifecycle record.
- Hand specs are kept.
- The registry-reset work ran the dry run only. It would retire `fictional-casualty-group-pdf@1` and keep 9 versions.
- **Owner:** the POC operator. They run it the day before the blind-set run, not earlier, as the Admin dev-auth user (Key Vault `devauth-users`), or with their Entra account once the app registration is on.
- **Overrides are not reset.** `dbo.Override` is an append-only ledger table, so overrides made before brief 30 keep their old values as display text (`"1,234.00"`). Since brief 32 item 5 they no longer break the as-reported view:
  - a number stored as text is read with the currency parser;
  - a value that is not of its field's type (a clipped object, a word in a number field) leaves the field at its current value, flags the override `unrevertable` and raises the escalation `override-unrevertable`, once per quote revision.
- **Record the legacy override count in the bake-off log** (brief 32 Answers 1): `N overrides stored before brief 30 (formatted text); revert falls back per brief 32 item 5`.
  - N is `SELECT COUNT(*) FROM dbo.Override WHERE AtUtc < '2026-10-26'`.
  - Brief 30's code first went live on the 2026-10-26 wake, and no override was made that day before it.
  - The ledger keeps these rows; nothing deletes them.
- Record the `formats.reset` audit event id in the bake-off log. Find it with `SELECT TOP 1 Id, AtUtc, Rationale, LedgerTransactionId FROM dbo.vAuditTrail WHERE Action = 'formats.reset' ORDER BY AtUtc DESC`.
- **Audit event id format (`docs/poc-plan/closeout/README.md`).** Record two values:
  - the event id: a `bigint` from `dbo.AuditEventSeq`, for example `8401`;
  - the ledger transaction that committed it: `LedgerTransactionId`, for example `62757`.

  Write it as `audit 8401 / ledger tx 62757`. `GET /quotes/{id}/audit` returns both, as `id` and `ledgerTransactionId`, and `lossrun-db audit-proof` verifies the ledger they sit in.
- **The close-out work dry run (2026-10-23 15:40 UTC, Admin).**
  - It would retire 10 learned versions and keep 9.
  - The 10 are `fictional-casualty-group-pdf@1` and 9 `notional-workers-comp-exchange-xlsx[-2…-9]@1`. The 9 are the duplicate learns of one workbook format from the integration work load test, from before the learn lock was held until commit.
  - A dry run writes no audit event. The real reset stays scheduled for the day before the blind set.

**Hand-spec versions share the live version space.** A hand-spec fix must take the next version that is free in the *live* registry, not just locally. In the live registry, learned `synthetic-mutual-pdf` v2–v4 (06/07 leftovers, retired in the registry-reset work) shadowed hand v2/v3, so the hand fix ships as **v5**.

## Next wake

**Cost:** about $4–5 with the corpus v5 run (the run itself is about $3.6: $3.62 on 2026-10-26, $3.67 on brief 23's run). The brief 31 Answers estimate of "~$1" was wrong. The rest of a wake is about $0.6 metered (chat evals, uploads, indexing) plus about $0.12 an hour for Search and Service Bus while awake.

1. **Cost pull for 2026-10-26.** Export Cost Management actual cost for the resource group (the day is billed by about 2026-10-27). Then run `lossrun eval reconcile` against it, with `eval-runs/` and the SQL usage, and commit the reconciliation. The day has three wakes: about $4.35 (`docs/evaluation/results/2026-10-26-corpus-v5-wake/README.md` §Cost), about $5.2 for brief 34 Part B (`docs/evaluation/results/2026-10-26-brief34-wake/README.md` §Wake cost) and about $4.1 for brief 35 Part B (`docs/evaluation/results/2026-10-26-brief35-wake/README.md` §Wake cost). A partial pull at 15:45 UTC showed $6.61.
2. **gpt-5.2 bill meter prefixes.** The quota was granted by 2026-10-26 (Data Zone Standard 1,100 K TPM in eastus2), but nothing is deployed on it. On the first bill after a gpt-5.2 deployment is used, confirm the `billMeterPrefixes` of the gpt-5.2 entries in `config/azure-prices.json` (`5.2 Inp`, `5.2 cd Inp`, `5.2 Opt`; written by analogy with gpt-5.4-mini, brief 31 item 22). A wrong prefix shows as an unclaimed platform line in the reconciliation, not as a gap.
3. **Chat multi-turn is the median of three runs.** `eval chat-multiturn --runs 3` (brief 31 Answers 8); the gate (≥ 36/40, 90 % of the turns) applies to the median. One run went 34, then 37, on the same code.
4. **The mock quote system's zip deploy answered 403** on the brief 35 wake (twice; `deploy-functions.ps1 -App <quote_mock_app_name>`). The brief 34 build is still running and its code is functionally unchanged, so it was not redeployed. On the next wake that changes the mock, look before changing anything (brief 35 Answers 2). The likely cause is that one of the brief 30/32 least-privilege scope changes affected the zip-deploy (SCM) credentials path. Check the Function App's `basicPublishingCredentialsPolicies` and the deploying identity's role on the mock, then read the error detail (`az functionapp deployment source config-zip --debug`). Not a blocker: the mock is demo-only.
5. **When gpt-5.2 deploys: the learn-model study** (brief 35 Answers 3). The POC subscription's account risk check refused the deployment on 2026-10-26. A support ticket on this subscription (the subscription owner's, optional) could clear it before the bake-off; the organisation's subscription has its own check. On the first wake where `deploy_gpt52 = true` applies:
   - run corpus v5 with `LOSSRUN_LEARN_MODEL=gpt-5.2` (the version follows from `config/models.json`), `--mode local --all-fields`;
   - compare it with the attached gpt-5.4-mini run (`docs/evaluation/results/2026-10-26-brief35-wake/`) by ADR 0004 amendment 1's rule;
   - switch only if gpt-5.2 meets it.
6. **Corpus v5, when the prompt artefact changes:** `eval run --mode local`, never `--mode api` (that would upload 332 documents into the live demo record and learn with the deployed code against the live registry).

## Briefs 30 and 31: next-wake items

**Done on the 2026-10-26 wake** (`docs/evaluation/results/2026-10-26-corpus-v5-wake/`): all six steps; the corpus run is attached to the prompt gate. Kept for the record.

1. **Corpus v5 run for the prompt artefact.** The artefact is now learn-v11 `e0d9753d8f5b` (brief 31; brief 30 left `ed9b8545c4d5`, never corpus-run). Corpus run pending. It changed with the glossary ("Injury Type" value-shape aliases), `SpecLint.cs` (the value-shape retarget, the fill-down declaration), `FormatLearner.cs`, `PromptBuilder.cs` (nullable word confidence) and `carrier-format.schema.json`. One run covers both briefs.
   - Run `eval generate --v5`, `eval run --mode local` and `eval score --all-fields` from a commit at or after brief 31. The all-fields study must be 0 wrong (scoring-protocol §8).
   - Commit the results folder, its files registered as synthetic with the sample guard (`--register-synthetic`).
   - The PR gate (`prompt-gate.py --pr`) flags the artefact until this run is attached.
2. **Chat index field.** Update the index from `infra/search/lossrun-docs.index.json` (new filterable `reviewStatus`) with `lossrun chat init-index`, then `lossrun chat reindex --from-api`.
   - **Rollback if the order slips (brief 31 item 5):** if the Functions were deployed first, chat search fails on the unknown field. Set the app setting `Chat__ReviewStatusFilter=false`, then update the index and set it back to `true` (the default). The store-side post-filter keeps rejected pages out meanwhile.
   - Do this **before** the Functions deploy: the search filter names the field, and the service refuses a filter on a field the index lacks.
3. **Chat evals** (`eval chat`, `eval chat-multiturn`). They need the live model; there is no stub mode. Gates: for `eval chat`, ≥ 90 % numeric exact (27/30), every citation correct and 0 leaks; ≥ 90 % of turns (36/40) for `eval chat-multiturn`; the 37/40 written here before was one run's score, not the gate.
4. **`lossrun-db deploy`** for changesets 0022 (quote owner) and 0023 (manifest unnormalized count) and the re-run 0016 procedure (decision keys); then `deploy --check`.
5. **App settings from Terraform:** `Models__DeploymentModels` (deployment → model, for pricing). Optional: `Learn__LeaseMinutes` (default 35, above the 30-minute function timeout).
6. **The mock quote system's page** is opened with `?code=<the mock's function key>`; its grid and stream are no longer anonymous.

## Model capacity

The learn concurrency cap is the `LearnGate` Durable entity, set from the Terraform variable `learn_max_concurrent` (app setting `Learn__MaxConcurrent`). Queued documents show `learn-queued` and wait on a 10 s durable timer. Set it by stage:

1. **POC and demo: cap 1 at 100K TPM.** `gpt-5.4-mini` DataZoneStandard, East US 2, keeps its 100K TPM quota.
   - Learns are rare and run one at a time; a learn takes about 25 s.
   - Replay uses no tokens.
   - **Live since the analytics work.** Terraform `learn_max_concurrent = 1` (default); app setting `Learn__MaxConcurrent = 1`.
2. **Production onboarding of many carriers: request 300K TPM first, then cap 3.**
   - **Evidence:** the registry-reset work burst test (`docs/evaluation/results/2026-10-22-corpus-v2/studies/learn-cap-burst.md`) made 8 simultaneous new-carrier learns at cap 3 / 100K TPM.
     - It produced 44 HTTP 429s against 26 successes.
     - The retry absorbed all of them, but each learn took 57–135 s instead of about 25 s.
   - Raise the cap only after the quota increase is granted.

## Deploy
- **Database first, always** (brief 23 Answers 4). Apply pending changesets before any app deploy:
  ```bash
  dotnet run --project src/LossRun.DbDeploy -c Release -- plan --param appPrincipalName=<function app>
  dotnet run --project src/LossRun.DbDeploy -c Release -- deploy --param appPrincipalName=<function app>
  ```
  The connection comes from `LOSSRUN_SQL_CONNECTION` (`Server=tcp:<sql_server_fqdn>,1433;Database=<sql_database_name>;Authentication=Active Directory Default;Encrypt=True;`, both from `terraform output`).
  - **Why it is mandatory:** brief 21's changeset 0018 was never applied to the live database, and every upload failed at parse (`Invalid object name 'dbo.GlossaryAliasEvidence'`) until brief 23 found it in the Application Insights exceptions. Nothing told us.
- **Gate 1, before the deploy.** `deploy-functions.ps1` and `deploy-web.ps1` both run `lossrun-db deploy --check` first (`infra/scripts/deploy-checks.ps1`, `Assert-DatabaseCurrent`). With pending changesets (exit 3) or drifted ones (exit 1) they stop, and nothing is deployed.
- **Gate 2, after the deploy: smoke upload.** Both scripts upload `src/TestShared/fixtures/lossrun-export.csv` under quote `Q-SMOKE` through the live API (`Invoke-SmokeUpload`), as the Key Vault dev-auth underwriter.
  - The upload must complete with its loss run processed at a published tier (ReplayAccepted, Learned or Delta), or the script ends with "DEPLOY FAILED".
  - `deploy-web.ps1` also checks that the production host answers.
  - The smoke fixture replays the seen hand format: 0 model calls, about $0.00.
  - `Q-SMOKE` is retired (hidden from the quote selector), so smoke uploads never reach a demo account. Its uploads stay in the audit trail.
  - **A failed smoke means the deploy failed.** Read the exceptions (`exceptions | where timestamp > ago(30m)` on the Application Insights component), fix, and redeploy.
- `infra/scripts/deploy-functions.ps1` zip-deploys the Function App. It and `deploy-web.ps1` take the resource names from the Terraform outputs (`infra/scripts/tf-output.ps1`, the export work), so run `terraform init` in `infra/` first; a parameter overrides an output.
- The CLI tools and the opt-in Azure tests name no environment either: set `LOSSRUN_AOAI_ENDPOINT`, `LOSSRUN_CU_ENDPOINT`, `LOSSRUN_API_BASE`, `LOSSRUN_BLOB_ENDPOINT` (and the others a command asks for) from `terraform output`. A missing one fails with its name and the output it comes from.
- After a deploy, run the simulator to check it:
  ```bash
  dotnet run --project src/LossRun.Simulator -- --concurrency 6
  ```
  Use an Underwriter account. Expect 5 uploads completed and bad-total awaiting review.
- The first run after a deploy is cold: about 15 s per upload, against 3–4 s warm.

### Integration (ADR 0014)
- **Outbox health.** `GET /integration/outbox` lists the latest rows. A row with no `publishedAt` after a minute means Service Bus or Event Grid is unreachable: `lastError` says why, and the 30 s `OutboxSweep` keeps retrying.
- **Dead letters.** Check that both are empty after a run:
  - Service Bus DLQ:

    ```bash
    az servicebus queue show -g <resource-group> --namespace-name <service-bus> -n quote-commands --query countDetails
    ```

  - Event Grid dead letters: the Blob container `eventgrid-deadletter`.
- **Mock quote system.** `<quote-mock-app>`, function key in Key Vault `quote-system-key`.
  - Read a quote with `GET https://<quote-mock-app>.azurewebsites.net/api/quotes/{quoteId}` and header `x-functions-key`.
  - Deploy it with `infra/scripts/deploy-functions.ps1 -App (terraform -chdir=infra output -raw quote_mock_app_name) -Project src/LossRun.QuoteSystemMock/LossRun.QuoteSystemMock.csproj`.
  - On a fresh environment, apply with `-var quote_mock_subscription=false`, deploy the mock, then apply again: the Event Grid subscription needs the mock's webhook handshake.
- **Before deploying orchestration code**, terminate in-flight uploads: they replay through the new code and fail. Use `lossrun admin terminate --upload <id>`. It terminates the upload's own orchestration only; document sub-orchestrations it started run to completion.
- **Deploy script.** It takes a lock per app and builds in its own temp folder; a second concurrent deploy fails at once. It disables MSBuild node reuse, because an idle node from another build held the DLLs locked (the cause of the earlier "flaky" deploys).

### Load test (ADR 0014)
- **Files:** `infra/loadtest/`:
  - `underwriters.jmx`;
  - `config.yaml` (criteria) with `user.properties` (25 users, 10 min, quote `Q-LOAD-1000`, its document ids);
  - `smoke.yaml` with `smoke.properties` (2 users, 90 s).
- **Render** the files for the environment first: `powershell -File infra/loadtest/render.ps1` fills the Key Vault URI and Function App host from the Terraform outputs into `infra/loadtest/out/` (`infra/loadtest/README.md`).
- **Create or update** the tests:

  ```bash
  az load test update --load-test-resource <load-testing> -g <resource-group> --test-id lossrun-12b-underwriters --load-test-config-file infra/loadtest/out/config.yaml
  ```

- **Run.** Start `az load test-run create … --test-id lossrun-12b-underwriters`. At the same time, run the browser sessions with `npx playwright test e2e/underload.spec.ts`; that needs `LIVE_BASE_URL`, `LOSSRUN_API_USER` / `LOSSRUN_API_PASSWORD` and `UNDERLOAD_DOCS`.
- **Results.**
  - Download with `az load test-run download-files … --result`, then run `python infra/loadtest/summarize.py <csv>`.
  - For server-side numbers, use `infra/loadtest/out/server-metrics.kql`.
- **Cost.** $0.15 per virtual-user hour. 25 users × 11 min ≈ 4.6 VUH ≈ $0.69, plus the minimum of 10 VUs per engine.

### Demo host (`docs/poc-plan/closeout/README.md`)
- **Quote names and retirement.** `PATCH /api/v1/quotes/{quoteId}` with `{ "rationale", "displayName"?, "retired"? }`. UWManager or Admin only.
  - The quote id never changes, because ledger rows name it. The display name shows in the quote selector and the Analytics title.
  - A retired quote leaves `GET /quotes` but stays readable by id. Use `?includeRetired=true` to list it.
  - Each change writes `quote.rename`, `quote.retire` or `quote.restore` to the ledger.
  - Send non-ASCII names from a script, not from Git Bash `curl -d`: Git Bash mangled `·` even as `\u00b7`.
- **Applied 2026-10-23.**
  - `Q-LOAD-1000` is displayed as "Q-DEMO-1000 · Large Account (synthetic)".
  - `Q-LOAD-BG`, `Q-12B-SMOKE` and `Q-LOAD-1K` are retired. The load test still targets `Q-LOAD-1000` by id.
- **Demo days: `demo_mode`.** Turn it on with `terraform apply -var-file=environments/poc.tfvars -var demo_mode=true` and back off after the demo.
  - It adds one always-ready instance each for `http` and `durable`. That removes the ~15 s cold start, at about $0.058 per hour per instance.
  - The close-out work checked it with a plan: the only change on the Function App is those two `always_ready` blocks, plus the usual in-place `ledger_digest_uploads` refresh.
  - Off today: `alwaysReady` is null on the site.
- **Inline Content Understanding** is on for documents up to 5 pages (`ContentUnderstanding__InlineMaxPages = 5`, Terraform `cu_inline_max_pages`).
- **Offline fallback.** If the live host or the network fails during a demo, switch to the offline demo (captured data, no sign-in). In order of preference:
  1. **The demo app:** `https://<demo-custom-domain>` (live since 2026-10-23). It is its own Static Web App, `<demo-static-web-app>` (Terraform `azurerm_static_web_app.demo`), default host `https://<demo-default-host>`. Its CSP is `connect-src 'self'`. `deploy-web.ps1` deploys it with every mock build (`-SkipDemo` skips it).
  2. **The demo pack on the laptop, no network:**
     - `lossrun demo pack --out <dir>`, then `node serve.mjs` in that folder and open http://localhost:4173.
     - The pack command fails if any Azure resource name is in the pack.
     - `e2e/pack.spec.ts` (with `PACK_BASE_URL`) walks every screen with every non-local request aborted.
  3. **The mock preview environment:** `https://<static-web-app-host>`.
- **Custom domain, one-time. Done 2026-10-23:** the CNAME was added, `demo_custom_domain` set, applied, and the managed certificate issued (GeoTrust, renews automatically). `e2e/pack.spec.ts` against the domain: chip shown, 0 outside requests.
  1. In your DNS provider (the demo domain's zone, `<your-domain>`), add `CNAME lossrun-demo → <demo-default-host>` with **DNS only** (not proxied). A proxied record hides the CNAME from Azure's validation and breaks the managed certificate.
  2. Set `demo_custom_domain = "<demo-custom-domain>"` in `environments/poc.tfvars`.
  3. Run `terraform apply`. It creates `azurerm_static_web_app_custom_domain.demo` (cname-delegation validation; the managed certificate is issued automatically, usually within minutes).
  4. `<your-domain>` is not needed: the demo app calls no API.
- **Attribution chip.** `VITE_ENV_LABEL` sets the top-bar chip at build time. The default is "Independent POC · synthetic data". There is no client branding in the app, and the bundle carries only the rule names, not the rule catalog's citations.

### SQL region move (ADR 0014)
The database is in Central US and the Function App in East US 2. Every SQL round trip crosses regions (~41–54 ms), which was most of the integration work override latency. Move it when the East US 2 SQL provisioning restriction (ADR 0003) is lifted for the subscription.

1. **Open the ticket on the org subscription, not the POC one** (`docs/poc-plan/closeout/README.md`; the request moves with the org subscription). Azure support → *Service and subscription limits (quotas)* → *SQL Database* → *Region access*. Paste:

   > Please enable Azure SQL Database provisioning in **East US 2** for this subscription. We need one logical server with one General Purpose serverless database (Gen5, up to 2 vCores; later up to 8) in East US 2, co-located with our Azure Functions app, to remove ~40 ms of cross-region latency per database round trip. Today, `az sql server create -l eastus2` fails with a region-restriction error, so the database runs in Central US. No other SQL regions are needed.

   Wait until a test `az sql server create -l eastus2` in a scratch resource group succeeds.
2. **Copy, once.** Create the database copy in East US 2:
   - Set `sql_location = "eastus2"` in `environments/poc.tfvars`. That creates `<sql-server>-<suffix>` there.
   - Then copy the data: `az sql db copy --dest-server <new server> …`, or a bacpac export/import.
   - A copy keeps the ledger tables and their digests. Take a fresh digest right after (`lossrun-db audit-proof`) and record both digest locations in the bake-off log.
3. **Point the app at it.** Terraform updates `Sql__ConnectionString` (it is built from the server resource). Then:
   - Run `lossrun-db deploy` against the new server, with `--param appPrincipalName=<function-app>-<suffix>`. It grants EXECUTE on `dbo.ApplyDecision`, and every changeset is rerunnable.
   - Run `lossrun-db audit-proof`.
4. **Re-measure.** Re-run the load test (Load test, above) and compare the override and features p95 with `docs/evaluation/results/2026-10-23-loadtest/` and the close-out work's run.
5. **Retire the old server** only after the bake-off. Keep it read-only until then as the audit copy.

### Chat (ADR 0013)
- **Index.** `lossrun chat init-index` creates or updates `lossrun-docs` from `infra/search/lossrun-docs.index.json`. Run it after any index-definition change. A field type change needs a new index name (`search_index`).
- **Backfill.** `lossrun chat reindex --from-api` (or `--uploads up-…,up-…`) queues uploads on `chat-index`. The `ChatIndex` queue trigger indexes them, idempotently by chunk id. After five failed tries a message goes to `chat-index-poison`; check that queue after a backfill:

  ```bash
  az storage message peek --account-name <storage-account> -q chat-index-poison --auth-mode login --num-messages 32
  ```

- **Queue binding.** The trigger's queue name is `%Chat:IndexQueue%`. The host resolves configuration keys, so writing it as `%Chat__IndexQueue%` disables the function at start ("does not resolve to a value").
- **Workspaces.** The hourly `ChatWorkspaceSweep` expires workspaces past their TTL and drops their chunks.
- **Smoke test.** `lossrun chat ask --scope quote --quote Q-DEMO-11 --question "What is the largest open claim?"` prints the answer, citations, tools, first-token time and cost.
- **Model.** Chat uses `Models__Chat` (today the delta deployment `gpt-5.4-mini`; ADR 0013 explains why there is no dedicated chat deployment yet).

### Workbench (ADR 0012)
- `infra/scripts/deploy-web.ps1` builds and deploys two environments:
  - **production:** live API, `https://<static-web-app-host>`;
  - **preview `mock`:** offline demo, `https://<static-web-app-host>`.
- **Before deploying:**
  - Stop any local `vite` dev server; it holds the native bundler module open on Windows.
  - Pass `-CleanInstall` for a CI-style `npm ci`.
- **After deploying:** sign in as `underwriter` and open a document. Or run `e2e/live.spec.ts` (docs/ui/README.md).
- **API CORS** allows only the Static Web App origins and, in the poc environment, `http://localhost:5173` (`infra/web.tf`; none outside poc by default, ADR 0027).
  - A new browser origin goes in `web_preview_origins` / `web_extra_origins`.
- **Any Function App site update** (CORS included) rewrites `AzureWebJobsStorage`. The `blank_webjobs_storage` step now re-runs on CORS changes too.
  - After any manual portal or CLI change to the site, check that it is empty:

    ```bash
    az functionapp config appsettings list -g <resource-group> -n <function-app> --query "[?name=='AzureWebJobsStorage'].length(value)"
    ```

  - It should print `0`.

## Hibernate

The POC is hibernated between work sessions (brief 24; `infra/hibernate.tf`). The switch is `hibernate = true` in `infra/environments/poc.tfvars`. It was applied on 2026-10-25: 2 added, 3 changed, 18 destroyed.

- **Removed while hibernated:**
  - AI Search (Basic, with the `lossrun-docs` index);
  - the Service Bus namespace and the `quote-commands` queue;
  - the Event Grid topic and the mock's subscription;
  - Azure Load Testing;
  - their role assignments;
  - the Search, Service Bus and Event Grid app settings.

  The always-ready instances are off (`demo_mode` is forced off), and the Log Analytics daily cap is 0.023 GB.
- **Kept:**
  - SQL, serverless, which auto-pauses;
  - Blob and Key Vault;
  - the Durable Task Scheduler (Consumption SKU, no idle charge);
  - both Function Apps (Flex Consumption);
  - the model deployments (pay per token);
  - both Static Web Apps (the offline demo keeps working);
  - the Defender for AI plan (subscription-wide, deliberately not touched; `defender_ai_enabled = true` in `poc.tfvars`, opt-in elsewhere, ADR 0027).
- **Budget.** `azurerm_consumption_budget_resource_group.poc` is $10/month. Its start date is the first of the month of the first apply and is ignored afterwards (a fixed date broke new environments; brief 30 item 51). It emails the subscription Owners at 100 % of actual spend and 80 % of the forecast. The expected standing cost while hibernated is under ~$5/month: SQL storage, Blob, Key Vault, logs.
- **What stops working:**
  - **Chat:** it needs Search, and the index is gone.
  - **Quote system:** quote commands stay in `dbo.Outbox`, nothing relays them, and the Service Bus-triggered dispatcher has no queue, so the host logs a listener error for it. No `lossrun.*` events are published.
  - **Uploads:** they still parse and route through the scheduler, but their quote payloads wait in the outbox. Do not run uploads, corpus runs or the smoke upload while hibernated: they are Azure calls, and brief 24 forbids them before its step 7.
  - **Load tests:** none.
- **Wake** (in this order):
  1. Set `hibernate = false`, then `terraform plan` and `apply`. The resources are recreated with new identities, and the role assignments and app settings come back.
  2. Recreate and fill the index: `lossrun chat init-index`, then `lossrun chat reindex --from-api`.
  3. Deploy with the gates, which run the database check and the smoke upload (§Deploy): `infra/scripts/deploy-functions.ps1`, then `deploy-web.ps1`.
  4. Check that the outbox drains (`GET /integration/outbox`: every row gets a `publishedAt`).
  5. Hibernate again afterwards, with the step below.
- **Before every `hibernate = true`** (brief 30 item 51). Destroying the Service Bus namespace drops every in-flight and dead-lettered message with it, and Event Grid stops publishing. So, with the API still awake:
  1. **Stop new work.** No uploads, decisions or overrides from here on.
  2. **The outbox is empty:** `GET /integration/outbox` lists no row without a `publishedAt`. A row with `lastError` is fixed or re-relayed first; the 30 s `OutboxSweep` retries it.
  3. **The queue and its DLQ are empty:**

     ```bash
     az servicebus queue show -g <resource-group> --namespace-name <service-bus> -n quote-commands --query "countDetails.{active:activeMessageCount,dead:deadLetterMessageCount}"
     ```

     Both must be `0`. Dead letters are read, and their quotes re-sent or recorded in the bake-off log, before the namespace goes: there is no way to read them afterwards.
  4. **Event Grid dead letters** (Blob container `eventgrid-deadletter`) are reviewed. The container stays, so this is not lost, but note what is in it.
  5. Set `hibernate = true`, then `terraform plan` (expect the namespace, queue, topic, subscription, Search and Load Testing to be destroyed) and `apply`.
- **Known drift.** `azapi_update_resource.ledger_digest_uploads` shows a one-attribute in-place update on every plan: Azure stores the digest endpoint without its trailing slash. It is harmless.

## Escalations

Brief 26 (ADR 0025): the **Needs investigation** queue, where the pipeline, the hourly pattern sweep and reviewers send what a review cannot fix.

- **Who watches.** The Engineer role (brief 26 Answers 3: works the queue, reads what a reviewer reads, never decides or overrides), with UWManager and Admin, on the workbench's **Investigate** screen.
  - **Alerts** go to `escalation_alert_emails` (a distribution list), or to the subscription Owners by role when the list is empty (action group `ag-escalation`), plus the optional `escalation_webhook_url` (a Teams incoming webhook, null by default):
    - on the first occurrence of each case (a 15-minute log alert on the `lossrun.escalation` event with change `raised` or `reopened`);
    - a daily digest while any case is open (the `lossrun.escalations.open` metric).
  - **Without the alert:** `GET /escalations?status=open`.
- **What raises a case** (thresholds in `config/escalation.json`; change them there, then redeploy):

  | Reason | Raised by | When |
  |---|---|---|
  | `learn-exhausted` | pipeline | a learn did not publish (attempts exhausted): a coverage signal |
  | `delta-exhausted` | pipeline | a delta repair did not publish: on an active format, a drift signal |
  | `replay-failed-active-format` | pipeline | a matched **active** format replayed with hard failures and went to review |
  | `unhandled-exception` | pipeline | an orchestration step failed; keyed by step and exception type |
  | `too-many-segments` | pipeline | one file split into more than `maxSegments` (3) segments |
  | `cost-over-limit` | pipeline | a document cost more than `maxCostUsd` ($0.15) |
  | `repeated-review` | hourly sweep (`EscalationSweep`, minute 17) | one layout fingerprint to document review ≥ 3 times in 7 days |
  | `rule-dominant` | hourly sweep | one rule fails on ≥ 80 % of a carrier's documents (at least 5) |
  | `review-rate-spike` | hourly sweep | a carrier's review rate this week is ≥ 20 points above its previous 30 days |
  | `repeated-reopen` | hourly sweep | a document reopened more than twice |
  | `manual-flag` | reviewer | "Flag for engineering" on the review screen, with a note |

- **Failure types the retry policy does not know (brief 33, ADR 0028).** An `unhandled-exception` case whose trace says `unknown: <Type>` is a failure type `config/retry-policy.json` does not list. It was not retried and failed fast.
  - **Your job:** decide, with evidence, whether the type is recoverable by running the activity again. Capture its failure details (`tools/capture-failures` builds the real conversion offline; add the case there). Then add a rule to the policy, transient or permanent, with the captured message or property, and a test in `Brief33Tests`.
  - **Never** flip `unknownIsTransient`: the lower layers have already retried what they can.
- **One case per reason and layout.** A repeat adds to the count only with a new document. The trace holds rule ids, scrubbed reasons and counts, never a value; to see values, open the linked document (an audited view).
- **How to resolve.**
  1. Open the case, read the step, the rules, the reasons and the counts, then open one or two linked documents.
  2. Set **Investigating** with a note.
  3. **Create tuning item** with the cause category, written into the next tuning batch:
     - (a) glossary gap;
     - (b) layout not expressible;
     - (c) classifier miss;
     - (d) scan quality;
     - (e) rule too strict or loose;
     - (f) source inconsistency;
     - (g) tool bug.
  4. Fix through the tuning loop (`tuning-loop.md`: tier 1 data or bug fixes with the regression gate, tier 2 design changes by pull request).
  5. Set **Resolved** with a note naming the fix (commit or PR).
  6. **If it happens again**, the case reopens as **New** by itself. **Won't fix** (for example a fax below the scan threshold) keeps counting without alerts.
- **How it feeds the tuning loop.**
  - **Where the items go.** Tuning items are files in `tuning/items/` (extractions container), listed by `GET /tuning-items`.
  - **How a batch reads them.** `lossrun tune` (brief 27) reads them with the batch's own failures and ranks the fix list by category and count.
  - **After a batch.** A case whose fix is merged is resolved; the next batch shows whether it stays quiet.
- **Hibernated.** Pipeline triggers still run when there is processing; the sweep runs hourly on the Function App. The alert rules stay (no idle cost).

