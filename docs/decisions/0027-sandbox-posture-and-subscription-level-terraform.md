# ADR 0027 — Sandbox posture and subscription-level resources in Terraform

**Status:** accepted (brief 30 items 10, 25, 50, 51) · **Date:** 2026-10-25

## Context

The Terraform in `infra/` was written for one POC subscription that the build developer owns. The end-to-end audit (brief 30) found that pointing it at an organisation sandbox, as `docs/solution-overview/next-steps-sandbox.md` says to, would not work or would do the wrong thing:

- **It would not plan.** The example `infra/environments/sandbox.tfvars.example` set `learn_model = "gpt-5.2"`, and the comment said `deploy_gpt52 = false` falls back to `gpt-5.4-mini`. No such fallback existed, so the CU-defaults precondition failed. Separately, the Key Vault name `lossrun-sandbox-kv-<6>` is 25 characters, one over Azure's limit. The offline test found that second error (below).
- **It would sign in with HTTP Basic.** `api_auth_mode` defaulted to `basic` and `create_entra_app` to `false`. An organisation sandbox would have stood up demo users on a public endpoint, while the handover says Basic is off.
- **It reached outside its resource group.** The stack touched the subscription itself:
  - `azurerm_resource_provider_registration` (three providers) fails when a provider is already registered, and unregisters it on destroy.
  - The Defender for Cloud AI plan (`azurerm_security_center_subscription_pricing`) was on by default, so a destroy would switch it off for every AI resource in the subscription.
  - `resource_provider_registrations = "none"` left the other providers the stack needs unregistered in a fresh subscription.
- **It granted more than it needed:**
  - Key Vault Secrets User on the whole vault for the load-test and mock identities;
  - Storage Blob Data Owner on the whole account for both Function Apps;
  - `localhost:5173` in the API's CORS list everywhere;
  - deployer role assignments typed `User`, wrong in a pipeline;
  - the SQL admin was always the person running Terraform.
- **It had no audit trail of its own.** There were no diagnostic settings and no SQL auditing.
- **First-apply hazards:**
  - one Key Vault secret was written without depending on the deployer's Secrets Officer role;
  - the budget had a fixed past `start_date`;
  - the cost export defaults to a past start date.

The POC environment must keep its plan: it is hibernated mid-bake-off, and nothing here is worth a change to live resources.

## Decision

**One switch, `environment`, selects the posture. `poc` keeps the POC as it is. Any other value gets the organisation posture.** The switch is `local.org_posture` in `main.tf`. Each setting it drives can also be overridden by its own variable.

| Concern | `poc` | Any other environment |
|---|---|---|
| API sign-in (`api_auth_mode`) | `basic` (demo host) | `entra`. `basic` is refused by variable validation; Entra needs `create_entra_app = true` or `auth_audience` (+ `auth_tenant_id`) from the identity team |
| Learn model | `local.effective_learn_model`: the delta model when `deploy_gpt52` gates the configured learn model off. The app setting, `Models__LearnVersion`, the CU completion alias and the output all use it | same |
| Resource providers | `resource_provider_registrations = "none"`; the three registrations in state are **forgotten** (`removed { destroy = false }`), never unregistered | the provider registers what is missing at init (`core` + the stack's namespaces, `providers.tf`) and never unregisters. Or the subscription owner runs the pre-step (`infra/README.md` *Resource providers*) and sets `resource_provider_registrations = "none"` |
| Defender for AI plan | on, set in `poc.tfvars` (owner's decision, 2026-10-25) | **opt-in** (`defender_ai_enabled`, default `false`): the subscription owner decides |
| Diagnostics (`diagnostics_enabled`) | off (daily log cap; plan unchanged) | on: Key Vault, Storage + Blob, SQL server auditing (`azurerm_mssql_server_extended_auditing_policy` → master DB `SQLSecurityAuditEvents`), Search, Service Bus, the Foundry resource, both Function Apps → the Log Analytics workspace |
| Key Vault access, load test / mock | whole vault | per secret (`devauth-users`; `quote-system-api-base` and `quote-system-api-credential`) |
| Blob Data Owner, Function Apps | whole account | per container: each app's package container, the mock's record container, and `azure-webjobs-hosts` / `azure-webjobs-secrets`, which Terraform pre-creates because the apps can no longer create them. Data access stays Blob Data Contributor |
| CORS extra origins (`web_extra_origins`) | `http://localhost:5173` | none |
| Applicant creates a quote by uploading (`Quotes__ApplicantMayCreate`, `applicant_may_create_quotes`) | `true` (demo convenience) | `false`: the quote exists in the quote system first; an Applicant upload to an unknown quote is 404 (brief 32 Answers 4) |
| Deployer principal type | `User` | derived from the sign-in tool's app id: `User` for the Azure CLI or Azure PowerShell, otherwise `ServicePrincipal` (`deployer_principal_type` overrides) |
| SQL Entra admin | the signed-in identity | `sql_admin_object_id`, a group, recommended |
| Key Vault name | `<key-vault>-<6>` (unchanged) | the dashless form when the dashed one is over 24 characters |

Environment-independent fixes:
- the `quote-system-key` secret now depends on the Secrets Officer role;
- the budget's `start_date` is the first of the month of the first apply, and is ignored afterwards;
- the example documents `cost_export_start`, `local_exec_interpreter`, `learn_model_version`, `budget_monthly_usd`, `escalation_alert_emails` and `defender_ai_enabled`;
- a hibernate runbook step drains the Service Bus DLQ and checks the outbox is empty before `hibernate = true`.

**CI.** `.github/workflows/terraform.yml` runs these on every change to `infra/`, with no sign-in and no backend:
- `terraform fmt -check`;
- `init -backend=false` and `validate`, for the stack and `bootstrap/`;
- the offline posture tests, `infra/tests/poc.tftest.hcl` and `sandbox.tftest.hcl`. They use mock providers in plan mode. The POC test asserts every POC-visible value above is unchanged. The sandbox test plans the example as written and with `deploy_gpt52 = false`. It also checks the refusals: `basic` auth, Entra without an audience, and a mistyped `learn_model`.

A signed-in `terraform plan` against a real subscription stays a deliberate step: it needs the organisation's service connection.

## Consequences

- **The next POC plan changes state only, not Azure:**
  - three provider registrations are forgotten ("will no longer be managed by Terraform");
  - `azurerm_role_assignment.quote_mock_kv` moves to `quote_mock_kv["vault"]`.

  Everything else plans as before. This was checked by the POC test, not by a live plan: the POC is hibernated and this brief makes no Azure calls.
- **An organisation sandbox using Entra auth breaks two parts until code follows:**
  - The mock quote system calls the API with a Basic service user, and the load test signs in with the `devauth-users` Basic accounts. Both need an Entra client-credentials token. That is a follow-up in `src/` and `infra/loadtest/`, out of this brief's scope.
  - The Event Grid subscription to the mock still carries the mock's function key in its URL. Entra-authenticated delivery (`active_directory_tenant_id` / `active_directory_app_id_or_uri`) needs the mock to validate Event Grid's token, which is a code change in `src/LossRun.QuoteSystemMock`. Deferred (brief 30 item 50); the key is a generated 48-character secret that lives in Key Vault and the state only.
- **Entra delivery to the quote system: the identity team's interface** (brief 31 Answers 4; not built until asked). In the organisation the directory team owns the two Entra steps. Terraform only takes their results, as two variables:

  | Variable | What the identity team creates | Used for |
  |---|---|---|
  | `quote_receiver_app_id_uri` | An app registration for the receiver, with identifier URI `api://<app id>` and the app role `AzureEventGridSecureWebhookSubscriber` (member type Application) | The Event Grid subscription's `azure_active_directory_app_id_or_uri`; the audience the receiver validates |
  | `quote_receiver_tenant_id` | Nothing new; the tenant of that registration | The subscription's `azure_active_directory_tenant_id`; the issuer the receiver validates |

  - **The identity team also assigns** the app role to Microsoft.EventGrid's first-party service principal. That step needs directory rights, so it is theirs, not Terraform's.
  - **Once both variables are set**, the subscription URL drops `?code=`, and the receiver validates issuer, audience, the `AzureEventGridSecureWebhookSubscriber` role, signature and lifetime.
  - **Until then**, the function key stays, as an accepted POC-only risk (`docs/poc-plan/closeout/06-rbac-configuration.md` §6.4).
- **Private endpoints remain a follow-up.** Foundry, Storage, SQL, Key Vault, Search and the Durable Task Scheduler stay on public endpoints with Entra-only data planes. The `TODO(private-endpoints brief)` markers in `ai.tf`, `sql.tf`, `storage.tf`, `monitoring.tf` and `orchestration.tf` are the work list. VNet integration for the Flex Consumption apps comes with it, and so does removing the SQL `AllowAzureServices` rule.
- **Diagnostics cost log volume.** The default daily cap (`log_analytics_daily_quota_gb = 1`) may be reached in an active sandbox. The example says where to raise it.
- **Making a subscription-level setting part of this stack again needs a new ADR.** That includes provider registration, Defender plans and policy assignments.
