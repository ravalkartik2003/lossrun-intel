# ADR 0006 — Database schema as rerunnable, Liquibase-formatted T-SQL scripts

**Status:** accepted 2026-10-22

**Context.** The client organisation uses Liquibase for database change management. The POC environment will not run Liquibase, but scripts should drop into the org's process unchanged if the POC is adopted. Scripts must also be safe to re-run in the POC (fresh database, repeated deploys, partial failures).

**Decision.**
- Location: `db/changelog/`, one file per changeset, ordered by numeric prefix (`0001-create-upload.sql`), plus `db/master.xml` listing includes, so Liquibase can consume it directly.
- Format: Liquibase formatted SQL (`--liquibase formatted sql`, `--changeset lossrun:0001 runOnChange:false`, `--rollback …`), T-SQL body. Preconditions expressed with Liquibase `--precondition-sql-check` where useful.
- Rerunnable bodies: `IF OBJECT_ID(...) IS NULL CREATE TABLE`, `CREATE OR ALTER` for views/procs/functions, `IF NOT EXISTS` for indexes/constraints, `MERGE` for seed data. Ledger (append-only) tables are never altered; a change creates a new versioned table and a view that unions them. Temporal tables use `IF NOT EXISTS` on the history table and `ALTER TABLE ... SET (SYSTEM_VERSIONING = ON)` guarded by a check on `sys.tables.temporal_type`.
- POC runner: a small .NET console (`src/LossRun.DbDeploy`) that applies changesets in order with Entra token auth, records `(id, author, filename, checksum, appliedAt)` in `dbo.SchemaChangeLog` (same shape as `DATABASECHANGELOG`), skips already-applied ids, and fails on checksum drift unless `runOnChange` is set. Also runnable with `sqlcmd` by concatenation.
- All objects in `dbo` (ADR 0005). EF Core is used for data access only; no EF migrations.

**Consequences.** The persistence work produces the first changesets (Upload, UploadItem, Document, Claim, FieldLineage, Override, AuditEvent ledger, FormatFieldStats, SchemaChangeLog). The org's DBAs can review and run the same files with Liquibase. Rollback lines are mandatory even for the POC.
