# ADR 0024 — Quote revision, re-send on every decision, and the receiver contract

**Status:** accepted (brief 24 step 3; review plan 2026-10-25 §2, `docs/research/notes/2026-10-25-review-plan-six-improvements.md`) · **Date:** 2026-10-25

## Context

The quote system heard about a quote once: `CompleteUpload` wrote a `quote.payload` command (a summary plus the payload URL, the ADR 0014 claim check). It did so even while documents were still waiting for review. After that, nothing told it a value had changed:

- **Decisions sent no command.** A review decision, a reopened decision or a direct override emitted only the `review.decided` event. For orchestrated and reopened decisions that event was written in a second transaction after the decision.
- **No quote-level revision.** Each document has its own revision. The quote had none, the payload had no ETag, and the URL in the command was unversioned.
- **Order was not guaranteed.** A Service Bus session delivers in publish order. A command whose fast path failed is published by the sweep about 20 s later, possibly after a newer one. The mock quote system replaced `lossRunAnalysis` unconditionally, so an older summary could overwrite a newer one.

## Decision

**Quote revision.**
- `dbo.QuoteRevision` (changeset `0020-quote-revision`, temporal) holds one row per quote: `Revision`, `ChangedUtc`, `Cause` (`upload-completed`, `review-decided`, `reopen-decided`, `override`, and since brief 30 item 16 `reopened`: a reopen takes a verified document out of the verified view, so it bumps the revision and re-sends the payload; the revision is also part of every cached feature snapshot's key). **Amendment (brief 31 item 6):** a `cause: reopened` payload may be smaller than the previous revision's (the reopened document's claims leave the verified view); the receiver must not treat a shrinking payload as an error. It holds the newest revision, as always, and the mock receiver's test (`ReopenPayloadTests`) locks this in, `DocumentId` and `DocumentRevision`. The history table keeps every revision with its cause.
- The revision is bumped by `dbo.BumpQuoteRevision`, inside the transaction that changes the quote's data:
  - `dbo.ApplyDecision`, on every decision path (orchestrated, reopened, direct override), when the decision JSON carries `QuoteBump`;
  - CompleteUpload's audit-and-outbox unit.
- Blob mode (dev and tests) keeps it in `quotes/{quoteId}/revision.json`. That store is not transactional, the same caveat as its outbox.

**Rows carry the revision of their own transaction.**
- The app writes `{{quoteRevision}}` where the revision goes: `"quoteRevision": "{{quoteRevision}}"` and the URL `…/quote-payload?revision={{quoteRevision}}`.
- The store replaces the token, in the same transaction, with the number it assigned (`QuoteRevisions.Apply`; the `REPLACE` in `dbo.ApplyDecision`).
- No row is ever written with the token left in it (tested).
- Service Bus messages also carry `quoteRevision` as an application property.

**What each change writes, in one transaction with the change:**
- `review.decided`, on decisions. It now carries `quoteRevision`.
- `quote.revised` `{quoteId, quoteRevision, cause, documentId}`, a new event.
- `quote.payload`, the command. Its body is the summary, `quoteRevision`, `cause`, `readiness` (the brief 24 step 2 object), `changedDocuments[{documentId, revision, reviewState, fieldStatusChanges}]` and `quotePayloadUrl` pinned to the revision.

**Push after review, not before.** Document reviews hold the push; format reviews do not. A confirm-format review means the document was already processed and verified against printed totals, so the push does not wait on a format decision (brief 24 Answers 4).
- When the upload completes with a document still waiting for its review, CompleteUpload bumps the revision and announces `upload.completed` and `quote.revised`, but sends no `quote.payload` yet.
- That command is written when the upload's last document-review item is decided.
- An upload that needed no review pushes at completion, as before.
- Every later decision or override pushes again.

**Payload endpoint.**
- `GET /quotes/{id}/quote-payload` returns `quoteRevision` and `ETag: "q-<n>"`.
- `?revision=<n>` is pinned:
  - a lower `n` answers 409 `revision-superseded`;
  - a higher one answers 404 `no-such-revision`.
- `If-None-Match: "q-<n>"` on the current revision answers 304.
- The revision is read before the account is loaded. A payload is therefore at least as new as the revision it names.

**Receiver contract** (the mock now; the real quote system later).
- `PUT /quotes/{id}` with a body `quoteRevision` lower than the highest the receiver holds is refused with 409 `stale-revision` and changes nothing. The check and the write are one read-modify-write.
- The same or a higher revision is applied.
- The dispatcher records 409 `stale-revision` as **`superseded`** on the outbox row and completes the message: it is not retried and not dead-lettered.
- Any other 4xx is still `rejected` and dead-lettered.
- The idempotency key (outbox id) still makes a redelivery a `replayed`.

**Event Grid stays notification-only.** Event delivery is unordered. A subscriber that acts on data fetches the payload by the revision in `quote.revised`.

## Consequences

- **The quote system sees every change.**
  - Each change arrives as one command, in the transaction of the change itself.
  - An older command arriving late cannot overwrite a newer one.
  - A reader of a pinned URL learns when a newer revision exists.
- **Cost of readiness.** Building `readiness` loads the quote's documents in the activity, before the decision commits. That is about one extra account read per decision. The readiness in the command is computed against that transaction's state; under concurrent decisions on one quote it can trail by one revision, which is why the command is a claim check and the URL is pinned.
- **Changed tests.** The brief-12b reopen test (`A_processed_document_can_be_reopened…`) now expects four `quote.payload` commands, revisions 1–4, one per change. Before, it expected a single command from the completion.
- **One rule for the real quote system.** Alongside the idempotency key, it must refuse a lower `quoteRevision` with 409 `stale-revision`. This is in `docs/poc-plan/closeout/handover.md`.
- **Not done (plan §2 change 7).** Sweep ordering — publishing a quote's pending rows strictly in creation order — is unnecessary while the receiver refuses stale revisions.
- **Who creates a quote (brief 32 Answers 4).** The quote id is the quote-management system's key.
  - **Organisation:** the quote exists before any upload, created by the quote system or an underwriter. `Quotes__ApplicantMayCreate` is false, so an Applicant upload to an unknown quote is a 404 `quote-not-found`. Terraform sets the setting by `environment`; the `applicant_may_create_quotes` variable overrides it.
  - **POC:** it is true, a demo convenience.
