import { test, expect } from "@playwright/test";
import { mkdirSync, writeFileSync } from "node:fs";
import { DOC, open } from "./helpers.ts";

// Brief 11 acceptance timings (mock mode, local machine): slider → KPI update < 200 ms on a 1,000-claim account (and the
// browser's event timing for the same interactions, INP-style); row trace drawn < 300 ms; Document screen interactive.
// The demo-host number for the Document screen comes from live.spec.ts.

const out: Record<string, unknown> = {};
const pct = (xs: number[], q: number) => [...xs].sort((a, b) => a - b)[Math.min(xs.length - 1, Math.ceil(q * xs.length) - 1)];

test("slider → KPI update on the 1,000-claim account", async ({ page }) => {
  await open(page, "/analytics", { quote: "Q-PERF-1K" });
  await page.locator(".lr-kpis").waitFor();
  await page.getByText(/claims included · baseline/).waitFor({ timeout: 30_000 });
  await page.evaluate(() => {
    const w = window as any;
    w.__events = [];
    new PerformanceObserver((list) => {
      for (const e of list.getEntries() as any[]) if (e.interactionId) w.__events.push(e.duration);
    }).observe({ type: "event", buffered: true, durationThreshold: 16 } as PerformanceObserverInit);
  });
  const slider = page.getByRole("slider", { name: "Reserve adjustment" });
  await slider.focus();
  const latencies: number[] = [];
  for (let i = 0; i < 20; i++) {
    const ms = await page.evaluate(
      () =>
        new Promise<number>((resolve) => {
          const strip = document.querySelector(".lr-kpis")!;
          const before = strip.textContent;
          const t0 = performance.now();
          const mo = new MutationObserver(() => {
            if (strip.textContent !== before) {
              mo.disconnect();
              resolve(performance.now() - t0);
            }
          });
          mo.observe(strip, { subtree: true, characterData: true, childList: true });
          (document.activeElement as HTMLElement).dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowRight", bubbles: true }));
          const input = document.activeElement as HTMLInputElement;
          input.stepUp();
          input.dispatchEvent(new Event("input", { bubbles: true }));
          input.dispatchEvent(new Event("change", { bubbles: true }));
        }),
    );
    latencies.push(ms);
  }
  // Real key presses for the browser's own event timing.
  for (let i = 0; i < 10; i++) {
    await slider.press("ArrowRight");
    await page.waitForTimeout(60);
  }
  const events = (await page.evaluate(() => (window as any).__events)) as number[];
  const timing = await page.locator(".analytics__timing").textContent();
  out.slider = { samples: latencies.length, p50: pct(latencies, 0.5), p95: pct(latencies, 0.95), max: Math.max(...latencies), eventTimingMaxMs: events.length ? Math.max(...events) : null, eventsOver16ms: events.length, workerLine: timing };
  expect(pct(latencies, 0.95)).toBeLessThan(200);
});

test("row trace draws within 300 ms", async ({ page }) => {
  await open(page, `/documents/${DOC}`);
  await page.locator(".lr-source__page img").waitFor();
  const samples: number[] = [];
  for (let i = 0; i < 5; i++) {
    await page.keyboard.press("Escape");
    const ms = await page.evaluate(
      () =>
        new Promise<number>((resolve) => {
          const cell = document.querySelector<HTMLElement>('[data-cell="0/0|claimNumber"]')!;
          const t0 = performance.now();
          const check = () => {
            const paths = document.querySelectorAll(".lr-thread__path").length;
            if (paths >= 5) resolve(performance.now() - t0);
            else requestAnimationFrame(check);
          };
          cell.click();
          requestAnimationFrame(check);
        }),
    );
    samples.push(ms);
  }
  out.rowTrace = { samples, p50: pct(samples, 0.5), max: Math.max(...samples), note: "click → every thread path in the DOM (the 240 ms draw animation then runs)" };
  expect(Math.max(...samples)).toBeLessThan(300);
});

test("Document screen interactive (mock, local)", async ({ page }) => {
  await open(page, "/upload");
  const samples: number[] = [];
  for (let i = 0; i < 3; i++) {
    await page.goto(`/documents/${DOC}?r=${i}`);
    const ms = await page.evaluate(
      () =>
        new Promise<number>((resolve) => {
          const check = () => {
            const img = document.querySelector<HTMLImageElement>(".lr-source__page img");
            if (img?.complete && img.naturalWidth > 0 && document.querySelector('[data-cell="0/0|medical.paid"]')) resolve(performance.now());
            else requestAnimationFrame(check);
          };
          check();
        }),
    );
    samples.push(ms);
  }
  out.documentMock = { samples, note: "ms from navigation start to page image + ledger cells rendered (mock API, Vite dev server)" };
  expect(Math.max(...samples)).toBeLessThan(2000);
});

test.afterAll(() => {
  mkdirSync("test-results", { recursive: true });
  writeFileSync("test-results/perf.json", JSON.stringify(out, null, 2));
});
