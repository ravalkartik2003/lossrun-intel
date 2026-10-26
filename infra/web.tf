# Brief 11 §7: the UI workbench on Azure Static Web Apps (ADR 0012). Free tier; the SPA calls the Function App API
# directly (CORS below), so the API stays reachable by the CLI, the simulator and the quote system with the same auth.
# Deploy: infra/scripts/deploy-web.ps1 (production = live API; preview environment "mock" = the offline demo build).

resource "azurerm_static_web_app" "web" {
  name                = local.names.static_web_app
  resource_group_name = azurerm_resource_group.poc.name
  location            = var.web_location
  sku_tier            = "Free"
  sku_size            = "Free"
  tags                = local.tags
}

locals {
  # Origins allowed to call the API from a browser: the Static Web App (production and preview environments share
  # the azurestaticapps.net host family) plus any extra origins (local Vite dev server).
  # Brief 30 item 50: the local dev-server origin only in the poc environment unless web_extra_origins says otherwise.
  web_extra_origins = var.web_extra_origins != null ? var.web_extra_origins : (local.org_posture ? [] : ["http://localhost:5173"])

  web_origins = concat(
    ["https://${azurerm_static_web_app.web.default_host_name}"],
    var.web_preview_origins,
    local.web_extra_origins,
  )
}

# Brief 13 addendum: the offline demo on its own Static Web App, so it can carry a custom domain (custom domains bind to
# an app's production environment; the "mock" preview environment above cannot). Content: the mock build with a
# self-only CSP, deployed by deploy-web.ps1 (-DemoApp). It calls no API, so it needs no CORS entry and shows no Azure name.
resource "azurerm_static_web_app" "demo" {
  name                = local.names.static_web_demo
  resource_group_name = azurerm_resource_group.poc.name
  location            = var.web_location
  sku_tier            = "Free"
  sku_size            = "Free"
  tags                = local.tags
}

# Set demo_custom_domain only after the DNS record exists (cname-delegation validates on create):
#   CNAME  <demo_custom_domain>  ->  <output demo_default_host>   (your DNS provider: DNS only, not proxied; a proxied record
#   hides the CNAME from validation and breaks the managed certificate)
resource "azurerm_static_web_app_custom_domain" "demo" {
  count             = var.demo_custom_domain == null ? 0 : 1
  static_web_app_id = azurerm_static_web_app.demo.id
  domain_name       = var.demo_custom_domain
  validation_type   = "cname-delegation"
}

