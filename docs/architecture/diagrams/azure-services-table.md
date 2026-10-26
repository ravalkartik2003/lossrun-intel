# Azure services — POC environment vs organisation subscription

| Capability | POC environment (deployed) | Recommended in the organisation's Azure subscription | Why it differs |
|---|---|---|---|
| Document layout, classification, bundle split | Azure AI Content Understanding (GA); inline analyze (preview) for ≤5-page documents to shorten demo latency | Same service, GA async API only; primary + paired region; Private Link; customer-managed keys where supported | The preview inline API has no SLA |
| Language models | gpt-5.4-mini (Data Zone · US) for learning new carrier formats, delta repair, classification and chat; 100K tokens/min; one format learned at a time | gpt-5.2 (Data Zone) for learning, gpt-5.4-mini for delta/classification/chat; 300K tokens/min; three concurrent learns; a dedicated chat deployment with Prompt Shields | Enterprise quota and a second deployment were not available in the POC subscription |
| Embeddings | text-embedding-3-large (Data Zone) | Same | — |
| Orchestration | Azure Functions (Flex Consumption) + Durable Task Scheduler (consumption); one always-ready instance for demos | Same; dedicated scheduler tier; two or more always-ready instances; deployment slots; zone redundancy | Avoids a ~15 s cold start |
| System of record | Azure SQL serverless; ledger (append-only) and temporal tables; Liquibase-format change scripts | Azure SQL General Purpose / Business Critical, zone-redundant, in the application region; ledger digests in a locked immutable store (7-year retention) | The POC database runs in a different region than the application (adds ~50 ms per round trip) |
| Documents and extraction data | Blob Storage with versioning and immutability on originals | Same with locked retention policy, Private Endpoint and customer-managed keys | — |
| Search and chat | Azure AI Search (Basic); chat agent with scope enforced server-side; shared model deployment | Azure AI Search (Standard); dedicated chat deployment with Prompt Shields; Defender for AI alerts routed to the security team | Dedicated deployment pending enterprise quota |
| Integration with quote management | Service Bus with transactional outbox and idempotent commands; Event Grid events; a mock quote system | Same pattern against the real policy/quote system; API Management in front of the API | — |
| Web hosting | Static Web Apps; Entra ID sign-in (basic sign-in available for demos) | Static Web Apps Standard or App Service behind API Management / Application Gateway with WAF; Entra app roles only; private endpoints | — |
| Identity and secrets | Managed identities on every hop; Key Vault; no keys in application settings | Same, plus Privileged Identity Management and Conditional Access | — |
| Observability and testing | Application Insights, Log Analytics, Azure Load Testing | Same, plus alert rules (queue depth, learn failures, drift rate, throttling) and a cost workbook | — |
| Security posture | Defender for Cloud AI threat protection; CORS restricted; default abuse monitoring | Modified abuse monitoring via the Microsoft account team; SOC 2 / ISO 27001 inherited from Azure; private networking end to end | Eligibility is per subscription |
| Cost tracking | Every billable call metered; daily cost export; reconciliation against the bill | Same, with per-environment tags and budget alerts | — |
| Infrastructure as code | Terraform (azurerm, azapi); remote state in Azure Storage | Same, in the organisation's pipeline with environment promotion | — |
