# Brief 24 step 0: hibernate. One switch (var.hibernate) removes every resource with a standing charge that the app can do
# without between work sessions, and keeps what the offline demo and a fast wake need. Runbook section Hibernate.
#
# Removed while hibernated (count = 0): AI Search (Basic, ~$74/month), the Service Bus namespace and queue (Standard,
# ~$10/month), the Event Grid topic and its subscription, Azure Load Testing, and the always-ready Function instances
# (demo_mode forced off). Log Analytics drops to the minimum daily cap. The Defender for AI plan stays: it is a
# subscription-wide setting, not this resource group's (owner's decision, 2026-10-25; opt-in since brief 30, ADR 0027).
# Before setting hibernate = true: drain the Service Bus DLQ and confirm the outbox is empty (runbook section Hibernate,
# brief 30 item 51); destroying the namespace drops every in-flight and dead-lettered message.
# Kept: SQL (serverless, auto-pauses), Blob, Key Vault, the Durable Task Scheduler (Consumption SKU: no idle charge),
# both Function Apps (Flex Consumption: no idle charge), the model deployments (pay per token) and both Static Web Apps.

locals {
  integration_on = !var.hibernate
  search_on      = var.search_enabled && !var.hibernate
  demo_on        = var.demo_mode && !var.hibernate
  defender_on    = var.defender_ai_enabled
  # 0.023 GB/day is the smallest daily cap Log Analytics accepts.
  log_analytics_daily_quota_gb = var.hibernate ? 0.023 : var.log_analytics_daily_quota_gb
}

# The resources that gained a count keep their state (no destroy and re-create when hibernate is false).
moved {
  from = azurerm_servicebus_namespace.poc
  to   = azurerm_servicebus_namespace.poc[0]
}

moved {
  from = azurerm_servicebus_queue.quote_commands
  to   = azurerm_servicebus_queue.quote_commands[0]
}

moved {
  from = azurerm_eventgrid_topic.poc
  to   = azurerm_eventgrid_topic.poc[0]
}

moved {
  from = azurerm_role_assignment.eventgrid_deadletter
  to   = azurerm_role_assignment.eventgrid_deadletter[0]
}

moved {
  from = azurerm_load_test.poc
  to   = azurerm_load_test.poc[0]
}

moved {
  from = azurerm_role_assignment.load_test_secrets
  to   = azurerm_role_assignment.load_test_secrets[0]
}

# ------------------------------------------------------------------ budget

# A $10/month budget on the resource group (brief 24): mail to the subscription's Owners at 100 % of actual spend and at
# 80 % of the forecast. Hibernated, the standing cost is a few dollars a month (SQL storage, Blob, Key Vault, logs).
resource "azurerm_consumption_budget_resource_group" "poc" {
  name              = "${local.base}-budget"
  resource_group_id = azurerm_resource_group.poc.id
  amount            = var.budget_monthly_usd
  time_grain        = "Monthly"

  # Brief 30 item 51: the first day of the month of the first apply (the API refuses a start before the current month,
  # so a fixed date breaks every new environment). Ignored afterwards, or the budget would be re-written every month.
  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", plantimestamp())
  }

  lifecycle {
    ignore_changes = [time_period]
  }

  notification {
    enabled        = true
    operator       = "GreaterThanOrEqualTo"
    threshold      = 100
    threshold_type = "Actual"
    contact_roles  = ["Owner"]
  }

  notification {
    enabled        = true
    operator       = "GreaterThanOrEqualTo"
    threshold      = 80
    threshold_type = "Forecasted"
    contact_roles  = ["Owner"]
  }
}
