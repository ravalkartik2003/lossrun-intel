terraform {
  required_version = ">= 1.9"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.81"
    }
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.13"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.4"
    }
  }

  # Partial config; values in backend.hcl (created by infra/bootstrap). See README.
  backend "azurerm" {}
}

provider "azurerm" {
  # Subscription: var.subscription_id from the git-ignored local.auto.tfvars, else ARM_SUBSCRIPTION_ID
  # (null falls through to the environment). Never committed. See README "Subscription id".
  subscription_id     = var.subscription_id
  storage_use_azuread = true

  # Brief 30 item 25 (ADR 0027): poc = "none" (registered once, brief 01/02); elsewhere the provider registers the
  # missing ones at init and never unregisters (providers.tf).
  resource_provider_registrations = local.provider_registrations.set
  resource_providers_to_register  = local.provider_registrations.extra

  features {
    key_vault {
      purge_soft_delete_on_destroy = false
    }
    cognitive_account {
      purge_soft_delete_on_destroy = true
    }
    resource_group {
      prevent_deletion_if_contains_resources = true
    }
  }
}

provider "azapi" {
  subscription_id = var.subscription_id
}

# Only used when create_entra_app = true (entra.tf); signs in with the az CLI identity's tenant.
provider "azuread" {}
