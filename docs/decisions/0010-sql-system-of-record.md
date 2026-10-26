# ADR 0010 — Azure SQL system of record: shape of the write path

**Status:** accepted 2026-10-22

**Context.** The persistence work moves manifests, results, reviews, usage and audit from the brief-07 Blob layout to Azure SQL. It requires:
- ledger and temporal tables;
- one command handler;
- EF Core without migrations;
- rerunnable Liquibase-formatted changesets.

Building it surfaced choices the brief and the research do not settle.

**Decisions.**
1. **Document ids stay strings.** `dbo.Document.Id` is the pipeline's `nvarchar` document id (`doc-…`, `…-p1-2` for bundle segments). Blob paths, Durable instance ids and the API already use it. The brief's `uniqueidentifier` applies to claims and periods, whose ids are deterministic from (document, period, claim index). Persisting and backfill are therefore idempotent without lookups.
2. **A record-store seam (`IRecordStore`).** It has a Blob implementation (the API work behaviour, used by local runs and the in-memory tests) and a SQL implementation. The host picks SQL with `Records__Store = sql` (Terraform `records_store`). Blob keeps originals, graphs, raw CU JSON and page renders.
3. **The edit is computed, then persisted.**
   - `DocumentEdits.Apply` is pure: overrides, re-derived values, re-validation, revision + 1.
   - `ClaimCommandService` is the only writer of `Claim`, `Override` and `AuditEvent`. Each command is one transaction.
   - A decision's correlation id (`{documentId}:r{revision}`) ties its Override rows, its AuditEvents and the one `Claim` history version it creates per claim. That one-to-one link is what `audit-proof` reconciles.
4. **`Document.ResultJson` is the API's read model.** It holds the current result: document, lineage and rule outcomes. It is rewritten in the same transaction as the claim columns it projects. As-reported values live in the `ExtractedValue` ledger; as-adjusted values live in temporal `Claim`.
5. **Ledger ids come from hi/lo sequences (blocks of 100).**
   - Append-only ledger tables reject the `MERGE … OUTPUT` that EF Core uses to batch inserts with server-generated keys. The engine reports "updates are not allowed".
   - EF allocates ids client-side from the `dbo` sequences, so inserts are plain `INSERT`s.
   - The sequence defaults remain for hand-written inserts (sqlcmd, backfill by script).
6. **Formats.** `SqlFormatRegistry` is the source for specs, lifecycle and field stats; Blob stays a cache for centroids and the learned glossary. Checked-in hand specs get SQL rows on their first stats update, so they can be counted, approved and retired like learned ones. The index leaves them to the hand-spec layer, so they are not listed twice.
7. **Entra auth goes through the repo's credential chain.** SqlClient's built-in `Active Directory Default` stops at the managed-identity probe on a developer machine. Connections with that keyword use an access-token callback from `AzureCredentials.Default()` instead: managed identity in Azure, Azure CLI locally. No SQL logins anywhere.
8. **Digests.** Automatic digest storage goes to the `sqldbledgerdigests` container: unlocked time-based retention, protected append writes, set in Terraform through `azapi_update_resource`. `audit-proof` also keeps each digest it generates in the `dbo.LedgerDigest` append-only table, which gives a local verification anchor on SQL Server containers.

**Consequences.**
- One code path writes the audited tables. A direct `UPDATE` on `dbo.Claim` (temporal, not ledger) is possible for a DBA, but `audit-proof` check (a) catches it, as `CommandTests.An_unaudited_claim_update_breaks_the_proof` shows.
- The API work's Blob history is backfilled as the latest revision only (Blob kept no per-revision results). Earlier overrides survive as `backfill` audit rows without claim ids.
- Hi/lo leaves gaps in event ids. Ordering uses `AtUtc` and the ledger transaction id, never id contiguity.

**Amendments.**
- **Guarantee:** preventive on `ExtractedValue`/`Override`/`AuditEvent` (append-only ledger), detective on `Claim` (temporal + `audit-proof`). Updatable ledger for `Claim` is the listed production option.
- **Digest container:** unlocked 7-day retention in the POC; a production environment sets `ledger_digest_immutability_locked = true` and `ledger_digest_immutability_days = 2557` (7 years) — see `infra/README.md`. A locked policy can only be extended.
- **The Blob store is demo topology only (brief 32 item 6).**
  - `BlobRecordStore.ApplyDecisionAsync` is a sequence of blob writes, not a transaction. A crash between two of them leaves a partial decision.
  - Since brief 32, the `review.decided` outbox row is written last and is the applied marker. Every write before it is idempotent (override lines by id, audit lines by text, a saved result recognised by revision and content), so the retried activity completes the decision once.
  - It is still not atomic, and a concurrent reader can see the partial state. SQL (`dbo.ApplyDecision`, one transaction) is the system of record for that reason, and the Blob store is never used where decisions are taken for real.
- **No Blob JSONL audit copy:** in SQL mode the ledger plus digest is the single audit record; Blob keeps originals, graphs, raw CU JSON and page renders. (`BlobRecordStore` still writes the JSONL only when Blob itself is the system of record, i.e. local runs and tests.)
