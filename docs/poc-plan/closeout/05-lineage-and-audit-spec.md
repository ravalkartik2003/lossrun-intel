# 5. Lineage & audit spec

Every extracted value can be traced from the quote back to the printed cell. Every change to a value is an append-only, attributed event.

## 5.1 The chain

```
upload (manifest: every file and segment, classification, status, reason, blob path)
  └─ document (page range in the file, graph = layout result, format ref, revision)
       └─ value (canonical field path)
            ├─ lineage: documentId, page + polygon | sheet + cell, sourceText, extractor (replay | learn | derived | override),
            │           formatId + formatVersion, model + prompt version (learn/delta), confidence
            ├─ rule outcomes: ruleId, severity, status, message, affected fields, catalogVersion
            ├─ overrides: old → new, rationale, user, role, time, supersedes (ledger: dbo.Override)
            └─ audit events: action, user, role, time, field path, old → new, rationale, correlation id (ledger: dbo.AuditEvent)
  └─ quote: feature snapshot (catalog version, inputs hash, view, scenario), quote payload, outbox events
```

## 5.2 Storage

| Record | Store | Mutability | Retention (proposed) |
|---|---|---|---|
| Original files | Blob `uploads/` | Write once | The document retention schedule |
| Layout graph, result JSON | Blob `extractions/`, `dbo.Document.ResultJson` | New revision per decision | As above |
| Extracted values | `dbo.ExtractedValue` | **Append-only ledger** | Records policy |
| Claims (current view) | `dbo.Claim` | **Temporal** (system-versioned history) | Records policy |
| Overrides | `dbo.Override` | **Append-only ledger**; each supersedes the field's previous override | Records policy |
| Audit events | `dbo.AuditEvent` (view `dbo.vAuditTrail` with the ledger transaction) | **Append-only ledger** | Records policy |
| Rule outcomes | `dbo.RuleOutcome` | Insert per revision | Records policy |
| Usage and cost | `dbo.UsageEvent` | **Append-only ledger** | 7 years (finance) |
| Chat turns | `dbo.ChatTurn` | **Append-only ledger** | Records policy |
| Quote labels and context | `dbo.QuoteLabel`, `dbo.QuoteContext` | Temporal | Records policy |
| Ledger digests | Blob `sqldbledgerdigests` | Immutable (time-based retention; POC unlocked 7 days, production locked) | Records policy |

## 5.3 Audit actions

| Area | Actions |
|---|---|
| Uploads and documents | `upload.create`, `upload.zip`, `upload.completed`, `extraction.persist`, `rederive` |
| Reviews and values | `review.accept`, `review.override`, `review.reject`, `review.reopened`, `review.expired`, `override` (one per changed field) |
| Formats | `format.learned`, `format.approve`, `format.retire`, `formats.reset` |
| Quotes | `quote.context`, `quote.annotation`, `quote.flags.pushed`, `quote.followup`, `quote.memo.exported`, `quote.payload.pushed`, `quote.rename`, `quote.retire`, `quote.restore` |
| Chat | `chat.turn` (one per turn, with citations and cost), `workspace.create`, `workspace.delete`, `workspace.expired` |

Every row records user, role, UTC time, source (`api`, `cli`, `system`) and correlation id, and is committed in a SQL ledger transaction.

## 5.4 One decision, one transaction (`docs/poc-plan/closeout/README.md`)

`dbo.ApplyDecision` writes a review decision or an override in one round trip:
- the override rows (ids from `dbo.OverrideSeq`, each superseding the field's last override);
- the audit rows;
- one temporal version per changed claim;
- the rule outcomes, the document revision, the review item;
- the `review.decided` outbox row.

They commit together under the document's row lock, or not at all. A second decision on the same revision is refused (optimistic concurrency).

## 5.5 Proof

`lossrun-db audit-proof` checks four things (artefact 3):
- every override is audited;
- the ledger tables reject UPDATE and DELETE;
- the ledger verifies against its digests, including from immutable storage;
- the temporal history agrees with every override.

`GET /quotes/{id}/audit` and `GET /documents/{id}/audit` show the trail with each row's ledger transaction id. The UI shows it in the **Audit trail** tab.

Record an audit event as `audit <id> / ledger tx <transaction id>` (runbook).
