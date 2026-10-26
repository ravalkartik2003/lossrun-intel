# Brief 26: engineering escalation, offline evidence (2026-10-25)

No Azure calls: in-process tests, Blob mode, Docker SQL (Testcontainers), the MSW mock in the browser. The alert Terraform is validated, not planned (planning reads Azure); it is planned and applied with the next wake.

| Check | Result |
|---|---|
| `EscalationTests` (Orchestration) | 12 pass:<br>• each single-event trigger (learn exhausted, replay failure on an active and not a shadow format, cost over the limit, segments over the limit, unhandled exception);<br>• dedupe and count (a document seen once; documentless signals count every time; resolved → new on a repeat; won't-fix counts without an event);<br>• a stale version refused;<br>• the four patterns, each just under and just over its threshold;<br>• the sweep through the Blob store (raises once, the second sweep changes nothing, open-count telemetry);<br>• no document value in a trace (claimant name, claim number, amounts, date, policy number scanned) |
| `A_learn_exhausted_document_creates_exactly_one_escalation_and_one_event` (end to end, Blob) | Pass:<br>• one `learn-exhausted` escalation, count 1, `fp:` key, one `escalation.raised` outbox row;<br>• the same document again → unchanged;<br>• flag, list (403 for Underwriter), status (400 without a note), tuning item (400 for category `z`, 201 for `b`), audit actions |
| `An_escalation_is_versioned_refuses_a_stale_write_and_commits_with_its_audit_and_event` (Docker SQL) | Pass:<br>• create, a second create refused, count, stale update refused;<br>• the audit rows, one outbox event;<br>• 2 temporal versions;<br>• the observations query |
| EF model vs deployed tables (Docker SQL) | Pass (`dbo.Escalation` with history, concurrency token) |
| `lossrun-db validate` / `emit` | 43 changesets parse, every one has a rollback; `deploy.sql` regenerated |
| `terraform validate` (`infra/escalation.tf`) | Success |
| Playwright `e2e/escalations.spec.ts` | 3 pass:<br>• queue filters, detail, status needs a note (`aria-invalid`), tuning item — light and dark, **axe 0**;<br>• Underwriter: queue refused, "Flag for engineering" dialog validation, axe 0, flagged |
| All suites | .NET 825 pass (Orchestration 98, Data 20); vitest 118; Playwright 93 pass, 22 skipped (live, pack and perf need a host) |

Screenshots: `docs/ui/screenshots/screens/escalations-light.jpg`, `escalations-dark.jpg` (mock data: three synthetic cases).
