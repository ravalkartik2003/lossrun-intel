import { test, type Browser, type Page } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";

// Brief 12b §5: front-end timing under load. 25 browser sessions on the deployed Static Web App, run while the Azure Load
// Testing run drives the API, each cycling: open the 1,000-claim account (Analytics), move the reserve slider 10 times
// (slider → KPI update, and the browser's own event timing as an INP proxy), open a document (navigation → ledger
// interactive). Runs only with LIVE_BASE_URL, LOSSRUN_API_USER, LOSSRUN_API_PASSWORD (credentials go to sessionStorage the way
// the sign-in form puts them; never logged). UNDERLOAD_SESSIONS (25), UNDERLOAD_MINUTES (8), UNDERLOAD_QUOTE, UNDERLOAD_DOCS.

const base = process.env.LIVE_BASE_URL;
const user = process.env.LOSSRUN_API_USER;
const password = process.env.LOSSRUN_API_PASSWORD;
const sessions = Number(process.env.UNDERLOAD_SESSIONS ?? 25);
const minutes = Number(process.env.UNDERLOAD_MINUTES ?? 8);
const quote = process.env.UNDERLOAD_QUOTE ?? "Q-LOAD-1000";
const docs = (process.env.UNDERLOAD_DOCS ?? "").split(",").filter(Boolean);
test.skip(!base || !user || !password || docs.length === 0, "set LIVE_BASE_URL, LOSSRUN_API_USER, LOSSRUN_API_PASSWORD, UNDERLOAD_DOCS");
test.setTimeout((minutes + 5) * 60_000);

const pct = (xs: number[], q: number) => (xs.length ? [...xs].sort((a, b) => a - b)[Math.min(xs.length - 1, Math.ceil(q * xs.length) - 1)] : null);

interface Samples {
  slider: number[];
  eventTiming: number[];
  documentOpen: number[];
  analyticsOpen: number[];
  errors: string[];
}

async function session(browser: Browser, index: number, until: number, s: Samples) {
  const context = await browser.newContext({ baseURL: base, viewport: { width: 1440, height: 900 } });
  await context.addInitScript(({ token, quote }) => {
    sessionStorage.setItem("lossrun.basic", token);
    localStorage.setItem("lossrun.quote", quote);
  }, { token: Buffer.from(`${user}:${password}`).toString("base64"), quote });
  const page = await context.newPage();
  try {
    while (Date.now() < until) {
      await analytics(page, s);
      await page.waitForTimeout(8_000 + Math.random() * 6_000);
      await documentOpen(page, docs[(index + s.documentOpen.length) % docs.length], s);
      await page.waitForTimeout(8_000 + Math.random() * 6_000);
    }
  } catch (e) {
    s.errors.push(`session ${index}: ${(e as Error).message.split("\n")[0]}`);
  } finally {
    await context.close();
  }
}

async function analytics(page: Page, s: Samples) {
  const t0 = Date.now();
  await page.goto("/analytics");
  await page.getByText(/claims included · baseline/).waitFor({ timeout: 60_000 });
  s.analyticsOpen.push(Date.now() - t0);
  await page.evaluate(() => {
    const w = window as any;
    w.__events = [];
    new PerformanceObserver((list) => {
      for (const e of list.getEntries() as any[]) if (e.interactionId) w.__events.push(e.duration);
    }).observe({ type: "event", buffered: false, durationThreshold: 16 } as PerformanceObserverInit);
  });
  const slider = page.getByRole("slider", { name: "Reserve adjustment" });
  await slider.focus();
  for (let i = 0; i < 10; i++) {
    const ms = await page.evaluate(
      (up) =>
        new Promise<number>((resolve) => {
          const strip = document.querySelector(".lr-kpis")!;
          const before = strip.textContent;
          const t = performance.now();
          const mo = new MutationObserver(() => {
            if (strip.textContent !== before) {
              mo.disconnect();
              resolve(performance.now() - t);
            }
          });
          mo.observe(strip, { subtree: true, characterData: true, childList: true });
          const input = document.activeElement as HTMLInputElement;
          if (up) input.stepUp();
          else input.stepDown();
          input.dispatchEvent(new Event("input", { bubbles: true }));
          input.dispatchEvent(new Event("change", { bubbles: true }));
          setTimeout(() => resolve(-1), 5_000);
        }),
      i % 2 === 0,
    );
    if (ms >= 0) s.slider.push(ms);
    await slider.press(i % 2 === 0 ? "ArrowRight" : "ArrowLeft"); // a real key press for the browser's event timing
    await page.waitForTimeout(250);
  }
  s.eventTiming.push(...((await page.evaluate(() => (window as any).__events)) as number[]));
}

async function documentOpen(page: Page, doc: string, s: Samples) {
  const t0 = Date.now();
  await page.goto(`/documents/${doc}`);
  // The ledger's first claim cell: interactive for PDFs and spreadsheets alike (the load account is spreadsheets: no page image).
  await page.locator("[data-cell]").first().waitFor({ timeout: 60_000 });
  s.documentOpen.push(Date.now() - t0);
}

test("25 underwriter sessions on the live workbench during the load test", async ({ browser }) => {
  const s: Samples = { slider: [], eventTiming: [], documentOpen: [], analyticsOpen: [], errors: [] };
  const until = Date.now() + minutes * 60_000;
  const started = new Date().toISOString();
  await Promise.all(Array.from({ length: sessions }, (_, i) => new Promise((r) => setTimeout(r, i * 1_500)).then(() => session(browser, i, until, s))));
  const summary = (xs: number[]) => ({ samples: xs.length, p50: pct(xs, 0.5), p95: pct(xs, 0.95), max: xs.length ? Math.max(...xs) : null });
  const result = {
    started, finished: new Date().toISOString(), sessions, minutes, quote,
    sliderToKpiMs: summary(s.slider), eventTimingMs: summary(s.eventTiming), documentOpenMs: summary(s.documentOpen), analyticsOpenMs: summary(s.analyticsOpen),
    errors: s.errors,
  };
  mkdirSync("test-results", { recursive: true });
  writeFileSync("test-results/underload.json", JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result, null, 2));
});
