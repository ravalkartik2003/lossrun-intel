# Blob storage: versioning + soft delete, identity-only access, version-level
# immutability (unlocked, short retention) on uploads only.

resource "azurerm_storage_account" "poc" {
  name                            = local.names.storage
  resource_group_name             = azurerm_resource_group.poc.name
  location                        = azurerm_resource_group.poc.location
  account_kind                    = "StorageV2"
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  access_tier                     = "Hot"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  allow_nested_items_to_be_public = false
  public_network_access_enabled   = true
  # TODO(private-endpoints brief): privatelink.blob.core.windows.net endpoint, then disable public access.

  blob_properties {
    versioning_enabled = true

    delete_retention_policy {
      days = var.blob_soft_delete_days
    }

    container_delete_retention_policy {
      days = var.blob_soft_delete_days
    }
  }

  tags = local.tags
}

resource "azurerm_storage_container" "data" {
  for_each = toset(var.storage_containers)

  name                  = each.value
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

# Brief 30 item 50: outside the POC the Function App identities get Storage Blob Data Owner per container, not on the
# account, so they can no longer create the Functions host's own containers; Terraform creates them up front. (The POC's
# host created them itself and its identities keep the account-level role.)
locals {
  functions_host_containers = local.org_posture ? { "azure-webjobs-hosts" = true, "azure-webjobs-secrets" = true } : {}
}

resource "azurerm_storage_container" "functions_host" {
  for_each = local.functions_host_containers

  name                  = each.key
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

locals {
  # Resource manager ids of the containers role assignments may be scoped to.
  blob_container_ids = merge(
    { for name, c in azurerm_storage_container.data : name => c.id },
    { for name, c in azurerm_storage_container.quote_mock : name => c.id },
    { for name, c in azurerm_storage_container.functions_host : name => c.id },
  )
}

# azurerm_storage_container cannot enable version-level immutability, so azapi.
resource "azapi_resource" "uploads_container" {
  type      = "Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01"
  name      = "uploads"
  parent_id = "${azurerm_storage_account.poc.id}/blobServices/default"

  body = {
    properties = {
      publicAccess = "None"
      immutableStorageWithVersioning = {
        enabled = true
      }
    }
  }
}

resource "azurerm_storage_container_immutability_policy" "uploads" {
  storage_container_resource_manager_id = azapi_resource.uploads_container.id
  immutability_period_in_days           = var.uploads_immutability_days
  protected_append_writes_enabled       = false
  locked                                = false
}

# Brief 12 section 1: chat indexing runs off the pipeline through this queue (one message per upload; the indexer is
# idempotent by chunk id), so a slow embedding call never holds up extraction.
resource "azurerm_storage_queue" "chat_index" {
  name               = "chat-index"
  storage_account_id = azurerm_storage_account.poc.id
}
