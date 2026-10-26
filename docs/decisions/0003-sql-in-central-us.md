# ADR 0003 — Azure SQL runs in Central US (amends ADR 0001)

**Status:** accepted 2026-10-21

**Context.** `terraform apply` failed with `ProvisioningDisabled` for Azure SQL in East US 2 on this subscription (the provisioning apply log). GP serverless is Available in Central US, West US 2/3, South Central US, West US.

**Decision.** The SQL logical server and database live in **Central US**; all other POC resources stay in East US 2 per ADR 0001. A support request to enable SQL in East US 2 is filed in parallel; if granted before private-endpoint work begins, SQL moves back with a one-time copy.

**Consequences.** ~25 ms cross-region latency on SQL calls and negligible egress at POC volume. Private endpoints for SQL, when added, will need a VNet peering or a second private DNS zone scope. The analytics "sub-second" target is unaffected because per-account data is cached and recomputed client-side.
