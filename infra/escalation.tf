# Brief 26 (ADR 0025): alerting for the engineering escalation queue. The API writes an Application Insights custom event
# "lossrun.escalation" on every raise/count/reopen/status change, and the hourly sweep a metric "lossrun.escalations.open".
# Two log alerts on the workspace-based App Insights:
#   - first occurrence: a raised or reopened case in the last 15 minutes (one alert per (reason, fingerprint): the API
#     raises once per key, so the event is the first occurrence);
#   - daily digest: open cases > 0, evaluated once a day.
# Both go to one action group (brief 26 Answers 1): the distribution list in escalation_alert_emails, or the subscription
# Owners by role when that list is empty (the POC needs no address in Terraform; the organisation sets theirs), plus an
# optional Teams/webhook URL (null = none; the organisation supplies it in the sandbox). Hibernate leaves all of this in place: alert rules have no idle cost
# beyond the evaluations (a few cents a month).

variable "escalation_alert_emails" {
  description = "Brief 26 Answers 1: addresses (a distribution list) that receive escalation alerts. Empty = the subscription Owners, by role."
  type        = list(string)
  default     = []
}

variable "escalation_webhook_url" {
  description = "Brief 26: optional Teams incoming-webhook (or other HTTPS) URL for escalation alerts; null = none."
  type        = string
  default     = null
  nullable    = true
  sensitive   = true
}

resource "azurerm_monitor_action_group" "escalation" {
  name                = "${local.base}-ag-escalation"
  resource_group_name = azurerm_resource_group.poc.name
  short_name          = "lr-escalate"
  tags                = local.tags

  dynamic "email_receiver" {
    for_each = { for i, address in var.escalation_alert_emails : tostring(i) => address }
    content {
      name                    = "alert-list-${email_receiver.key}"
      email_address           = email_receiver.value
      use_common_alert_schema = true
    }
  }

  dynamic "arm_role_receiver" {
    for_each = length(var.escalation_alert_emails) == 0 ? ["owners"] : []
    content {
      name                    = "subscription-owners"
      role_id                 = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635" # Owner (built-in role id)
      use_common_alert_schema = true
    }
  }

  dynamic "webhook_receiver" {
    for_each = var.escalation_webhook_url == null ? [] : [var.escalation_webhook_url]
    content {
      name                    = "teams-webhook"
      service_uri             = webhook_receiver.value
      use_common_alert_schema = true
    }
  }
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "escalation_raised" {
  name                    = "${local.base}-alert-escalation-raised"
  resource_group_name     = azurerm_resource_group.poc.name
  location                = azurerm_resource_group.poc.location
  scopes                  = [azurerm_application_insights.poc.id]
  description             = "A new engineering escalation (or a resolved one that happened again): open the workbench's Needs investigation screen."
  severity                = 2
  evaluation_frequency    = "PT15M"
  window_duration         = "PT15M"
  auto_mitigation_enabled = false
  tags                    = local.tags

  criteria {
    query                   = <<-KQL
      customEvents
      | where name == "lossrun.escalation"
      | extend change = tostring(customDimensions.change), reasonCode = tostring(customDimensions.reasonCode), escalationId = tostring(customDimensions.escalationId)
      | where change in ("raised", "reopened")
      | summarize arg_max(timestamp, *) by escalationId
      | project timestamp, escalationId, reasonCode, change
    KQL
    time_aggregation_method = "Count"
    operator                = "GreaterThan"
    threshold               = 0
    dimension {
      name     = "reasonCode"
      operator = "Include"
      values   = ["*"]
    }
    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.escalation.id]
  }
}

resource "azurerm_monitor_scheduled_query_rules_alert_v2" "escalation_digest" {
  name                    = "${local.base}-alert-escalation-digest"
  resource_group_name     = azurerm_resource_group.poc.name
  location                = azurerm_resource_group.poc.location
  scopes                  = [azurerm_application_insights.poc.id]
  description             = "Daily digest: engineering escalations still open (new or investigating)."
  severity                = 3
  evaluation_frequency    = "P1D"
  window_duration         = "P1D"
  auto_mitigation_enabled = false
  tags                    = local.tags

  criteria {
    query                   = <<-KQL
      customMetrics
      | where name == "lossrun.escalations.open"
      | summarize open = max(value) by bin(timestamp, 1d)
      | top 1 by timestamp desc
    KQL
    time_aggregation_method = "Maximum"
    metric_measure_column   = "open"
    operator                = "GreaterThan"
    threshold               = 0
    failing_periods {
      minimum_failing_periods_to_trigger_alert = 1
      number_of_evaluation_periods             = 1
    }
  }

  action {
    action_groups = [azurerm_monitor_action_group.escalation.id]
  }
}
