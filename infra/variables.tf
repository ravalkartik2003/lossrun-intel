variable "subscription_id" {
  description = "Target subscription. Set it in the git-ignored local.auto.tfvars (or ARM_SUBSCRIPTION_ID) so plans never prompt; never commit it."
  type        = string
  default     = null
}

variable "location" {
  description = "Azure region for every POC resource (ADR 0001)."
  type        = string
  default     = "eastus2"
}

variable "name_prefix" {
  description = "Prefix for resource names."
  type        = string
  default     = "lossrun"
}

variable "environment" {
  description = "Environment name, used in resource names and the env tag."
  type        = string
  default     = "poc"
}

variable "owner" {
  description = "Value of the owner tag."
  type        = string
  default     = "poc-owner"
}

# ---- Models (names are configuration; see CLAUDE.md) ----------------------

variable "model_deployments" {
  description = <<-EOT
    Model deployments on the Foundry resource. Key = deployment name (equal to the
    model name). All Data Zone Standard; capacity is in units of 1K TPM.
    Sized from the East US 2 quota check made when the POC was provisioned (ADR 0001).
  EOT
  type = map(object({
    model_name    = string
    model_version = string
    sku_name      = optional(string, "DataZoneStandard")
    capacity      = number
    # Content-filter policy (key of local.rai_policies); null = the account default (Microsoft.DefaultV2).
    rai_policy = optional(string)
    # Whether CU's model-name -> deployment defaults may point at it (false for a second deployment of the same model).
    cu_default = optional(bool, true)
  }))
  default = {}

  validation {
    condition     = alltrue([for d in values(var.model_deployments) : d.sku_name == "DataZoneStandard"])
    error_message = "Pipeline deployments must be Data Zone Standard (never Global Standard)."
  }
}

variable "deploy_gpt52" {
  description = "Deploy the gpt-5.2 entry of model_deployments. False while an Azure risk check blocks it (ADR 0004)."
  type        = bool
  default     = false
}

variable "learn_model" {
  description = "Deployment used by the learn path and CU prebuilt-analyzer-completion."
  type        = string
}

variable "delta_model" {
  description = "Deployment used by delta repair / classification and CU prebuilt-analyzer-completion-mini."
  type        = string
}

variable "chat_model" {
  description = "Deployment used by the chat agent (brief 12, ADR 0013): its own deployment of the delta model, so chat cannot throttle the learn path and Prompt Shields can block document attacks without touching extraction."
  type        = string
  default     = "chat-gpt-5.4-mini"
}

variable "embedding_model" {
  description = "Embedding deployment, also CU prebuilt-analyzer-embedding."
  type        = string
}

variable "content_understanding_api_version" {
  description = "CU data-plane API version used to set resource defaults."
  type        = string
  default     = "2025-11-01"
}

# ---- Data -----------------------------------------------------------------

variable "storage_containers" {
  description = "Blob containers without immutability. 'uploads' is created separately with version-level immutability; 'deployments' holds the Flex Consumption package; 'formats-test' isolates the opt-in Azure tests from the live format registry."
  type        = list(string)
  default     = ["extractions", "formats", "formats-test", "evals", "deployments"]
}

variable "uploads_immutability_days" {
  description = "Unlocked time-based retention on the uploads container (POC: short)."
  type        = number
  default     = 1
}

variable "blob_soft_delete_days" {
  type    = number
  default = 7
}

variable "sql_location" {
  description = "Region for the SQL server and database (ADR 0003: Central US; provisioning is restricted in East US 2)."
  type        = string
  default     = "centralus"
}

variable "sql_max_vcores" {
  description = "General Purpose serverless Gen5 max vCores."
  type        = number
  default     = 2
}

variable "sql_use_free_limit" {
  description = "Azure SQL free offer (100K vCore-s, 32 GB data + 32 GB backup per month). Creation-time only; overage billed (BillOverUsage)."
  type        = bool
  default     = true
}

variable "sql_min_capacity" {
  type    = number
  default = 0.5
}

variable "sql_auto_pause_minutes" {
  type    = number
  default = 60
}

variable "sql_max_size_gb" {
  type    = number
  default = 10
}

variable "sql_admin_login" {
  description = "Display login of the Entra SQL admin: the deploying user, or a group with sql_admin_object_id set (null object id = the signed-in identity)."
  type        = string
}

variable "sql_client_ip_allowlist" {
  description = "Public IPs allowed through the SQL firewall (developer machines). Set in a local *.auto.tfvars, not in git."
  type        = map(string)
  default     = {}
}

# ---- Compute / orchestration ----------------------------------------------

variable "dts_sku" {
  type    = string
  default = "Consumption"
}

variable "task_hub_name" {
  type    = string
  default = "lossrun"
}

variable "function_instance_memory_mb" {
  type    = number
  default = 2048
}

variable "function_max_instances" {
  type    = number
  default = 40
}

# ---- Search ---------------------------------------------------------------

variable "search_enabled" {
  description = "Create the AI Search service. A subscription may hold only one free-tier service; use basic where it is taken."
  type        = bool
  default     = false
}

variable "search_sku" {
  type    = string
  default = "free"
}

variable "search_location" {
  description = "Region of the Search service (null = var.location). Centralus since 2026-10-23 (East US 2 out of capacity)."
  type        = string
  default     = null
}

variable "search_index" {
  description = "Chat index name (definition: infra/search/lossrun-docs.index.json)."
  type        = string
  default     = "lossrun-docs"
}

variable "chat_workspace_ttl_hours" {
  description = "Lifetime of an ad-hoc chat workspace; expired workspaces are removed from the index by a timer (brief 12 section 1)."
  type        = number
  default     = 72
}

# ---- Monitoring / cost ----------------------------------------------------

variable "hibernate" {
  description = "Brief 24: stop standing cost between work sessions. When true, AI Search, Service Bus, Event Grid and Azure Load Testing are not created, demo_mode is forced off (no always-ready instances) and the Log Analytics daily cap drops to the minimum. SQL (serverless, auto-pause), Blob, Key Vault, the Function Apps, the model deployments and both Static Web Apps stay. Wake: runbook section Hibernate."
  type        = bool
  default     = false
}

variable "budget_monthly_usd" {
  description = "Brief 24: Cost Management budget on the resource group, with an alert to the subscription owners at 100 % of actual spend and 80 % forecast."
  type        = number
  default     = 10
}

variable "log_analytics_daily_quota_gb" {
  description = "Daily ingestion cap to keep standing cost down."
  type        = number
  default     = 1
}

variable "cost_export_start" {
  description = "First day of the daily Cost Management export (RFC3339, must be in the future at apply time)."
  type        = string
  default     = "2026-10-23T00:00:00Z"
}

variable "cost_export_end" {
  type    = string
  default = "2027-10-23T00:00:00Z"
}

# ---- Local-exec -----------------------------------------------------------

variable "local_exec_interpreter" {
  description = "Interpreter for the CU defaults script (Windows PowerShell by default; use [\"pwsh\",\"-NoProfile\",\"-File\"] elsewhere)."
  type        = list(string)
  default     = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File"]
}

# ---------------------------------------------------------------- brief 07: API

variable "task_hub_dev_name" {
  description = "Second task hub on the same scheduler for local runs (brief 07 §5)."
  type        = string
  default     = "lossrun-dev"
}

variable "learn_model_version" {
  description = "Model version behind learn_model (recorded on learned specs and usage events; the API returns only the deployment name)."
  type        = string
  default     = "2026-03-17"
}

variable "delta_model_version" {
  description = "Model version behind delta_model."
  type        = string
  default     = "2026-03-17"
}

variable "cu_inline_max_pages" {
  description = "Use Content Understanding inline (sync, 2026-06-01-preview) analyze for documents up to this many pages; 0 = async only (brief 04 Answers 4: measure, do not build on the preview)."
  type        = number
  default     = 0
}

variable "applicant_may_create_quotes" {
  description = "Brief 32 Answers 4 (app setting Quotes__ApplicantMayCreate): may an Applicant's upload create a quote nothing names yet? Null: true in the poc environment (a demo convenience), false elsewhere (the quote is the quote system's key and exists before any upload)."
  type        = bool
  default     = null
}

variable "review_timeout_days" {
  description = "Durable timer raced with the review decision."
  type        = number
  default     = 7
}

variable "api_auth_mode" {
  description = "entra (Entra ID bearer, needs Auth__TenantId/Auth__Audience from an app registration) or basic (the POC demo host only, users in Key Vault). Null = basic in the poc environment, entra everywhere else."
  type        = string
  default     = null

  validation {
    condition     = var.api_auth_mode == null || contains(["entra", "basic"], coalesce(var.api_auth_mode, "entra"))
    error_message = "api_auth_mode must be entra or basic."
  }

  # Brief 30 item 10: HTTP Basic demo users on a public endpoint are a POC demo-host convenience, never an organisation
  # environment's sign-in (ADR 0027).
  validation {
    condition     = var.api_auth_mode != "basic" || var.environment == "poc"
    error_message = "api_auth_mode = \"basic\" is allowed only in the poc environment; set api_auth_mode = \"entra\" with create_entra_app = true or auth_audience (ADR 0027)."
  }
}

variable "auth_tenant_id" {
  description = "Entra tenant of the API app registration when the identity team creates it (create_entra_app = false). Null = the deploying identity's tenant."
  type        = string
  default     = null
}

variable "auth_audience" {
  description = "Application ID URI or client id of the API app registration the identity team created (Auth__Audience), when create_entra_app = false. Required for api_auth_mode = entra without create_entra_app."
  type        = string
  default     = null

  validation {
    condition     = var.auth_audience != null || var.create_entra_app || coalesce(var.api_auth_mode, var.environment == "poc" ? "basic" : "entra") != "entra"
    error_message = "Entra auth needs an audience: set create_entra_app = true, or auth_audience (and auth_tenant_id) from the identity team's app registration."
  }
}

variable "dev_auth_users" {
  description = "Roles that get one basic-auth demo user each (user name = lower-case role)."
  type        = list(string)
  default     = ["Applicant", "Underwriter", "UWManager", "Admin", "Engineer"]
}

# ---------------------------------------------------------------- 07 Answers 2-3

variable "demo_mode" {
  description = "Demo days: one always-ready Function instance each for HTTP and Durable (removes the ~15 s cold start; ~$0.058/h at 2 GB, Always Ready Baseline)."
  type        = bool
  default     = false
}

variable "create_entra_app" {
  description = "Create the API app registration (four app roles, SPA redirect) and point Auth__TenantId/Auth__Audience at it. Needs Application Administrator (or app-creation rights) in the tenant; the client's identity team owns this in their tenant."
  type        = bool
  default     = false
}

variable "ui_redirect_uris" {
  description = "SPA redirect URIs of the workbench SPA client on the API app registration."
  type        = list(string)
  default     = ["http://localhost:5173/"]
}

variable "records_store" {
  description = "System of record for manifests, results, reviews, usage and audit: \"sql\" (Azure SQL, brief 09) or \"blob\" (brief 07 layout). Deploy the db/changelog first (lossrun-db deploy)."
  type        = string
  default     = "sql"

  validation {
    condition     = contains(["sql", "blob"], var.records_store)
    error_message = "records_store must be sql or blob."
  }
}

variable "ledger_digest_immutability_days" {
  description = "Time-based retention on the sqldbledgerdigests container. POC 7; production 2557 (7 years)."
  type        = number
  default     = 7
}

variable "ledger_digest_immutability_locked" {
  description = "Lock the digest container's retention policy (irreversible: a locked policy can only be extended). POC false; production true (09 Answers 1)."
  type        = bool
  default     = false
}

variable "learn_max_concurrent" {
  description = "Concurrent learn/delta activities across all uploads (LearnGate). POC/demo: 1 at 100K TPM. Production onboarding: request 300K TPM, then 3 (08d burst test: 44 HTTP 429 at 3 / 100K)."
  type        = number
  default     = 1

  validation {
    condition     = var.learn_max_concurrent >= 1 && var.learn_max_concurrent <= 10
    error_message = "learn_max_concurrent must be between 1 and 10."
  }
}

variable "demo_custom_domain" {
  description = "Custom domain of the offline demo Static Web App (brief 13 addendum), e.g. lossrun-demo.example.com. Null until its CNAME (DNS only) points at output demo_default_host."
  type        = string
  default     = null
}

variable "web_location" {
  description = "Region of the Static Web App (its content is served from the global edge; this is where its metadata lives)."
  type        = string
  default     = "eastus2"
}

variable "web_preview_origins" {
  description = "Static Web App preview environment origins allowed by API CORS (e.g. the 'mock' environment), known after its first deploy."
  type        = list(string)
  default     = []
}

variable "web_extra_origins" {
  description = "Extra browser origins allowed to call the API. Null = the local Vite dev server (http://localhost:5173) in the poc environment, none elsewhere (brief 30 item 50)."
  type        = list(string)
  default     = null
}

# ------------------------------------------------------------------ brief 12b

variable "quote_command_queue" {
  description = "Service Bus queue for commands to the quote system (sessions by quoteId, duplicate detection, DLQ)."
  type        = string
  default     = "quote-commands"
}

variable "quote_mock_subscription" {
  description = "Create the Event Grid subscription to the mock quote system. false on the first apply: the mock's code must be deployed before the CloudEvents webhook handshake can succeed."
  type        = bool
  default     = true
}

variable "quote_mock_max_instances" {
  description = "Mock quote system scale-out limit."
  type        = number
  default     = 40
}

variable "defender_ai_enabled" {
  description = "Defender for Cloud AI threat protection plan on the subscription (12 Answers). Opt-in (brief 30 item 25, ADR 0027): the plan is a subscription-wide setting the subscription owner decides, and destroying this stack would switch it off for every AI resource in the subscription. The POC sets it in poc.tfvars."
  type        = bool
  default     = false
}

variable "diagnostics_enabled" {
  description = "Diagnostic settings (logs and metrics to the Log Analytics workspace) on Key Vault, Storage, SQL (with server auditing), Search, Service Bus, the Foundry resource and both Function Apps (brief 30 item 25). Null = on outside the poc environment, off in poc."
  type        = bool
  default     = null
}

variable "deployer_principal_type" {
  description = "Principal type of the identity running Terraform, for its role assignments. Null = derived: User when signed in through the Azure CLI or Azure PowerShell, ServicePrincipal otherwise (a pipeline)."
  type        = string
  default     = null

  validation {
    condition     = var.deployer_principal_type == null || contains(["User", "ServicePrincipal", "Group"], coalesce(var.deployer_principal_type, "User"))
    error_message = "deployer_principal_type must be User, ServicePrincipal or Group."
  }
}

variable "sql_admin_object_id" {
  description = "Object id of the Entra SQL admin (a group, so the admin outlives one person). Null = the identity running Terraform. sql_admin_login is its display name."
  type        = string
  default     = null
}
