# Loss Run Intelligence — review and plan for six improvements

Date: 2026-10-25 · Repository state: `main` at `ce2939c` (brief 23 closed) · Review only: nothing was edited, deployed, committed or pushed. TAM integration is out of scope.

Every observation below was verified against the current code; citations are `path:line`. Where something does not exist, the text says "not found". Labels used: **[Assumption]** for things not verifiable from the repo; **[Optional]** for enhancements beyond the stated requirement.

---

## 0. Summary

| # | Topic | Confirmed gap | Priority | Timing |
|---|---|---|---|---|
| 1 | Field-level review status | No per-field status exists; approval/rejection is per document; no readiness concept; rejected documents still feed the payload | **P1** | POC (data model + readiness), production (UI polish) |
| 2 | Downstream updates | Decisions emit a count-only event; payload is pushed *before* review; no quote-level revision; receiver overwrites unconditionally | **P1** | POC |
| 3 | Review screen | Typed-but-unstaged edits are lost silently; server drops overrides sent with accept/reject; no Reset; format activation blurred with approval; UI/API audit-action mismatch breaks the as-reported view live | **P1** (two defects), P2 (labels) | POC |
| 4 | Chat memory | Last 4 turns only, raw text, prior citations not replayed; separate from the tool-iteration limit (6); no compaction | P2 | POC (budgeted history), production (framework compaction) |
| 5 | Chat correction explanations | No tool returns override records; review status invisible to the model; Search holds original page text and is never overwritten (good) | P2 | POC |
| 6 | Production token protection | MSAL in `sessionStorage`, direct SPA→API bearer; no BFF, no cookies, CORS without credentials; Entra sign-out not wired | P3 (assessment now, build later) | Production |

Three defects found during the review that are not in the six asks but block them and should be fixed first: the server silently discards `overrides` on `accept`/`reject` (`src/LossRun.Data/Records.cs:252`); the UI reads `review.override`+`fieldPath` audit rows that the backend never writes (`src/web/apps/workbench/src/document/model.ts:92-99` vs `src/LossRun.Data/ClaimCommandService.cs:169-174`), so override chips and the as-reported toggle are empty against the live API; and the `quote.payload` command is pushed at `CompleteUpload` before the review loop runs (`src/LossRun.Orchestration/Orchestrations.cs:107` vs `:122-145`).

---

## 1. Field-level review status

### Current behaviour (verified)
- The decision unit is one `ReviewItem` per document (`db/changelog/0005-override-audit.sql:98-111`; `src/LossRun.Api.Contracts/ApiModels.cs:140-157`). One `Action` for the whole document: `accept | override | reject` (`ApiModels.cs:159-169`).
- Document state: `dbo.Document.ReviewState` (`db/changelog/0003-document-claim.sql:20`) takes `null`, `pending`, `reopened`, then the raw action string after a decision (`src/LossRun.Data/Records.cs:288`).
- Per-field review status: **not found**. `LineageRecord` has extractor, confidence and location only (`src/LossRun.Contracts/CanonicalModel.cs:148-167`); `dbo.ExtractedValue` has no status column (`db/changelog/0004-extracted-value.sql:12-34`). The only per-field signals are implicit: lineage `Extractor == "manual"` after an override (`Records.cs:258-262`) and `AffectedFields` of a failing rule (UI `failingPaths`, `src/web/apps/workbench/src/document/model.ts:151-153`).
- Overrides are rich: `dbo.Override` holds old/new value, rationale (non-empty), user, role, time, revision, `SupersedesOverrideId` (`0005-override-audit.sql:14-35`), append-only ledger.
- Reject: same path as accept with no overrides; sets `ReviewState="reject"`, bumps revision, manifest item → `rejected` (`Records.cs:286-288`; `src/LossRun.Orchestration/Activities.cs:404`). Document, claims and result remain and are **not filtered anywhere** downstream (`src/LossRun.Data/SqlRecordStore.cs:215-220, 240-250`). No test exercises reject.
- Readiness: **not found** (grep `readiness|quoteReady`). Hard rules route to review before a decision (`src/LossRun.Formats/FormatRouter.cs:158-166`); after a decision, remaining hard failures are only text in the manifest reason (`Activities.cs:399-405`). No "required field" concept in `src/LossRun.Rules`; schema `required` lists only `schemaVersion, documentId, noLossesStatement, policyPeriods` (`docs/evaluation/canonical-schema.json:8`).
- Quote payload v2 includes every document regardless of `ReviewState`, with `reviewState` per document and a `pendingReview` count only (`src/LossRun.Orchestration/QuoteAnalytics.cs:299-383`).

### Confirmed gap
No per-field approved / unresolved / rejected state; no readiness rule; rejected and pending documents are delivered as usable data.

### Proposed changes
1. **Field status as a derived view, not a new ledger.** Compute `FieldStatus` per (document, revision, fieldPath) from facts that already exist, so no new mutable state is introduced:
   - `rejected` — the document's `ReviewState == "reject"` (whole-document rejection preserved; every field inherits it), or a field explicitly rejected (see 2).
   - `approved` — document decided (`accept`/`override`) **and** the field is not in any hard-rule `AffectedFields` at the current revision, or the field has a `manual` override at the current revision.
   - `unresolved` — field in a hard-rule failure at the current revision, or field required and null, or document still `pending`/`reopened`.
   Implement in `src/LossRun.Rules` (new `FieldStatusEvaluator`) and surface in `DocumentResult` as `fieldStatus: { [fieldPath]: approved|unresolved|rejected, reason? }` (`ApiModels.cs` + `docs/api/openapi.yaml`).
2. **Per-field reject in the decision** [small schema change]: `FieldOverride` gains `action: "set" | "reject"` (default `set`); a rejected field is stored as an override with `NewValue = null` and `Source = "field-reject"` in `dbo.Override` (no new table; the ledger stays append-only). `rationale` stays required.
3. **Required-field list in the rule catalog.** Add `R-33 required-for-quoting` with the field list as data (`docs/evaluation/rule-catalog.json`), severity `hard` at the **quote** level only — it must not change document routing (keep the deck table unchanged). Proposed initial list [Assumption — to confirm with underwriting]: `document.carrier`, `document.valuationDate`, `policyPeriod.start/end`, `claim.claimNumber`, `claim.dateOfInjury`, `claim.status`, `claim.total.incurred`.
4. **Quote readiness** as a computed object in `QuoteAnalytics`: `readiness: { ready: bool, blockingFields: [{documentId, fieldPath, status, reason}], rejectedDocuments: [...], pendingDocuments: [...] }`. `ready` is false while any required field is `unresolved` on any non-rejected document of the quote. Exposed on `GET /quotes/{id}/quote-payload`, `GET /quotes/{id}/analytics`, and the Analytics screen header.
5. **Exclusion rules** (shared by payload, analytics, chat scope): rejected documents' claims are excluded from `claims`, `features`, `periods` and from chat `DocumentsInScopeAsync` (`SqlRecordStore.cs:373-387`), but listed under `excluded[]` with reason and revision so the audit trail stays visible; unresolved fields are delivered as `null` with the field flagged in `fieldStatus`, never as the extracted-but-unverified value.

### Concrete example
Document D1 (revision 2, decided `override`) has 10 key fields. Rule R-10 (paid ≤ incurred) still fails on claim 7's `total.paid`; the reviewer did not touch it. `fieldStatus` → 8 fields `approved`, `claim[7].total.paid` and `claim[7].total.incurred` `unresolved` (reason `R-10`). Quote Q-1 payload carries all 8 approved values, `null` for the two with `fieldStatus.unresolved`, and `readiness.ready = false` with `blockingFields` naming them. Reviewer later rejects document D2 outright: D2 disappears from `claims` and `features`, appears under `excluded[] = [{documentId: D2, reason: "rejected", revision: 3}]`; readiness is re-evaluated without it.

### Acceptance tests
- Unit (`LossRun.Rules.Tests`): status matrix — pending doc → all unresolved; accept with no failures → all approved; override on a failing field → approved; remaining hard failure → unresolved; reject doc → all rejected; required null → unresolved.
- Orchestration (`OrchestrationTests`): "8 of 10" scenario above; payload excludes rejected D2 and lists it in `excluded`; `readiness.ready` flips to true after the two overrides; `ReviewActions.Reject` finally has a test.
- Regression: corpus v5 deck table unchanged (R-33 must not affect routing); `lossrun-db audit-proof` passes (field-reject rows are ordinary overrides).
- Web: Analytics header shows the readiness banner with the blocking fields linked to the document cell; axe clean.

### Priority and timing
**P1.** POC: items 1, 3, 4, 5 (data model, readiness, exclusion). Item 2 (per-field reject) is POC-sized but optional for the demo. Production: readiness thresholds agreed with underwriting; the required-field list becomes a configured policy per line of business.

---

## 2. Downstream updates (Service Bus + Event Grid kept)

### Current behaviour (verified)
- **Sequence on upload completion:** `CompleteUpload` runs **before** the review wait loop (`src/LossRun.Orchestration/Orchestrations.cs:107` vs `:122-145`). It writes, in one transaction with the audit row (`src/LossRun.Data/ClaimCommandService.cs:273-288`), an Event Grid `upload.completed` event and a Service Bus `quote.payload` command whose body is a **summary** `{quoteId, uploadId, status, documents, awaitingReview, claims, costUsd, completedAt, quotePayloadUrl}` (`Activities.cs:349-357`). The URL is relative and unversioned (`/api/v1/quotes/{quoteId}/quote-payload`, `Activities.cs:352`). Claim-check pattern per ADR 0014 §4.
- **Dispatcher** maps `quote.payload` → `PUT quotes/{q}` with `Idempotency-Key = outbox id` (`src/LossRun.Integration/Dispatcher.cs:22-31, 70-106`). The mock stores the summary under `lossRunAnalysis`, **replacing** the previous value unconditionally (`src/LossRun.Integration/QuoteSystem.cs:48-60`); it never fetches the payload URL (no HTTP client in the mock).
- **Sequence on a review decision:** all three decision paths emit **only** the `review.decided` CloudEvent `{documentId, uploadId, quoteId, action, overrides: <count>, revision, user, role, decidedAt}` (`Activities.cs:71-76, 425-429`). No `quote.payload` command is re-sent, no values, no field paths, no URL. For orchestrated and reopened decisions the event is written in a **second** transaction after the decision (`Activities.cs:388` then `:414`); only direct overrides commit it with the decision (`db/changelog/0016-apply-decision-proc.sql:84-89`).
- **Revisions:** per-document `Revision`, monotonic, enforced by `dbo.ApplyDecision` (`0016-apply-decision-proc.sql:19-25`). Payload v2 carries `documents[].revision` and `generatedUtc` (`QuoteAnalytics.cs:350-353`). **No quote-level revision**, no ETag on the payload endpoint, nothing in the command body or the quote-system call; the internal `RevisionKey` hash (`QuoteAnalytics.cs:59-68`) is not exposed.
- **Ordering:** Service Bus sessions per quote, duplicate detection 1 day, DLQ after 10 (`infra/integration.tf:30-36`). Session order = publish order; a fast-path failure followed by a sweep ≥ 20 s later can deliver an older summary after a newer one (`Activities.cs:61-68`; `src/LossRun.Functions/IntegrationFunctions.cs:19-22`). Event Grid is unordered; the mock appends events without comparing `revision` (`QuoteSystem.cs:109-113`).
- Tests cover outbox atomicity, duplicate drop, replay, dead-letter (`src/LossRun.Integration.Tests/OutboxTests.cs`; `src/LossRun.Data.Tests/DataTests.cs:316-340`). **Not covered:** re-send after a decision, exclusion of rejected documents, stale-revision rejection at the receiver, that `quotePayloadUrl` is resolvable.

### Confirmed gap
Downstream never receives updated values or field statuses after review; rejected and pending data is pushed as usable; the consumer cannot identify the latest revision from the message; an outdated command can overwrite a newer one at the receiver.

### Proposed changes
1. **Quote-level revision.** `dbo.QuoteRevision` (temporal): `QuoteId, Revision int, ChangedUtc, Cause (upload-completed | review-decided | override | reopen), DocumentId, DocumentRevision`. Incremented inside `dbo.ApplyDecision` and in `CompleteUpload`'s transaction. Exposed as `quoteRevision` in payload v2 (top level), as an `ETag: "q-<n>"` header on `GET …/quote-payload`, as `quoteRevision` in every Service Bus command body and `ApplicationProperties`, and in every CloudEvent `data`.
2. **Re-send on decision.** Every decision path writes a `quote.payload` command in the **same transaction** as the decision (extend `dbo.ApplyDecision`'s outbox block to the orchestrated path by passing the command rows through `TryApplyDecisionAsync`, as the direct-override path already does). Body = summary + `quoteRevision` + `readiness` + `changedDocuments: [{documentId, revision, fieldStatusChanges: n}]` + versioned URL `…/quote-payload?revision=<n>`.
3. **Push after review, not before.** `CompleteUpload` still fires `upload.completed` (informational), but the first `quote.payload` command for an upload is written when the last `document-review` item of that upload is decided or when there was none. If a quote-linked upload needs no review, behaviour is unchanged.
4. **Payload content by review state** (consumes item 1's `fieldStatus`): approved values; unresolved as `null` with `fieldStatus`; rejected documents in `excluded[]`; `readiness` object. The payload endpoint accepts `?revision=` and returns 409 `revision-superseded` if a newer one exists (so a late fetch cannot be mistaken for current), and 304 on `If-None-Match`.
5. **Receiver contract (mock first, real system later):** `PUT quotes/{q}` carries `If-Match: "q-<n-1>"` or the body's `quoteRevision`; the receiver stores the highest revision seen and answers 409 `stale-revision` for a lower one. Dispatcher treats 409-stale as **delivered-superseded** (recorded on the outbox row, not dead-lettered, not retried). Document this in the handover as the one rule the real quote system must honour alongside the idempotency key.
6. **Event Grid stays notification-only**: `review.decided` and a new `quote.revised {quoteId, quoteRevision, cause}`; consumers that act on data fetch the payload by revision. Ordering is then irrelevant for events.
7. [Optional] Sweep ordering: publish per session in `CreatedUtc` order and let the fast path skip rows older than an unpublished sibling of the same quote — cheap insurance once revisions exist, not strictly needed given 5.

### Concrete example
Upload U-9 for quote Q-1 completes with one document awaiting review: `upload.completed` (EG) fires; **no** `quote.payload` yet (change 3). Reviewer overrides two fields → `dbo.ApplyDecision` bumps D1 to revision 2 and Q-1 to `quoteRevision 7`, and in the same transaction writes `quote.payload {quoteRevision: 7, readiness: {ready: true}, changedDocuments: [{D1, 2, 2}], quotePayloadUrl: …?revision=7}` and `quote.revised`. A retry of an older command with `quoteRevision 6` reaches the mock after 7 → 409 `stale-revision` → outbox row marked `superseded`. A consumer that only subscribed to Event Grid sees `quote.revised 7`, fetches `?revision=7`, gets `ETag "q-7"`.

### Acceptance tests
- `OrchestrationTests`: decision on a quote-linked document produces exactly one `quote.payload` command in the same ledger transaction (audit proof passes); `quoteRevision` strictly increases across upload → decision → override → reopen → decision.
- `OutboxTests` with a sessions-aware fake bus: out-of-order delivery of revisions 6 and 7 leaves the receiver at 7; a duplicate of 7 is `replayed`; 6 is `superseded`, not dead-lettered.
- Payload: rejected document excluded and listed; `?revision=6` after 7 exists → 409; `If-None-Match` → 304.
- Live smoke (post-deploy step already in `deploy-functions.ps1`): the synthetic CSV upload ends with the mock holding the same `quoteRevision` the API reports.

### Priority and timing
**P1**, POC. The receiver contract (5) is the only part the real quote system must implement; everything else is on our side. Production: the same with APIM in front of the payload endpoint (architecture-overview §2) and the real adapter.

---

## 3. Review-screen improvements

### Current behaviour (verified — `src/web/apps/workbench/src/screens/ReviewDetail.tsx`)
- Actions on a pending item (`:115-156`): **Save N override(s)** (disabled when nothing staged), **Accept extracted values** (disabled when something is staged), **Reject document** (needs a ≥ 8-char note). The design brief specifies exactly these (`docs/ui/design-brief.md:98-100`).
- Edits are staged per cell with an RHF+Zod form in the lineage panel; "Add to decision" pushes into React state `staged` (`:36, :113, :186-190`). Removal is per item (trash icon, `:126-128`). **No Reset.**
- **Typed-but-unstaged text is lost silently**: the form is keyed by path (`key={path}`, `:113`), so selecting another cell unmounts it; "Accept" is disabled only by staged items, not by typed text.
- **Server drops overrides on accept/reject**: `Records.cs:252` applies `decision.Overrides` only when `Action == Override`; no error is returned. The MSW mock applies overrides on any action (`src/web/apps/workbench/src/mock/handlers.ts:328-337`), so the offline demo and the live API diverge.
- Save and approve is already one call (an `override` decision closes the item and bumps the revision), but the label says "Save N override(s)" and the status line says "Overrides saved…" — the word "approve" never appears.
- **Format activation is blurred**: the checkbox "Apply this mapping to future {carrier} documents" is shown for both review kinds and `promoteToFormat` is sent with every action including reject (`:64, :145-148`). Server-side, any non-reject action on a `confirm-format` item activates the format regardless of the checkbox (`Activities.cs:381`), and `promoteToFormat: true` with `reject` can still activate a format when `FormatRef` is set (reopened documents, `DocumentCommands.cs:52`).
- **Audit-action mismatch**: the UI derives override chips and the as-reported view from audit rows `action === "review.override" && fieldPath` (`document/model.ts:92-99`; `audit.tsx:25`), but the backend writes per-field rows as `"override"` and the `review.override` row has no `fieldPath` (`ClaimCommandService.cs:169-174`). Only the mock emits the UI's shape. Against the live API the override chips are empty. Same for `format.approve` (backend) vs `format.approved` (UI label map, `audit.tsx:31`).
- Tests: backend covers accept/override/reopen/409/403 (`OrchestrationTests.cs:91, 296, 387, 590, 682, 724`); **no** test of reject, of overrides sent with accept/reject, or of `promoteToFormat` on a document-review item. Web e2e renders the screen (`e2e/a11y.spec.ts:63-73`) and clicks Accept once (`e2e/live.spec.ts:71-89`); nothing clicks Save/Reject/the checkbox.

### Confirmed gap
Reset missing; typed corrections silently lost (client and server); labels do not say "approve"; format activation not clearly separated from document approval; live/mock divergence; UI reads audit rows the API never writes.

### Proposed changes
1. **Server guard (defect fix):** `POST /reviews/{id}/decision` returns 400 `overrides-not-allowed` when `overrides` is non-empty and `action != override`; `accept` with a non-empty `overrides` is never silently accepted. Mock aligned with the API.
2. **State machine in the screen** (one Zustand slice `reviewDraft` keyed by review id): `dirty = staged.length > 0 || anyFormDirty`.
   - Not dirty → buttons **Accept extracted values**, **Reject document**.
   - Dirty → buttons **Save changes and approve** (primary; submits `override` with all staged items, after auto-staging any valid unsubmitted form), **Reset changes** (clears `staged` and every form, with an "Undo" toast for 5 s), **Reject document**.
   - Leaving a cell with typed text: the form value is kept in the draft slice (not unmounted state), shown as a "draft" chip on the cell; "Save changes and approve" with an *invalid* draft focuses it and blocks; "Accept extracted values" is hidden while any draft exists. Navigating away with a dirty draft prompts (route guard).
3. **Format activation separated:** the checkbox is rendered only for `document-review` items whose document has a `FormatRef`, and is never sent with `reject` (client) nor honoured with `reject` (server: `approve` requires `action != reject` unconditionally, `Activities.cs:381`). For `confirm-format` items the primary action is relabelled **Confirm format and approve document**, with an explanatory line "This activates the learned format for future {carrier} documents", and a secondary **Approve document only** (sends `promoteToFormat: false`; server must respect it — today it does not).
4. **Audit shape alignment:** backend writes `fieldPath` on the per-field `override` rows (it already does) and the UI reads `action === "override"`; label map `format.approve`. One shape, mock regenerated from the live capture (the brief-23 capture script).
5. **Status lines** say what happened: "Saved 2 changes and approved. Revision 2." / "Approved as extracted." / "Rejected. Document kept in the upload, excluded from the quote."

### Concrete example
Reviewer opens D1, types `12,340.00` in `claim[7].total.incurred` with rationale, clicks another cell without "Add to decision". The cell shows a draft chip; the primary button reads **Save changes and approve**; **Accept extracted values** is gone. They click **Reset changes** → both the staged list and the draft clear, toast offers Undo. They re-enter the value, click **Save changes and approve** → one `override` decision, audit tab shows `override claim[7].total.incurred 11,340.00 → 12,340.00` and `review.override`; the as-reported toggle now works live because the UI reads the row the API wrote.

### Acceptance tests
- Web unit (vitest, `reviewDraft` slice): dirty detection across cells; reset clears all; auto-stage of a valid draft on save; invalid draft blocks save.
- Playwright (mock and live): both button sets by state; Reset+Undo; route-guard prompt; confirm-format shows "Confirm format and approve document" and "Approve document only"; reject never sends `promoteToFormat`.
- API: 400 on `accept`+overrides; `promoteToFormat:false` on a confirm-format item leaves the format in shadow (new behaviour); reject on a confirm-format item never activates.
- Contract: `ApiContractTests` extended with the new 400 code; mock handlers regenerated and diffed against openapi.
- axe: 0 violations in both themes for every state.

### Priority and timing
**P1** for 1, 3 (server side) and 4 — they are defects visible in a live demo. **P2** for the state machine and labels (2, 5). All POC.

---

## 4. Chat conversation memory

### Current behaviour (verified)
- History window: `ChatServiceOptions(... MaxToolIterations = 6, HistoryTurns = 4)` (`src/LossRun.Chat/ChatService.cs:17-18`); applied as `history.TakeLast(options.HistoryTurns)` replaying only `t.Question` and `t.Text` (`:89-93`). Not configurable: `Program.cs:164-165` passes only the TTL.
- Tool-call limit is separate: `FunctionInvokingChatClient.MaximumIterationsPerRequest = 6` (`ChatService.cs:74`) plus an app-level `MaxToolCalls = 6` / `MaxChunksPerTurn = 12` (`src/LossRun.Chat/ChatTools.cs:179-180`, tested `ChatServiceTests.cs:32-50`). **Confirmed: the four-turn window and the six-iteration limit are independent.**
- The whole conversation is loaded from `dbo.ChatTurn` (unbounded query, `SqlRecordStore.cs:441-450`) and trimmed in memory. Every turn is persisted with `CitationsJson` (DocumentId, Page, Sheet, FieldPath, ClaimId, Metric, FileName, QuoteId — `ChatModels.cs:27-28, 43-45`) and the tool trace, in an append-only ledger (`db/changelog/0014-chat.sql`).
- Citations from earlier turns are **not** re-injected; ref numbering (`[C1]`, `[S2]`) restarts each turn (`ChatTools.cs:68-73`), so a ref quoted from turn 2 is invalid in turn 6 (`ChatService.cs:131-133`). Scope is restated each turn (`:95`), so quote/workspace survives; document and claim identity from older turns does not.
- Agent Framework: `Microsoft.Agents.AI.OpenAI 1.25.0` (`LossRun.Chat.csproj:9`), `ChatClientAgent` with `RunStreamingAsync(messages, null, …)` — **no `AgentThread`, no `ChatMessageStore`, no compaction** (`ChatService.cs:75-101`). Whether 1.25.0 ships a summarization/compaction strategy is **not verifiable offline** here [Assumption to check on a machine with the package cache or the published API docs].

### Confirmed gap
Older context is dropped outright; references to quotes/documents/claims in earlier turns are not preserved as structured context; no token budget.

### Proposed changes
1. **Evaluate the framework first** (half a day, on a machine with NuGet): does `Microsoft.Agents.AI` 1.25.0 provide `AgentThread` + a message store with a compaction/summarization strategy, and is it stable (not `[Experimental]`)? If yes and stable → adopt it for the raw message window and keep our own structured context (2). If experimental or absent → implement 2–4 ourselves; they are small.
2. **Structured context block, always present** (cheap, deterministic, no model call): before the recent turns, inject a system-side block "Conversation facts" built from persisted citations and tool traces of **all** prior turns: the quote id, workspace id, every document (`documentId`, `fileName`, carrier), every claim number and metric previously cited, with the turn number they came from. Deduplicated, capped at ~1.5k tokens, newest first. Refs are issued fresh per turn, so the block carries *identifiers*, not refs; the model is told that prior refs are not valid and to cite from this turn's tools.
3. **Recent turns verbatim within a budget**: replace `TakeLast(4)` with "as many recent Q/A pairs as fit in `HistoryTokenBudget` (default 6k tokens, configurable via `Chat:HistoryTokenBudget`)", counting with the model's tokenizer (`Microsoft.ML.Tokenizers` is already transitively available [Assumption — verify]); always at least the last 2 pairs.
4. **Rolling summary for the remainder** [POC-optional, production-recommended]: when turns fall out of the verbatim window, maintain one summary per conversation (`dbo.ChatConversationSummary`, temporal, ≤ 800 tokens) regenerated at most once per 5 dropped turns with a single model call on the delta; the summary is also persisted with the turn that created it, so the audit shows what the model saw. If the framework's compaction strategy is usable, it replaces this item.
5. **Configuration**: `HistoryTurns` becomes a ceiling, `HistoryTokenBudget` the real control; both read in `Program.cs`.

### Concrete example
Turn 1: "What is the total incurred on Q-DEMO-1000?" → cites D-17 and metric M. Turn 7: "Is the claim we discussed earlier still open?" Today the model has turns 3–6 only and no record of which claim; it either asks or guesses. With 2, the facts block lists `claim 2019-04471 (D-17, cited turn 2)`, so `query_claims` is called with that claim number, and the answer cites this turn's `[C1]`.

### Acceptance tests
- Unit: facts block contains every document/claim/metric id from prior citations and nothing outside scope; capped size respected; budget window includes the newest pairs first.
- `ChatServiceTests`: a 10-turn conversation where turn 10 refers to "the claim from earlier" resolves to the right claim number through the facts block (model stubbed).
- `lossrun eval chat` gains a **multi-turn** suite (5 conversations × 8 turns) with reference-resolution questions; gate: ≥ 90 % correct, 0 leaks, 0 invalid citations; cost per turn recorded.
- Audit: the ledger row for each turn records which prior turns were verbatim, which summarised, and the summary id.

### Priority and timing
**P2.** POC: 1 (evaluation), 2, 3, 5. Production: 4 or the framework's compaction, chosen after 1.

---

## 5. Chat correction explanations

### Current behaviour (verified)
- Three tools only: `search_documents`, `query_claims`, `compute_metric` (`ChatTools.cs:194-357`). `query_claims`/`compute_metric` read SQL-backed state (`dbo.Document.ResultJson` + `dbo.Override` via `ResultCache`) and support `view = adjusted | reported` (ADR 0013 §3). **No tool returns an override record**: overrides are loaded (`ChatTools.cs:398-402`) but used only to build the reported view (`src/LossRun.Analytics/AccountBuilder.cs:34, 96-107`); rows carry no override fields.
- Override data exists with everything the requirement asks for — `OldValue, NewValue, UserId, Role, AtUtc, Rationale, SupersedesOverrideId` (`db/changelog/0005-override-audit.sql:14-35`) — readable via `OverridesOfDocumentAsync` (`Records.cs:101`; `SqlRecordStore.cs:222-227`; note `StoredOverride` drops `Role`, `Source`, `SupersedesOverrideId`, `Records.cs:233`) and `GET /documents/{id}/audit` (`HttpApi.cs:417-421`). Not wired to chat.
- AI Search index holds one chunk per page/sheet with the original `markdown` from the document graph and metadata; **no field-level documents**, and overrides never touch the text (`src/LossRun.Chat/ChatIndexer.cs:91-127`; `infra/search/lossrun-docs.index.json`). Re-index is upload-lifecycle only, never on a decision. **Requirement "never overwrite source text in Search" is already met**; one caveat: metadata `carrier`/`policyNumbers` come from the result and *would* change on a re-index after a carrier override — nothing triggers that today.
- Review status is invisible to the model and not used as a filter: `DocumentsInScopeAsync` returns every document regardless of `ReviewState` (`SqlRecordStore.cs:373-389`), so pending/rejected documents are answered as facts.
- Authorization: scope enforced twice (OData filter + `scope.Admits`), roles checked at the service (`ChatService.cs:174-202`); **no per-tool authorization** — every caller gets all three tools (`:81`).
- Runtime guards check that cited refs exist (`ChatService.cs:131-142`); the text eval checks for unexplained numbers (`src/LossRun.Evaluation/ChatTextEval.cs:262`). No eval covers corrections or multi-turn.

### Confirmed gap
No tool exposes who changed what, when and why; review status is not surfaced or filtered; no tool-level authorization; explanations of adjusted-vs-reported differences are inference.

### Proposed changes
1. **New tool `explain_value`** (SQL-backed): input `claimNumber | claimId | documentId`, `fieldPath?`. Returns, per field, `{ current: {value, status (from item 1 fieldStatus), revision}, original: {value, extractor, confidence, location {page/polygon | sheet/cell}, ref S#}, corrections: [{old, new, reviewer (display name or id), role, atUtc, rationale, revision, supersedes}] }`. Values come from `dbo.ExtractedValue` (as-reported), `dbo.Override` (history) and the current result; the original's location is also issued as an `S#` ref pointing at the Search chunk for that page, so the evidence citation goes to the **unmodified** page text. `StoredOverride` gains `Role` and `SupersedesOverrideId`.
2. **`get_review_status`** tool: per document in scope → `reviewState`, `revision`, pending items, `fieldStatus` summary (counts), `readiness` for the quote. Also used by the prompt to state, every turn, which documents are pending/rejected.
3. **Scope filter by review state**: `DocumentsInScopeAsync` excludes rejected documents by default; pending documents are included but every row returned by `query_claims` carries `reviewState` and `fieldStatus` for the fields in the row, so the model can say "pending review" instead of asserting.
4. **Tool authorization**: a `ToolPolicy` keyed by role — Applicant: `search_documents`, `query_claims` (own workspace, no `reported` view), no `explain_value`; Underwriter/UWManager/Admin: all. Enforced inside `ChatTools` (role passed in, tool absent from the model's tool list **and** rejected if called), recorded in the turn trace.
5. **Prompt rule + guard**: when asked why a value differs, the model must call `explain_value` and quote the rationale verbatim; the runtime verifies that every reviewer name, date and rationale in the answer appears in that tool's output for the turn (extend the existing citation check with a "facts from tools" check for strings), otherwise the sentence is flagged `unverified` in `InvalidCitations`-style output and the eval fails it.
6. Search stays read-only for chat: no writer path from decisions; add a test asserting a decision does not change any `markdown` field.

### Concrete example
Underwriter: "Why does claim 2019-04471 show $12,340 incurred when the loss run says $11,340?" → `explain_value(claimNumber: "2019-04471", fieldPath: "total.incurred")` → `{original: 11,340.00 (replay, 0.97, page 3 [S1]), current: 12,340.00 (approved, rev 2), corrections: [{11,340 → 12,340, reviewer "M. Chen", Underwriter, 2026-10-23T18:04Z, "Carrier subtotal on page 3 includes the 2019-04 reserve change; corrected to the printed detail line", rev 2}]}`. Answer: "The loss run prints $11,340.00 [S1]. M. Chen (Underwriter) changed it to $12,340.00 on 23 Oct 2026, revision 2, with the reason: '…' [C1]." If the override had no rationale (impossible — non-null check) or the reviewer were unknown, the answer says so rather than inventing one.

### Acceptance tests
- Unit: `explain_value` returns the chain `original → override 1 → override 2` with `supersedes` links; location ref resolves to the Search chunk of the right page; Applicant calling it → tool not offered and 403 in trace.
- `ChatServiceTests`: pending document's rows carry `reviewState: pending`; rejected document absent from scope by default.
- Eval: new `lossrun eval chat-corrections` — 10 questions on the synthetic corpus with planted overrides; pass = correct old/new values, reviewer, date, verbatim rationale, 0 invented names/dates (rubric extended from numbers to names, dates and quoted strings).
- Index invariance test: decision → re-read of the page chunk is byte-identical.

### Priority and timing
**P2**, POC (1–3, 6 depend on item 1's `fieldStatus`; 4–5 are independent and small). Production: display names from Entra (today only `oid`/`sub` are stored, `Auth.cs:61`) — [Assumption] a directory lookup or a `displayName` claim will be added when Entra is on.

---

## 6. Production token protection (POC unchanged; BFF assessed)

### Current behaviour (verified)
- SPA: `@azure/msal-browser 5.26.0` (`src/web/apps/workbench/package.json:20`), `cacheLocation: "sessionStorage"` (`src/auth/msal.ts:14`), redirect flow, one scope, `acquireTokenSilent` per request with `acquireTokenRedirect` on `InteractionRequiredAuthError` (`msal.ts:17-30`). Bearer attached in `auth.ts:99-104` from three call sites (`api/client.ts:12-13`, `api/sse.ts:89,109`, `shell/Login.tsx:30`). Basic-auth credentials also live in `sessionStorage` (`auth.ts:43`). **Entra sign-out exists but is never called** (`msal.ts:33-35`; the Sign-out button renders only in basic mode, `shell/Shell.tsx:99-103`). 401 handling in Entra mode is a thrown error (`client.ts:14` clears only Basic).
- API: raw `Microsoft.IdentityModel` 8.23.0 validation of issuer, audience, lifetime, signature (`src/LossRun.Orchestration/Auth.cs:30-64`); roles from the `roles` claim, highest-privilege-first (`Auth.cs:59-62`); user id `oid` else `sub`; **no server-side session, no cookies** (grep: not found); stateless per request. Per-endpoint 403s in the orchestration layer (`UploadApi.cs:236-249`, `WorkbenchApi.cs:21-39`, …). Basic/demo auth selected by `LOSSRUN_DEV_AUTH` / `Auth:Mode` (`Program.cs:170-180`); the deployed POC runs Basic with no app registration (`infra/variables.tf:265-274, 290-294`; `poc.tfvars` sets neither).
- Topology: Static Web Apps Free, browser → Function App directly over CORS with `support_credentials = false` (`infra/orchestration.tf:131-134`; `infra/web.tf:17-21`); CSP `connect-src 'self' https://*.azurewebsites.net https://login.microsoftonline.com` (`public/staticwebapp.config.json`). No BFF/APIM/App Gateway in `infra/`. ADR 0012 chose this deliberately (lines 12-16, 55); production recommendation is APIM / Application Gateway + WAF, Entra-only (`docs/architecture/architecture-overview.md:46, 104`; closeout 06 §6.1).
- Things that matter for a BFF: SSE is **fetch-based with headers**, not `EventSource`, up to 200 s (`sse.ts:59-62`; `HttpApi.cs:157-202`); downloads go fetch→Blob→object URL (`queries.ts:38, 138-146`); the what-if worker makes no network calls; non-browser clients (CLI, eval, simulator, load test, the quote system) use the same bearer API (`ChatCli.cs:102-107`, `EvalRunner.cs:341-344`); multiple SPA origins (live, `mock` preview, demo domain, localhost). No authenticator unit tests and no HTTP-level 401 tests (grep: not found).

### Confirmed gap
For the POC: none that the requirement asks to change (Entra + MSAL `sessionStorage` is the accepted POC design). Minor POC defects: Entra sign-out not wired; 401 in Entra mode not recovered; no authenticator tests. For production: access tokens are readable by any script running on the page (XSS → token theft within the token's lifetime); no server-side revocation; the API is reachable from any origin that holds a token.

### Assessment: Backend for Frontend with server-side tokens and HttpOnly cookies

**Shape.** A small ASP.NET Core app (or the existing Function App behind the same host via APIM) at the **same site** as the SPA (e.g. `app.<org>` serving the static files and `/bff/*`), holding Entra tokens server-side and issuing an opaque session cookie `Secure; HttpOnly; SameSite=Strict (or Lax); Path=/bff`. The SPA calls `/bff/api/*`; the BFF validates the cookie, attaches the user's access token (or an on-behalf-of token for the Functions audience) and forwards. The bearer API stays as is for CLI, evaluation, the quote system and load tests (ADR 0012's reason), behind APIM with per-client policies.

| Concern | With BFF | Notes / tradeoffs |
|---|---|---|
| **Sign-in** | `GET /bff/login` → authorization-code + PKCE redirect to Entra handled by the BFF (`Microsoft.Identity.Web`); callback stores tokens in a server session store (Redis or SQL `dbo.BffSession`, encrypted at rest with Data Protection keys in Key Vault) and sets the cookie. The SPA never sees a token. | Replaces `msal.ts` + `Login.tsx`; `AUTH_MODE` gains `bff`. Entra app becomes a confidential client (certificate in Key Vault), not a SPA registration. |
| **Token renewal** | BFF refreshes the access token with the refresh token when < 5 min remain, on request; the SPA is unaware. Sliding session: cookie lifetime renewed on activity up to an absolute cap. | Removes `acquireTokenSilent` and the iframe/redirect dance; long SSE streams keep working because the BFF holds the token for the connection's life. |
| **Session expiration** | Two limits: idle (e.g. 30 min) and absolute (e.g. 8 h, aligned to Entra's sign-in frequency / Conditional Access). Expiry → 401 from `/bff/*` → SPA shows "session expired" and offers re-login; SSE streams end with a typed event. | Server-side revocation becomes possible (delete the session row); today a stolen token is valid until expiry. |
| **Logout** | `POST /bff/logout` (CSRF-protected) deletes the session, clears the cookie, then front-channel redirects to Entra end-session so the SSO cookie is cleared too; optional back-channel logout from Entra to kill sessions on admin action. | Fixes the current unused `signOutEntra`. |
| **CSRF** | Cookie `SameSite=Strict` for `/bff/*`, plus a double-submit header (`X-CSRF: 1` with a custom-header precondition — a cross-site form cannot set custom headers) and origin check against the configured site list. State-changing routes require both. | Needed because cookies are sent automatically; today bearer tokens are CSRF-immune. |
| **Authorization** | Unchanged in the API: the BFF forwards a token whose `roles` claim the API already evaluates (`Auth.cs:59`); user id stays `oid` so audit/ownership keys do not change. BFF enforces nothing beyond "valid session". | Avoid duplicating role logic in the BFF; keep one authorization surface. |
| **SSE / streaming** | BFF proxies `fetch` streams with response buffering off; cookie auth works because the SPA uses `fetch(credentials: "include")` already (not `EventSource`). | Verify APIM/App Gateway buffering settings; 200 s streams need idle timeouts raised. |
| **Downloads / images** | Unchanged: fetch→Blob paths send the cookie automatically. | No cookie-authenticated top-level navigations exist, so no leakage via `<a href>`. |
| **Origins** | SPA and BFF on one site; the demo app (mock, calls no API) stays separate; `mock` preview env is excluded from BFF. CORS on the Functions API narrows to the BFF only (`support_credentials` stays false). | Removes CORS preflights for the SPA (custom headers `Idempotency-Key`, `Accept: text/event-stream`). |
| **Operations** | New stateful component: session store (Redis Basic or SQL), Data Protection key ring in Key Vault, certificate rotation for the confidential client, health checks, one more deploy. Observability: session count, refresh failures, logout rate. | Cost: ~$15–40/month for Redis Basic or $0 on SQL; one more thing to run. Benefit: tokens never in the browser, revocable sessions, simpler SPA. |
| **Risks moved, not removed** | XSS can still *use* the session (make requests) while the page is open — it cannot *exfiltrate* a token. CSP and the existing `staticwebapp.config.json` headers remain the first defence. | State this plainly in the governance pack. |

**Alternatives considered.** (a) MSAL with `cacheLocation: "memory"` — reduces persistence but tokens are still in page memory; sign-in on every tab. (b) Static Web Apps Standard linked backend with App Service Authentication v2 (EasyAuth) — gives cookie sessions "for free" but SWA takes over auth for all routes, breaks the direct bearer API for CLI/quote system, and was the reason ADR 0012 rejected linking; could be revisited if the API moved behind APIM with a separate hostname. (c) APIM alone — policy enforcement and WAF, but tokens still live in the SPA.

**Recommendation.** POC: unchanged (Entra + MSAL `sessionStorage`), plus the three small fixes below. Production: BFF per the table, built as a separate project `src/LossRun.Bff`, deployed alongside; bearer API retained for non-browser clients behind APIM. Decide (b) vs BFF with the organisation's cloud architect, since it depends on their APIM/App Gateway standard.

### POC fixes (small, do now)
- Wire `signOutEntra` to the Sign-out button in Entra mode (`Shell.tsx:99-103`).
- 401 in Entra mode → one `acquireTokenSilent({forceRefresh: true})` retry, then redirect (`client.ts:14`).
- Authenticator tests: valid/expired/wrong-audience tokens for `EntraAuthenticator`; Basic parsing; HTTP-level 401 on missing header.

### Acceptance tests (production BFF)
- Playwright: login via BFF sets only an HttpOnly cookie (no `Authorization` header, nothing in `sessionStorage`/`localStorage`); API calls succeed; SSE upload stream runs > 60 s; download works; logout clears the cookie and the Entra session; cross-site POST without the CSRF header → 403; expired idle session → 401 and re-login prompt.
- Security: automated check that no response to the browser contains a JWT; token refresh under a 5-minute-expiry test tenant policy.
- Audit: `UserId` in `dbo.AuditEvent` identical before and after the BFF for the same user (oid preserved).

### Priority and timing
**P3** for the build (production); the assessment is complete now; the three POC fixes are P2 and small.

---

## 7. Staged implementation plan

Order is chosen so each stage is demonstrable and the earlier ones unblock the later.

### Stage A — defects that block everything else (POC, ~1 day)
- Server rejects `overrides` on `accept`/`reject` (400); mock aligned.
- UI reads `override` audit rows with `fieldPath`; `format.approve` label; mock regenerated from the live capture.
- `promoteToFormat` never honoured with `reject`; honoured as `false` on confirm-format (approve document only).
- Entra sign-out wired; 401 retry; authenticator tests.
- Tests: new API 400 test, reject test, web unit for audit mapping, live e2e for the as-reported toggle.

### Stage B — field status and readiness (POC, ~3 days) — item 1
- `FieldStatusEvaluator`, `fieldStatus` in `DocumentResult` and openapi; R-33 required-for-quoting as data; `readiness` in `QuoteAnalytics`; exclusion of rejected documents from payload/analytics/chat scope with `excluded[]`.
- Regression: corpus v5 deck table unchanged (gate), audit proof passes.

### Stage C — downstream revisions and re-send (POC, ~3 days) — item 2
- `dbo.QuoteRevision` (changeset, Liquibase style), bump in `dbo.ApplyDecision` and `CompleteUpload`; `quoteRevision` + `readiness` + `changedDocuments` in the `quote.payload` command; command written in the decision's transaction on every path; first push after review, not before; `?revision=`/ETag on the payload endpoint; mock implements `If-Match`/409-stale; dispatcher treats 409-stale as superseded; `quote.revised` event.
- Sessions-aware fake bus in `OutboxTests`; live smoke extended with revision equality.
- ADR 0024 (quote revision and receiver contract); handover note for the real quote system.

### Stage D — review screen (POC, ~2 days) — item 3
- `reviewDraft` slice, two button sets, Reset + Undo, draft chips, route guard, confirm-format labels; status lines.
- Playwright for every state; axe both themes; demo script step 5–8 updated.

### Stage E — chat: corrections and review status (POC, ~2 days) — item 5
- `explain_value`, `get_review_status`, review-state-aware scope, tool policy by role, verbatim-rationale guard, index-invariance test, `eval chat-corrections`.

### Stage F — chat memory (POC part ~2 days; production part after framework check) — item 4
- Framework evaluation (package cache available) → decision recorded in ADR 0013 amendment.
- Facts block + token-budgeted verbatim window + configuration; multi-turn eval suite.
- Rolling summary or framework compaction (production).

### Stage G — production token protection (production) — item 6
- `src/LossRun.Bff` per the assessment; Terraform for the session store, confidential client certificate, same-site hosting; APIM in front of the bearer API; governance pack §RBAC updated. Decision with the organisation's architect between BFF and SWA-linked EasyAuth.

Dependencies: B before C and E (both consume `fieldStatus`/`readiness`); A before D (audit shape); F independent; G independent.

---

## 8. Assumptions and optional enhancements

**Assumptions** (not verifiable from the repository; confirm before building)
- The required-for-quoting field list (item 1, R-33) — proposed set needs underwriting sign-off.
- The real quote system can honour an `If-Match`/revision check and an idempotency key; the mock will be the reference (item 2).
- `Microsoft.Agents.AI 1.25.0` compaction support is unknown offline (item 4); a tokenizer package is available for budget counting.
- Reviewer display names: today only `oid`/`sub` is stored (`Auth.cs:61`); item 5 shows ids until Entra display names are available.
- The organisation's edge standard (APIM vs Application Gateway) decides the BFF hosting shape (item 6).

**Optional enhancements** (beyond the stated requirements)
- Per-field reject with its own override row (item 1, change 2).
- Sweep ordering by `CreatedUtc` per session (item 2, change 7).
- Undo toast after Reset (item 3).
- Rolling conversation summary persisted with the turn (item 4, change 4) if the framework does not provide compaction.
- Back-channel logout from Entra to revoke BFF sessions (item 6).
- A "facts from tools" guard generalised from numbers to names, dates and quoted strings in the chat runtime (item 5, change 5) — useful beyond corrections.
