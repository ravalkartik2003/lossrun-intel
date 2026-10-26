# Loss Run Intelligence — UI design brief

Source of truth for the design pass (Figma Make) and the UI implementation brief. Data shapes come from `docs/api/openapi.yaml` and, for analytics, `docs/analytics/catalog.yaml` (ADR 0011). Accessibility is WCAG 2.1 AA, mandatory.

## 1. What this product is, in one paragraph

An underwriter at a California workers' comp insurer receives loss runs — claims-history reports from other carriers — in whatever shape the carrier sends them. This tool turns each one into a verified ledger of claims, shows exactly which printed cell every number came from, says what it is sure of and what a human should check, and keeps a tamper-evident record of every change. The underwriter then explores the account's loss history, tests scenarios, asks questions across the documents, and sends a priced-quote package on. The design's job is to make *trust* visible: provenance, confidence, and cost are first-class content, not metadata.

## 2. Design position

The subject's world is paper reports, actuarial tables and ledgers. We take that seriously rather than hiding it behind generic dashboard chrome. Three commitments:

1. **The document is always present.** Any screen that shows an extracted value can show the source page beside it. Provenance is a visible thread, not a tooltip.
2. **One bold moment, everything else quiet.** The memorable element is the *lineage thread*: select a value and a drawn connector links the ledger cell to its highlighted region on the source page, with the confidence and the rules that touched it. Alongside it, the format-memory badge ("0 model calls") is the product's signature claim. Every other surface is disciplined: tables, plain type, measured color.
3. **Numbers are the typography.** Tabular lining figures everywhere, right-aligned money, consistent precision. The type scale is built around how a column of dollar amounts reads, not around headlines.

What we deliberately avoid: card kits with identical rounded tiles and soft shadows; all-caps eyebrow labels; decorative gradients; emoji; stock illustration; monospace for data labels; a dark hero.

## 3. Tokens

### Color (light theme; dark theme in §3.4)
| Token | Hex | Use | Contrast on `surface` |
|---|---|---|---|
| `ink` | #15233B | primary text, nav rail background | 13.9:1 |
| `ink-2` | #3C4858 | secondary text | 8.1:1 |
| `ink-3` | #5F6B7B | tertiary text, disabled | 5.1:1 (was #6B7686, measured 4.32:1; ADR 0012) |
| `surface` | #F7F8F6 | page background (cool paper, not cream) | — |
| `surface-raised` | #FFFFFF | tables, panels | — |
| `line` | #D9DEE3 | borders, table rules | — |
| `action` | #1E5AA8 | links, primary buttons, focus ring | 6.3:1 |
| `verified` | #1F7A4D | delivered / passed / format-memory hit | 5.0:1 |
| `review` | #A5590A | needs human review, soft rule | 4.9:1; 4.6:1 on its tint (was #B4620B, measured 4.20:1; ADR 0012) |
| `fail` | #B3261E | hard rule failure, rejected | 5.9:1 |
| `learned` | #5B4BB5 | model-learned format, shadow status | 5.6:1 |
| `highlight` | #FFE08A at 45 % | source-page highlight fill (never carries text) | — |

Rules: color never carries meaning alone — every status also has an icon (Lucide) and a word. Status words are the vocabulary: *Verified*, *Needs review*, *Failed rule*, *Learned*, *Kept, not processed*, *Unsupported*.

### Type
- **Family:** Public Sans (open source, designed for US government services; excellent at small sizes; tabular figures via `font-variant-numeric: tabular-nums lining-nums`). One family only. Weights 400, 500, 600.
- **Scale (px / line-height):** 12/16 (table meta), 13/18 (dense table body), 14/20 (body, default), 16/24 (section titles, KPI labels), 20/28 (page title), 28/34 (KPI value), 40/44 (the single hero number on Costs only).
- Money: right-aligned, tabular, two decimals, thousands separators, negatives as −1,234.00 (true minus), never parentheses in the UI (the comparator handles parentheses on input).
- Dates: 2024-03-17 in tables; "Mar 17, 2024" in prose.
- Line length ≤ 75 characters in prose panels.

### Spacing and shape
- 4px base; component padding 8/12/16; section gaps 24/32; page gutter 24 (≥ 1280px), 16 (< 1280px).
- Radius: 4px on inputs/buttons, 6px on panels, 2px on table cells' highlight; nothing larger. Pills only for status badges (height 22px).
- Shadow: none on static panels; one 0 2px 8px rgba(21,35,59,.12) on floating panels (menus, the lineage panel when overlaid).
- Density: a dense table row is 32px; a comfortable row 40px; the user can toggle.

### Motion
- Respect `prefers-reduced-motion`. Durations 120–200ms, ease-out.
- Exactly two choreographed moments: (a) the lineage thread drawing from the selected cell to the page region (≤ 250ms), (b) the processing stepper's step advancing. Everything else is instant.

### 3.4 Dark theme
`surface` #121A26, `surface-raised` #182230, `line` #2A3544, `ink` #E8ECF1, `ink-2` #B7C0CC, `ink-3` #909BAB (was #8793A3: 4.32:1 on the selected-row tint; ADR 0012), `action` #7FB0F0, `verified` #5BC48A, `review` #E6A14A, `fail` #F08A80, `learned` #A99CF0, highlight #FFE08A at 30 %. Status icons unchanged. All pairs ≥ 4.5:1 on `surface-raised`.

Added in the workbench work: `line-strong` (light #7A8593, dark #738298) for component boundaries at ≥ 3:1 (WCAG 1.4.11). The implemented values live in `src/web/packages/ui/src/tokens.css`; `contrast.test.ts` asserts every pair the components use.

## 4. Component vocabulary

| Component | Purpose | Data (from the API) | States / a11y |
|---|---|---|---|
| **StatusBadge** | status word + icon in a pill | `ManifestItem.status`, `RuleOutcome.severity/status`, `FormatSummary.status` | icon has `aria-hidden`; text is the accessible name |
| **RoutingBadge** | the product's signature: "Format memory hit · AmTrust v2 · 0 model calls" | `ManifestItem.badge`, `routingTier`, `formatRef`, `formatStatus` | color by tier (verified / learned / review); when `formatStatus=shadow` append "· confirming (1 of 3)" |
| **CostLine** | "$0.034 · 6 pages · 0 model calls" | `UploadCost`, `ManifestItem.costUsd` | lives in the top bar and on every document header; tooltip shows the meter breakdown |
| **ConfidenceMeter** | 24×6px bar + number (0.00–1.00) | per-field confidence in `DocumentResult.document` lineage | `role="meter"`, `aria-valuenow`; thresholds 0.9 / 0.7 map to verified / review / fail colors, with the number always shown |
| **LineagePanel** | the hero: field value, raw text, confidence, source (page + region or sheet!A1), format version, extractor (replay / learn / delta / manual / context / derived), rules that touched it | `DocumentResult` lineage map + `RuleOutcome.affectedFields` | opens as a side panel; focus moves in; Esc closes; the connector is decorative (`aria-hidden`) and the panel text carries the same information |
| **SourceViewer** | page image or spreadsheet grid with highlight regions | `GET /documents/{id}/source/pages/{n}` + `/layout` | highlights are focusable with a visible 2px `action` outline; arrow keys move between highlights |
| **ClaimsTable** | TanStack table; sticky header; virtualized ≥ 200 rows; column groups Indemnity / Medical / Expense / Total each with Paid / Reserve / Incurred | `DocumentResult.document.policyPeriods[].claims[]` | `<table>` semantics preserved; sortable headers announce sort; row selection via checkbox, not row click |
| **RuleOutcomeList** | grouped by severity; each row: rule id, message, affected claim, "Open in source" | `RuleOutcome[]` | `notInformative` rendered as muted with the text "derived, not printed" |
| **ManifestList** | package contents with per-item status, classification, page range, reason | `UploadManifest.items[]` | the applicant-facing variant hides routing tiers and shows plain-language next steps |
| **ProcessingStepper** | Classified → Layout → Format match → Extracted → Validated → Ready, with per-step time | `UploadStatusView` / SSE `UploadProgress.step` | `aria-live="polite"` announces step changes once |
| **KpiTile** | label, value (28px tabular), delta vs baseline when a scenario is active | catalog metrics | not a card kit: tiles sit in a single bordered strip, separated by rules, not individual boxes |
| **ScenarioPanel** | what-if controls: reserve ±%, exclude catastrophic (> threshold), trend factor, include non-CA, include CT | `Scenario` | sliders are `<input type="range">` with visible numeric inputs; "Reset to as-reported" |
| **AuditTrail** | append-only list: time, user, role, field, old → new, rationale, revision; "Ledger verified <time>" | `vAuditTrail` via API (ADR 0012) | never editable; copy action per row |
| **Citation** | chip "loss-run-2024.pdf · p.4" or "claims.xlsx · Sheet1!F12"; opens SourceViewer at the region | chat answers (ADR 0013) | button semantics, accessible name includes "open source" |

Icons: Lucide only, 16px in tables, 20px elsewhere, stroke 1.75. Each status uses a fixed icon: Verified `check-circle-2`, Needs review `alert-circle`, Failed rule `x-octagon`, Learned `sparkles`, Kept `archive`, Unsupported `file-question`.

## 5. Screens

Global frame: 56px left nav rail (`ink` background, icons + labels, collapsible to icons at < 1280px); 48px top bar with quote selector, environment chip "POC · synthetic data", CostLine for the current context, user + role. Content max-width 1440, left-aligned. Everything works at 1280px; tables scroll horizontally below that.

### 5.1 Upload
- Drop zone (whole content area on drag), accepted types listed in words. On drop, the **ManifestList** fills in live from SSE: one row per file/segment with StatusBadge and classification. Non-loss-run items read "Kept, not processed — payroll report; available to chat".
- Per loss run: **ProcessingStepper** with timings; on completion the **RoutingBadge** and **CostLine** appear in the row.
- Right rail (applicant variant): plain-language outcome and next steps: "We found 1 loss run and 2 other documents. Policy year 2022 is missing — upload it or continue without it."
- Empty state: one sentence and the drop target. No illustration required; if one is used it carries no text.

### 5.2 Document
- Two panes, resizable: **SourceViewer** left (default 45 %), ledger right: policy periods as tabs; **ClaimsTable**; a totals strip under each period: *Printed* vs *Extracted* with ✓ or the delta in `fail` color.
- Selecting any cell opens the **LineagePanel** and draws the thread. The header carries RoutingBadge, valuation date, carrier, revision ("Revision 2 — 1 override"), CostLine.
- **RuleOutcomeList** docked below the table, collapsible. "Send to review" on any failure.
- As-reported / as-adjusted toggle when overrides exist.

### 5.3 Review queue
- Two tabs: *Documents needing review* and *Confirm new formats* (`ReviewItem.kind`). Rows: reasons as plain words, RoutingBadge, opened time, claim count.
- Detail: SourceViewer + ClaimsTable with failing cells outlined in `fail`; override form (new value, required rationale; `FieldOverride`); actions by state (brief 24 step 4): nothing changed → *Accept extracted values* (on a new format: *Confirm format and approve document*, with *Approve document only*), *Reject document*; something staged or typed → *Save changes and approve*, *Reset changes* (Undo for 5 s), *Reject document*. Typed text survives selecting another cell (a *Draft* chip on the cell) and leaving the screen with it asks first. "Also apply this document's format to future <carrier> documents" (`promoteToFormat`) only on document-review items whose document has a format, never sent with a reject. The audit entry preview is shown before saving; after saving, the AuditTrail row appears with the ledger lock icon.

### 5.4 Analytics (one quote)
- Filter bar: policy years, injury type, body part, class code, jurisdiction (CA / other), CT only.
- KPI strip (not tiles-as-cards): claims, total incurred, frequency, severity, paid-to-incurred, litigation rate, cumulative-trauma share, open claims, median reporting lag, closure velocity.
- Charts (ECharts canvas, one palette: `action` series with `ink-3` comparison; status colors only for status): trend by policy year (frequency, severity, incurred), med-only vs lost-time stacked bars, loss drivers by cause / body part (horizontal bars, not treemap), large-loss scatter with the threshold line, paid vs incurred development.
- **ScenarioPanel** docked right; every KPI shows baseline → scenario → delta; charts redraw instantly (web worker; TS twin of the catalog).
- Footer strip: "From 318 claims in 3 documents · valued 2024-08-31 · 2 overrides applied · catalog v1.2" with the as-reported / as-adjusted toggle. Actions: *Export pricing memo*, *Push flags to quote*, *Assign follow-up*.
- Every chart has a data-table alternative (toggle) for screen readers and for copying.

### 5.5 Chat
- Scope control first, as a segmented control: *All documents* / *This quote* / *My workspace (n files)*. The scope is enforced server-side; the UI only shows which is active.
- Thread; answers carry **Citation** chips; a "How this was answered" disclosure lists the tools used (search documents → query claims → compute metric) and the turn's cost.
- Suggested questions are specific to the active scope.

### 5.6 Costs
- The one 40px number: POC to date. Then: documents processed, average cost per document, share with zero model calls.
- Chart: cost per document over time colored by path (replay / delta / learn); per-carrier bars "learn once vs replay savings" with the break-even marker.
- Table of per-document cost lines with meter breakdown; reconciliation line "Metered $42.18 · billed $43.90 · 96 % · export of Oct 23".

### 5.7 Formats (small)
- Registry list: carrier, format id, version, status (shadow / active / retired), created by (hand / learn / delta), verification gate, documents processed, success counters. Retire with reason. This is where "learned once" becomes visible.

## 6. Accessibility checklist (acceptance for the UI brief)
- Contrast ≥ 4.5:1 text, ≥ 3:1 UI components and focus indicators; both themes.
- Visible focus (2px `action` outline, 2px offset) on every interactive element; logical tab order; skip link to content.
- All tables are real tables; charts have table alternatives; status is never color-only.
- Lineage thread, highlights and stepper have text equivalents; live regions announce once.
- Keyboard: open/close panels (Enter/Esc), move between highlights (arrows), operate sliders (arrows, Home/End) and tabs (arrows).
- Zoom to 200 % without loss; reflow at 320px for the applicant upload screen (the workbench screens are desktop-only by requirement, but must not break).
- Reduced motion honored. Language set; page titles per screen; form errors named and linked to fields.
- Test with axe-core in CI and one manual NVDA + keyboard pass before the demo.

## 7. Mock data
`docs/ui/mock/` holds JSON that matches the OpenAPI schemas: `upload-manifest.json`, `document-result.json` (with lineage and rule outcomes), `review-items.json`, `formats.json`, `quote-payload.json`, `features.json` (after the analytics work), `chat-turns.json` (after the chat-service work). Figma Make should be given these verbatim as its sample data; the React app should run against them with `VITE_MOCK=1` before the live API.

## 8. How Figma Make is used
- The first prompt is §1–§5 of this document plus the mock JSON. Ask for all seven screens, both themes, 1280 and 1440 widths, and component states (loading, empty, error, dense/comfortable).
- Iterate on: the lineage thread, the KPI strip (must not become cards), table density, and focus visibility.
- Export the project zip to `docs/ui/figma-export/` (keep screenshots, CSS variables, component JSX; remove bundled dependencies). It is a reference. The implementer builds on the existing stack from this brief, the tokens, and the mocks — not by porting the export.
