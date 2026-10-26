# Third-party packages

All runtime and test dependencies are MIT, Apache-2.0 or BSD-style. **Pins with a licence or security reason must not be bumped without re-checking the reason** (the preprocessing work asked for this record; the client's architect will ask).

| Package | Version | Licence | Used by | Why this version |
|---|---|---|---|---|
| Azure.AI.ContentUnderstanding | 1.1.0 | MIT | LossRun.Ingestion | Latest stable (GA API 2025-11-01). |
| Azure.Identity | 1.21.0 | MIT | Ingestion, Tool | Keyless auth. See `AzureCredentials.Default()` for the managed-identity probe workaround off Azure. |
| Azure.Storage.Blobs | 12.30.1 | MIT | Ingestion, Formats | Raw results, usage JSONL, format registry. |
| ExcelDataReader | 3.9.0 | MIT | Ingestion | XLS/XLSX/XLSB/CSV, workbook passwords. |
| PdfPig | 0.1.16 | Apache-2.0 | Ingestion | PDF open/unlock, text-layer first-page check. |
| **PDFsharp** | **6.2.4 (pinned)** | MIT | Ingestion, fixture generator | **6.5.0 relicensed to AGPL-3.0-only** (nuspec), and 6.3–6.4 publish no licence expression. 6.2.4 is the last MIT release. Used only to write a decrypted copy of an unlocked PDF and to generate fixtures. |
| **System.Security.Cryptography.Pkcs** | **10.0.13 (pinned)** | MIT | Ingestion, fixture generator | Overrides 8.0.1, pulled in transitively by PDFsharp/ExcelDataReader, which has known vulnerabilities (GHSA-2fmv-rv2c-59x7, GHSA-846f-f764-76mx). |
| **JsonSchema.Net** | **7.4.0 (pinned)** | MIT | Contracts.Tests, Formats.Tests (test only) | **9.x moved to the OSMF EULA (not MIT)**. 7.x and 8.x are MIT; its dependencies JsonPointer.Net 5.3.1 and Json.More.Net 2.1.1 are MIT too. Never shipped in runtime code. |
| ClosedXML | 0.105.1 | MIT | fixture generator only | Writes the synthetic XLSX. |
| PDFtoImage | 5.4.0 | MIT (PDFium: BSD-3-Clause/Apache-2.0; SkiaSharp: MIT) | fixture generator; LossRun.Learning (opt-in page image); API page images | Rasterizes the synthetic "scan"; renders page 1 for the learn request when `IncludePageImage` is on (default off); renders `GET /documents/{id}/source/pages/{n}` on the Linux Function App (it brings `SkiaSharp.NativeAssets.Linux.NoDependencies` 4.150.x itself; do not add an older one). |
| Microsoft.Azure.Functions.Worker / .Sdk / .Extensions.Http.AspNetCore / .ApplicationInsights | 2.52.0 / 2.1.0 / 2.1.1 / 2.51.0 | MIT | LossRun.Functions | .NET 10 isolated worker on Flex Consumption; ASP.NET Core integration for the SSE stream. |
| Microsoft.Azure.Functions.Worker.Extensions.DurableTask / .DurableTask.AzureManaged | 1.20.0 / 1.10.4 | MIT | LossRun.Functions | Durable Functions on the Durable Task Scheduler (identity-based connection). |
| Microsoft.DurableTask.Abstractions | 1.27.0 | MIT | LossRun.Orchestration | `TaskOrchestrationContext` for the orchestrators (tested with a fake context). |
| Microsoft.ApplicationInsights.WorkerService | 2.23.0 | MIT | LossRun.Functions | Telemetry with uploadId/documentId tags. |
| Microsoft.IdentityModel.Protocols.OpenIdConnect / .JsonWebTokens | 8.23.0 | MIT | LossRun.Orchestration | Entra ID bearer validation (ADR 0008). |
| YamlDotNet | 16.3.0 | MIT | Orchestration.Tests (test only) | Reads docs/api/openapi.yaml for the DTO conformance test. |
| hashicorp/random (Terraform provider) | ~> 3.7 | MPL-2.0 | infra | Generates the demo-host passwords and webhook secret (stored in Key Vault). |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk / coverlet.collector | 2.9.3 / 3.1.4 / 17.14.1 / 6.0.4 | Apache-2.0 / Apache-2.0 / MIT / MIT | tests | Template defaults. |

Deliberately not used:
- **Azure.AI.OpenAI.** The only stable release is 2.1.0 (2024), and later versions are beta. The single embeddings call (`LossRun.Formats.AzureOpenAiEmbeddingService`) goes over REST with an Entra bearer token instead.
- **QuestPDF.** Its licence is not MIT for commercial use (ADR 0005).
