import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { mkdirSync, writeFileSync } from "node:fs";

// Live checks against the deployed Static Web App (brief 11 acceptance): Document screen interactive on the demo host
// (warm), browser what-if = server what-if on real quotes, axe on the live screens in both themes. Runs only with
// LIVE_BASE_URL, LIVE_API_BASE, LOSSRUN_API_USER and LOSSRUN_API_PASSWORD set (like the simulator); credentials are
// put in the tab's sessionStorage the way the sign-in form does, and never logged.

const base = process.env.LIVE_BASE_URL;
const api = process.env.LIVE_API_BASE;
const user = process.env.LOSSRUN_API_USER;
const password = process.env.LOSSRUN_API_PASSWORD;
test.skip(!base || !api || !user || !password, "live: set LIVE_BASE_URL, LIVE_API_BASE, LOSSRUN_API_USER, LOSSRUN_API_PASSWORD");
test.use({ baseURL: base });

const DOC = process.env.LIVE_DOC ?? "doc-9997d9e37c0fcb03-p1-1";
const QUOTES = (process.env.LIVE_QUOTES ?? "Q-DEMO-11,Q-SAMPLE-10").split(",");
const out: Record<string, unknown> = {};
const auth = () => "Basic " + Buffer.from(`${user}:${password}`).toString("base64");

async function signIn(page: Page, theme: "light" | "dark" = "light", quote = QUOTES[0]) {
  await page.addInitScript(({ token, theme, quote }) => {
    sessionStorage.setItem("lossrun.basic", token);
    localStorage.setItem("lossrun.theme", theme);
    localStorage.setItem("lossrun.quote", quote);
  }, { token: Buffer.from(`${user}:${password}`).toString("base64"), theme, quote });
}

test("Document screen interactive on the demo host (warm)", async ({ page }) => {
  await signIn(page);
  await page.goto(`/documents/${DOC}`);
  await page.locator(".lr-source__page img").waitFor({ timeout: 60_000 }); // warm the Function App and the CDN
  const samples: number[] = [];
  for (let i = 0; i < 5; i++) {
    await page.goto(`/documents/${DOC}`);
    samples.push(
      await page.evaluate(
        () =>
          new Promise<number>((resolve) => {
            const check = () => {
              const img = document.querySelector<HTMLImageElement>(".lr-source__page img");
              if (img?.complete && img.naturalWidth > 0 && document.querySelector('[data-cell="0/0|medical.paid"]')) resolve(performance.now());
              else requestAnimationFrame(check);
            };
            check();
          }),
      ),
    );
  }
  const sorted = [...samples].sort((a, b) => a - b);
  out.documentLive = { samples, p50: sorted[2], max: sorted[4], note: "ms from navigation start to page image + ledger cells rendered; Static Web App + Function App, warm" };
  expect(sorted[2]).toBeLessThan(2000);
});

test("Chat streams a cited answer from the live API and the citation opens its page", async ({ page }) => {
  await signIn(page);
  await page.goto("/chat");
  const asked = Date.now();
  await page.getByRole("button", { name: "What is the largest open claim?" }).click();
  await page.getByLabel("Sources").waitFor({ timeout: 60_000 });
  const total = Date.now() - asked;
  await page.getByText("How this was answered").click();
  const usage = await page.locator(".chat__how .lr-meta").first().innerText();
  await page.locator(".chat__cites .lr-citation").first().click();
  await page.locator(".dialog .lr-source__page img").waitFor({ timeout: 60_000 });
  out.chat = { totalMs: total, usage };
  expect(usage).toMatch(/\$0\.00\d\d/);
});

test("Reopen for review end to end: document → reason → review → accept, with audit entries", async ({ page, request }) => {
  const doc = process.env.LIVE_REOPEN_DOC;
  test.skip(!doc, "set LIVE_REOPEN_DOC to a processed document without an open review");
  await signIn(page);
  await page.goto(`/documents/${doc}`);
  await page.locator("[data-cell]").first().waitFor({ timeout: 60_000 });
  await page.getByRole("button", { name: "Reopen for review…" }).click();
  await page.getByLabel("Reason").fill("12b live check: broker sent a corrected loss run");
  await page.getByRole("button", { name: "Reopen", exact: true }).click();
  await page.waitForURL(`**/reviews/${doc}`, { timeout: 30_000 });
  await page.getByRole("button", { name: "Accept extracted values" }).click();
  await page.getByText(/^Approved as extracted\. Revision \d+\.$/).waitFor({ timeout: 30_000 }); // brief 24 step 4 status line

  const trail = await (await request.get(`${api}/documents/${doc}/audit`, { headers: { Authorization: auth() } })).json();
  const actions = (trail.entries as { action: string; rationale?: string }[]).map((e) => e.action);
  out.reopen = { doc, actions: actions.slice(0, 6) };
  expect(actions).toContain("review.reopened");
  expect(actions).toContain("review.accept");
  expect(actions.indexOf("review.accept")).toBeLessThan(actions.indexOf("review.reopened")); // newest first
});

test("browser what-if equals the server's on live quotes", async ({ page, request }) => {
  await signIn(page);
  await page.goto("/upload");
  await page.locator("main h1").waitFor();
  const catalogText = await (await request.get(`${api}/catalog`, { headers: { Authorization: auth() } })).text();
  const scenarios = [
    { reserveFactorBps: 1000 },
    { reserveFactorBps: -2000, capPerClaimCents: 1_000_000 },
    { trendFactorBpsByYear: { "2022": 11025, "2023": 10500 }, includeCT: false },
    { excludeCatastrophicAboveCents: 1_500_000, includeNonCA: false },
  ];
  const results: { quote: string; scenario: string; same: boolean }[] = [];
  for (const quote of QUOTES) {
    const claimsText = await (await request.get(`${api}/quotes/${quote}/claims?view=adjusted`, { headers: { Authorization: auth() } })).text();
    for (const scenario of scenarios) {
      const server = await (await request.post(`${api}/quotes/${quote}/features/what-if`, { headers: { Authorization: auth() }, data: { scenario } })).json();
      const browser = await page.evaluate(
        ([c, cat, s]) => (window as any).__lossrun.snapshot(c, cat, s, "adjusted"),
        [claimsText, catalogText, scenario] as const,
      );
      const same = JSON.stringify(JSON.parse(browser.json)) === JSON.stringify(server.scenario);
      results.push({ quote, scenario: JSON.stringify(scenario), same });
    }
  }
  out.liveParity = { comparisons: results.length, identical: results.filter((r) => r.same).length, results };
  expect(results.filter((r) => !r.same)).toEqual([]);
});

const SCREENS: [string, string, string][] = [
  ["Upload", "/upload", "main h1"],
  ["Document", `/documents/${DOC}`, ".lr-source__page img"],
  ["Review queue", "/reviews", "[role=tab]"],
  ["Analytics", "/analytics", ".lr-kpis"],
  ["Chat", "/chat", "main h1"],
  ["Costs", "/costs", ".costs-hero"],
  ["Formats", "/formats", "[role=grid]"],
];
const axe: { screen: string; theme: string; violations: number }[] = [];
for (const theme of ["light", "dark"] as const) {
  for (const [name, path, ready] of SCREENS) {
    test(`live axe: ${name} · ${theme}`, async ({ page }) => {
      await signIn(page, theme);
      await page.emulateMedia({ reducedMotion: "reduce" });
      await page.goto(path);
      await page.locator(ready).first().waitFor({ timeout: 60_000 });
      await page.waitForTimeout(500);
      const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      axe.push({ screen: name, theme, violations: r.violations.length });
      expect(r.violations.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => n.target) }))).toEqual([]);
    });
  }
}

test.afterAll(() => {
  mkdirSync("test-results", { recursive: true });
  writeFileSync("test-results/live.json", JSON.stringify({ ...out, axe }, null, 2));
});
