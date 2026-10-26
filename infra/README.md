# infra — POC environment (Terraform)

Provisions the POC resource group (`<prefix>-rg`, prefix from the tfvars) in **East US 2** (ADR 0001).
Everything is keyless (Entra ID + managed identity); no secrets in variables, outputs or state inputs.

## Layout
| File | Contents |
|---|---|
| `versions.tf` | Terraform ≥ 1.9, `azurerm ~> 4.81`, `azapi ~> 2.13`, partial `azurerm` backend, provider settings |
| `providers.tf` | Resource provider registration policy (none in poc; the provider registers missing ones elsewhere), `removed` blocks for the old registrations (ADR 0027) |
| `diagnostics.tf` | Diagnostic settings to Log Analytics and SQL server auditing, behind `diagnostics_enabled` (on outside poc) |
| `tests/*.tftest.hcl` | Offline posture tests (mock providers, plan only): the POC keeps its plan; the sandbox example plans and refuses Basic auth |
| `environments/sandbox.tfvars.example` | The organisation sandbox (ADR 0027 posture) |
| `main.tf` | Naming, tags (`project`, `env`, `owner`), resource group |
| `ai.tf` | Foundry (AIServices) resource + project, model deployments, Content Understanding defaults |
| `storage.tf` | Storage account, containers, version-level immutability on `uploads` |
| `sql.tf` | Azure SQL server (Entra-only) + GP serverless database, firewall |
| `monitoring.tf` | Log Analytics, Application Insights, Key Vault |
| `orchestration.tf` | Durable Task Scheduler + task hub, Flex Consumption plan + Function App |
| `search.tf` | Azure AI Search for the chat service (`search_enabled`; on in the POC, Basic tier, removed while hibernated) |
| `rbac.tf` | Role assignments for the Function App identity and the deploying user |
| `cost.tf` | Daily actual-cost export to `evals/cost-export/` |
| `environments/poc.tfvars` | POC values (regions, SKUs, model versions, TPM) |
| `bootstrap/` | One-time remote-state storage account in `<name_prefix>-tfstate-rg` |
| `scripts/set-cu-defaults.ps1` | Data-plane call used by `terraform_data.cu_defaults` |

## Where `azapi` / local-exec are used (and why)
| Resource | Mechanism | Reason |
|---|---|---|
| Durable Task Scheduler + task hub | `azapi_resource` (`Microsoft.DurableTask/schedulers@2025-11-01`) | No azurerm resource in 4.81 |
| `uploads` container | `azapi_resource` (`blobServices/containers@2023-05-01`) | `azurerm_storage_container` cannot enable version-level immutability |
| Cost Management export | `azapi_resource` (`Microsoft.CostManagement/exports@2023-07-01-preview`) | azurerm export has no managed identity; the storage account has shared-key access disabled |
| SQL database `<sql-database>` | `azapi_resource` (`Microsoft.Sql/servers/databases@2025-01-01`) | Free offer (`useFreeLimit`, `freeLimitExhaustionBehavior = BillOverUsage`) can only be set at creation and azurerm 4.81 has no argument for it |
| Blank `AzureWebJobsStorage` on the Function App | `terraform_data` + `local-exec` → `az functionapp config appsettings set` | azurerm 4.81 always writes it as a connection string (empty key) and hides it on read; the host would prefer it over `AzureWebJobsStorage__accountName`. Re-runs whenever the app's settings change |
| CU default model mapping | `terraform_data` + `local-exec` → `az rest PATCH /contentunderstanding/defaults` | Data-plane API; neither azurerm nor azapi (ARM only) can call it |

Flex Consumption settings are fully covered by `azurerm_function_app_flex_consumption`.

## Notes on specific choices
- **Function App gets Storage Blob Data Owner** (in addition to Blob Data Contributor from the brief). This is a documented Functions host requirement for identity-based `AzureWebJobsStorage` (blob leases, keys, deployment package). In the POC it is on the storage account; outside poc it is per container (the package container, `azure-webjobs-hosts`, `azure-webjobs-secrets`, pre-created by Terraform; ADR 0027).
- **Azure SQL free offer** is on (`sql_use_free_limit = true`) with **BillOverUsage**, so a demo never stalls when the monthly 100K vCore-seconds run out. Azure does not allow switching back to auto-pause-until-next-month, and changing `sql_use_free_limit` later forces a new database. The first free database fixes the free-tier region for the subscription (Central US, see below).
- **SQL is in Central US** (`sql_location`, ADR 0003): SQL provisioning is restricted in East US 2 for this subscription. Same resource group; everything else stays in East US 2.
- **gpt-5.2 is behind `deploy_gpt52 = false`** (ADR 0004): the deployment is blocked by an Azure risk check. `learn_model` and both CU completion aliases point at `gpt-5.4-mini`. To switch back once support clears it: `deploy_gpt52 = true`, `learn_model = "gpt-5.2"`, `learn_model_version = "2025-12-11"`, apply (the CU defaults re-run automatically because the mapping changes). When `learn_model` names a listed deployment that `deploy_gpt52` gates off, the learn path (app setting, model version, CU completion alias, output) uses `delta_model` instead (`local.effective_learn_model`, brief 30 item 10).
- **AI Search** is on in the POC (`search_enabled = true`, `search_sku = "basic"`, the chat-service work) and removed while hibernated. Only one free Search service is allowed per subscription, and the POC subscription's was already in use.

## Organisation posture (ADR 0027)
Any `environment` other than `poc` gets the organisation posture; the POC keeps its plan (`tests/poc.tftest.hcl`):
- `api_auth_mode = "basic"` is refused; Entra needs `create_entra_app = true` or `auth_audience` / `auth_tenant_id`.
- Diagnostics on (`diagnostics_enabled`), Defender for AI opt-in (`defender_ai_enabled`, default `false`; the POC sets it).
- Per-secret Key Vault and per-container Blob Owner scopes; no `localhost` CORS origin unless `web_extra_origins` sets one.
- Deployer role assignments typed from the sign-in (user or service principal); `sql_admin_object_id` for a group admin.

### Resource providers
Registration is per subscription, not per stack, and `azurerm_resource_provider_registration` fails on an already-registered provider and unregisters on destroy, so the stack no longer manages it. Outside poc the provider registers what is missing at init (`core` plus the list in `providers.tf`) and never unregisters. If the deploying identity may not register providers, the subscription owner runs this once and the tfvars sets `resource_provider_registrations = "none"`:
```bash
for ns in Microsoft.AlertsManagement Microsoft.CognitiveServices Microsoft.Consumption Microsoft.CostManagement \
  Microsoft.CostManagementExports Microsoft.DurableTask Microsoft.EventGrid Microsoft.Insights Microsoft.KeyVault \
  Microsoft.LoadTestService Microsoft.ManagedIdentity Microsoft.OperationalInsights Microsoft.Search \
  Microsoft.ServiceBus Microsoft.Sql Microsoft.Storage Microsoft.Web; do az provider register --namespace "$ns"; done
```
(`Microsoft.Security` too when `defender_ai_enabled = true`.) The POC's three old registration resources are forgotten (`removed { destroy = false }`): its next plan says "will no longer be managed by Terraform" for them and changes nothing in Azure.

### Offline checks (no sign-in; also `.github/workflows/terraform.yml`)
```bash
terraform -chdir=infra fmt -check -recursive
terraform -chdir=infra init -backend=false && terraform -chdir=infra validate
terraform -chdir=infra test -filter=tests/poc.tftest.hcl -var-file=environments/poc.tfvars
terraform -chdir=infra test -filter=tests/sandbox.tftest.hcl -var-file=environments/sandbox.tfvars.example
```
On Windows write the filter as `tests\poc.tftest.hcl`.

## Demo days and Entra (ADR 0008)
- **`demo_mode = true`** keeps one always-ready instance for the HTTP group and one for the Durable group. This removes the ~15 s cold start measured in the API work.
  - Cost: about $0.058 per hour at 2 GB (Always Ready Baseline; price in `config/azure-prices.json`), billed as a separate "demo readiness" line in the TCO.
  - Set it for the demo, apply, then set it back to `false`.
- **`create_entra_app = true`** creates the API app registration (`entra.tf`) and its service principal, and sets `Auth__TenantId` and `Auth__Audience` on the app.
  - The registration has six app roles (Applicant, Underwriter, UWManager, Admin, Engineer, QuoteSystem; `entra.tf`), an `access_as_user` scope, and a SPA redirect for the workbench SPA client.
  - It needs app-creation rights in the tenant. In the client tenant, their identity team creates the registration and hands over `auth_tenant_id` and `auth_audience`.
  - To move from Basic to Entra auth, switch `api_auth_mode` to `entra`. Outside the poc environment Entra is the default and Basic is refused (ADR 0027).

## Ledger digests: POC vs production (ADR 0010)

Azure SQL uploads ledger digests to the `sqldbledgerdigests` container (`ledger.tf`). The POC keeps its retention policy **unlocked, 7 days**, so the environment can be torn down. A production environment (not created yet) sets, in its own tfvars:

```hcl
ledger_digest_immutability_days   = 2557 # 7 years
ledger_digest_immutability_locked = true
```

Locking is irreversible: a locked policy can only be extended (at most 5 times), and the container and account cannot be deleted while it holds digests. Apply it once, deliberately, in the production subscription only.

## Browser access to the API (ADR 0012)
- **CORS on the Function App** (`site_config.cors` in `orchestration.tf`, origins from `local.web_origins` in `web.tf`) allows exactly:
  - `https://<terraform output static_web_app_hostname>` (the Static Web App, production);
  - `http://localhost:5173` (`web_extra_origins`, the local Vite dev server; poc only by default);
  - anything in `web_preview_origins` (empty today; the `mock` preview environment calls no API).
  - `support_credentials = false`: no cookies, only `Authorization` headers.
- **The API validates every request itself:** Entra tokens (or Basic on the demo host); no keys reach the browser.
- **Production:** front the SPA and the API with APIM or App Gateway plus private endpoints (governance pack). A Static Web Apps linked backend is not required.
- Verify the live list:

  ```bash
  az functionapp cors show -g "$(terraform output -raw resource_group)" -n "$(terraform output -raw function_app_name)"
  ```

## Prerequisites
- Terraform ≥ 1.9, Azure CLI signed in (`az login`) as the deploying user, Owner (or Contributor + User Access Administrator) on the subscription.
- **Subscription id (once per machine).** Without it, `terraform plan` stops at an interactive prompt. If that prompt is interrupted it leaves a stale state lock; this happened in the learn-path work. Write the id into the git-ignored `infra/local.auto.tfvars`, which Terraform loads automatically:
  ```bash
  echo "subscription_id = \"$(az account show --query id -o tsv)\"" >> infra/local.auto.tfvars
  ```
  PowerShell:
  ```powershell
  Add-Content infra/local.auto.tfvars ('subscription_id = "{0}"' -f (az account show --query id -o tsv))
  ```
  Exporting `ARM_SUBSCRIPTION_ID` instead still works, because a null `subscription_id` falls through to the environment. The id is never committed (`*.auto.tfvars` is git-ignored).
- In scripts and agents, pass `-input=false` to `plan` and `apply`, so a missing value fails fast instead of waiting at a prompt that holds the state lock.
- PowerShell available for the CU defaults script (on non-Windows set `local_exec_interpreter = ["pwsh","-NoProfile","-File"]`).
- Optional: `infra/local.auto.tfvars` (git-ignored) with `sql_client_ip_allowlist = { me = "x.x.x.x" }` to reach SQL from your machine.

## First-time bootstrap (remote state)
```bash
cd infra/bootstrap
terraform init
terraform apply            # creates <name_prefix>-tfstate-rg + state storage account (local state, git-ignored)
terraform output -raw backend_hcl > ../backend.hcl   # the shape is in backend.hcl.example
```

## Plan / apply
```bash
cd infra
terraform init -backend-config=backend.hcl
terraform plan  -var-file=environments/poc.tfvars -out=poc.tfplan
terraform apply poc.tfplan
terraform plan  -var-file=environments/poc.tfvars   # must report "No changes" (idempotency check)
```
Before the state account exists you can still plan against an empty local state by creating a git-ignored
`backend_override.tf` containing `terraform { backend "local" {} }` and running `terraform init -reconfigure`.
Delete it before the real init.

## Teardown
```bash
cd infra
terraform destroy -var-file=environments/poc.tfvars
```
Notes:
- Blobs written to `uploads` carry version-level immutability for `uploads_immutability_days` (default 1 day). The
  storage account cannot be deleted until those versions expire, so wait a day (or delete the unlocked per-version
  policies) before destroying.
- The Foundry resource is purged on destroy (`purge_soft_delete_on_destroy = true`) so the name can be reused; Key Vault is soft-deleted (7 days).
- Destroy never unregisters resource providers (ADR 0027). In the POC it removes the Defender for AI plan from the subscription (`defender_ai_enabled = true` in `poc.tfvars`); set it to `false` first if the plan should stay.
- Bootstrap state is destroyed last with `terraform destroy` in `bootstrap/`.
