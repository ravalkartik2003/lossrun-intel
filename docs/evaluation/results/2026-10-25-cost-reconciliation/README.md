# Cost reconciliation, 2026-10-24 and 2026-10-25 (brief 24)

**Files.**
- `billing-cost-query.json`: the Cost Query API response (ActualCost, resource-group scope, daily, grouped by service, meter and meter subcategory). It was pulled on 25 Oct at about 11:45 UTC, after the second hibernate.
- `reconciliation.md` / `.json`: `lossrun eval reconcile` against our metering, `dbo.UsageEvent` plus the local eval runs' per-document usage.

No wake and no deploy. Reading the SQL ledger resumed the auto-paused serverless database.

**Hourly is not available from billing.** The Cost Query API refuses `granularity: Hourly` ("valid values: 'Daily'"). The hourly view below therefore comes from our own `UsageEvent` timestamps, and the standing hours from the Terraform apply times:
- step-0 hibernate, about 04:30 UTC on 25 Oct;
- wake 1, about 09:40–10:44 UTC;
- wake 2, about 10:58–11:33 UTC.

See the brief 24 report, section "Cost reconciliation, 24–25 Oct", for the tables and the classification.
