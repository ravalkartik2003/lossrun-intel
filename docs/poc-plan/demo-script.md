# Demo script — 25 minutes (final, the close-out work)

The bake-off demo has two moments that carry it (design-brief §2):
1. **Format memory:** "0 model calls".
2. **The lineage thread:** every number back to its printed cell, then a correction into the tamper-evident ledger.

Use Case 2 (analytics, chat, integration) and the cost and governance close show that the build covers the whole requirement.

Everything shown is synthetic. No client document is used.

## Timeline

| Time | Segment | Screen | Host |
|---|---|---|---|
| 0:00–1:30 | Opening: what the build is | Upload (empty) | live |
| 1:30–6:30 | **Moment 1: format memory** | Upload → Formats | live |
| 6:30–13:30 | **Moment 2: lineage thread and audited correction**, then the quote system fills (Integration) | Document → Review → Audit trail → quote-system page | live |
| 13:30–18:30 | Use Case 2: the large account, what-if, CT, memo, quote system | Analytics | live |
| 18:30–21:30 | Chat with citations | Chat | live |
| 21:30–24:00 | Cost and governance close | Costs → Formats | live |
| 24:00–25:00 | Buffer, or the offline proof that nothing is staged | — | offline |

**Fallback rule.** If a live screen has not rendered within 10 seconds, or the network drops, say "let me switch to the recorded copy". Then continue on the offline host (section *Fallback*) at the same step. Do not debug in front of the room.

## Before the demo (T–1 day and T–30 min)

**T–1 day**
- Turn on `demo_mode` (runbook §Demo host):
  ```bash
  terraform apply -var-file=environments/poc.tfvars -var demo_mode=true
  ```
  This gives one always-ready instance each for HTTP and Durable, removing the ~15 s cold start.
- Rehearse once end to end on the live host, then once on the offline host.
- Create a fresh review item for Moment 2:
  - upload `src/TestShared/fixtures/lossrun-badtotal.pdf` under quote `Q-DEMO-<date>`;
  - note its document id.

**T–30 min**
- Open each screen once to warm the caches: Upload, the Moment 2 document, Analytics on **Q-DEMO-1000 · Large Account (synthetic)**, Chat, Costs.
- **Screen:** 1440 × 900 or larger. Light theme (top-bar toggle). Dense rows.
- **Sign-in on the live host:** user `underwriter`. The password is the demo-users secret (runbook, *Credentials*). Read it on the presenter's machine only; it never goes on a slide. The live host's address is in the runbook (*Workbench*). It is never shown, so no Azure name appears on screen.
- **A second browser tab, already open:** the offline host (section *Fallback*).

**After the demo:** set `demo_mode` back to false (Terraform apply without the variable).

## 0:00–1:30 Opening

| Click / key | What the room sees | Say |
|---|---|---|
| Nav rail → **Upload** | The drop zone and "What we received" | "This is an independent proof of concept, built on Azure and run on synthetic data. In production it would run in your tenant, on your data, under Microsoft's DPA. It reads any carrier's loss run, learns each layout once, and replays it without a model from then on." |

## 1:30–6:30 Moment 1: format memory

| # | Click / key | What the room sees | Say |
|---|---|---|---|
| 1 | **Replay demo** (top right). Live alternative: drop `bundle.pdf` and `lossrun2-native.pdf` from `src/TestShared/fixtures` with quote `Q-DEMO-<date>` | The package opens: the payroll report and the letter are *Kept, not processed*; two loss runs start their steppers | "Nothing is thrown away: the payroll page and the letter stay with the upload and are searchable in chat." |
| 2 | Wait about 10 s | Synthetic Mutual goes Classified → Layout → Format match → Extracted → Validated → Ready. The badge reads **Format memory hit · Synthetic Mutual … · 0 model calls**, and the cost line reads $0.00 for extraction | **"0 model calls. We learned this carrier's layout once; every later report is a deterministic replay: the same answer every time, at cents per document."** |
| 3 | Point at the second row | **Format learned · shadow 2/3 · Fictional Casualty** | "A carrier we met recently. It was learned once in about 2–3 model calls and is now confirming on its next documents before it is trusted for straight-through." |
| 4 | Nav rail → **Formats** | The registry: active, shadow and retired versions, and who created each (hand, learn, delta) | "'Learned once' is visible and governable. A manager can retire a version, with a reason that is audited." |
| 5 | Nav rail → **Upload** → open a workbook row (any `Q-DEMO-1000` document, e.g. `/documents/doc-0feacbd9e91be8f7`) | The **sheet grid** with *Sheet 1 of 2: Claims Detail*, and the ledger beside it | "Spreadsheets are parsed natively: no layout call, no model call, $0.00. The source here is the workbook itself." |
| 6 | Click **14,305.30** (first row, Medical · Paid) | The thread draws to cell **L7**; the panel shows *Claims Detail!L7* | "Even in a workbook, every value points at its cell." |

## 6:30–13:30 Moment 2: the lineage thread and an audited correction

| # | Click / key | What the room sees | Say |
|---|---|---|---|
| 1 | Open `/documents/doc-9997d9e37c0fcb03-p1-1` (Synthetic Mutual) | The source page on the left, the claims ledger on the right; *Printed subtotal vs extracted rows* all "matches" | "Left is what the carrier printed. Right is our ledger." |
| 2 | Click **8,000.00** (WC-22-0001, Medical · Paid). Keyboard: Tab to the ledger, arrows to the cell, Enter | A thread to the highlighted "8,000.00" on the page. The panel shows the printed text, confidence, page and position, format `synthetic-mutual-pdf@5`, extractor *Replay*, and the rules that read the value | **"Every number knows where it came from: page, box, format version, confidence, and which validations it passed."** |
| 3 | Panel → **Trace whole row**, or Enter on **WC-22-0001** | A 50/50 split; threads fan out to every printed value; the strip reads "Row WC-22-0001 · 11 values traced" | "The whole claim at once." |
| 4 | Press **→** three times, then **Esc** | The strip steps through the values; Esc closes and returns focus to the cell | "All of this works from the keyboard and with a screen reader. The thread is decoration; the panel carries the facts." |
| 5 | Nav rail → **Review** → *Documents needing review* → the `Q-DEMO-<date>` bad-total document | The printed subtotal **9,300.00** is outlined in red, the totals check reads "off by 100.00", and the decision offers **Accept extracted values** and **Reject document** | "Here the carrier's own subtotal is wrong. We don't silently fix it." |
| 6 | In the totals check, click **9,300.00** under *Medical paid* | The thread goes to the printed subtotal; the panel shows R-10 / R-26 failing and **Correct this value** | |
| 7 | New value `9,200.00`. Rationale: "Printed subtotal misprint; rows sum to 8,000 + 1,200". Click **Add to decision** | *Audit entries that will be recorded*: field, old → new, user, role, rationale. The buttons change to **Save changes and approve**, **Reset changes**, **Reject document**; *Accept extracted values* is gone | "Before anything is saved, you see exactly what goes into the audit trail. Once you change something, approving as extracted is no longer on offer." |
| 8 | **Save changes and approve** | "Saved 1 change and approved. Revision 2." Then the **Audit trail** tab: *Override* and *Saved changes and approved*, each with a lock and "ledger tx …", under *Append-only SQL ledger table* | **"Every correction is an append-only, tamper-evident ledger entry: who, when, old, new, why. The database itself refuses an update or delete on it."** Optional: type a value in another cell, click away (a *Draft* chip marks it), then **Reset changes** and **Undo** within 5 s |

### Moment 2, continued: Integration — the quote system fills (inside Moment 2's time, about 90 s)

Before the demo: open a second browser window on the mock quote system's page for the demo quote, `https://<mock host>/api/ui/loss-information?quote=<quote>&code=<the mock's function key>`, beside the workbench. It updates on its own (server-sent events). Use a quote whose upload is waiting for its review.

| # | Click / key | What the room sees | Say |
|---|---|---|---|
| 1 | Point at the quote-system window | "Loss Information": one column per policy period, newest left. The pending document's column says *Waiting for review*, every amount *Needs input*. Badge *Quote revision 1*, *n fields need input* | "This is the policy system's prior-insurance screen. It only shows what the integration has delivered, and nothing unverified." |
| 2 | Tab to a *Needs input* cell | Its reason appears ("the document is waiting for review"; or "validation rule R-10 still fails") | "Every gap says why, for the keyboard and the screen reader too, not just a colour." |
| 3 | In the workbench, finish the review: **Save changes and approve** (Moment 2 step 8) | Within a few seconds the quote-system column fills: dates, carrier, policy number, incurred, expense, claim counts. The badge goes to *Quote revision 2* | "The approval was one transaction. It bumped the quote's revision and queued the payload command with it. The quote system fetched revision 2 and filled the column." |
| 4 | Open **Integration** at the bottom of the quote-system page | Last command id (the outbox id), idempotency key, *Quote revision received 2*, last event `lossrun.quote.revised (revision 2)` | "Idempotent by key, ordered by revision: an older command that arrives late is refused, so the screen can never go backwards." |
| 5 | Point at *Total premium / payroll / Ex-Mod* | *not from loss run* | "We never fill what a loss run does not print." |
| 6 | Upload `src/TestShared/fixtures/lossrun-export-dated.csv` to a second demo quote with **Valuation date** `2024-06-30` (brief 25 Answers 3: it prints the policy term on every row; it replays the hand spec `synthetic-mutual-csv-dated@1`, no model call), with that quote's page open | Both policy columns fill at once, newest left, with effective and expiration dates; *Quote revision 1*, ready | "When the loss run says it, the quote system has it, with no human step." |
| 7 | Upload the plain `lossrun-export.csv` to a third quote, page open | The columns fill in the order the export lists them; the header says *Effective date needs input*, and the date and valuation rows show *Needs input* (R-33) | "And when the loss run doesn't say, the quote system knows exactly what is still unverified, instead of guessing." |

Keep this line in the script: **revision 2 appeared on the open page one second after the decision** (the brief 25 live check). That is the integration moment.

**Fallback (offline).** Open the same page with `?replay=1` (on the mock host, `/api/ui/loss-information?quote=Q-DEMO-REPLAY&replay=1`; or from the demo pack with no network, `http://localhost:4173/quote-system/loss-information.html?quote=Q-DEMO-REPLAY&replay=1`). The page plays revision 1, then fills revision 2 six seconds later, with a *Replay (recorded)* badge. Say "this is the recorded run of the same step".

## 13:30–18:30 Use Case 2: analytics on the large account

| # | Click / key | What the room sees | Say |
|---|---|---|---|
| 1 | Top bar **Quote** → **Q-DEMO-1000 · Large Account (synthetic)** → nav rail **Analytics** | The KPI strip and charts for 1,046 claims across 34 workbooks; *As adjusted* in the footer | "A large account: 1,046 claims. The features come from a cached snapshot. Under 25 concurrent underwriters this answered in 158 ms at p95." |
| 2 | **Scenario** → drag **Reserve adjustment** to +15 % | Every KPI reads baseline → scenario → delta, instantly | "The what-if runs in the browser on the same engine as the server, byte for byte, so the number on screen is the number in the memo." |
| 3 | **Reset to baseline**. Then the footer → **As reported** | The KPIs move back by the overrides; the footer names the overrides applied | "Underwriters price on adjusted numbers and can always see what the carrier reported." |
| 4 | Quote → **Q-DEMO-CT · California CT account (synthetic)**. **Filters** → check **Cumulative trauma only** | CT claims only; cause code 78 leads *Loss drivers by cause (incurred)* | "California specifics are first-class: cumulative trauma, post-termination CT, litigation rate. All 41 catalog metrics are measured against truth on the synthetic corpus, within tolerance." |
| 5 | **Export pricing memo** | The Word memo downloads | "The memo is generated from the same snapshot, and the export is audited." |
| 6 | **Push flags to quote** (rationale "bind review") | A confirmation: flags queued for the quote system | "Integration goes through a transactional outbox to Service Bus. The quote system receives it exactly once, and every push is in the audit trail." |

## 18:30–21:30 Chat with citations

| # | Click / key | What the room sees | Say |
|---|---|---|---|
| 1 | Nav rail → **Chat** → scope *This quote* (Q-DEMO-CT) | The suggested questions | "Chat answers only from this quote's documents and the system of record, and every fact is cited." |
| 2 | Type: "What is the litigation rate on lost-time claims?" | The answer streams: "15.0 % [M1]" | |
| 3 | Type: "Which claims are litigated? Give their claim numbers." | The claim numbers, with citation chips | |
| 4 | Click a citation chip | The source opens at the cited cell (sheet grid or page) | **"No number without a source. A document that tries to instruct the assistant is treated as data. We test that with a planted instruction, and it is ignored."** |

The text-answer eval passed 10/10 on its rubric with 0 fabricated numbers (`docs/evaluation/results/2026-10-23-chat-text/`).

## 21:30–24:00 Cost and governance close

| # | Click / key | What the room sees | Say |
|---|---|---|---|
| 1 | Nav rail → **Costs** | Metered cost to date, the share of documents with zero model calls, cost per document by path, and the **reconciliation line** against the Azure bill | "Every billable call is metered per document and reconciled to the Azure invoice. Replay costs about $0.006 a page. A new carrier costs about 5 cents once." |
| 2 | Point at the reconciliation line | Metered vs billed and the ratio, with its scope note | "Where they differed, we found why: a test harness that metered nothing. Excluding it, metering is within 6 % of the bill." |
| 3 | Nav rail → **Formats** | The registry again | "Governance in one sentence: tamper protection is preventive on extracted values, overrides and audit events (append-only ledger tables: the database refuses an update or delete) and detective on claim rows (every version is kept, and the audit proof flags any change without an audited decision)." |
| 4 | Close | — | "Three-year build TCO is people, not cloud: cloud is 4–21 % of it. The full pack, with the TCO, the governance artefacts and the sealed-set protocol, is in your hands." |

## 24:00–25:00 Buffer or the offline proof

If time remains, switch to the offline host (below) and show that the same app runs on recorded API responses. This proves the live demo was not a mock-up.

## Fallback: the offline host

- **URL:** https://<demo-custom-domain> (live since 2026-10-23, managed certificate). If it is unreachable, use the mock preview host from the runbook (*Workbench*).
- **Or the demo pack on the laptop, no network at all:** `lossrun demo pack --out <dir>`, then `node serve.mjs` in that folder and open http://localhost:4173.
- **What it is:** the same build on responses recorded from the live API. The data is synthetic. There is no sign-in; a **Role** picker sits in the top bar.
- **Local alternative:** `npm run dev:mock` in `src/web`, then http://localhost:5173.
- **Changes live in memory.** A page reload resets them.

| Live step | Offline equivalent |
|---|---|
| Moment 1, steps 1–4 | Identical: **Replay demo** plays the recorded upload |
| Moment 1, steps 5–6 (sheet grid) | Not recorded: skip, and say "workbooks show their sheet grid live" |
| Moment 2, steps 1–4 | Identical: the same document id |
| Moment 2, steps 5–8 | **Review** → **Synthetic Mutual** (quote Q-DEMO-11B) |
| Large account | **Q-PERF-1K · Performance account (synthetic, 1,000 claims)** |
| CT account | **Q-SAMPLE-10**. The CT filter is available but shows few claims (corpus v2 data) |
| Chat | Recorded answers to the suggested questions only |
| Costs | The recorded snapshot, including the reconciliation line |

## Numbers on the slides (sources)

| Claim | Number | Source |
|---|---|---|
| Delivered key-field accuracy, native recurring / XLSX-CSV / scans ≥ 300 dpi | 100.0 % on each row, with CIs | `docs/evaluation/results/2026-10-23-corpus-v3/deck-table.md` |
| First-seen native PDF | 100.0 % [98.8–100.0 %], 5/5 delivered | `…/corpus-v3/studies/first-seen.md` |
| Insight accuracy | 41/41 metrics within tolerance | `…/corpus-v3/insights/insights-delivered.md` |
| Override p95 / chat p50, 25 underwriters | 860 ms / 2.9 s | `docs/evaluation/results/2026-10-23-loadtest/` (run 3) |
| Chat text answers | 10/10 rubric, 0 fabrications | `docs/evaluation/results/2026-10-23-chat-text/` |
| Metered vs billed (AI services) | 0.94 excluding the unmetered study window | `docs/evaluation/results/2026-10-23-cost-reconciliation/` |

**"What the POC found" slide.** Measuring found these, and each is fixed:
1. The format lock was released before commit, so one new workbook format was learned 35 times under load. It is now held until commit.
2. An override took about 17 SQL round trips across regions. It is now one stored procedure, 3 round trips on the response path, p95 from 1.87 s to 0.86 s.
3. The bill showed an unmetered test harness. The reconciliation is now part of the cost screen.
4. Live uploads exposed two learn weaknesses:
   - a summary sheet's totals were left unchecked;
   - a date constant was located by its value.

   Both are now deterministic lint fixes, and the planted inconsistent workbook is caught.
5. Measuring the 50 MB batch found an upload ceiling: any request over about 28.6 MB failed with an HTTP 500 and nothing in the logs. The app server's default body limit, 30,000,000 bytes, sat below the platform's 100 MB. Uploads now take up to 100 MB and get a clear "too large" answer above it. The 50 MB batch then ran in 89 s.
