# ADR 0012 — Workbench hosting: Static Web Apps (Free) calling the Function App over CORS; UI stack details

**Status:** accepted · **Date:** 2026-10-22

## Context

The workbench work asks for:
- Azure Static Web Apps, set up by Terraform;
- linked to the Function App API with the Basic or Entra setting;
- CI that runs the unit and axe tests on every pull request.

Linking a backend to Static Web Apps works like this:
- **It needs the Standard plan.** Standard costs $9 per app per month; Free has no linked backends.
- **Static Web Apps takes over the Function App's authentication.** Unless the Function App runs App Service Authentication v2, only Static Web Apps can call it ([bring your own functions](https://learn.microsoft.com/en-us/azure/static-web-apps/functions-bring-your-own)). Our CLI, the submission simulator, the webhook test sink and, later, the quote system call the API directly with Basic or Entra headers. All of them would lose access.
- **Flex Consumption looks unreliable here.** There are open reports of `/api/*` returning 404 through the Static Web App when the linked backend is Flex Consumption ([Q&A, Jan 2026](https://learn.microsoft.com/en-us/answers/a/12550635)).
- **The `Authorization` header may not survive.** Reports differ on whether it is passed through to a linked backend; the UI sends Basic or bearer in that header.

## Decision

1. **Hosting: a Free-tier Static Web App** (`azurerm_static_web_app.web`, `infra/web.tf`).
   - **Production** is the live build (`VITE_API_BASE` = the Function App, `VITE_AUTH=basic` on the demo host).
   - **Preview environment `mock`** is the offline demo build (`VITE_MOCK=1`).
   - **Deployment:** `infra/scripts/deploy-web.ps1` uses Static Web Apps CLI 2.1.0 with the deployment token. The token is read through `az` into memory and never printed or stored.
2. **Calling the API: the browser calls the Function App directly.**
   - CORS on the Function App is set in Terraform (`site_config.cors`). It allows only the Static Web App origins and `http://localhost:5173`, without `support_credentials`, because no cookies are used.
   - The API keeps one authentication model for every client: Basic on the demo host, Entra bearer in production. Its role checks are unchanged.
   - **Entra:** the SPA uses MSAL (`VITE_AUTH=entra`) for an access token to the existing API app registration. App roles come from the token; the UI reads them back through `GET /me`.
3. **Pages are protected by headers.** `staticwebapp.config.json` sets:
   - a strict CSP: `connect-src` limited to `*.azurewebsites.net` and Entra login; no inline scripts; blob workers for what-if;
   - `nosniff`, `no-referrer` and `noindex`;
   - a SPA fallback.
4. **The UI stack is the one the brief fixes**, with these version choices (recorded so upgrades are deliberate):
   - TanStack Table **8.21** rather than 9.x, and MSW **2.15** rather than 3.x. Both are mature majors whose APIs the code uses.
   - React 19.3, Vite 8, TanStack Router 1.170, Query 5, Zustand 5, React Hook Form 7 with Zod 3.25, Radix primitives, ECharts 6 (canvas; tree-shaken core) and Recharts 3.
   - Lucide 1.61, Public Sans via `@fontsource`, Playwright 1.64 with `@axe-core/playwright` 4.14, Vitest 5, Ladle 5.
   - **Licenses are permissive or weak-copyleft, and nothing is modified:**
     - MIT for most packages;
     - Apache-2.0 for ECharts and Playwright;
     - MPL-2.0 for axe-core, which is test-only;
     - OFL for Public Sans;
     - ISC for Lucide.
   - React Aria is not needed: Radix and native elements cover every component.
5. **Two token values change from the brief, for contrast.** The brief's light `ink-3` (#6B7686) measures 4.32:1 on `surface`, and its `review` (#B4620B) measures 4.20:1. Both are below the 4.5:1 the brief requires.
   - **Light:** `ink-3` becomes #5F6B7B and `review` becomes #A5590A.
   - **Dark:** `ink-3` becomes #909BAB, because the brief's value is 4.32:1 on the selected-row tint.
   - **New token `line-strong`:** added for component boundaries at ≥ 3:1 (WCAG 1.4.11).
   - **Check:** `apps/workbench/src/a11y/contrast.test.ts` asserts every pair the components use, in both themes.

## Consequences

- **Cost:** none, on the Free tier.
- **The API stays one surface for every client**, and the brief's auth model is unchanged.
- **CORS is managed in Terraform.** A new SPA origin, such as a preview environment, has to be added to `web_preview_origins`. Until it is, that environment's browser calls are refused, so a `mock` preview has no reason to call the API.
- **Updates can break the storage setting.** Any Function App site update rewrites `AzureWebJobsStorage`, not only app-setting changes. The CORS change did this on first apply. The `blank_webjobs_storage` step now also runs whenever the CORS origins change (`infra/orchestration.tf`).
- **Moving to linked backends later is possible.** It would mean the Standard plan plus App Service Authentication v2 on the Function App, and re-testing the Flex Consumption routing. Nothing in the SPA depends on the choice beyond `VITE_API_BASE`.
