# Resource provider registration (brief 30 item 25, ADR 0027).
#
# Registration is a subscription-level, once-per-subscription setting, not part of this stack: an
# azurerm_resource_provider_registration fails when the provider is already registered (an organisation subscription
# usually has most of them) and unregisters it again on destroy (which would break anything else in the subscription
# that uses it). So the stack no longer manages registrations as resources:
#   - environment = "poc": resource_provider_registrations = "none" (versions.tf); the POC subscription's providers were
#     registered once (brief 01/02) and stay registered.
#   - any other environment: the provider registers what is missing at init (versions.tf, local.provider_registrations)
#     and never unregisters. Or the subscription owner runs the documented pre-step once (infra/README.md
#     "Resource providers") and sets resource_provider_registrations = "none".
#
# The three registrations the POC state still holds are forgotten, not destroyed: the next POC plan shows
# "will no longer be managed by Terraform" for them and makes no change in Azure.

variable "resource_provider_registrations" {
  description = "azurerm provider registration set (none, legacy, core, extended, all). Null = none in the poc environment, core elsewhere. Set none when the subscription owner has run the pre-step (infra/README.md \"Resource providers\")."
  type        = string
  default     = null
}

locals {
  # Every namespace this stack creates resources in. "core" covers the common ones; the list makes the rest explicit
  # (Durable Task Scheduler, cost exports, load testing and the rest are not in any built-in set).
  stack_resource_providers = [
    "Microsoft.AlertsManagement",
    "Microsoft.CognitiveServices",
    "Microsoft.Consumption",
    "Microsoft.CostManagement",
    "Microsoft.CostManagementExports",
    "Microsoft.DurableTask",
    "Microsoft.EventGrid",
    "Microsoft.Insights",
    "Microsoft.KeyVault",
    "Microsoft.LoadTestService",
    "Microsoft.ManagedIdentity",
    "Microsoft.OperationalInsights",
    "Microsoft.Search",
    "Microsoft.ServiceBus",
    "Microsoft.Sql",
    "Microsoft.Storage",
    "Microsoft.Web",
  ]

  provider_registration_set = coalesce(var.resource_provider_registrations, var.environment == "poc" ? "none" : "core")

  provider_registrations = {
    set = local.provider_registration_set
    extra = local.provider_registration_set == "none" ? [] : concat(
      local.stack_resource_providers,
      var.defender_ai_enabled ? ["Microsoft.Security"] : [],
    )
  }
}

removed {
  from = azurerm_resource_provider_registration.durable_task
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_resource_provider_registration.cost_exports
  lifecycle {
    destroy = false
  }
}

removed {
  from = azurerm_resource_provider_registration.load_testing
  lifecycle {
    destroy = false
  }
}
