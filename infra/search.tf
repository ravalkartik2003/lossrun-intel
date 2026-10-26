# Azure AI Search for the chat service (brief 12, ADR 0002). Basic tier (decided 2026-10-23): a subscription holds at
# most one free-tier service, and the POC subscription's was already in use. The index is data plane: its definition is
# infra/search/lossrun-docs.index.json, created by "lossrun chat init-index" (RBAC, no keys).

resource "azurerm_search_service" "poc" {
  count = local.search_on ? 1 : 0 # brief 24: removed while hibernated

  name                         = local.names.search
  resource_group_name          = azurerm_resource_group.poc.name
  location                     = coalesce(var.search_location, azurerm_resource_group.poc.location)
  sku                          = var.search_sku
  local_authentication_enabled = false # RBAC-only data plane
  # Semantic ranker on the free plan (1,000 queries / month); billable tiers only.
  semantic_search_sku = var.search_sku == "free" ? null : "free"

  # Managed identity is not available on the free tier.
  dynamic "identity" {
    for_each = var.search_sku == "free" ? [] : [1]
    content {
      type = "SystemAssigned"
    }
  }

  tags = local.tags
}
