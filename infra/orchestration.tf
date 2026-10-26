# Durable Task Scheduler (azapi: azurerm has no resource for it) and the Flex
# Consumption Function App (.NET 10 isolated, Linux, identity-only settings).

resource "azapi_resource" "dts" {
  type      = "Microsoft.DurableTask/schedulers@2025-11-01"
  name      = local.names.dts
  parent_id = azurerm_resource_group.poc.id
  location  = azurerm_resource_group.poc.location
  tags      = local.tags

  body = {
    properties = {
      # TODO(private-endpoints brief): restrict once VNet integration exists.
      ipAllowlist = ["0.0.0.0/0"]
      sku = {
        name = var.dts_sku
      }
    }
  }

  response_export_values = ["properties.endpoint"]
}

resource "azapi_resource" "task_hub" {
  type      = "Microsoft.DurableTask/schedulers/taskHubs@2025-11-01"
  name      = var.task_hub_name
  parent_id = azapi_resource.dts.id

  body = {
    properties = {}
  }
}

resource "azurerm_service_plan" "poc" {
  name                = local.names.plan
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  os_type             = "Linux"
  sku_name            = "FC1"
  tags                = local.tags
}

locals {
  dts_endpoint = azapi_resource.dts.output.properties.endpoint

  function_app_settings = merge(
    {
      # Host storage via identity (no connection string / keys). See
      # terraform_data.blank_webjobs_storage below for the plain AzureWebJobsStorage key.
      "AzureWebJobsStorage__accountName"          = azurerm_storage_account.poc.name
      "APPLICATIONINSIGHTS_AUTHENTICATION_STRING" = "Authorization=AAD"

      # Durable Functions on DTS.
      "DURABLE_TASK_SCHEDULER_CONNECTION_STRING" = "Endpoint=${local.dts_endpoint};Authentication=ManagedIdentity"
      "TASKHUB_NAME"                             = var.task_hub_name

      # Application configuration (model names are configuration, not code).
      "AzureAI__Endpoint"                = azurerm_cognitive_account.ai.endpoint
      "ContentUnderstanding__Endpoint"   = local.cu_endpoint
      "ContentUnderstanding__ApiVersion" = var.content_understanding_api_version
      "Models__Learn"                    = local.effective_learn_model
      "Models__Delta"                    = var.delta_model
      "Models__Embedding"                = var.embedding_model
      # Brief 30 item 22: usage is priced by model name; this maps each deployment to its model (deployments may be named apart).
      "Models__DeploymentModels" = join(";", [for k, d in local.model_deployments : "${k}=${d.model_name}"])
      "Storage__BlobEndpoint"    = azurerm_storage_account.poc.primary_blob_endpoint
      "Sql__ConnectionString"    = "Server=tcp:${azurerm_mssql_server.poc.fully_qualified_domain_name},1433;Database=${azapi_resource.sql_database.name};Authentication=Active Directory Default;Encrypt=True;"
      "KeyVault__Uri"            = azurerm_key_vault.poc.vault_uri
      # System of record (brief 09): "sql" = Azure SQL (ledger/temporal), "blob" = the brief-07 Blob layout.
      "Records__Store" = var.records_store
      # Concurrent learn/delta activities across uploads (LearnGate entity, brief 08c; 08d Answers 1): 1 for the POC at
      # 100K TPM; production onboarding requests 300K TPM first, then 3 (docs/operations/runbook.md).
      "Learn__MaxConcurrent" = tostring(var.learn_max_concurrent)
    },
    local.search_on ? {
      "Search__Endpoint" = "https://${azurerm_search_service.poc[0].name}.search.windows.net"
      "Search__Index"    = var.search_index
      "Search__Region"   = azurerm_search_service.poc[0].location
    } : {},
    {
      # Brief 12: chat agent deployment, indexing queue (the queue trigger's connection is identity-based), workspace TTL.
      "Models__Chat"                    = var.chat_model
      "Chat__IndexQueue"                = azurerm_storage_queue.chat_index.name
      "ChatIndexQueue__queueServiceUri" = azurerm_storage_account.poc.primary_queue_endpoint
      "Chat__WorkspaceTtlHours"         = tostring(var.chat_workspace_ttl_hours)
    },
    local.integration_app_settings,
    local.api_app_settings,
    local.entra_app_settings
  )
}

resource "azurerm_function_app_flex_consumption" "poc" {
  name                = local.names.function_app
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  service_plan_id     = azurerm_service_plan.poc.id

  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.poc.primary_blob_endpoint}deployments"
  storage_authentication_type = "SystemAssignedIdentity"

  runtime_name           = "dotnet-isolated"
  runtime_version        = "10.0"
  instance_memory_in_mb  = var.function_instance_memory_mb
  maximum_instance_count = var.function_max_instances
  https_only             = true

  webdeploy_publish_basic_authentication_enabled = false

  # 07 Answers 3: demo days keep one warm instance for HTTP and one for the Durable workers (no ~15 s cold start).
  # Billed as "Always Ready Baseline" per GB-second while idle (config/azure-prices.json); off outside demo days.
  dynamic "always_ready" {
    for_each = local.demo_on ? ["http", "durable"] : [] # brief 24: never while hibernated
    content {
      name           = always_ready.value
      instance_count = 1
    }
  }

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.poc.connection_string
    minimum_tls_version                    = "1.2"

    # Brief 11 / ADR 0012: the workbench (Static Web App) calls the API from the browser with an Authorization
    # header; only its origins are allowed. No credentials mode (no cookies): Basic / bearer headers only.
    cors {
      allowed_origins     = local.web_origins
      support_credentials = false
    }
  }

  app_settings = local.function_app_settings

  tags = local.tags

  depends_on = [azurerm_storage_container.data]
}

# azurerm 4.81 always writes AzureWebJobsStorage as a connection string (with an
# empty AccountKey when no key is given) on create and update, and hides it from
# app_settings on read, so it cannot be blanked declaratively without a perpetual
# diff. The Functions host prefers that string over AzureWebJobsStorage__accountName
# and would fail against a shared-key-disabled account, so blank it after every
# change to the app's settings. Read-back of an empty value matches config (no drift).
resource "terraform_data" "blank_webjobs_storage" {
  triggers_replace = {
    app      = azurerm_function_app_flex_consumption.poc.id
    settings = sha256(jsonencode(local.function_app_settings))
    # Any site update rewrites AzureWebJobsStorage, not only app-setting changes (brief 11: the CORS change did).
    cors = sha256(jsonencode(local.web_origins))
  }

  provisioner "local-exec" {
    interpreter = var.local_exec_interpreter
    command     = "${path.module}/scripts/blank-webjobs-storage.ps1"
    environment = {
      FUNC_RG   = azurerm_resource_group.poc.name
      FUNC_NAME = azurerm_function_app_flex_consumption.poc.name
    }
  }
}
