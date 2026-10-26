# ADR 0014 — Quote-system integration (SQL outbox, Service Bus, Event Grid), reopen, and what the load test changed

**Status:** accepted · **Date:** 2026-10-23

## Context

The research (`docs/analytics/underwriter-analytics.md`, Key Question 6) gives the integration shape:
- commands to the quote system go over Service Bus, using sessions, duplicate detection and a dead-letter queue;
- notifications go out as Event Grid CloudEvents;
- both sit behind a transactional outbox, so every push is audited and delivered at least once with an idempotency key.

The outbox sample the research cites is Cosmos-specific; it notes that a SQL outbox table plus a relay is the obvious analogue. Our system of record is Azure SQL (ADR 0010).

The integration work also asks for:
- a mock quote system shaped like Guidewire Cloud API;
- reopen-for-review (ADR 0012);
- a 25-underwriter load test with p95 criteria.

## Decision

1. **Outbox: `dbo.Outbox` in Azure SQL, written in the same transaction as the AuditEvent** (changeset 0015, `ClaimCommandService.AuditAsync(audit, actor, outbox)`).
   - **Row id:** the row id is three things at once: the Service Bus `MessageId`, the CloudEvent `id`, and the `Idempotency-Key` the quote system receives.
   - **Relay:** publishes first and marks second:
     - a fast path right after the command, not awaited by the HTTP response;
     - a sweep timer every 30 s, which makes delivery at-least-once.
   - **Crash after publish, before mark:** the same id is re-published. Duplicate detection drops it, and the receiver's idempotency key would too.
   - **Crash before publish:** the row stays pending until a sweep.
   - **Tests:** `OutboxTests` (fault-injecting publisher), and a SQL test proving the audit and outbox rows commit atomically.
   - **Blob mode** (dev/tests) writes the two separately. That mode is not production.
2. **Commands go to a Service Bus Standard queue `quote-commands`.**
   - Sessions with `SessionId = quoteId`, so commands for one quote are delivered in order.
   - Duplicate detection over a 1-day window.
   - Dead-lettering after 10 deliveries, and immediately on a 4xx from the quote system.
   - A session-enabled dispatcher function calls the quote system and records its answer on the outbox row (delivered | replayed | rejected).
   - Standard is the cheapest tier with sessions and duplicate detection, at $10/month.
3. **Events go to an Event Grid topic using the CloudEvents 1.0 schema.**
   - Publishing is Entra-only (local auth off).
   - Dead letters go to Blob `eventgrid-deadletter`.
   - The mock subscribes over the CloudEvents webhook handshake.
   - Events: `lossrun.upload.completed`, `lossrun.review.decided`, `lossrun.format.learned`.
   - The 07 HMAC webhook stays.
4. **Mock quote system.** A separate Flex Consumption app, `<quote-mock-app>-*`.
   - **Routes:** `GET`/`PUT /quotes/{id}` and `POST /quotes/{id}/annotations|flags|followups`. Responses use the `{ data: { attributes } }` shape.
   - **Idempotency:** writes are keyed by `Idempotency-Key`; a repeat returns the first response with `replayed: true`.
   - **Storage:** state lives in Blob, not memory, because the app scales out.
   - **Auth:** a function key held in Key Vault (`quote-system-key`), as with someone else's API. The dispatcher reads it through a Key Vault reference.
   - **Large payloads:** the quote payload v2 goes by claim check (URL plus summary), because a 1,000-claim payload exceeds Service Bus Standard's 256 KB.
5. **Reopen and direct override apply outside the upload orchestration**, through the same command path as review decisions (`DocumentCommands`). That orchestration has finished, so it is no longer waiting for decision events.
   - **Reopen:** `POST /reviews/{id}/reopen` sets `ReviewItem.Reopened` (changeset 0015) and audits `review.reopened`.
   - **Direct override:** `POST /documents/{id}/overrides` retries when a concurrent decision takes the revision first (up to 5 attempts).
6. **What the load test changed** (each found by measuring, then fixed):
   - **Burst learning.** When a burst of documents of one new carrier format arrives in one upload, a document that queued for the learn slot now routes again once it gets the slot, and replays the format learned meanwhile.
     - Before: 35 spreadsheets meant 35 sequential learns, about 1 per minute, with about a third failing to review.
     - After: one learn, then replays at $0. Test: `A_burst_of_one_new_format_learns_once…`.
   - **Round trips.** The SQL database is in Central US and the app in East US 2 (ADR 0003): **~41 ms per SQL round trip**. Fixes:
     - one statement for a revision's rule outcomes (EF had split them into many batches);
     - a two-column row lock instead of `SELECT *`, which carried ~0.5 MB of result JSON;
     - a one-query document head;
     - the account load in two queries, not two per document;
     - an in-process cache of parsed document results per (document, revision), so an override reloads one document, not 34.

     Results: overrides went from 2.5 s to 0.7 s, a features recompute after an override from 3.0 s to 0.5 s, and a what-if from 1.5–2.8 s to 0.4 s.
   - **Embeddings retry on 429** with `Retry-After`, up to 5 tries. A burst of indexing had exceeded the deployment's request rate.
   - **`X-Feature-Snapshot: hit|miss`** on features responses, so "p95 features (cached)" is measured on cached reads, with recomputes reported separately.
7. **Azure Load Testing** (Terraform `azurerm_load_test`). The JMeter plan, properties and pass/fail criteria are checked in under `infra/loadtest/`.
   - Credentials come from the Key Vault secret `devauth-users`, read by the test's managed identity.
   - The tests themselves are created with `az load test create/update`; there is no azurerm resource for a test definition.
8. **Defender for AI Services** is enabled (subscription plan, the chat-service work) at $0.0008 per 1,000 tokens.

## Consequences

- The quote system is decoupled. A quote-system outage piles commands up in Service Bus, or dead-letters them after 10 tries, while the API keeps answering.
- Cross-region SQL latency is the dominant cost of every write. Co-locating SQL with the app (East US 2, once vCore quota allows; ADR 0003) would cut an override to about a third. That move is recorded as a production prerequisite.
- The in-process document cache is per instance. On a scaled-out host, each instance warms itself; entries never go stale, because a revision is immutable.

## Amendment (2026-10-23, the export work)

- **The CloudEvent `source` is configuration:** `Integration:EventSource` (app setting `Integration__EventSource`), default `/lossrun/api` (`EventGridOutboxPublisher.DefaultSource`). It was the literal `/lossrun-poc/api`. Subscribers should match on `type` (`lossrun.*`), not on `source`. The mock quote system now records each event's `source` with the event. The POC Function App picks up the new default at its next deploy; no app setting is needed.
