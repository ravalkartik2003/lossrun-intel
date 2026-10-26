# No keys or connection secrets here: everything is identity-based.

output "learn_model" {
  description = "The deployment the learn path uses (the delta model while deploy_gpt52 gates the configured learn model off)."
  value       = local.effective_learn_model
}

output "delta_model" {
  value = var.delta_model
}

output "embedding_model" {
  value = var.embedding_model
}

output "resource_group" {
  value = azurerm_resource_group.poc.name
}

output "ai_resource_name" {
  description = "Foundry resource; also the resource to name in the modified-abuse-monitoring request."
  value       = azurerm_cognitive_account.ai.name
}

output "ai_endpoint" {
  value = azurerm_cognitive_account.ai.endpoint
}

output "content_understanding_endpoint" {
  value = local.cu_endpoint
}

output "ai_project_name" {
  value = azurerm_cognitive_account_project.poc.name
}

output "storage_account_name" {
  value = azurerm_storage_account.poc.name
}

output "storage_blob_endpoint" {
  value = azurerm_storage_account.poc.primary_blob_endpoint
}

output "sql_server_fqdn" {
  value = azurerm_mssql_server.poc.fully_qualified_domain_name
}

output "sql_database_name" {
  value = azapi_resource.sql_database.name
}

output "key_vault_uri" {
  value = azurerm_key_vault.poc.vault_uri
}

output "durable_task_scheduler_endpoint" {
  value = local.dts_endpoint
}

output "task_hub_name" {
  value = var.task_hub_name
}

output "function_app_name" {
  value = azurerm_function_app_flex_consumption.poc.name
}

output "function_app_hostname" {
  value = azurerm_function_app_flex_consumption.poc.default_hostname
}

output "search_endpoint" {
  value = local.search_on ? "https://${azurerm_search_service.poc[0].name}.search.windows.net" : null
}

output "static_web_app_name" {
  value = azurerm_static_web_app.web.name
}

output "static_web_app_hostname" {
  value = azurerm_static_web_app.web.default_host_name
}

output "demo_default_host" {
  description = "Default host of the offline demo Static Web App: the CNAME target for demo_custom_domain."
  value       = azurerm_static_web_app.demo.default_host_name
}

output "demo_url" {
  value = var.demo_custom_domain == null ? "https://${azurerm_static_web_app.demo.default_host_name}" : "https://${var.demo_custom_domain}"
}


output "demo_static_web_app_name" {
  value = azurerm_static_web_app.demo.name
}

output "quote_mock_app_name" {
  value = azurerm_function_app_flex_consumption.quote_mock.name
}

output "app_insights_name" {
  value = azurerm_application_insights.poc.name
}
