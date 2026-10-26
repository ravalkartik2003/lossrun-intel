# Foundry (AIServices) resource, project, model deployments, CU defaults.

resource "azurerm_cognitive_account" "ai" {
  name                          = local.names.ai
  resource_group_name           = azurerm_resource_group.poc.name
  location                      = azurerm_resource_group.poc.location
  kind                          = "AIServices"
  sku_name                      = "S0"
  custom_subdomain_name         = local.names.ai
  project_management_enabled    = true
  local_auth_enabled            = false # keyless: Entra ID only
  public_network_access_enabled = true
  # TODO(private-endpoints brief): set public_network_access_enabled = false and add
  # azurerm_private_endpoint for privatelink.cognitiveservices.azure.com,
  # privatelink.openai.azure.com and privatelink.services.ai.azure.com.

  identity {
    type = "SystemAssigned"
  }

  tags = local.tags
}

resource "azurerm_cognitive_account_project" "poc" {
  name                 = local.names.ai_project
  cognitive_account_id = azurerm_cognitive_account.ai.id
  location             = azurerm_resource_group.poc.location
  display_name         = "Loss run POC"
  description          = "Workers' comp loss run ingestion POC"

  identity {
    type = "SystemAssigned"
  }

  tags = local.tags
}

# Deployments on one account conflict when created in parallel; azurerm takes a
# per-account lock in create/update, so for_each is safe. If a 409 "another
# operation is in progress" ever appears, re-run apply with -parallelism=1.
#
# gpt-5.2 is gated by deploy_gpt52 (ADR 0004: deployment blocked by Azure risk
# check until support clears it).
locals {
  model_deployments = {
    for k, d in var.model_deployments : k => d
    if d.model_name != "gpt-5.2" || var.deploy_gpt52
  }

  # Brief 30 item 10: the learn path falls back to the delta model when the learn model's deployment is listed in
  # model_deployments but gated off by deploy_gpt52 (ADR 0004). Everything that consumes the learn model (the app
  # setting, its model version, the CU completion alias, the output) reads these locals, so "learn_model = gpt-5.2,
  # deploy_gpt52 = false" plans instead of failing the precondition below. A learn_model that names no listed
  # deployment at all is a typo, not a gate: it still fails the precondition.
  learn_model_gated             = contains(keys(var.model_deployments), var.learn_model) && !contains(keys(local.model_deployments), var.learn_model)
  effective_learn_model         = local.learn_model_gated ? var.delta_model : var.learn_model
  effective_learn_model_version = local.learn_model_gated ? var.delta_model_version : var.learn_model_version
}

# Brief 12 section 5 / ADR 0013: the chat deployment's content filter. Default harm categories (prompt and completion,
# medium), plus Prompt Shields: user-prompt jailbreak and indirect attacks hidden in grounding data (document chunks)
# are blocked, not only annotated.
resource "azurerm_cognitive_account_rai_policy" "chat" {
  name                 = "lossrun-chat"
  cognitive_account_id = azurerm_cognitive_account.ai.id
  base_policy_name     = "Microsoft.DefaultV2"
  mode                 = "Default"

  dynamic "content_filter" {
    for_each = flatten([for f in ["Hate", "Sexual", "Violence", "Selfharm"] : [for src in ["Prompt", "Completion"] : { name = f, source = src }]])
    content {
      name               = content_filter.value.name
      source             = content_filter.value.source
      filter_enabled     = true
      block_enabled      = true
      severity_threshold = "Medium"
    }
  }

  content_filter {
    name               = "Jailbreak"
    source             = "Prompt"
    filter_enabled     = true
    block_enabled      = true
    severity_threshold = "High"
  }

  content_filter {
    name               = "Indirect Attack"
    source             = "Prompt"
    filter_enabled     = true
    block_enabled      = true
    severity_threshold = "High"
  }

  tags = local.tags
}

locals {
  rai_policies = { chat = azurerm_cognitive_account_rai_policy.chat.name }
}

resource "azurerm_cognitive_deployment" "model" {
  for_each = local.model_deployments

  name                   = each.key
  cognitive_account_id   = azurerm_cognitive_account.ai.id
  version_upgrade_option = "NoAutoUpgrade"
  rai_policy_name        = each.value.rai_policy == null ? null : local.rai_policies[each.value.rai_policy]

  model {
    format  = "OpenAI"
    name    = each.value.model_name
    version = each.value.model_version
  }

  sku {
    name     = each.value.sku_name
    capacity = each.value.capacity
  }
}

# Content Understanding resource defaults (data plane, so neither azurerm nor
# azapi can express it): PATCH /contentunderstanding/defaults maps model names and
# prebuilt aliases to our deployments. Source: CU "Model deployment options",
# ms.date 2026-09-15 (docs/architecture/azure-extraction-stack.md §CU model deployments).
locals {
  cu_endpoint = "https://${local.names.ai}.services.ai.azure.com"

  cu_model_deployments = merge(
    { for k, d in local.model_deployments : d.model_name => k if d.cu_default },
    {
      "prebuilt-analyzer-completion"      = local.effective_learn_model
      "prebuilt-analyzer-completion-mini" = var.delta_model
      "prebuilt-analyzer-embedding"       = var.embedding_model
    }
  )
}

resource "terraform_data" "cu_defaults" {
  triggers_replace = {
    endpoint    = local.cu_endpoint
    api_version = var.content_understanding_api_version
    mapping     = jsonencode(local.cu_model_deployments)
  }

  provisioner "local-exec" {
    interpreter = var.local_exec_interpreter
    command     = "${path.module}/scripts/set-cu-defaults.ps1"
    environment = {
      CU_ENDPOINT    = local.cu_endpoint
      CU_API_VERSION = var.content_understanding_api_version
      CU_MAPPING     = jsonencode({ modelDeployments = local.cu_model_deployments })
    }
  }

  lifecycle {
    precondition {
      condition     = alltrue([for m in [local.effective_learn_model, var.delta_model, var.embedding_model] : contains(keys(local.model_deployments), m)])
      error_message = "learn_model, delta_model and embedding_model must each name a deployment that is actually deployed (check deploy_gpt52)."
    }
  }

  depends_on = [
    azurerm_cognitive_deployment.model,
    azurerm_role_assignment.user,
  ]
}
