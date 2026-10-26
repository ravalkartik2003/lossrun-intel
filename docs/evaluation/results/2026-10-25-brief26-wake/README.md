# Brief 26 wake (2026-10-25, 15:32 → 16:00 UTC, 28 min)

The one wake for brief 26 (Answers 5), with the brief 25/26/27 answers and brief 28 deployed.

| Step | Result |
|---|---|
| `hibernate = false`: plan | 22 to add, 4 to change, 1 to destroy:<br>• `azurerm_monitor_action_group.escalation` (Owners by role, `escalation_alert_emails` empty, no webhook);<br>• `azurerm_monitor_scheduled_query_rules_alert_v2.escalation_raised` and `.escalation_digest`;<br>• `random_password.dev_auth["Engineer"]`;<br>• `devauth-users` updated (Engineer user, the quote system's user as QuoteSystem);<br>• the usual wake set (Search, Service Bus, Event Grid and their role assignments; Function App settings) |
| Apply | 22 added, 4 changed, 1 destroyed |
| `lossrun-db deploy` | `0021-escalation` applied (1 of 43; 42 already applied, 0 drifted) |
| `chat init-index` | Index created (no reindex: the check does not use chat) |
| `deploy-functions.ps1`, API | Database check passed; smoke upload Completed, ReplayAccepted, 3 claims |
| `deploy-functions.ps1`, mock | Smoke upload Completed, ReplayAccepted |
| `deploy-web.ps1` | Live and mock builds deployed; smoke upload Completed, ReplayAccepted |
| Live check (`live-check.json`) | See below |
| Alert resources | `alert-escalation-raised`, `alert-escalation-digest` and `ag-escalation` (1 role receiver, 0 e-mails) exist |
| `hibernate = true` | 1 added, 3 changed, 18 destroyed; the alert rules and the action group stay |

**Live check** (`live-check.json`):
- **The dated CSV** (`lossrun-export-dated.csv`, valuation date 2024-06-30) went **ReplayAccepted** on `synthetic-mutual-csv-dated@1`, and both payload periods have effective and valuation dates.
- **The mock quote system** fetched it with its **QuoteSystem** credential: grid revision 1, sorted by date (`orderedByEffectiveDate: true`), both effective dates as values, no fetch error.
- **Engineer:** `/me` gives role Engineer, `canReview: false`, `canRead: true`. Document 200, `/escalations` 200, decision **403**.
- **QuoteSystem:** quote payload 200, upload manifest 200, document **403**, quote list **403**.
- **Flag for engineering:** 201, a `manual-flag` case; it is in the open queue (read as Engineer); one `escalation.raised` outbox row, published.

**Cost (estimate, not yet billed):** no model or CU calls (spreadsheets only). Standing time is SQL for about 30 min plus the 15-min auto-pause, and Search for about 20 min: roughly $0.25–0.50. The 26 Oct pull reconciles this wake and brief 25's together (brief 25 Answers 4).
