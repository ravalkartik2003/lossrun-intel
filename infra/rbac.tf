# Role assignments for the Function App identity and the deploying user (brief 02 item 9).

locals {
  principals = {
    func = azurerm_function_app_flex_consumption.poc.identity[0].principal_id
    user = data.azurerm_client_config.current.object_id
  }

  # role => scope, granted to both principals.
  common_roles = merge(
    {
      "Cognitive Services User"        = azurerm_cognitive_account.ai.id
      "Cognitive Services OpenAI User" = azurerm_cognitive_account.ai.id
      "Storage Blob Data Contributor"  = azurerm_storage_account.poc.id
      "Durable Task Data Contributor"  = azapi_resource.dts.id
      "Key Vault Secrets User"         = azurerm_key_vault.poc.id
      "Monitoring Metrics Publisher"   = azurerm_application_insights.poc.id
    },
    local.search_on ? {
      "Search Index Data Contributor" = azurerm_search_service.poc[0].id
      "Search Service Contributor"    = azurerm_search_service.poc[0].id
    } : {}
  )

  assignments = merge(
    { for role, scope in local.common_roles : "func|${role}" => { principal = "func", role = role, scope = scope } },
    { for role, scope in local.common_roles : "user|${role}" => { principal = "user", role = role, scope = scope } },
    # Functions host storage (leases, keys, deployment package) needs Owner, not Contributor. Brief 30 item 50: outside
    # the POC on the host's three containers only (storage.tf); the app's data access stays Blob Data Contributor above.
    local.org_posture ? {
      for c in concat(["deployments"], keys(local.functions_host_containers)) :
      "func|Storage Blob Data Owner|${c}" => { principal = "func", role = "Storage Blob Data Owner", scope = local.blob_container_ids[c] }
      } : {
      "func|Storage Blob Data Owner" = { principal = "func", role = "Storage Blob Data Owner", scope = azurerm_storage_account.poc.id }
    },
    {
      # Brief 12: the chat-index queue (send from activities, queue trigger) and the CLI's reindex.
      "func|Storage Queue Data Contributor" = { principal = "func", role = "Storage Queue Data Contributor", scope = azurerm_storage_account.poc.id }
      "user|Storage Queue Data Contributor" = { principal = "user", role = "Storage Queue Data Contributor", scope = azurerm_storage_account.poc.id }
    }
  )
}

resource "azurerm_role_assignment" "func" {
  for_each = { for k, v in local.assignments : k => v if v.principal == "func" }

  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = local.principals.func
  principal_type       = "ServicePrincipal"
}

resource "azurerm_role_assignment" "user" {
  for_each = { for k, v in local.assignments : k => v if v.principal == "user" }

  scope                = each.value.scope
  role_definition_name = each.value.role
  principal_id         = local.principals.user
  principal_type       = local.deployer_principal_type
}
