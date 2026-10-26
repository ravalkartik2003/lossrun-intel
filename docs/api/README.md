# LossRun POC API (v1) — first cut

The contract is [`openapi.yaml`](openapi.yaml) (OpenAPI 3.1). The DTOs are in `src/LossRun.Api.Contracts`, and `ApiContractTests` fails if the two drift apart.

- **Host:** the Durable Functions app `<function-app>` (ADR 0008).
- **Base URL:** `https://<function-app>.azurewebsites.net/api/v1`.

## Auth

Every route needs credentials. `/webhooks/test` is the one exception: the HMAC signature authenticates it instead.

- **Production shape: Entra ID bearer tokens.** The app roles `Applicant`, `Underwriter`, `UWManager` and `Admin` live on the API app registration. Set `Auth__Mode=entra`, `Auth__TenantId` and `Auth__Audience`.
- **Demo host (deployed today): `LOSSRUN_DEV_AUTH=basic`, i.e. HTTP Basic.**
  - There is one user per role: `applicant`, `underwriter`, `uwmanager`, `admin`.
  - Terraform generates the passwords. They are stored only in Key Vault, secret `devauth-users`, in the form `name:password:Role;…`.
  - To read them (needs Key Vault access):

```bash
az keyvault secret show --vault-name <key-vault> --name devauth-users --query value -o tsv
```

What each role can see:
- **Applicants** see only their own uploads and documents.
- **Underwriter, UWManager and Admin** see all uploads and documents, and can decide reviews.

## Walkthrough (curl)

Set the variables first. Don't commit credentials.

```bash
API=https://<function-app>.azurewebsites.net/api/v1
AUTH="underwriter:<password>"
```

### 1. Upload

Multipart form, one or more `file` fields. The optional `Idempotency-Key` header makes retries safe.

```bash
curl -s -u "$AUTH" -H "Idempotency-Key: demo-001" -F "file=@src/TestShared/fixtures/lossrun-native.pdf" -F quoteId=Q-1001 -F "carrierHint=Synthetic Mutual Insurance Co." "$API/uploads"
```

The response is `202` with `{ "uploadId": "up-…", "statusUrl": "/api/v1/uploads/up-…", "eventsUrl": "/api/v1/uploads/up-…/events", "existing": false }`.

### 2. Follow progress

Server-Sent Events: `event: progress` carries an `UploadStatusView`, and `event: end` closes the stream.

```bash
curl -N -s -u "$AUTH" "$API/uploads/<uploadId>/events"
```

Polling fallback:

```bash
curl -s -u "$AUTH" "$API/uploads/<uploadId>/status"
```

### 3. Manifest

Every file and segment, with its status (processed / kept-not-processed / unsupported / awaiting-review), routing badge and cost line.

```bash
curl -s -u "$AUTH" "$API/uploads/<uploadId>"
```

### 4. Extracted document

The canonical loss run, with lineage per field (page + polygon, or sheet + cell; formatId@version; extractor; confidence), the rule outcomes, the routing decision and the cost.

```bash
curl -s -u "$AUTH" "$API/documents/<documentId>"
```

Page image and the layout slice for click-to-highlight:

```bash
curl -s -u "$AUTH" -o page1.png "$API/documents/<documentId>/source/pages/1"
```

```bash
curl -s -u "$AUTH" "$API/documents/<documentId>/source/pages/1/layout"
```

Payload for the pricing engine (see [quote-payload.md](quote-payload.md)):

```bash
curl -s -u "$AUTH" "$API/documents/<documentId>/quote-payload"
```

### 5. Review queue

There are two kinds of review item:
- **`document-review`** — a hard-rule failure, a carrier mismatch, or a learn that could not verify. Processing waits for the decision.
- **`confirm-format`** — the first documents of a newly learned (shadow) format. They are already processed; the reviewer confirms the format.

List the pending items:

```bash
curl -s -u "$AUTH" "$API/reviews?state=pending"
```

To fix a misread subtotal, send an override with a rationale. The document is re-validated and the change is audited:

```bash
curl -s -u "$AUTH" -H "Content-Type: application/json" -d '{"action":"override","overrides":[{"fieldPath":"policyPeriods[0].subtotals.medical.paid","newValue":9200,"rationale":"printed subtotal disagrees with its claim rows"}],"rationale":"subtotal misprint"}' "$API/reviews/<documentId>/decision"
```

To confirm a new format, accept it: on a `confirm-format` item `promoteToFormat` defaults to true. Send `promoteToFormat: false` to approve the document only and keep the format in shadow. A reject never promotes, and accept or reject with `overrides` answers 400 `overrides-not-allowed` (brief 24):

```bash
curl -s -u "$AUTH" -H "Content-Type: application/json" -d '{"action":"accept","rationale":"format looks right"}' "$API/reviews/<documentId>/decision"
```

### 6. Format registry

All formats, with live status (shadow / active):

```bash
curl -s -u "$AUTH" "$API/formats"
```

One format, with its spec:

```bash
curl -s -u "$AUTH" "$API/formats/fictional-casualty-group-pdf?version=1"
```

Learned glossary aliases (ADR 0019): headers a verified learn mapped that `glossary.json` did not know.
- **Candidate:** never used.
- **Active:** confirmed by a reviewer, or verified on 3 documents across 2 formats.
- **Retired:** corrected or rejected.

```bash
curl -s -u "$AUTH" "$API/glossary/aliases?formatId=fictional-casualty-group-pdf"
```

A `confirm-format` decision may carry `aliases`: `[{ header, canonicalField, action: accept | edit | reject, newField? }]`. The aliases not listed are accepted with the format. `lossrun glossary export --base "$API" --out glossary.learned.json` writes them for review.

### 7. Quote analytics (Use Case 2, the analytics work)

These endpoints are for Underwriter, UWManager and Admin; an Applicant gets `403`. Definitions are in `docs/analytics/catalog.yaml`; design notes are in ADR 0011.

| Endpoint | Returns |
|---|---|
| `GET /quotes/{quoteId}/features?view=adjusted\|reported` | Cached baseline feature snapshot: every catalog metric with breakdowns, contributing claims and the overrides behind it. |
| `POST /quotes/{quoteId}/features/what-if` | Body `{ "scenario": { "reserveFactorBps": 1000, "capPerClaim": 250000, "excludeCatastrophicAbove": 500000, "includeCT": false, "includeNonCA": false, "trendFactorBpsByYear": {"2023": 10500}, "excludeClaimIds": [] }, "context": { "payrollByYear": {"2023": 1200000} } }`. Returns baseline, scenario and per-metric delta. |
| `GET /quotes/{quoteId}/claims?view=…` | The per-account claims payload (amounts in cents, ≤ 5,000 claims) the client twin `src/web/packages/metrics` recomputes from, with baseline flags. |
| `GET /quotes/{quoteId}/quote-payload` | Quote payload v2 (quote-payload.md): both feature views, periods, claims with flags, lineage. |
| `GET /catalog` | The catalog as JSON. |

Latency on the deployed host, sample quote (ADR 0011):
- cached features: server p50 42 ms / p95 84 ms; client round trip p50 159 ms / p95 280 ms;
- the first request per instance after a deploy: 2–3.5 s.

To build the pricing memo:

```bash
lossrun report --quote <id> --out memo.docx
```

It writes `memo.docx` and `memo.json`.

### 8. Workbench endpoints (ADR 0012)

These are what the UI needs beyond the API work through the analytics work. All are underwriting roles only, except `/me`.

| Endpoint | Returns |
|---|---|
| `GET /me` | The caller: `userId`, `role`, `canReview`. The UI's role-aware navigation reads this. |
| `GET /quotes[?includeRetired=true]` | Quotes with loss-run documents: documents, claims, insured, valuation, pending reviews, `displayName`, `retiredAt`. Feeds the quote selector. Retired quotes are left out unless asked for (`docs/poc-plan/closeout/README.md`). |
| `POST /quotes/{quoteId}/owner` | Brief 32 item 4. UWManager and Admin. `{ rationale, applicantUserId? }` transfers the quote to an Applicant, or clears the owner (null: underwriter roles only). Audited as `quote.owner` (old → new); `dbo.QuoteOwner` is system-versioned, so every earlier owner stays answerable. An Applicant claims a quote only with an accepted upload to an id nothing names yet (no owner record, documents, uploads, context, revision or label), and only where `Quotes__ApplicantMayCreate` is true (the POC). Elsewhere such an upload is 404 `quote-not-found` (brief 32 Answers 4). |
| `PATCH /quotes/{quoteId}` | UWManager and Admin. `{ rationale, displayName?, retired? }` sets the display name (an empty string clears it) and retires or restores the quote. The id never changes and nothing is deleted. Audited as `quote.rename`, `quote.retire` and `quote.restore` (ADR 0015; `docs/poc-plan/closeout/README.md`). |
| `GET` and `PUT /quotes/{quoteId}/context` | Submission context (ADR 0011): `payrollByPeriod[]` and `fteByPeriod[]` as `{ effectiveDate, amount, policyNumber? }`, plus `lastDayWorked`. Notes below. |
| `GET /documents/{documentId}/audit`, `GET /quotes/{quoteId}/audit` | The audit trail, newest first. Notes below. |
| `GET /costs` | POC-to-date metered cost: by path (replay / learn / delta / review), learn-once vs replay per carrier, and per-document cost lines. `reconciliation` gives metered vs billed from the latest `lossrun eval reconcile --publish`: the ratio, the export date, the platform bill and a scope note (`docs/poc-plan/closeout/README.md`). |
| `GET /quotes/{quoteId}/memo?format=docx\|json` | The pricing memo (Word, or its JSON twin). Audited. PDF comes from the export pipeline: [memo-pdf.md](../analytics/memo-pdf.md). |
| `POST /quotes/{quoteId}/flags` | Pushes per-claim flags to the quote. Recorded and audited (`quote.flags.pushed`); Service Bus delivery is the integration work. |
| `GET` and `POST /quotes/{quoteId}/follow-ups` | Assigns or lists follow-up actions, as `{ assignee, action, note?, claimIds?, due? }`. Audited as `quote.followup`. |

**Quote ids are immutable.** A quote is renamed by its display name only. Ledger rows keep the original id on purpose: the audit trail must show that, say, `Q-LOAD-1000` and "Q-DEMO-1000 · Large Account (synthetic)" are the same quote (`docs/poc-plan/closeout/README.md`).

Quote context:
- An upload can carry the same fields as multipart `payrollByPeriod`, `fteByPeriod` and `lastDayWorked`. They merge into the quote context.
- Feature snapshots key on the context.
- Metrics without an input print `notMeasurable` with the reason (catalog 1.1.0).

Audit trail:
- On SQL, each row shows the ledger transaction that committed it (`dbo.vAuditTrail`). The API's database user has `VIEW LEDGER CONTENT` (changeset 0013).
- Quote-level events carry `fieldPath = quotes/{quoteId}`.
- The engine's committing principal is shown as "app managed identity"; tenant ids are not returned.

Source layouts: `GET /documents/{id}/source/pages/{n}/layout` serves PDFs and spreadsheets alike. For a workbook, page n is sheet n: its cells carry `a1`, and `sheets` lists the workbook's sheets. The UI renders that grid. Page images (`/source/pages/{n}`) exist for PDFs only (409 otherwise).

CORS: the Function App allows only the Static Web App origins and `http://localhost:5173` (ADR 0012, `infra/web.tf`).

### 9. Chat (ADR 0013)

- **Review-status filter (brief 30 item 8, brief 31 item 5).** `search_documents` leaves out rejected and superseded documents in two places: the index filter (`reviewStatus`) and a store-side post-filter. The index side is the app setting **`Chat__ReviewStatusFilter`** (default `true`). Turn it off only when the Functions were deployed before the index gained `reviewStatus`: the search call then stops failing, and the post-filter still leaves rejected pages out.

| Endpoint | Returns |
|---|---|
| `POST /chat/turns` | One answer: `{ scope: org\|quote\|workspace, question, quoteId?, workspaceId?, conversationId? }` returns a `ChatTurnResult`. With `Accept: text/event-stream` it streams events instead: `tool` (each tool call as it finishes), `delta` (`{ text }`), then `final` (the turn) or `error`. |
| `GET /chat/conversations/{id}` | The caller's own conversation, oldest turn first. Another user's conversation is 404. |
| `POST /workspaces`, `GET`/`DELETE /workspaces/{id}` | An ad-hoc workspace, private to its creator, expiring after 72 h (`Chat__WorkspaceTtlHours`). Upload into it with multipart `workspaceId` (not together with `quoteId`). Delete removes its chunks from the index; the files stay in Blob (ADR 0005). |

```bash
curl -N -u "$LOSSRUN_API_USER:$LOSSRUN_API_PASSWORD" -H "Accept: text/event-stream" -H "Content-Type: application/json" \
  -d '{"scope":"quote","quoteId":"Q-DEMO-11","question":"What is the largest open claim?"}' "$BASE/chat/turns"
```

How a turn works:
- **Scope.** The scope comes from the request and the caller, never from the model. Applicants may use only their own workspace (403 otherwise); underwriting roles may use all three. Every tool pushes the scope down as a filter and post-filters what comes back.
- **Tools.** The model has five (brief 24 step 5 added the last two):
  - `search_documents`: AI Search `lossrun-docs`, hybrid plus semantic ranking. Kept (not processed) documents are included.
  - `query_claims`: the scope's claims, as adjusted or as reported, with filters.
    - **Rows** carry their document's `reviewState` and the claim's fields that are not approved (`fieldStatus`).
    - **`status: open` includes reopened claims,** as `open_claim_count` does.
    - **Totals** carry each component's paid, reserve and incurred (brief 24 step 7).
  - `compute_metric`: catalog metrics, with optional filters and a what-if scenario.
  - `explain_value`:
    - **With a claim number:** one claim value. It returns the printed value with an `S#` ref to the unmodified page chunk, the current value with its field status, and the corrections chain: reviewer, role, time, revision, the field's previous override, and the rationale verbatim.
    - **Without one** (brief 24 step 7): every correction in scope, newest first, at most 50, with a `more` count of the rest.
  - `get_review_status`: per document in scope, its review state, revision and field-status counts, and whether the quote is ready.
- **Tool policy by role.** Applicants have no `explain_value` and no `reported` view. A tool the role may not use is not offered to the model, and a call to it is refused and traced as `denied by tool policy`.
- **Out of scope.** Rejected documents are out of every chat scope.
- **Citations.** Inline refs `S#` (page), `C#` (claim, value or review status) and `M#` (metric) must be ones this turn's tools returned. Any other ref is dropped and listed in `invalidCitations`.
- **Fact guard.** Every quoted passage, date and reviewer named after "by" or "reviewer" in an answer must appear in this turn's tool output. Anything else is listed in `unverified` and as a `fact-guard` entry in the ledger row's tool trace.
- **The index is never edited by a decision.** A decision changes the result, never the indexed page text (tested).
- **Memory (brief 24 step 6).** A turn sees a *Conversation facts* block: the documents, claim numbers and metrics every earlier turn cited, as identifiers, never refs. It also sees the newest question/answer pairs verbatim, within `Chat:HistoryTokenBudget` (default 6,000 tokens, at least two pairs, at most `Chat:HistoryTurns`, default 12). The ledger row's tool trace records which earlier turns were verbatim (`history`).
- **Follow-ups (brief 24 step 7).**
  - **What is kept.** Each turn records its last result set and the filter that produced it as a `last-result` trace entry.
  - **What the next turn sees.** The facts block opens with "Last result (turn N): …", listing the tool, the filter and up to 10 rows (claim number, carrier, status, year, incurred).
  - **The rule.** Prompt rule 8 (`chat-v3`) resolves "it", "its", "those" and "of those" against the last result first, keeps the last filter for "of those", and asks one clarifying question when a phrase has two readings.
- **Record.** Each turn is written to `dbo.ChatTurn` (an append-only ledger) with a `chat.turn` audit event and usage events. `usage.costUsd` is the turn's metered cost.
- **Pages.** A page citation of a kept segment resolves through `GET /documents/{id}/source/pages/{n}` like any document.

CLI: `lossrun chat init-index | reindex (--uploads <ids> | --from-api) | ask --scope … --question …`. Evaluation: `lossrun eval chat` (docs/evaluation/results/2026-10-23-chat) and `lossrun eval chat-corrections` (brief 24: ten questions on corrections it plants; first run in step 7).

### 10. Quote-system integration, reopen, direct overrides (ADR 0014)

| Endpoint | Returns |
|---|---|
| `POST /quotes/{quoteId}/flags`, `/follow-ups`, `/annotations` | Recorded with their audit row and an outbox row in one transaction, then delivered to the quote system: Service Bus queue `quote-commands` → dispatcher → quote system, with `Idempotency-Key` = outbox id. The flags result's `delivery` names the outbox id. |
| `GET /quotes/{quoteId}/memo` | Also pushes the memo's link to the quote record (`quote.memo-link`). |
| `GET /quotes/{quoteId}/integration`, `GET /integration/outbox` | What was sent (commands and events), when it was published, and what the quote system answered (`delivered`, `replayed`, `superseded` (409 stale-revision, ADR 0024) or `rejected`). |
| `POST /reviews/{documentId}/reopen` | Sends a processed document back to review with `{ reason }` (audited as `review.reopened`). Its decision applies at once. |
| `POST /documents/{documentId}/overrides` | `{ overrides: [{ fieldPath, newValue, rationale }], rationale }` on a processed document, outside a review. 409 while a document review is open. Since the close-out work it is applied by `dbo.ApplyDecision` in one round trip (ADR 0015). |

The quote record gets the analysis summary as `quote.payload`, a claim check: the summary plus the quote-payload URL. Brief 24 step 3 (ADR 0024) changes when and how:
- **When.** Every change to the quote's data bumps its revision (`dbo.QuoteRevision`): upload completed, review decided, reopened decision, override. Each change writes its `quote.payload` command in its own transaction. An upload with documents awaiting review sends its first command when the last one is decided.
- **Body.** The summary, `quoteRevision`, `cause`, `readiness`, `changedDocuments[]` and `quotePayloadUrl` pinned with `?revision=<n>`.
- **Fetching the payload.** `GET /quotes/{id}/quote-payload` returns `quoteRevision` and `ETag: "q-<n>"`.
  - `?revision=` lower than the current one: 409 `revision-superseded`. Higher: 404.
  - `If-None-Match` matching the current ETag: 304.
- **The receiver.** It answers a lower `quoteRevision` with 409 `stale-revision`, which the outbox records as `superseded`.

Events go to Event Grid as CloudEvents 1.0, types `lossrun.upload.completed`, `lossrun.review.decided`, `lossrun.quote.revised` (brief 24: `{quoteId, quoteRevision, cause, documentId}`), `lossrun.format.learned`, and (brief 26) `lossrun.escalation.raised` / `lossrun.escalation.updated` (`{escalationId, reasonCode, formatFingerprint, formatRef, status, count, documents, firstSeen, lastSeen, change}`). The CloudEvent id is the outbox id, and the 07 HMAC webhook is unchanged.

### 11. Engineering escalation (brief 26, ADR 0025)

| Endpoint | Returns |
|---|---|
| `GET /escalations?status=&reason=&carrier=` | The Needs investigation queue (UWManager, Admin), most recently seen first; `status` is `open` (new or investigating), `new`, `investigating`, `resolved` or `wont-fix`. One row per (reason, layout fingerprint), with its count, documents (≤ 50), and a trace of identifiers and counts. |
| `GET /escalations/{id}` | One case. |
| `PATCH /escalations/{id}` | `{ status, note }`; the note is required (audit rationale, resolution note). |
| `POST /escalations/{id}/tuning-items` | `{ category: a–g, note }`: writes `tuning/items/{id}.json` for the next `lossrun tune` batch; a new case moves to investigating. |
| `GET /tuning-items` | The tuning items, oldest first. |
| `POST /documents/{documentId}/flag` | `{ note }` from any reviewing role: "Flag for engineering". |

Triggers, thresholds (`config/escalation.json`) and alerting are in the runbook, §Escalations.

`GET /quotes/{quoteId}/features` answers with `X-Feature-Snapshot: hit|miss`.

## Webhook

When an upload completes, the app POSTs an `upload.completed` `WebhookEnvelope` to `Webhook__Url`.
- **Signature header:** `X-LossRun-Signature: sha256=<hex HMAC-SHA256 of the raw body>`.
- **Replay protection (brief 30 item 30):** the signed body carries `sentAt` and a `nonce`; the receiver refuses a delivery more than 5 minutes from its clock (401 `stale`) or a nonce it has seen (409 `replayed`).
- **Secret:** Key Vault `webhook-secret`.
- **Today:** the URL points at the app's own `/webhooks/test` sink, which verifies the signature and stores the payload under `extractions/webhooks/received/`.
- **Later:** the workbench work moves this to Service Bus / Event Grid with an outbox.

## Submission-engine simulator

`src/LossRun.Simulator` posts N concurrent uploads of the fixture mix. It prints time-to-first-claim and time-to-complete (p50/p95) and the metered cost.

Run it from `src/` after setting `LOSSRUN_API_USER` and `LOSSRUN_API_PASSWORD`:

```bash
dotnet run --project LossRun.Simulator -- --concurrency 5 --sse
```

## Brief 30 contract changes

- **Overrides are typed** (`POST /reviews/{id}/decision`, `POST /documents/{id}/overrides`). `newValue` is JSON of the field's canonical type: a number for money and counts; a string for identifiers and text (a digit-only claim number stays a string); `true`/`false`; `yyyy-MM-dd` for dates; an enum's schema name; `{ "code", "codeSystem" }` for a coded field, on its own path or on its `.code` path. A wrong type is **400 `bad-override-value`** naming the path, before any 202.
- **Decisions are idempotent.**
  - The API gives every decision a `decisionId`; its outbox rows are keyed on `(decisionId, kind)`, so a retried activity applies it once.
  - A decision that cannot be applied after the 202 leaves the review open and raises the escalation `decision-apply-failed`.
  - A step after a committed decision (format approval, alias review, chat re-index, the manifest) that still fails after three tries raises `decision-postcommit-failed`, naming the step; the decision stands (brief 32 item 3).
  - The loser of two concurrent decisions on the same review answers 409 `not-pending` and changes nothing (brief 32 item 1).
  - An override whose path names an index the document lacks (`claims[99]` on a period of three) is a 400 `bad-override-path`, before the 202 (brief 32 item 5).
  - **`formatChanges: { "periods.fillDown": true }`** (brief 34 A3) answers a review whose reason starts "policy cell blank on …": the carrier's export fills the policy down.
    - The decision publishes a new **shadow** version of the document's format with `periods.fillDown: true`. It becomes active on approval (a promoting decision) or after 3 clean replays (ADR 0018 amendment 2).
    - The document is replayed with that version.
    - The change is audited as `format.change` (`periods.fillDown`: false → true, with both format refs).
    - Any other key or value, a reject, or a review without the question is a 400 `bad-format-change`.
  - **`formatChanges: { "carrierNames.add": "<name as printed>" }`** (brief 35 Answers 2) answers a review whose reason starts "carrier name on the page differs from the format's: …": the printed name is another legal name of the same carrier.
    - The decision publishes a new version of the format with the name added to `carrierNames`. Only the carrier's names change, not the layout, so the version keeps its parent's status (an active format stays active), and the next document that prints that name replays clean.
    - Names compare without corporate words (Inc, Co, Company, Corp, Insurance, Assurance, Indemnity, Casualty, Mutual, Group, a trailing "of America", The): "X Indemnity Co" and "X Insurance Company" are one carrier with no alias; "X Property Casualty Co of America" needs one.
    - Audited as `format.change` on `carrierNames` (the names before → after, both format refs). It can be sent with `periods.fillDown`; the version is then shadow.
  - On a reopened review the API answers **409 `decision-not-applied`**.
- **Reopen** bumps the quote revision (cause `reopened`) and re-sends `quote.revised` and the payload (ADR 0024).
- **A direct override** on a document whose confirm-format review is pending applies the values and leaves the format decision open.
- **Quote ownership.** An Applicant uploads only to a quote they created, or to a new one, which becomes theirs. Otherwise the answer is **403 `not-quote-owner`**. Underwriter roles attach to any quote (`dbo.QuoteOwner`, changeset 0022).
- **Blob-reference uploads** name `https://<this account>/uploads/…` only:
  - `uploads/incoming/<your user id>/…`, or an upload of your own; underwriter roles may name any upload.
  - Dot segments, encoded separators, other containers and other hosts are **400 `bad-blob-reference`**.
  - Another caller's prefix is **403 `forbidden-blob`**.
- **Duplicate file names** in one upload are all kept, as `run.csv` and `run (2).csv`.
- **The manifest item** gains `unnormalizedValues`: printed values the replay could not normalize. Each one sends the document to Review, with the field and page or cell in the reasons.
- **Formats and learned aliases** (`GET /formats`, `/formats/{id}`, `/glossary/aliases`) are for Underwriter, UWManager, Admin and Engineer. **`POST /formats/{id}/versions/{v}/unretire`** (Engineer, Admin; audited `format.unretire`) is the only way a retired version comes back: an approval or an alias review never does it.
- **Errors.**
  - An unhandled exception is a 500 `internal` naming a correlation id, never the exception text.
  - A body that is not JSON is 400 `bad-json`.
  - A rationale or reason over 2,000 characters is 400 `too-long`.
- **Cost.** A metered line with no price is reported unpriced (`DocumentCost.unpricedLines`, `unpriced`) and raises `missing-price`.
- **Chat.** `search_documents` leaves out rejected (and superseded) documents unless the model asks for history with `includeRejected`; such results carry `fromRejectedDocument: true`. Index chunks carry `reviewStatus`.

## Limits

- Messages between orchestration steps carry ids and blob paths only, because the Durable Task Scheduler caps payloads at 1 MB.
- An upload request may be up to 100 MB, the Functions host's limit. Above that it gets 413 `upload-too-large`; split the batch. Other routes keep the worker's default body cap (`docs/poc-plan/closeout/README.md`).
- HTTP requests are capped at 230 s by the front end. The event stream closes after about 200 s; clients then reconnect or poll.
