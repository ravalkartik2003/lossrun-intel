# Solution overview

| Document | What it answers |
|---|---|
| `lossrun-architecture.pdf` | Page 1: the architecture diagram with each component's role. Page 2: Azure services, POC deployment vs the recommended organisation setup. Pages 3–5: why each service is required and what was considered instead. Editable diagram: `docs/architecture/diagrams/lossrun-architecture.drawio` |
| `next-steps-sandbox.pdf` (source `next-steps-sandbox.md`) | How the POC moves into the organisation's sandbox and how accuracy is measured on historical documents |
| `sandbox-deployment-request.pdf` (source `sandbox-deployment-request.md`) | For the cloud architect: where the Terraform is, what it creates, what the subscription must provide (roles, state, providers, quota, Entra, two subscription-wide decisions), who runs it |
| `azure-services-table.md` | The services table in markdown, for the wiki |
| `../architecture/architecture-overview.md` | The same architecture with the production topology and the open items that need the organisation |
| `../poc-plan/closeout/` | Governance pack (9 artefacts), acceptance table, sealed-set protocol |
| `../poc-plan/tco.md` | Three-year cost model; assumptions labelled |
| `../evaluation/scoring-protocol.md` | How accuracy is measured, for every entrant |

Everything was built and measured on synthetic data in an independent subscription. Every number in these documents has a results folder under `docs/evaluation/results/` and is replaced by the blind run on the organisation's archive.
