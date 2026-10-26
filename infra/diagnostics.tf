# Brief 30 item 25 (ADR 0027): diagnostic settings to the Log Analytics workspace, so an organisation environment has
# the control-plane and data-plane audit trail its security team expects (who read a secret, who wrote a blob, who
# queried SQL, every model call). Behind diagnostics_enabled: on by default outside the poc environment, off in poc
# (the POC's daily cap of 1 GB, 0.023 GB hibernated, would be spent on logs, and the POC plan stays unchanged).
#
# Categories: "audit" where the service defines it (Key Vault AuditEvent, Blob StorageWrite/StorageDelete, SQL security
# audit events); "allLogs" for Search, Service Bus and the Foundry resource (RequestResponse + Audit); FunctionAppLogs for
# the two Function Apps. AllMetrics everywhere a resource has platform metrics. Log volume counts against
# log_analytics_daily_quota_gb; raise it in the organisation's tfvars if the cap is reached.

locals {
  diagnostics_on = coalesce(var.diagnostics_enabled, local.org_posture)

  diagnostic_targets = local.diagnostics_on ? merge(
    {
      key_vault = { id = azurerm_key_vault.poc.id, logs = ["audit"], categories = [], metrics = true }
      storage   = { id = azurerm_storage_account.poc.id, logs = [], categories = [], metrics = true }
      blob      = { id = "${azurerm_storage_account.poc.id}/blobServices/default", logs = ["audit"], categories = [], metrics = true }
      ai        = { id = azurerm_cognitive_account.ai.id, logs = ["allLogs"], categories = [], metrics = true }
      func      = { id = azurerm_function_app_flex_consumption.poc.id, logs = [], categories = ["FunctionAppLogs"], metrics = true }
      quote_mock = {
        id = azurerm_function_app_flex_consumption.quote_mock.id, logs = [], categories = ["FunctionAppLogs"], metrics = true
      }
      # SQL auditing lands through the master database's SQLSecurityAuditEvents (server auditing below).
      sql_audit = {
        id = "${azurerm_mssql_server.poc.id}/databases/master", logs = [], categories = ["SQLSecurityAuditEvents"], metrics = false
      }
    },
    local.search_on ? {
      search = { id = azurerm_search_service.poc[0].id, logs = ["allLogs"], categories = [], metrics = true }
    } : {},
    local.integration_on ? {
      service_bus = { id = azurerm_servicebus_namespace.poc[0].id, logs = ["allLogs"], categories = [], metrics = true }
    } : {},
  ) : {}
}

resource "azurerm_mssql_server_extended_auditing_policy" "poc" {
  count = local.diagnostics_on ? 1 : 0

  server_id              = azurerm_mssql_server.poc.id
  enabled                = true
  log_monitoring_enabled = true # to Log Analytics via the master database's diagnostic setting; no storage account
}

resource "azurerm_monitor_diagnostic_setting" "poc" {
  for_each = local.diagnostic_targets

  name                       = "${local.base}-diag"
  target_resource_id         = each.value.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.poc.id

  dynamic "enabled_log" {
    for_each = each.value.logs
    content {
      category_group = enabled_log.value
    }
  }

  dynamic "enabled_log" {
    for_each = each.value.categories
    content {
      category = enabled_log.value
    }
  }

  dynamic "enabled_metric" {
    for_each = each.value.metrics ? ["AllMetrics"] : []
    content {
      category = enabled_metric.value
    }
  }

  depends_on = [azurerm_mssql_server_extended_auditing_policy.poc]
}
