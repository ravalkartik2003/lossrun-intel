# 07 Answers 2: Entra ID app registration for the API, behind create_entra_app (default false). The POC tenant can
# create it; the client tenant's identity team will. Basic auth (api.tf) stays as the demo fallback (api_auth_mode).

locals {
  app_roles = {
    Applicant   = "Uploads loss runs and sees only their own uploads."
    Underwriter = "Sees all uploads, decides reviews and overrides values."
    UWManager   = "Underwriter rights plus format retirement."
    Admin       = "Full access, including format retirement."
    Engineer    = "Brief 26: works the Needs investigation queue and reads what a reviewer reads; cannot decide reviews or override values."
    QuoteSystem = "Brief 25: the quote system's service identity; reads quote payloads, upload manifests and integration status; writes nothing."
  }
}

resource "random_uuid" "app_role" {
  for_each = var.create_entra_app ? local.app_roles : {}
}

resource "random_uuid" "api_scope" {
  count = var.create_entra_app ? 1 : 0
}

resource "azuread_application" "api" {
  count            = var.create_entra_app ? 1 : 0
  display_name     = "${local.names.function_app}-api"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azurerm_client_config.current.object_id]

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = random_uuid.api_scope[0].result
      value                      = "access_as_user"
      type                       = "User"
      admin_consent_display_name = "Access the LossRun POC API"
      admin_consent_description  = "Lets the LossRun UI call the API as the signed-in user."
      user_consent_display_name  = "Access the LossRun POC API"
      user_consent_description   = "Lets the LossRun UI call the API on your behalf."
    }
  }

  dynamic "app_role" {
    for_each = local.app_roles
    content {
      id                   = random_uuid.app_role[app_role.key].result
      value                = app_role.key
      display_name         = app_role.key
      description          = app_role.value
      allowed_member_types = ["User"]
    }
  }

  # The workbench SPA app registration (React, separate design pass) signs in against this registration.
  single_page_application {
    redirect_uris = var.ui_redirect_uris
  }
}

resource "azuread_application_identifier_uri" "api" {
  count          = var.create_entra_app ? 1 : 0
  application_id = azuread_application.api[0].id
  identifier_uri = "api://${azuread_application.api[0].client_id}"
}

resource "azuread_service_principal" "api" {
  count                        = var.create_entra_app ? 1 : 0
  client_id                    = azuread_application.api[0].client_id
  app_role_assignment_required = false
  owners                       = [data.azurerm_client_config.current.object_id]
}
