# Brief 30 items 10, 25, 50, 51: the POC values keep the POC's plan. Offline (mock providers, plan only, no sign-in):
#   terraform -chdir=infra init -backend=false
#   terraform -chdir=infra test -filter=tests/poc.tftest.hcl -var-file=environments/poc.tfvars

mock_provider "azurerm" {
  mock_data "azurerm_client_config" {
    defaults = {
      subscription_id = "00000000-0000-0000-0000-000000000001"
      tenant_id       = "00000000-0000-0000-0000-000000000002"
      object_id       = "00000000-0000-0000-0000-000000000003"
      client_id       = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
    }
  }
}

mock_provider "azapi" {
  mock_resource "azapi_resource" {
    defaults = {
      output = { properties = { endpoint = "https://dts.example.invalid" } }
    }
  }
}

mock_provider "azuread" {}
mock_provider "random" {}

run "poc_plan_unchanged" {
  command = plan

  # Brief 32 Answers 4: Applicants still create quotes by uploading in the POC demo.
  assert {
    condition     = local.api_app_settings["Quotes__ApplicantMayCreate"] == "true"
    error_message = "POC: an Applicant upload must still create a new quote."
  }

  # Item 10: deploy_gpt52 = false, learn_model = gpt-5.4-mini: the learn path is gpt-5.4-mini, version 2026-03-17.
  assert {
    condition     = local.effective_learn_model == "gpt-5.4-mini" && local.effective_learn_model_version == "2026-03-17"
    error_message = "POC learn model changed."
  }
  assert {
    condition     = local.cu_model_deployments["prebuilt-analyzer-completion"] == "gpt-5.4-mini"
    error_message = "POC CU completion alias changed."
  }
  # Item 10: the POC keeps Basic auth on the demo host.
  assert {
    condition     = local.api_auth_mode == "basic" && length(local.entra_app_settings) == 0
    error_message = "POC auth mode changed."
  }
  # Item 25: Defender stays on (set in poc.tfvars), diagnostics stay off, no provider registration.
  assert {
    condition     = length(azurerm_security_center_subscription_pricing.ai) == 1
    error_message = "POC Defender plan changed."
  }
  assert {
    condition     = length(azurerm_monitor_diagnostic_setting.poc) == 0 && length(azurerm_mssql_server_extended_auditing_policy.poc) == 0
    error_message = "POC gained diagnostic settings."
  }
  assert {
    condition     = local.provider_registrations.set == "none" && length(local.provider_registrations.extra) == 0
    error_message = "POC provider registration changed."
  }
  assert {
    condition     = local.deployer_principal_type == "User"
    error_message = "POC deployer principal type changed."
  }
  # Item 50: account-level scopes and the localhost origin stay in the POC.
  assert {
    condition     = keys(azurerm_role_assignment.quote_mock_kv) == ["vault"]
    error_message = "POC mock Key Vault assignment changed address."
  }
  assert {
    condition     = contains(keys(azurerm_role_assignment.func), "func|Storage Blob Data Owner") && length(azurerm_storage_container.functions_host) == 0
    error_message = "POC Function App storage role changed."
  }
  assert {
    condition     = local.names.key_vault == "<key-vault>-${local.suffix}"
    error_message = "POC Key Vault name changed."
  }
  assert {
    condition     = join(",", local.web_extra_origins) == "http://localhost:5173"
    error_message = "POC CORS origins changed."
  }
}
