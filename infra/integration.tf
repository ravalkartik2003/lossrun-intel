# Brief 12b: quote-system integration, load testing, Defender for AI.
#
# Commands (push flags / annotations / follow-ups / memo link / quote payload v2) leave through the transactional
# outbox (dbo.Outbox) to a Service Bus queue with sessions (one per quote, FIFO), duplicate detection (MessageId =
# outbox id) and a dead-letter queue; a dispatcher function calls the quote system with Idempotency-Key = outbox id.
# Notifications (upload.completed, review.decided, format.learned) go out as CloudEvents on an Event Grid topic,
# dead-lettered to Blob. The quote system is a mock Function App shaped like Guidewire Cloud API
# (docs/analytics/underwriter-analytics.md Key Question 6).

# Microsoft.LoadTestService registration: providers.tf / versions.tf (brief 30 item 25).

# ------------------------------------------------------------------ Service Bus

resource "azurerm_servicebus_namespace" "poc" {
  count = local.integration_on ? 1 : 0 # brief 24: removed while hibernated

  name                = local.names.service_bus
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  sku                 = "Standard" # sessions and duplicate detection need Standard (Basic has neither)
  local_auth_enabled  = false      # Entra only
  minimum_tls_version = "1.2"
  tags                = local.tags
}

resource "azurerm_servicebus_queue" "quote_commands" {
  count = local.integration_on ? 1 : 0

  name         = var.quote_command_queue
  namespace_id = azurerm_servicebus_namespace.poc[0].id

  requires_session                        = true  # SessionId = quoteId: commands for one quote arrive in order
  requires_duplicate_detection            = true  # MessageId = outbox id: a re-published command is dropped
  duplicate_detection_history_time_window = "P1D" # the relay re-publishes within minutes; a day is ample
  max_delivery_count                      = 10
  lock_duration                           = "PT1M"
  default_message_ttl                     = "P7D"
  dead_lettering_on_message_expiration    = true
}

# ------------------------------------------------------------------ Event Grid

resource "azurerm_eventgrid_topic" "poc" {
  count = local.integration_on ? 1 : 0 # brief 24: removed while hibernated

  name                = local.names.event_grid
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  input_schema        = "CloudEventSchemaV1_0"
  local_auth_enabled  = false # publishers use Entra (EventGrid Data Sender)
  tags                = local.tags

  identity {
    type = "SystemAssigned" # writes dead letters to Blob
  }
}

resource "azurerm_storage_container" "eventgrid_deadletter" {
  name                  = "eventgrid-deadletter"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "eventgrid_deadletter" {
  count = local.integration_on ? 1 : 0

  scope                = azurerm_storage_account.poc.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_eventgrid_topic.poc[0].identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# The mock subscribes to every lossrun.* event. Created after the mock's code is deployed (the CloudEvents webhook
# handshake is an OPTIONS request to the endpoint), so it sits behind a flag for the first apply.
resource "azurerm_eventgrid_event_subscription" "quote_mock" {
  count = var.quote_mock_subscription && local.integration_on ? 1 : 0

  name                  = "quote-system-mock"
  scope                 = azurerm_eventgrid_topic.poc[0].id
  event_delivery_schema = "CloudEventSchemaV1_0"

  webhook_endpoint {
    url                               = "https://${azurerm_function_app_flex_consumption.quote_mock.default_hostname}/api/events?code=${random_password.quote_mock_key.result}"
    max_events_per_batch              = 1
    preferred_batch_size_in_kilobytes = 64
  }

  retry_policy {
    max_delivery_attempts = 30
    event_time_to_live    = 1440
  }

  dead_letter_identity {
    type = "SystemAssigned"
  }

  storage_blob_dead_letter_destination {
    storage_account_id          = azurerm_storage_account.poc.id
    storage_blob_container_name = azurerm_storage_container.eventgrid_deadletter.name
  }

  depends_on = [azurerm_role_assignment.eventgrid_deadletter, azapi_resource_action.quote_mock_key]
}

# ------------------------------------------------------------------ mock quote system

resource "azurerm_storage_container" "quote_mock" {
  for_each = toset(["deployments-qsmock", "quote-system-mock"])

  name                  = each.value
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_service_plan" "quote_mock" {
  name                = local.names.quote_mock_plan
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  os_type             = "Linux"
  sku_name            = "FC1" # Flex Consumption: one app per plan
  tags                = local.tags
}

# Brief 25: the mock quote system calls the loss-run API (GET quote payload and upload manifests) to fill its
# loss-information page. Its own Basic user, generated here, lives only in Key Vault (and the remote state).
resource "random_password" "quote_system_api" {
  length  = 24
  special = false
}

resource "azurerm_key_vault_secret" "quote_system_api_credential" {
  name         = "quote-system-api-credential"
  key_vault_id = azurerm_key_vault.poc.id
  value        = "quotesystem:${random_password.quote_system_api.result}"
  content_type = "name:password (HTTP Basic, the mock quote system's service user)"

  depends_on = [azurerm_role_assignment.user_kv_secrets_officer]
}

# The API's address for the mock, through Key Vault: the API's settings already name the mock, so the mock's settings cannot
# name the API resource without a cycle; its reference is built from the vault URI and this fixed secret name instead.
resource "azurerm_key_vault_secret" "quote_system_api_base" {
  name         = "quote-system-api-base"
  key_vault_id = azurerm_key_vault.poc.id
  value        = "https://${azurerm_function_app_flex_consumption.poc.default_hostname}/api/v1"
  content_type = "URL of the loss-run API, for the mock quote system"

  depends_on = [azurerm_role_assignment.user_kv_secrets_officer]
}

# Brief 30 item 50: outside the POC the mock reads only its two secrets (per-secret scope), not the whole vault.
resource "azurerm_role_assignment" "quote_mock_kv" {
  for_each = local.org_posture ? {
    api_base       = azurerm_key_vault_secret.quote_system_api_base.resource_versionless_id
    api_credential = azurerm_key_vault_secret.quote_system_api_credential.resource_versionless_id
    } : {
    vault = azurerm_key_vault.poc.id
  }

  scope                = each.value
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_function_app_flex_consumption.quote_mock.identity[0].principal_id
}

moved {
  from = azurerm_role_assignment.quote_mock_kv
  to   = azurerm_role_assignment.quote_mock_kv["vault"]
}

locals {
  quote_mock_settings = {
    "AzureWebJobsStorage__accountName"          = azurerm_storage_account.poc.name
    "APPLICATIONINSIGHTS_AUTHENTICATION_STRING" = "Authorization=AAD"
    "Storage__BlobEndpoint"                     = azurerm_storage_account.poc.primary_blob_endpoint
    "QuoteSystem__Container"                    = "quote-system-mock"
    "LossRunApi__BaseUrl"                       = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault.poc.vault_uri}secrets/quote-system-api-base/)"
    "LossRunApi__Credential"                    = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.quote_system_api_credential.versionless_id})"
  }
}

resource "azurerm_function_app_flex_consumption" "quote_mock" {
  name                = local.names.quote_mock
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  service_plan_id     = azurerm_service_plan.quote_mock.id

  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.poc.primary_blob_endpoint}deployments-qsmock"
  storage_authentication_type = "SystemAssignedIdentity"

  runtime_name           = "dotnet-isolated"
  runtime_version        = "10.0"
  instance_memory_in_mb  = 2048
  maximum_instance_count = var.quote_mock_max_instances
  https_only             = true

  webdeploy_publish_basic_authentication_enabled = false

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.poc.connection_string
    minimum_tls_version                    = "1.2"
  }

  app_settings = local.quote_mock_settings
  tags         = local.tags

  depends_on = [azurerm_storage_container.quote_mock]
}

resource "azurerm_role_assignment" "quote_mock" {
  for_each = merge(
    { "Monitoring Metrics Publisher" = azurerm_application_insights.poc.id },
    # Host storage + its record store. Brief 30 item 50: outside the POC per container (its package, its records and
    # the two Functions host containers, pre-created in storage.tf), not the whole account.
    local.org_posture ? {
      for c in concat(["deployments-qsmock", "quote-system-mock"], keys(local.functions_host_containers)) :
      "Storage Blob Data Owner|${c}" => local.blob_container_ids[c]
    } : { "Storage Blob Data Owner" = azurerm_storage_account.poc.id }
  )

  scope                = each.value
  role_definition_name = split("|", each.key)[0]
  principal_id         = azurerm_function_app_flex_consumption.quote_mock.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

resource "terraform_data" "blank_webjobs_storage_quote_mock" {
  triggers_replace = {
    app      = azurerm_function_app_flex_consumption.quote_mock.id
    settings = sha256(jsonencode(local.quote_mock_settings))
  }

  provisioner "local-exec" {
    interpreter = var.local_exec_interpreter
    command     = "${path.module}/scripts/blank-webjobs-storage.ps1"
    environment = {
      FUNC_RG   = azurerm_resource_group.poc.name
      FUNC_NAME = azurerm_function_app_flex_consumption.quote_mock.name
    }
  }
}

# The mock's endpoints use a function key (the quote system is "someone else's" API: a shared secret, held in Key Vault
# and read by the dispatcher through a Key Vault reference; Event Grid carries it in the subscription URL).
resource "random_password" "quote_mock_key" {
  length  = 48
  special = false
}

resource "azapi_resource_action" "quote_mock_key" {
  type        = "Microsoft.Web/sites/host/functionKeys@2024-04-01"
  resource_id = "${azurerm_function_app_flex_consumption.quote_mock.id}/host/default/functionKeys/lossrun"
  method      = "PUT"
  body = {
    properties = {
      name  = "lossrun"
      value = random_password.quote_mock_key.result
    }
  }

  depends_on = [terraform_data.blank_webjobs_storage_quote_mock]
}

resource "azurerm_key_vault_secret" "quote_mock_key" {
  name         = "quote-system-key"
  value        = random_password.quote_mock_key.result
  key_vault_id = azurerm_key_vault.poc.id
  content_type = "function key of the mock quote system (brief 12b)"

  # Brief 30 item 25: on a first apply the deploying identity must hold Secrets Officer before it can write.
  depends_on = [azurerm_role_assignment.user_kv_secrets_officer]
}

# ------------------------------------------------------------------ the main app's access

resource "azurerm_role_assignment" "func_integration" {
  for_each = local.integration_on ? {
    "Azure Service Bus Data Sender"   = azurerm_servicebus_namespace.poc[0].id
    "Azure Service Bus Data Receiver" = azurerm_servicebus_namespace.poc[0].id
    "EventGrid Data Sender"           = azurerm_eventgrid_topic.poc[0].id
  } : {}

  scope                = each.value
  role_definition_name = each.key
  principal_id         = azurerm_function_app_flex_consumption.poc.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# The operator peeks queues and the DLQ and publishes test events from the CLI.
resource "azurerm_role_assignment" "user_integration" {
  for_each = local.integration_on ? {
    "Azure Service Bus Data Owner" = azurerm_servicebus_namespace.poc[0].id
    "EventGrid Data Sender"        = azurerm_eventgrid_topic.poc[0].id
  } : {}

  scope                = each.value
  role_definition_name = each.key
  principal_id         = data.azurerm_client_config.current.object_id
  principal_type       = local.deployer_principal_type
}

locals {
  # Brief 24: while hibernated the outbox keeps its rows (nothing relays them) and no event is published; the quote
  # system settings stay (the mock app stays).
  integration_app_settings = merge(
    local.integration_on ? {
      "ServiceBus__fullyQualifiedNamespace" = "${azurerm_servicebus_namespace.poc[0].name}.servicebus.windows.net"
      "Integration__CommandQueue"           = azurerm_servicebus_queue.quote_commands[0].name
      "Integration__EventGridEndpoint"      = azurerm_eventgrid_topic.poc[0].endpoint
    } : {},
    {
      "QuoteSystem__BaseUrl" = "https://${azurerm_function_app_flex_consumption.quote_mock.default_hostname}/api"
      "QuoteSystem__Key"     = "@Microsoft.KeyVault(SecretUri=${azurerm_key_vault_secret.quote_mock_key.versionless_id})"
    }
  )
}

# ------------------------------------------------------------------ Azure Load Testing

resource "azurerm_load_test" "poc" {
  count = local.integration_on ? 1 : 0 # brief 24: removed while hibernated

  name                = local.names.load_test
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  description         = "Brief 12b: 25 concurrent underwriters against the deployed API"
  tags                = local.tags

  identity {
    type = "SystemAssigned" # reads the demo credentials from Key Vault at run time
  }
}

resource "azurerm_role_assignment" "load_test_secrets" {
  count = local.integration_on ? 1 : 0

  # Brief 30 item 50: outside the POC only the devauth-users secret the plan signs in with (infra/loadtest/config.yaml).
  scope                = local.org_posture ? azurerm_key_vault_secret.dev_auth_users.resource_versionless_id : azurerm_key_vault.poc.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_load_test.poc[0].identity[0].principal_id
  principal_type       = "ServicePrincipal"
}

# ------------------------------------------------------------------ Defender for AI (12 Answers)

# Subscription-level plan: threat protection (jailbreak / prompt-injection / data-leak alerts) on Azure AI services.
# A platform cost line in the TCO sheet, not per document.
resource "azurerm_security_center_subscription_pricing" "ai" {
  count = local.defender_on ? 1 : 0 # brief 24: kept while hibernated (subscription-wide plan; owner's decision 2026-10-25)

  tier          = "Standard"
  resource_type = "AI"
}
