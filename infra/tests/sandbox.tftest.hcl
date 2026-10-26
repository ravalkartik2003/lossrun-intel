# Brief 30 items 10, 25, 50: the organisation sandbox example plans, and the posture refuses what it must.
# Offline (mock providers, plan only, no sign-in):
#   terraform -chdir=infra init -backend=false
#   terraform -chdir=infra test -filter=tests/sandbox.tftest.hcl -var-file=environments/sandbox.tfvars.example

mock_provider "azurerm" {
  mock_data "azurerm_client_config" {
    defaults = {
      subscription_id = "00000000-0000-0000-0000-000000000001"
      tenant_id       = "00000000-0000-0000-0000-000000000002"
      object_id       = "00000000-0000-0000-0000-000000000003"
      client_id       = "00000000-0000-0000-0000-000000000004" # a pipeline's service principal
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

# The example's <placeholders> that the provider validates as ids get syntactically valid stand-ins.
variables {
  sql_admin_object_id = "00000000-0000-0000-0000-000000000005"
  auth_tenant_id      = "00000000-0000-0000-0000-000000000002"
}

run "example_as_written" {
  command = plan

  assert {
    condition     = local.api_app_settings["Quotes__ApplicantMayCreate"] == "false"
    error_message = "Brief 32 Answers 4: outside the POC the quote exists before any upload; an Applicant must not create one."
  }

  assert {
    condition     = local.effective_learn_model == "gpt-5.2" && local.effective_learn_model_version == "2025-12-11"
    error_message = "With deploy_gpt52 = true the learn path must be gpt-5.2 at its own version."
  }
  assert {
    condition     = local.api_auth_mode == "entra" && local.entra_app_settings["Auth__Audience"] != null
    error_message = "The sandbox must sign in with Entra and carry the identity team's audience."
  }
  assert {
    condition     = length(azurerm_security_center_subscription_pricing.ai) == 0
    error_message = "Defender for AI must be opt-in."
  }
  assert {
    condition     = contains(keys(azurerm_monitor_diagnostic_setting.poc), "key_vault") && contains(keys(azurerm_monitor_diagnostic_setting.poc), "sql_audit") && length(azurerm_mssql_server_extended_auditing_policy.poc) == 1
    error_message = "Diagnostics must be on outside poc."
  }
  assert {
    condition     = local.provider_registrations.set == "core" && contains(local.provider_registrations.extra, "Microsoft.DurableTask")
    error_message = "Missing providers must be registered outside poc."
  }
  assert {
    condition     = local.deployer_principal_type == "ServicePrincipal"
    error_message = "A pipeline identity must be assigned as a service principal."
  }
  assert {
    condition     = toset(keys(azurerm_role_assignment.quote_mock_kv)) == toset(["api_base", "api_credential"])
    error_message = "The mock must read its two secrets only."
  }
  assert {
    condition     = !contains(keys(azurerm_role_assignment.func), "func|Storage Blob Data Owner") && contains(keys(azurerm_role_assignment.func), "func|Storage Blob Data Owner|deployments")
    error_message = "The Function App's Blob Data Owner must be per container."
  }
  assert {
    condition     = length(local.web_extra_origins) == 0
    error_message = "No localhost origin outside poc."
  }
}

run "gpt52_quota_pending_falls_back" {
  command = plan

  variables {
    deploy_gpt52 = false
  }

  assert {
    condition     = local.effective_learn_model == "gpt-5.4-mini" && local.effective_learn_model_version == "2026-03-17"
    error_message = "With deploy_gpt52 = false the learn path must fall back to the delta model and its version."
  }
  assert {
    condition     = local.cu_model_deployments["prebuilt-analyzer-completion"] == "gpt-5.4-mini" && !contains(keys(azurerm_cognitive_deployment.model), "gpt-5.2")
    error_message = "The CU completion alias must follow the fallback and gpt-5.2 must not be deployed."
  }
}

run "basic_auth_refused_outside_poc" {
  command = plan

  variables {
    api_auth_mode = "basic"
  }

  expect_failures = [var.api_auth_mode]
}

run "entra_without_audience_refused" {
  command = plan

  variables {
    auth_audience    = null
    create_entra_app = false
  }

  expect_failures = [var.auth_audience]
}

run "typo_in_learn_model_still_fails" {
  command = plan

  variables {
    learn_model = "gpt-5.2-typo"
  }

  expect_failures = [terraform_data.cu_defaults]
}
