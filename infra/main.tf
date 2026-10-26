data "azurerm_client_config" "current" {}

locals {
  # Deterministic 6-char suffix for globally unique names (hash, not the id itself).
  suffix = substr(md5(data.azurerm_client_config.current.subscription_id), 0, 6)
  base   = "${var.name_prefix}-${var.environment}"

  # Brief 30 item 25 (ADR 0027): the organisation posture. Outside the poc environment Basic auth is refused, the API
  # does not allow the local dev-server origin by default, diagnostics are on, and identities get per-secret /
  # per-container scopes. The POC keeps its plan.
  org_posture = var.environment != "poc"

  # Key Vault names are 3-24 characters: "lossrun-sandbox-kv-<6>" is 25. Brief 30 item 10 (found by
  # tests/sandbox.tftest.hcl): longer environment names fall back to the dashless form; the POC name is unchanged.
  key_vault_name_dashed = "${local.base}-kv-${local.suffix}"
  key_vault_name        = length(local.key_vault_name_dashed) <= 24 ? local.key_vault_name_dashed : substr("${var.name_prefix}${var.environment}kv${local.suffix}", 0, 24)

  # The identity running Terraform: a user from the Azure CLI / Azure PowerShell, a service principal in a pipeline.
  # client_id is the first-party app id of the sign-in tool for a user. The POC is always the signed-in user.
  signin_tool_app_ids = ["04b07795-8ddb-461a-bbee-02f9e1bf7b46", "1950a258-227b-4e31-a9cf-717495945fc2"] # Azure CLI, Azure PowerShell
  deployer_principal_type = coalesce(
    var.deployer_principal_type,
    !local.org_posture || contains(local.signin_tool_app_ids, data.azurerm_client_config.current.client_id) ? "User" : "ServicePrincipal",
  )

  tags = {
    project = "${var.name_prefix}-${var.environment}"
    env     = var.environment
    owner   = var.owner
  }

  names = {
    resource_group  = "${local.base}-rg"
    ai              = "${local.base}-ai"
    ai_project      = "${local.base}-project"
    storage         = "${var.name_prefix}${var.environment}st${local.suffix}"
    sql_server      = "${local.base}-sql-${local.suffix}"
    sql_database    = "${var.name_prefix}${var.environment}"
    key_vault       = local.key_vault_name
    log_analytics   = "${local.base}-law"
    app_insights    = "${local.base}-appi"
    dts             = "${local.base}-dts"
    plan            = "${local.base}-plan"
    function_app    = "${local.base}-func-${local.suffix}"
    search          = "${local.base}-search"
    cost_export     = "${local.base}-daily-actual"
    static_web_app  = "${local.base}-web-${local.suffix}"
    static_web_demo = "${local.base}-demo-${local.suffix}"
    service_bus     = "${local.base}-sb-${local.suffix}"
    event_grid      = "${local.base}-events-${local.suffix}"
    quote_mock      = "${local.base}-qsmock-${local.suffix}"
    quote_mock_plan = "${local.base}-qsmock-plan"
    load_test       = "${local.base}-loadtest"
  }
}

resource "azurerm_resource_group" "poc" {
  name     = local.names.resource_group
  location = var.location
  tags     = local.tags
}
