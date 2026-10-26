# Loss Run Intelligence — UI workbench (ADR 0012)

The React workbench implementing `design-brief.md`. The Figma Make export in `figma-export/` is a visual reference only; no code was ported from it.

## Where things are

| Path | What it is |
|---|---|
| `src/web/packages/ui/` | The design system: `tokens.css` (every color, type size, spacing, motion value; light and dark), `base.css`, `components.css`, and the component vocabulary from design-brief §4. Ladle stories in `src/stories/`. |
| `src/web/packages/metrics/` | The TypeScript twin of the analytics engine (ADR 0011). The what-if web worker runs it. |
| `src/web/apps/workbench/` | The app, with seven screens: Upload, Document, Review (queue and detail), Analytics, Chat, Costs, Formats. Supporting code: the API client, the mock layer (MSW), Playwright specs. |
| `docs/ui/mock/` | Mock data. See the next table. |
| `docs/ui/screenshots/` | Deck screenshots: `lineage-single-cell-{light,dark}.png`, `lineage-row-trace-{light,dark}.png`, and `screens/` (every screen in both themes; Upload at 320px; the Document summary at 375px). |
| `infra/web.tf`, `infra/scripts/deploy-web.ps1` | Hosting on a Static Web App (Free) with API CORS (ADR 0012). |

What is in `docs/ui/mock/`:

| File or folder | What it holds |
|---|---|
| `*.json` | The design-brief mocks. |
| `capture/` | A live-API capture: three quotes, five documents with page images and layouts, reviews, formats, costs, audit trails, and a recorded upload event stream. Synthetic fixtures only; made by `apps/workbench/scripts/capture-mock.mjs`. |
| `chat-turns.json` | Recorded chat examples until the chat-service work. |

## Run it

```bash
cd src/web && npm install
```

```bash
npm run dev:mock
```

That starts the offline mock mode at http://localhost:5173. Pick a role (Applicant, Underwriter, UWManager, Admin) in the top bar.

Live mode against the deployed API. Sign in with the demo-host Basic user.

```bash
VITE_API_BASE=https://<function-app>.azurewebsites.net/api/v1 npm run dev
```

The design-system stories:

```bash
npm run ladle
```

Entra mode sets `VITE_AUTH=entra`, `VITE_ENTRA_CLIENT_ID`, `VITE_ENTRA_AUTHORITY` and `VITE_ENTRA_SCOPE`. MSAL then signs in with a redirect, and `GET /me` reads the app roles back.

## Tests

| Command (in `src/web/apps/workbench`) | What it proves |
|---|---|
| `npx vitest run` | Contrast ≥ 4.5:1 for text and ≥ 3:1 for UI and focus on every token pair, in both themes. Also the ledger ↔ lineage model, scenario conversion and number formatting. |
| `npx playwright test e2e/a11y.spec.ts` | axe-core finds zero violations (WCAG 2.0 / 2.1 A + AA). Coverage: the seven screens plus their key states (lineage panel, row trace, what-if on, replay finished, applicant view, chat answered), each in light and dark — 26 runs. |
| `npx playwright test e2e/keyboard.spec.ts` | The keyboard pass: <ul><li>the skip link</li><li>table cell navigation</li><li>panels opening and closing, with focus returned</li><li>row trace with ←/→</li><li>page highlights reached by Tab, moved by arrows</li><li>tabs, slider, dialogs</li><li>reduced motion</li><li>200 % zoom</li><li>320px reflow</li></ul> |
| `npx playwright test e2e/golden.spec.ts` | The 60 golden cases of the analytics work, run in the app's what-if web worker, are byte-identical to the C# engine. |
| `npx playwright test e2e/perf.spec.ts` | On the 1,000-claim account: slider to KPI update; row-trace draw time; Document interactive (mock). |
| `npx playwright test e2e/screenshots.spec.ts` | Regenerates `docs/ui/screenshots/`. |
| `LIVE_BASE_URL=… LIVE_API_BASE=… LOSSRUN_API_USER=… LOSSRUN_API_PASSWORD=… npx playwright test e2e/live.spec.ts` | Against the deployed app: Document interactive (warm); browser what-if equals the server's `POST /features/what-if`; axe on the live screens. |
| `LIVE_BASE_URL=… LOSSRUN_API_USER=… LOSSRUN_API_PASSWORD=… npx playwright test e2e/sheet.spec.ts` | The close-out work, against the deployed app. A workbook's source renders as its sheet grid (`SheetViewer`), with no page-image request. A ledger cell traces to its sheet cell. axe finds zero violations in both themes. `LIVE_SHEET_DOC` picks the document. |
| `PACK_BASE_URL=http://localhost:4173 npx playwright test e2e/pack.spec.ts` | The close-out work: the offline demo pack (`lossrun demo pack`, then `node serve.mjs`), or the demo host, renders every screen while every request to another host is aborted and recorded. None may be recorded. |

CI (`.github/workflows/web.yml`) runs the metrics twin, Vitest, the typecheck and both builds, then the axe, keyboard and golden specs on every pull request that touches the web workspace.

## Responsive policy (ADR 0012)

| Width | What you get |
|---|---|
| **≥ 1280px** | The full workbench. Lineage panel docked on the right; the thread is drawn. |
| **768–1279px** | Source and ledger become tabs. Lineage becomes a bottom sheet (no thread; the panel carries the facts). The KPI strip wraps and charts go one per row. |
| **< 768px** | Upload, Chat, Costs, and a read-only Document summary (claims and failing rules, plus "open on a larger screen to trace values"). Review and Analytics show a one-line note. Upload reflows at 320px with no horizontal scroll. |

## Departures from the design brief

All of these are in ADR 0012.

- **Token contrast:** light `ink-3` and `review`, and dark `ink-3`, are one step darker or lighter than the brief's values. As given, they measured below 4.5:1.
- **New token:** `line-strong`, for ≥ 3:1 component boundaries.
- **Nav rail width:** 72px with labels at ≥ 1280px, instead of 56px. At 56px, "Analytics" does not fit at 11px without truncation; below 1280px the rail is icons only.
- **Charts:** the large-loss scatter plots as-reported incurred. Claims a scenario excludes appear as hollow dots; the KPIs and the other charts carry the scenario amounts.
- **No class-code filter:** class code is not printed on the corpus loss runs, and the filter bar says so.
- **"Send to review":** opens the pending review when one exists. Otherwise it assigns an audited follow-up, because a finished upload cannot re-enter review (report Questions).
