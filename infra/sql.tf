# Azure SQL: GP serverless, auto-pause, Entra-only auth. Ledger tables are
# enabled per table by the application brief, not here.
# Region is var.sql_location (Central US, ADR 0003): SQL provisioning is
# restricted for this subscription in East US 2. Same resource group.

resource "azurerm_mssql_server" "poc" {
  name                          = local.names.sql_server
  resource_group_name           = azurerm_resource_group.poc.name
  location                      = var.sql_location
  version                       = "12.0"
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true
  # TODO(private-endpoints brief): privatelink.database.windows.net endpoint.

  azuread_administrator {
    login_username              = var.sql_admin_login
    object_id                   = coalesce(var.sql_admin_object_id, data.azurerm_client_config.current.object_id)
    tenant_id                   = data.azurerm_client_config.current.tenant_id
    azuread_authentication_only = true
  }

  identity {
    type = "SystemAssigned"
  }

  tags = local.tags
}

# azapi, not azurerm_mssql_database: the free offer (useFreeLimit) can only be set
# at creation and azurerm 4.81 has no argument for it. BillOverUsage keeps the
# database online after the monthly free vCore-seconds are used (report 02 Answer 3);
# Azure does not allow switching back to AutoPause afterwards.
resource "azapi_resource" "sql_database" {
  type      = "Microsoft.Sql/servers/databases@2025-01-01"
  name      = local.names.sql_database
  parent_id = azurerm_mssql_server.poc.id
  location  = var.sql_location
  tags      = local.tags

  body = {
    sku = {
      name     = "GP_S_Gen5"
      tier     = "GeneralPurpose"
      family   = "Gen5"
      capacity = var.sql_max_vcores
    }
    properties = {
      minCapacity                      = var.sql_min_capacity
      autoPauseDelay                   = var.sql_auto_pause_minutes
      maxSizeBytes                     = var.sql_max_size_gb * 1024 * 1024 * 1024
      requestedBackupStorageRedundancy = "Local"
      zoneRedundant                    = false
      useFreeLimit                     = var.sql_use_free_limit
      freeLimitExhaustionBehavior      = var.sql_use_free_limit ? "BillOverUsage" : null
    }
  }

  response_export_values = ["properties.useFreeLimit", "properties.freeLimitExhaustionBehavior"]
}

# 0.0.0.0 = "allow Azure services" (the Flex Consumption app has no fixed egress IP
# until VNet integration lands with the private-endpoints brief).
resource "azurerm_mssql_firewall_rule" "azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.poc.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_firewall_rule" "client" {
  for_each = var.sql_client_ip_allowlist

  name             = "client-${each.key}"
  server_id        = azurerm_mssql_server.poc.id
  start_ip_address = each.value
  end_ip_address   = each.value
}
