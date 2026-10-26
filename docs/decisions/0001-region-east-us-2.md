# ADR 0001 — POC runs in East US 2 in a new resource group

**Status:** accepted 2026-10-21

**Context.** Existing AI resources are in East US (an earlier demo resource group). The orchestration design uses Durable Functions with the Durable Task Scheduler, which is not offered in East US (offered in East US 2, West US, West US 3, South Central US, per the Azure inventory and quota check made before provisioning). Content Understanding is supported in East US 2. The security checklist in `docs/architecture/azure-extraction-stack.md` already assumes a single Foundry resource in East US 2.

**Decision.** Provision all POC resources in a new resource group `<resource-group>` in **East US 2**: Foundry (AIServices) resource + project, model deployments as Data Zone Standard, Storage, Azure SQL, Key Vault, Log Analytics/App Insights, Durable Task Scheduler, Function App (Flex Consumption, .NET 10 isolated). The earlier demo resource group in East US is left untouched as a scratch area and is not referenced by new code.

**Consequences.** One region for everything (no cross-region egress or latency; private endpoints and CMK apply to one perimeter). The existing `gpt-5-mini` Global Standard deployment is not reused; a DZ `gpt-5.4-mini` replaces it. The subscription must register the `Microsoft.DurableTask` provider.
