# ADR 0008 — Orchestration shape, review wait placement and API auth for the POC

**Status:** proposed 2026-10-22

**Context.** The API work wires the preprocessing work through the learn-path work into Durable Functions on the Durable Task Scheduler and exposes an HTTP API. Three choices differ from the original plan or from the research:
- where the human-in-the-loop wait sits;
- how the activities are cut;
- how callers authenticate on a host that has no Entra app registration yet.

**Decision.**

1. **The review wait lives in the upload orchestration, not in `ProcessLossRun`.** Each `ProcessLossRun` sub-orchestration persists its document and returns.
   - The upload orchestration then completes the upload: manifest status `completed` or `awaiting-review`, cost roll-up, and the `upload.completed` webhook.
   - Only after that does it wait for `ReviewDecision` events (one per open item, addressed to the upload instance), raced with a durable timer (`Review__TimeoutDays`, default 7).
   - **Why:** the upload's time-to-complete and the webhook do not wait on a human. The brief's placement would have held the whole upload open for days.
2. **One `ParseFile` activity per file** runs the brief-04 pipeline end to end: sniff, unlock, archive expansion, classifier, and CU layout or native spreadsheet read.
   - The brief asked for separate `UnlockAndParse` and `ClassifyOrLayout` activities. The pipeline is one tested unit, and splitting it would push graphs through activity payloads and blobs twice for no gain.
   - The stepper still shows `parse` per upload and `fingerprint → route → replay|delta|learn → validate → done` per document.
3. **Auth.** Entra ID bearer validation uses Microsoft.IdentityModel directly (OIDC metadata plus `JsonWebTokenHandler`). These are the libraries under Microsoft.Identity.Web, without its ASP.NET-host assumptions, which do not fit the isolated worker.
   - The deployed demo host runs `Auth__Mode=basic` (the brief's `LOSSRUN_DEV_AUTH=basic`), with one user per role.
   - Terraform generates the passwords (`random_password`) and stores them only in Key Vault (`devauth-users`). The app reads them through a Key Vault reference with its managed identity.
   - The webhook HMAC secret is handled the same way. The deploying user gained **Key Vault Secrets Officer** so Terraform can write the two secrets.
4. **Blob layout stands in for SQL** until the persistence work: `uploads/` holds the original files (immutable) and `extractions/` holds manifest, graphs, results, reviews, indexes, the audit log and usage. The persistence work backfills from it.
5. **CU inline (sync, `2026-06-01-preview`) is implemented but off** (`cu_inline_max_pages = 0`), as the preprocessing work requires: measure, do not build on the preview. The measurement and the open decision are in the brief-07 report.

**Consequences.**
- An upload orchestration with open review items stays Running until every decision arrives or the timer expires. Its custom status says why.
- A shadow format's `confirm-format` items keep the instance open but never block the data.
- Entra auth needs an app registration with the four app roles before the UI ships. That is an open question in the report.
- The plaintext demo passwords exist in the remote Terraform state (in the Azure state account, never committed) and in Key Vault.
