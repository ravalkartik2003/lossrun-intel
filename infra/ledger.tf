# Azure SQL ledger: automatic digest storage to immutable Blob (brief 09 §1 0009, §7).
# Azure SQL uploads a database digest about every 30 s while there are ledger transactions
# (https://learn.microsoft.com/sql/relational-databases/security/ledger/ledger-digest-management) to the container
# "sqldbledgerdigests" of the endpoint below. The container is created here so its time-based retention policy
# (protected append writes allowed; unlocked 7 days for the POC, locked 7 years in production: see README) exists
# before the first digest.
# Ledger tables themselves are per table in the db/changelog scripts; the database is not a ledger database.

resource "azurerm_storage_container" "ledger_digests" {
  name                  = "sqldbledgerdigests"
  storage_account_id    = azurerm_storage_account.poc.id
  container_access_type = "private"
}

resource "azurerm_storage_container_immutability_policy" "ledger_digests" {
  storage_container_resource_manager_id = azurerm_storage_container.ledger_digests.id
  immutability_period_in_days           = var.ledger_digest_immutability_days
  protected_append_writes_enabled       = true
  locked                                = var.ledger_digest_immutability_locked
}

# The SQL logical server writes the digests with its system-assigned identity.
resource "azurerm_role_assignment" "sql_digest_writer" {
  scope                = azurerm_storage_container.ledger_digests.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_mssql_server.poc.identity[0].principal_id
}

# No azurerm argument exists for automatic digest storage. Azure creates the ARM child resource "current" (disabled)
# with every database, so it is updated in place rather than created.
resource "azapi_update_resource" "ledger_digest_uploads" {
  type      = "Microsoft.Sql/servers/databases/ledgerDigestUploads@2023-08-01"
  name      = "current"
  parent_id = azapi_resource.sql_database.id

  body = {
    properties = {
      digestStorageEndpoint = azurerm_storage_account.poc.primary_blob_endpoint
    }
  }

  depends_on = [azurerm_role_assignment.sql_digest_writer, azurerm_storage_container_immutability_policy.ledger_digests]
}
