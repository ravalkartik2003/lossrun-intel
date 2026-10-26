# Log Analytics + workspace-based Application Insights (Entra-only ingestion),
# Key Vault (RBAC mode).

resource "azurerm_log_analytics_workspace" "poc" {
  name                = local.names.log_analytics
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  sku                 = "PerGB2018"
  retention_in_days   = 30
  daily_quota_gb      = local.log_analytics_daily_quota_gb # brief 24: the minimum while hibernated
  tags                = local.tags
}

resource "azurerm_application_insights" "poc" {
  name                         = local.names.app_insights
  resource_group_name          = azurerm_resource_group.poc.name
  location                     = azurerm_resource_group.poc.location
  workspace_id                 = azurerm_log_analytics_workspace.poc.id
  application_type             = "web"
  local_authentication_enabled = false
  tags                         = local.tags
}

resource "azurerm_key_vault" "poc" {
  name                          = local.names.key_vault
  resource_group_name           = azurerm_resource_group.poc.name
  location                      = azurerm_resource_group.poc.location
  tenant_id                     = data.azurerm_client_config.current.tenant_id
  sku_name                      = "standard"
  rbac_authorization_enabled    = true
  soft_delete_retention_days    = 7
  purge_protection_enabled      = false
  public_network_access_enabled = true
  # TODO(private-endpoints brief): privatelink.vaultcore.azure.net endpoint.
  tags = local.tags
}
