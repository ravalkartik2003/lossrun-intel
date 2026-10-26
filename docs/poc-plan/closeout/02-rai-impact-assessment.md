# 2. Responsible AI impact assessment

The sections follow Microsoft's Responsible AI Impact Assessment template: system information, intended uses, adverse impact, data requirements, and summary of impact. The goals named (A1–A5, T1–T3, RS1–RS3, PS1–PS2) are those of the Microsoft Responsible AI Standard v2. This is scoped to the POC as built. It is to be reviewed with State Fund's compliance and legal before production.

## Section 1 — System information

- **System:** Loss Run Intelligence (artefact 1).
- **Lifecycle stage:** proof of concept, competing in a build-vs-buy bake-off. The next gate is the sealed blind-set run on State Fund's archive.
- **System description:**
  - It reads carrier loss runs and turns them into a canonical claims record. Every value carries a source: page and box, or sheet and cell.
  - It learns each carrier's layout once with a model and replays it deterministically afterwards.
  - It validates every document with 26 rules (`docs/evaluation/rule-catalog.json` v0.4.0).
  - It offers underwriters analytics, what-if scenarios and a cited chat assistant over the record.

## Section 2 — Intended uses

| Intended use | Stakeholders | Fitness for purpose (evidence) | Oversight model |
|---|---|---|---|
| UC1: extract loss-run claims for quoting | Underwriters, UW assistants, applicants (upload only) | Synthetic corpus v5 (the run the prompt gate holds), by input type: delivered key-field accuracy 100.0 % on native recurring, XLSX/CSV and scans ≥ 300 dpi, with CIs; 0 wrong of 179,409 delivered values. First-seen native PDF 100.0 % [98.8–100.0 %] (n = 5, corpus v3 study). Faxes and narrative statements are always reviewed (`acceptance-table.md`) | Human in the loop on exceptions: a review queue, override with rationale, confirm-new-format |
| UC2: account analytics, what-if, pricing memo | Underwriters, UW managers | 42/42 catalog metrics within tolerance against truth on 252 delivered documents. The engine and its browser twin are byte-identical (ADR 0011) | Human decides. Metrics show "not measurable" with a reason rather than a guess |
| Chat over the quote's documents and record | Underwriters (any scope); applicants (own workspace only) | 30-question numeric eval (ADR 0013); text-answer eval 10/10 rubric with 0 fabrications (`docs/poc-plan/closeout/README.md`). 0 leaks in 11 leakage tests and in the live probes | Answers are cited. "Not in these documents" is an allowed answer. Scope is enforced by the system, not the model |
| Quote-system integration | Quote system, underwriters | Outbox → Service Bus / Event Grid, exactly-once delivery verified (ADR 0014) | Pushes are explicit user actions with a rationale, and audited |

**Deployment complexity.**
- *Task complexity:* moderate. Structured extraction with validation; no free-form generation reaches a quote.
- *Human oversight:* the human decides, and exceptions are routed to them.
- *Technical readiness:* POC.

## Section 3 — Adverse impact

| Area | Assessment |
|---|---|
| **Restricted / unsupported uses** | Not for claims adjudication, reserve setting, pricing without an underwriter, or decisions about individual claimants. Not for non-WC lines: non-WC rows are flagged (R-18) and excluded from metrics. Chat is not a source of truth: its numbers come from tools over the record and are cited. |
| **Known limitations** | Faxes under 200 dpi and narrative statements always go to review. A first document from a new carrier needs a learn (2–4 model calls) or review. Synthetic data is cleaner than real loss runs. Class codes are not printed on the corpus loss runs, so class-based metrics are not offered. |
| **Failure modes, false positives** (a wrong value delivered) | Mitigated in layers: deterministic replay; 26 validation rules, hard failures to review; printed totals must tie to claim rows (R-10/R-11); a learned format starts in shadow; cell-level lineage for every value. Measured: 0 wrong delivered key fields on the synthetic corpus. **Residual risk:** a value both wrong and internally consistent with its totals. The sealed blind set measures this. |
| **Failure modes, false negatives** (a correct document sent to review) | Costs reviewer time only. Measured review rates by input type are in `acceptance-table.md`. |
| **Misuse** | Prompt injection through documents: documents are treated as data; a planted-instruction test is ignored; output is structured; the tools enforce scope. Cross-quote leakage: tool-layer scope enforcement plus 11 leakage tests. Tampering with corrections: append-only ledger tables. |
| **Sensitive uses** | None triggered. No consequential decision about a person is made by the system. Claimant names are personal data, handled under State Fund's existing claims-data controls. |

## Section 4 — Data requirements

- **Training data: none.** No model is trained or fine-tuned (a hard constraint of the POC). Format specs are learned per carrier from the document itself and verified before use.
- **Evaluation data:**
  - Synthetic corpus v5 (332 documents, `corpus-gen-0.9.0`, seed 8). Truth is generated, so no labelling is needed.
  - The production evaluation is the sealed blind set from State Fund's keyed archive (`sealed-set-protocol.md`).
- **Data residency:** Azure Data Zone (US) model deployments. Storage, SQL and Search are in the State Fund tenant.
- **Retention:**
  - the ledger and audit records follow State Fund's records policy;
  - uploads follow the document retention schedule;
  - chat workspaces expire by TTL.

## Section 5 — Summary of impact

| Potential harm | Mitigation | Owner |
|---|---|---|
| Wrong number reaches a quote | Validation gates; totals tie; review queue; lineage; override audit | Business owner |
| Model behaviour changes on a version update | Version pinning (configuration). A golden-set regression on every model or prompt change (artefact 4). Shadow status for new formats | Technical owner |
| Prompt injection via an uploaded document | Tool-layer scope, data-not-instructions, structured output, audit of every turn. **Prompt Shields policy defined; attached when the dedicated deployment is approved** (artefact 3) | Technical owner |
| Unauthorised change to the record | Entra app roles; append-only ledger; temporal history plus `audit-proof` | Technical owner, security |
| Key-person dependency | Runbooks, IaC, ADRs; a named State Fund owner; 0.25–0.5 FTE maintenance (TCO) | Business owner |

**Applicability:**
- *RAI Standard goals addressed:* A1 (this assessment), A3 (release criteria: `acceptance-table.md`), A4 (data: section 4), A5 (human oversight), T1/T2 (artefact 8), RS1/RS2 (artefact 4), RS3 (artefacts 3 and 7), PS1/PS2 (artefacts 6 and 9).
- *Not applicable:* fairness goals F1–F3 on protected groups. No decision about a person is made, and no protected attribute is used.

**Sign-off:**

| Role | Name | Date |
|---|---|---|
| Business owner | | |
| Technical owner | | |
| Compliance | | |
| Security | | |
