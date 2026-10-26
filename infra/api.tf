# Brief 07: API configuration for the Function App — demo-host auth users and the webhook HMAC secret in Key Vault
# (read by the app through Key Vault references with its managed identity), and a dev task hub for local runs.

resource "azapi_resource" "task_hub_dev" {
  type      = "Microsoft.DurableTask/schedulers/taskHubs@2025-11-01"
  name      = var.task_hub_dev_name
  parent_id = azapi_resource.dts.id

  body = {
    properties = {}
  }
}

# LOSSRUN_DEV_AUTH=basic users for the external demo host (brief 07 §2). Passwords are generated here and live only in
# Key Vault (and the remote state, which is never committed). Read them with:
#   az keyvault secret show --vault-name <kv> --name devauth-users --query value -o tsv
resource "random_password" "dev_auth" {
  for_each = toset(var.dev_auth_users)

  length  = 24
  special = false
}

resource "azurerm_key_vault_secret" "dev_auth_users" {
  name         = "devauth-users"
  key_vault_id = azurerm_key_vault.poc.id
  # Brief 25: plus the mock quote system's service user, role QuoteSystem (read-only: quote payload, manifests, integration status).
  value = join(";", concat([for user in var.dev_auth_users :
    "${lower(user)}:${random_password.dev_auth[user].result}:${user}"
  ], ["quotesystem:${random_password.quote_system_api.result}:QuoteSystem"]))
  content_type = "name:password:Role;…"

  depends_on = [azurerm_role_assignment.user_kv_secrets_officer]
}

resource "random_password" "webhook" {
  length  = 40
  special = false
}

resource "azurerm_key_vault_secret" "webhook_secret" {
  name         = "webhook-secret"
  key_vault_id = azurerm_key_vault.poc.id
  value        = random_password.webhook.result
  content_type = "HMAC-SHA256 key for X-LossRun-Signature"

  depends_on = [azurerm_role_assignment.user_kv_secrets_officer]
}

# The deploying user writes the two secrets above (the Function App only reads: Key Vault Secrets User, rbac.tf).
resource "azurerm_role_assignment" "user_kv_secrets_officer" {
  scope                = azurerm_key_vault.poc.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

locals {
  api_app_settings = {
    "Models__LearnVersion"                 = local.effective_learn_model_version
    "Models__DeltaVersion"                 = var.delta_model_version
    "Formats__Container"                   = "formats"
    "ContentUnderstanding__InlineMaxPages" = tostring(var.cu_inline_max_pages)
    "Review__TimeoutDays"                  = tostring(var.review_timeout_days)
    "Quotes__ApplicantMayCreate"           = tostring(coalesce(var.applicant_may_create_quotes, !local.org_posture)) # brief 32 Answers 4
    "Cu__DefaultsHash"                     = sha256(jsonencode(local.cu_model_deployments))                          # brief 34 A1.2: the mapping terraform_data.cu_defaults sends; part of the CU reuse key
    "Auth__Mode"                           = local.api_auth_mode
    "DevAuth__Users"                       = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.dev_auth_users.versionless_id})"
    "Webhook__Url"                         = "https://${local.names.function_app}.azurewebsites.net/api/v1/webhooks/test"
    "Webhook__Secret"                      = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.webhook_secret.versionless_id})"
  }
  # Brief 30 item 10: basic only in the poc environment (variable validation); entra elsewhere, with the audience from
  # the app registration this stack creates (create_entra_app) or the identity team's (auth_audience / auth_tenant_id).
  api_auth_mode = coalesce(var.api_auth_mode, var.environment == "poc" ? "basic" : "entra")

  entra_app_settings = var.create_entra_app ? {
    "Auth__TenantId" = data.azurerm_client_config.current.tenant_id
    "Auth__Audience" = azuread_application.api[0].client_id
    } : var.auth_audience == null ? {} : {
    "Auth__TenantId" = coalesce(var.auth_tenant_id, data.azurerm_client_config.current.tenant_id)
    "Auth__Audience" = var.auth_audience
  }
}
