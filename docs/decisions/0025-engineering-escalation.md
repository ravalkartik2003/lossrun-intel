# ADR 0025 — Engineering escalation: a "Needs investigation" queue, keyed by reason and layout fingerprint

**Status:** proposed (brief 26) · **Date:** 2026-10-25

## Context

Some failures are not the reviewer's to fix:
- **One-off events:** a learn that runs out of attempts, a replay failing on a format that was active, an exception nothing handled, a bundle split into a dozen segments, a document costing far more than the path should.
- **Patterns no single review shows:** the same layout coming to review again and again, one rule failing on most of a carrier's documents, a carrier's review rate jumping, one document reopened repeatedly.

Before this brief, each case ended as a review item or a failed upload. Nothing collected them for engineering, counted repeats, or fed them into the tuning loop (`docs/operations/tuning-loop.md`). Engineering may read identifiers, rule ids and counts; reading document values means opening a document, which stays a review-screen action with its own audit.

## Decision

**The record.**
- `dbo.Escalation` (changeset `0021-escalation`, `dbo`, system-versioned) holds one row per **(reasonCode, formatFingerprint)**, with:
  - `count`, `firstSeen`, `lastSeen`;
  - the documents and uploads, at most 50 each;
  - `status`: `new | investigating | resolved | wont-fix`;
  - `resolutionNote`, `tuningItemRef`;
  - an optimistic-concurrency `Version`.
- Blob mode keeps the same record in `escalations/{id}.json`.
- **The fingerprint** is the most specific key available:
  - the document's structural fingerprint (`fp:<identity12>:<header12>`, which is now on `RoutingDecision.FingerprintKey`);
  - for carrier-level patterns, a hash of the carrier name (`carrier:<hash12>`, plus `/R-nn` for a rule);
  - `exception:<step>:<type>`;
  - `document:<id>`.
- **The trace summary** is identifiers and counts only:
  - rule ids;
  - verifier and routing reasons, scrubbed by `EscalationTraceText.Sanitize` (digits become `#`, quoted text and parenthesized examples go, header lists after "header(s):" go, 160 characters at most);
  - counts (claims, hard and soft failures, unmapped headers, model calls, cost, segments);
  - the pipeline step and an exception type.

  It never holds a message or a value. A test scans traces built from a document whose reasons quote a claimant name, a claim number and amounts.

**Merging (`EscalationLedger.Merge`, pure).**
- A signal for a key that does not exist creates the record. Its count is the number of distinct documents, or 1.
- A repeat counts only when it brings a document not seen before, or, for a signal about an upload only (an exception), an upload not seen before (brief 30 item 31; before, every retry of a failing upload counted again). So a retried activity, or the hourly detector finding the same pattern again, changes nothing.
- **Reason codes added by brief 32:** `override-unrevertable` (an override's stored old value is not of its field's type, so the as-reported view shows the current value; keyed `quote:<id>`, raised once per quote revision, the trace names the fields), and `decision-postcommit-failed`. A step after a committed decision failed after three tries: format approval, alias confirmation or retirement, the chat re-index or the manifest. The decision stands. The key is `document:<id>:<step>`, the trace names the step, and an engineer completes it.
- **Reason codes added by brief 30:** `decision-apply-failed` (a reviewer's decision the API acknowledged could not be applied; the document is back in Review and the orchestration waits for the next decision) and `missing-price` (a metered call whose model has no price; keyed `price:<service meter (model)>`; the cost is reported unpriced, never $0).
- A **resolved** case that happens again goes back to `new`, with the old note kept ("reopened: …").
- A **won't-fix** case keeps counting without an event or an alert.
- Each change is one unit: the record (version checked), an audit row (`escalation.raised`, `escalation.updated`, `escalation.status` or `escalation.tuning-item`, with `FieldPath = escalations/{id}`), and the CloudEvent (`lossrun.escalation.raised`, or `lossrun.escalation.updated` for counted, reopened, status and tuning-item changes) through the outbox. A version conflict re-reads and merges again, up to 5 times.

**Triggers.**
- **Single-event, raised by the pipeline.** Raising never fails the document: errors are logged and swallowed.
  - a learn not published (`learn-exhausted`, a coverage signal) or a delta repair not published (`delta-exhausted`, a drift signal on an active format; Answers 4);
  - a matched **active** format with hard failures ending in Review (`replay-failed-active-format`);
  - an orchestration step failing (`unhandled-exception`, keyed by `TaskFailedException.TaskName` and the exception type);
  - more than `maxSegments` segments in one file (`too-many-segments`);
  - document cost above `maxCostUsd` (`cost-over-limit`).
- **Patterns, from the hourly timer** `EscalationSweep` at minute 17, over 37 days of observations (`IRecordStore.EscalationObservationsAsync`):
  - one fingerprint to document review ≥ `fingerprintReviewCount` times in `fingerprintReviewWindowDays`;
  - one rule failing on ≥ `ruleShareOfCarrierDocuments` of a carrier's documents (at least `ruleShareMinDocuments`);
  - a carrier's review rate in the window minus its rate over the preceding `reviewRateBaselineDays` ≥ `reviewRateDelta` (both periods with at least `reviewRateMinDocuments`);
  - a document reopened more than `maxReopens` times.
- **Manual:** "Flag for engineering" on the review screen (`POST /documents/{id}/flag`, any reviewing role); the reviewer's note is the audit rationale.
- **Thresholds** live in `config/escalation.json` (embedded in `LossRun.Data`), never in code. First values:
  - `maxCostUsd` 0.15 (Answers 2: more than five learns' worth is a runaway) and `maxSegments` 3;
  - repeated review: 3 in 7 days;
  - rule share: 80 % of at least 5 documents;
  - review-rate delta: +20 points against a 30-day baseline;
  - reopens: more than 2.

**Alerting** (`infra/escalation.tf`, written and validated, applied with the next wake):
- **Telemetry.** The API writes an Application Insights custom event `lossrun.escalation` (identifiers only) on every change. The sweep writes the metric `lossrun.escalations.open`.
- **First occurrence.** A log alert every 15 minutes fires on a `raised` or `reopened` event. The API raises once per key, so that event is the first occurrence.
- **Daily digest.** A daily log alert fires on open cases > 0.
- **Action group.** The distribution list `escalation_alert_emails`, or the subscription Owners by role when it is empty (no addresses in Terraform for the POC), plus an optional webhook (`escalation_webhook_url`, null by default, for example a Teams incoming webhook).
- **Hibernate.** It leaves the alerts in place; they have no idle cost.

**The queue.**
- **API** (Engineer, UWManager, Admin; the Engineer role reads what a reviewer reads and never decides or overrides):
  - `GET /escalations` with status, reason and carrier filters;
  - `GET /escalations/{id}`;
  - `PATCH /escalations/{id}` with `{ status, note }` (the note is required);
  - `POST /escalations/{id}/tuning-items` with `{ category: a–g, note }`, which writes `tuning/items/{id}.json` for `lossrun tune` and moves a new case to investigating;
  - `GET /tuning-items`.
- **Workbench:** "Investigate" in the navigation (the screen is titled Needs investigation) and "Flag for engineering" on the review screen.

## Consequences

- **Engineering sees repeats without a document.** It sees the count, the step, the rules and the scrubbed reasons. The documents are links; opening one is the ordinary, audited document view.
- **Escalation and review are separate.** Escalation does not change routing, review or the quote payload: it is a side record. The deck table and the corpus runs are unaffected.
- **Carrier hashes.** Carrier-level keys hash the carrier name, so a fingerprint string never names a carrier. The record's `carrier` column does name it, for filtering, the same as `dbo.Document.CarrierName`.
- **The queue is POC-sized.** `ListEscalationsAsync` returns every row and the API filters. A production queue would page and index on `(Status, LastSeenUtc)` (the index exists).
- **Blob mode is not transactional.** As with its outbox, record, audit and event are written in sequence (dev and tests only).
- **The tuning loop** gets its inputs from the queue: brief 27's `lossrun tune` reads `GET /tuning-items` or the folder, and classifies with the same a–g categories.
