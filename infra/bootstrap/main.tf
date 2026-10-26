# One-time bootstrap: storage account for the main stack's remote state.
# Uses local state (git-ignored); run once, then `terraform output -raw backend_hcl > ../backend.hcl`.

terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.81"
    }
  }
}

provider "azurerm" {
  storage_use_azuread             = true
  resource_provider_registrations = "none"
  features {}
}

variable "location" {
  type    = string
  default = "eastus2"
}

variable "name_prefix" {
  type    = string
  default = "lossrun"
}

variable "owner" {
  type    = string
  default = "poc-owner"
}

data "azurerm_client_config" "current" {}

locals {
  suffix = substr(md5(data.azurerm_client_config.current.subscription_id), 0, 6)
  tags = {
    project = "${var.name_prefix}-poc"
    env     = "poc"
    owner   = var.owner
    purpose = "terraform-state"
  }
}

resource "azurerm_resource_group" "state" {
  name     = "${var.name_prefix}-tfstate-rg"
  location = var.location
  tags     = local.tags
}

resource "azurerm_storage_account" "state" {
  name                            = "${var.name_prefix}tfstate${local.suffix}"
  resource_group_name             = azurerm_resource_group.state.name
  location                        = azurerm_resource_group.state.location
  account_kind                    = "StorageV2"
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  default_to_oauth_authentication = true
  allow_nested_items_to_be_public = false

  blob_properties {
    versioning_enabled = true
    delete_retention_policy {
      days = 30
    }
    container_delete_retention_policy {
      days = 30
    }
  }

  tags = local.tags
}

resource "azurerm_storage_container" "tfstate" {
  name                  = "tfstate"
  storage_account_id    = azurerm_storage_account.state.id
  container_access_type = "private"
}

# The backend authenticates with Entra ID (use_azuread_auth), so the deploying
# user needs data-plane access.
resource "azurerm_role_assignment" "state_user" {
  scope                = azurerm_storage_account.state.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = data.azurerm_client_config.current.object_id
}

output "backend_hcl" {
  value = <<-EOT
    resource_group_name  = "${azurerm_resource_group.state.name}"
    storage_account_name = "${azurerm_storage_account.state.name}"
    container_name       = "${azurerm_storage_container.tfstate.name}"
    key                  = "poc.tfstate"
    use_azuread_auth     = true
  EOT
}
