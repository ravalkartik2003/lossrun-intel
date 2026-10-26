# ADR 0005 — Upload manifest for non-loss-run documents; single `dbo` schema

**Status:** accepted 2026-10-22

**Context.** Applicants and underwriters upload packages that mix loss runs with payroll reports, X-Mod worksheets, ACORD forms, letters and unreadable files. The preprocessing work's classifier already segments bundles and reports unsupported files with reasons. The user needs to see what was found and what was done with it. Separately, the SQL design should stay simple for the POC.

**Decision.**
1. Every upload yields an `UploadManifest` listing each file and segment with classification, status (`processed` | `kept-not-processed` | `unsupported`), reason, page range and blob path. Non-loss-run documents are retained in Blob under the upload, returned by the API (`GET /uploads/{id}`), shown in the UI as "Other documents in this package", and indexed for the chat service. Only `LossRun` segments enter extraction. The applicant-facing flow uses the manifest for remediation prompts ("no loss run found in this package; the payroll report was kept").
2. All Azure SQL tables, including temporal history and ledger tables, live in `dbo`.

**Consequences.** The API, persistence (`dbo.Upload`, `dbo.UploadItem`), the UI and chat indexing carry the manifest. No custom SQL schemas; object names use a prefix only where needed for clarity.
