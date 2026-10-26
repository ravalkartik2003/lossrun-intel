# Daily actual-cost export for the resource group into evals/cost-export/, for
# reconciling per-document meters to the bill (docs/architecture/cost-tracking.md §5).
# azapi: azurerm_resource_group_cost_management_export has no managed identity,
# and the storage account has shared-key access disabled.

resource "azapi_resource" "cost_export" {
  type      = "Microsoft.CostManagement/exports@2023-07-01-preview"
  name      = local.names.cost_export
  parent_id = azurerm_resource_group.poc.id
  location  = azurerm_resource_group.poc.location

  identity {
    type = "SystemAssigned"
  }

  body = {
    properties = {
      format        = "Csv"
      partitionData = true
      schedule = {
        status     = "Active"
        recurrence = "Daily"
        recurrencePeriod = {
          from = var.cost_export_start
          to   = var.cost_export_end
        }
      }
      definition = {
        type      = "ActualCost"
        timeframe = "MonthToDate"
        dataSet = {
          granularity = "Daily"
        }
      }
      deliveryInfo = {
        destination = {
          type           = "AzureBlob"
          resourceId     = azurerm_storage_account.poc.id
          container      = "evals"
          rootFolderPath = "cost-export"
        }
      }
    }
  }

  response_export_values = ["identity.principalId"]

  depends_on = [
    azurerm_storage_container.data,
  ]
}

resource "azurerm_role_assignment" "cost_export" {
  scope                = azurerm_storage_account.poc.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azapi_resource.cost_export.identity[0].principal_id
  principal_type       = "ServicePrincipal"
}
