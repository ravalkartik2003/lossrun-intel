# Proof of Concept (POC) Use Case Roadmap — Loss Run Ingestion Technology

Transcribed from the client's requirement document (see `POC_Requirement_1.jpg` and `POC_Requirement_2.jpg` in this folder for the originals).

- **POC Duration:** TBD (we propose 8 weeks — see `docs/poc-plan/`)
- **Number of Use Cases Tested in POC:** 2
- **Purpose:** Explain the primary scenarios the loss run ingestion technology must support (at a minimum), to help define how a POC might be structured to test the technology's ability to deliver on those scenarios.

## Data Scope
De-identified WC loss runs (≥12 formats: PDFs incl. scans, native PDFs, XLSX/CSV, portal exports); 3 policy years; multiple carriers/TPAs; multi-state (incl. CA).

## Environments
Vendor sandbox + secure carrier enclave (read-only); synthetic real-time submission simulator; UW workbench sandbox.

## POC Objectives (apply to both use cases)
- **Accuracy & Normalization:** Extract/map core WC fields (Claim #, DOI, Nature/Cause/Part of Body, Indemnity/Medical Paid/Reserved, Subrogation, Status, Litigation flag, Closure date) to a canonical schema with data lineage.
- **Speed & Resilience:** Meet real-time ingestion SLAs; handle edge cases and degraded inputs; provide exception workflows.
- **Interoperability:** Demonstrate robust REST/GraphQL APIs, eventing (webhooks/queues), and SDKs; support batch and real-time.
- **Security & Compliance:** SOC 2/ISO 27001 posture, encryption in transit/at rest, PII/PHI handling, role-based access, audit trails.
- **Explainability:** Show confidence scoring, validation rules, and human-in-the-loop review paths.

---

## Use Case 1 — Customer-Facing Real-Time Quoting (Self-Serve Submission Engines)

**Goal:** Applicant uploads loss runs in submission engine; system ingests, normalizes, and feeds pricing engine; returns quote in real time, without human intervention.

### POC Scenarios to Test
1. **Mixed Document Intake & Auto-Classification**
   - Batch with clean native PDFs, low-quality scans (skewed/rotated), password-protected PDFs, multi-tab XLSX, CSV, and portal exports.
   - Detect WC loss run vs. misc docs; split multi-carrier bundles.
2. **Field Extraction & Canonical Mapping**
   - Accurate capture of loss run data elements (to be defined by carrier).
3. **Real-Time Pricing Integration**
   - API call to pricing engine with normalized features (loss triangles, frequency/severity metrics, incurred vs. paid development).
   - Latency test: end-to-end from upload → quote returned.
4. **Data Quality & Guardrails**
   - Confidence thresholds, validation rules (e.g., DOI in future, negative reserves), automatic remediation prompts to applicant; graceful failure + alternative path (manual continue).
5. **Edge Cases**
   - Missing years, partial runs, closed-claims-only, claims with reopened status, catastrophic claims outliers, mixed NAICS classes, multiple policy numbers, other states' (non-California) losses, lines of insurance other than WC.

### Success Metrics (Acceptance Criteria)
- Extraction accuracy (key fields): ≥ XX% (Claim #, DOI, Paid/Reserved splits).
- End-to-end latency: ≤ XXX seconds for ≤ 15 MB uploads; ≤ XXX seconds for 50 MB mixed batch.
- Auto-classification precision/recall: ≥ XX% / ≥ XX%.
- Data quality validation pass rate: ≥ XX% on first pass; clear error messaging otherwise.
- Quote success rate: ≥ XX% of valid uploads result in priceable submissions.

### Deliverables
API spec & sample code; canonical mapping dictionary; latency & accuracy report; exception workflow.

---

## Use Case 2 — Underwriter Analytics (Embedded in Quote Management System)

**Goal:** Underwriters view enriched insights in our own quote management system from loss runs already uploaded by applicant, perform interactive analyses, and optionally adjust data with full auditability. Underwriters also have the ability to upload loss runs themselves for the same purposes.

### POC Scenarios to Test
1. **Analytical Feature Generation**
   - Derived metrics: frequency/severity by cause/part of body, trend lines by policy year, paid-to-incurred ratios, med-only vs. lost time (incl. claim count analysis), large loss analysis, primary loss drivers, litigation rate, CT vs. specific injury mix (for CA), subrogation effectiveness, closure velocity, claim reporting lag-time analysis.
2. **Interactive Exploration & What-If**
   - Drill-downs to claim level; filters by injury type, location, class code; scenario sliders (e.g., adjust reserves ±10%, exclude catastrophes) with real-time recalculation.
3. **Data Provenance & Audit**
   - Lineage view: source cell → normalized field → feature; confidence scores; change log capturing underwriter overrides with rationale.
4. **Workflow Integration**
   - Export to pricing memo; push annotations to quote record; flag high-risk signals; assign follow-ups; role-based permissions.
5. **Performance & Scale**
   - Load 1,000+ claims across 5 policy years with sub-second chart interactions; concurrent underwriters (≥ 25) without degradation.

### Success Metrics (Acceptance Criteria)
- Insight accuracy vs. reference set: ≥ XX% on derived metrics.
- UI responsiveness: p95 interactive actions ≤ X.X seconds.
- Audit completeness: 100% of edits tracked with user/time/old→new value.
- Integration fit: Successful read/write to quote management sandbox via APIs/webhooks.

### Deliverables
Analytics catalog; UX demo; sample UW report package; audit & governance model; role-based access configuration; performance test results.

---

## Our constraints (not in the client document)
- Solution must run on **Azure**.
- **No labelled training data** is available.
- Minimize large-model calls: learn each carrier's format/glossary once, replay deterministically, model only as fallback.
- Competing vendors: Nomad Data, Bevaya (formerly Roots Automation), SortSpoke, Convr.
