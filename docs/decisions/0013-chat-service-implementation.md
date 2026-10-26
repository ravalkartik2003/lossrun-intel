# ADR 0013 — Chat service: how the chat-service work was built, and where it departs from the brief

**Status:** accepted · **Date:** 2026-10-23

## Context

ADR 0002 chose our own agent over Foundry Agent Service. The chat-service work then specified the service:
- an Azure AI Search index, `lossrun-docs`;
- a `ChatClientAgent` with three server-side tools that carry a scope the server injects;
- `query_claims` as "parameterized SQL over `vClaimAsAdjusted`";
- Prompt Shields on the chat deployment;
- Search on the Free tier for development.

Building it turned up five constraints:
- The subscription's risk check refused a dedicated chat deployment, as it did for `gpt-5.2` in ADR 0004 (2026-10-23).
- Search creation in East US 2 failed with `InsufficientResourcesAvailable`.
- The Free tier holds 50 MB and 3 indexes. The corpus is 3072-dimension vectors on every page.
- The analytics engine (ADR 0011) already reads claims through `IRecordStore`, and does so in both store modes.
- The OpenAI SDK's Responses client for `IChatClient` is still flagged experimental (`OPENAI001`; `MAAI001` in Agent Framework).

## Decision

1. **Search.**
   - **Tier:** Basic (~$74/month; the user chose it on 2026-10-23), `search_sku = "basic"`.
   - **Region:** Central US (`search_location`), the same region as SQL (ADR 0003). East US 2 had no capacity.
   - **Access:** keyless. Local auth is disabled; the Function App and the operator hold the Search Index Data Contributor role.
   - **Semantic ranker:** free plan, 1,000 queries a month. Each query is metered at $0.001 (`config/azure-prices.json`), so the cost stays visible past the free quota.
   - **Index definition:** checked in at `infra/search/lossrun-docs.index.json` and embedded in `LossRun.Chat`. `lossrun chat init-index` creates or updates the index.
   - **Indexing:** there is no indexer. We push our own chunks:
     - one chunk per page or sheet, split at 8,000 characters;
     - built from the document graph markdown we already have;
     - kept (not processed) segments are taken from the parent document's graph.

   Chunk ids are a SHA-256 of document, page and part, so re-indexing replaces chunks in place.
2. **Queue.** A Storage queue `chat-index` carries one message per upload id. The message is sent:
   - by `ParseFile` when the upload has kept items;
   - by `ValidateAndPersist`;
   - by `CompleteUpload`.

   A queue trigger indexes the upload. The connection is identity-based (`ChatIndexQueue__queueServiceUri`), and `AzureWebJobsStorage` stays blank. The binding expression is `%Chat:IndexQueue%`: the host resolves configuration keys, not environment variable spellings, so `%Chat__IndexQueue%` disables the function. The backfill is `lossrun chat reindex --from-api | --uploads`.
3. **Claims tools read through `IRecordStore` and `AccountBuilder`, not ad-hoc SQL.** `query_claims` and `compute_metric` load the scope's documents with their overrides, then build the same `AnalyticsInput` that `GET /quotes/{id}/claims` and the features endpoint use. This choice:
   - keeps one definition of as-adjusted and as-reported (ADR 0011);
   - works in Blob mode;
   - avoids model-shaped SQL.

   The model passes filters only (status, type, year, policy, carrier, amounts, litigated, CT, jurisdiction). It never passes a scope or a document id.
4. **Scope is enforced twice at the tool layer.**
   - First, as the pushed-down OData filter. Ids are validated against `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$` and quotes are doubled.
   - Second, as an in-process post-filter (`ChatScope.Admits`) on every search hit, document and claim.

   Citations are refs (S#/C#/M#) that this turn's tools issued. A ref the model invents is dropped and reported in `invalidCitations`. Applicants get workspace scope only. Workspaces are owner-only: 404 when absent or someone else's, 410 when expired. Tests: `src/LossRun.Chat.Tests/ScopeLeakageTests.cs`, which covers a leaky index, a rogue record store and a hijacked model.
5. **Model: the chat runs on the delta deployment (`gpt-5.4-mini`, Data Zone).**
   - The dedicated `chat-gpt-5.4-mini` deployment, with RAI policy `lossrun-chat` (Prompt Shields jailbreak + indirect-attack blocking), was refused.
   - The policy resource exists and is ready. It is deliberately not attached to the delta deployment, because that deployment also extracts loss runs, and an indirect-attack filter on document text could block extraction calls.
   - Until a chat deployment is allowed, the injection defence is:
     - the instructions (tool results are data);
     - the structured answer;
     - citation validation;
     - a scope the model cannot change.
   - The planted-instruction test passes in unit tests and live (`lossrun eval chat`, probe-injection). Model name, version and deployment type are configuration (`Models:Chat` falls back to `Models:Delta`).
6. **SDK surface.** OpenAI SDK 2.14 on the Azure v1 endpoint with an Entra bearer policy. The Responses API runs with `store=false` and low reasoning effort, wrapped in Microsoft.Extensions.AI and `ChatClientAgent` (Microsoft.Agents.AI.OpenAI 1.25.0). `OPENAI001` and `MAAI001` are suppressed in one place (`src/LossRun.Functions/Program.cs`) with a comment. Durable agents stay out, as the brief says.
7. **Turns are append-only.** `dbo.ChatTurn` is an append-only ledger table (changeset `0014-chat-turn`). `dbo.Workspace` is an ordinary table whose changes are audited. Every turn writes an `AuditEvent` (`chat.turn`) and `UsageEvent`s:
   - `chat` model tokens;
   - `chat-embedding`;
   - AI Search semantic queries.

   Indexing embeddings are charged to the upload (`chat-index`).

## Consequences

- About $74/month for Search, plus chat at ~$0.002 per turn. On the 30-question eval the mean is $0.0019 and the max $0.0059. An open "list everything" question at org scope costs ~$0.02.
- The chat deployment and Prompt Shields wait on the subscription's risk review. Two Terraform changes switch them on:
  - add the `chat` entry to `model_deployments` with `rai_policy = "chat"`;
  - set `chat_model`.
- Search and the app are in different regions (Central US and East US 2). That adds ~30–60 ms per search call and is negligible next to model latency.
- Chat answers are as good as the system of record. The eval scores against `GET /quotes/{id}/claims`, not corpus truth, which the evaluation harness scores separately.

## Amendment (brief 24 steps 5 and 6, 2026-10-25): corrections, review status, tool policy, conversation memory

**Corrections and review status (step 5; review plan 2026-10-25 §5).**
- **Two new tools.**
  - `explain_value`: the printed value with an `S#` ref to the unmodified page chunk, the current value with its field status, and the corrections chain (reviewer, role, time, revision, superseded override, verbatim rationale).
  - `get_review_status`: review state, revision and field-status counts per document.
- **Review state on rows.** `query_claims` rows carry `reviewState` and the claim's non-approved fields.
- **Rejected documents** are out of every scope (`DocumentsInScopeAsync`).
- **`ToolPolicy` by role.** Applicants get no `explain_value` and no `reported` view. A disallowed tool is not offered to the model, and a call to it is refused and traced.
- **Fact guard.** It checks quoted passages, dates and reviewers named after "by"/"reviewer" against this turn's tool output. Misses go to `ChatTurnResult.unverified` and a `fact-guard` trace entry.
- **Index invariance.** A decision never rewrites indexed page text (test).
- **Prompt** `chat-v2`.

**Conversation memory (step 6; plan §4 changes 2, 3, 5).**
- **Conversation facts.** Built from every prior turn's persisted citations: documents, claim numbers and metrics with the turns that cited them, newest first, deduplicated, capped at 1,500 tokens. The block carries identifiers only and states that earlier refs are not valid this turn. It is sent as a system message.
- **Verbatim window.** The newest Q/A pairs within `Chat:HistoryTokenBudget` (default 6,000 tokens), at least the last two. `Chat:HistoryTurns` (default 12) is now a ceiling. Both are read in `Program.cs`.
- **Ledger.** The ledger row's tool trace gets a `history` entry: which prior turns were verbatim, the facts-block size and the budget. `dbo.ChatTurn` is a ledger table and cannot gain columns; the trace JSON is how it is recorded.
- **Token counting.** The model's tokenizer data (o200k) is not in the local package cache. `Microsoft.ML.Tokenizers` 2.0.0 is present, but its encoding data ships as a separate package. Counts are therefore estimated at four characters per token, which leans high for English prose and money.

**Framework check (`Microsoft.Agents.AI` 1.25.0, from the local NuGet cache only).** Read from the package's XML docs and its metadata (`ExperimentalAttribute` per type):
- **Compaction is experimental.** `Microsoft.Agents.AI.Compaction` provides `CompactionProvider` and the strategies `SlidingWindow…`, `Truncation…`, `ToolResult…`, `Summarization…`, `ContextWindow…`, `Pipeline…` and `ChatReducer…`. Every one of these types is `[Experimental("MAAI001")]`.
- **Sessions and history are stable.** `AgentSession` / `ChatClientAgentSession`, `ChatHistoryProvider` and `InMemoryChatHistoryProvider` (with a chat reducer trigger) are not marked experimental. `AgentSessionStore` is (`MAAI001`).
- **Decision: nothing adopted in brief 24.**
  - The facts block and the budgeted window are ours, about 150 lines with no model call.
  - The rolling summary (plan §4 change 4) is left for production. Use `SummarizationCompactionStrategy` once it leaves `MAAI001`; until then, our own summary per conversation.
  - Moving the window onto `ChatClientAgentSession` + `ChatHistoryProvider` is possible today with stable types. It would change how history is persisted, which the ledger already does, so it waits for a reason beyond tidiness.

**Not run yet.** `lossrun eval chat-corrections` and `lossrun eval chat-multiturn` (5 × 8) are written and unit-checked offline. Their first run is brief 24 step 7.

## Amendment 2 (brief 24 step 7 Answers, 2026-10-25): follow-ups, corrections listing, open includes reopened

- **Why.** The first runs of `chat-corrections` (6/10) and `chat-multiturn` (30/40) failed their gates. Fixes:
  - **Corrections listing.** `explain_value` without a claim number lists every correction in scope, newest first, capped at 50 with a `more` count. It is the same tool with optional parameters.
  - **Follow-ups.** Each turn records its last result set and filter (`last-result` trace entry). The next turn's facts block opens with "Last result (turn N)". Prompt rule 8 (`chat-v3`) resolves pronouns and "those / of those / its" against it first, keeps its filter, and asks one clarifying question on a two-way reading.
  - **Open includes reopened.** `status: open` includes reopened, as `open_claim_count` already did.
- **The 28/30 in `eval chat`** was neither the verified-only view (chat reads the adjusted view) nor memory (single-turn questions). `query_claims` totals had no per-component sums, so "total indemnity paid" and "total medical incurred" depended on the model choosing `compute_metric`. The totals now carry each component. A model that answered twice in one stream wrote two JSON objects; the parser now keeps the last.
- **Superseded pages (brief 32 Answers 2).**
  - Today a reopen is the only superseded case: the document's chunks carry `superseded` until it is decided again (`approved`).
  - When a re-process path exists, a document re-processed under a newer format version will mark its old pages superseded.
  - A retired format version alone does not, because its pages are probably right.
