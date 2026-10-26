import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

// Brief 25: the mock quote system's loss-information page (src/LossRun.QuoteSystemMock/wwwroot), served here from its files
// through route interception — no Functions host, no network. Live mode gets the recorded grid and a one-event stream.

const ORIGIN = "http://quote-system.test";
const wwwroot = (name: string) => fileURLToPath(new URL(`../../../../LossRun.QuoteSystemMock/wwwroot/${name}`, import.meta.url));
const html = readFileSync(wwwroot("loss-information.html"), "utf8");
const replay = readFileSync(wwwroot("replay.json"), "utf8");
const script = readFileSync(wwwroot("loss-information.js"), "utf8");
const grids = JSON.parse(replay) as { grid: { quoteId: string } }[];

async function serve(page: Page, revision: 1 | 2 = 2) {
  await page.route(`${ORIGIN}/api/ui/loss-information*`, (r) => r.fulfill({ contentType: "text/html", body: html }));
  await page.route(`${ORIGIN}/api/ui/replay.json`, (r) => r.fulfill({ contentType: "application/json", body: replay }));
  await page.route(`${ORIGIN}/api/ui/loss-information.js`, (r) => r.fulfill({ contentType: "text/javascript", body: script }));
  await page.route(`${ORIGIN}/api/quotes/Q-DEMO-REPLAY/loss-information`, (r) => r.fulfill({ contentType: "application/json", body: JSON.stringify(grids[revision - 1].grid) }));
  await page.route(`${ORIGIN}/api/quotes/Q-DEMO-REPLAY/loss-information/stream`, (r) =>
    r.fulfill({ contentType: "text/event-stream", body: `event: revision\ndata: {"quoteRevision":${revision}}\n\n` }));
}

for (const theme of ["light", "dark"] as const) {
  test(`loss information: grid, markers and axe · ${theme}`, async ({ page }) => {
    await serve(page);
    await page.goto(`${ORIGIN}/api/ui/loss-information?quote=Q-DEMO-REPLAY&theme=${theme}`);
    const grid = page.getByRole("table", { name: /One column per policy period/ });
    await expect(grid).toBeVisible();
    await expect(page.locator("#revision")).toHaveText("Quote revision 2");
    await expect(page.locator("#readiness")).toHaveText("1 field need input");
    await expect(page.getByText("Replay (recorded)")).toBeHidden(); // live mode

    // Columns newest left; the rejected document's column says so in text, not colour only.
    const headers = grid.locator("thead th");
    await expect(headers.nth(1)).toContainText("SYN-WC-1002");
    await expect(headers.nth(2)).toContainText("SYN-WC-1001");
    await expect(headers.nth(3)).toContainText("Excluded from quote (document rejected)");
    await expect(grid.getByRole("row", { name: /Total premium/ })).toContainText("not from loss run");
    await expect(grid.getByRole("row", { name: /Total # incurred claims/ })).toContainText("+1 report-only");

    // A cell that needs input carries its reason, reachable by keyboard and announced through aria-describedby.
    const needs = grid.getByRole("row", { name: /Total incurred loss/ }).locator(".needs").first();
    await needs.focus();
    const tip = page.locator(`#${await needs.getAttribute("aria-describedby")}`);
    await expect(tip).toBeVisible();
    await expect(tip).toContainText("validation rule R-10 still fails");

    await page.locator("details#integration summary").click();
    await expect(page.locator("#strip")).toContainText("ob-replay-2");

    const axe = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(axe.violations.map((v) => `${v.id}: ${v.nodes.length}`)).toEqual([]);
  });
}

test("replay mode plays the recording: revision 1 then 2, the pending column fills", async ({ page }) => {
  await serve(page);
  await page.goto(`${ORIGIN}/api/ui/loss-information?quote=Q-DEMO-REPLAY&replay=1`);
  await expect(page.getByText("Replay (recorded)")).toBeVisible();
  await expect(page.locator("#revision")).toHaveText("Quote revision 1");
  await expect(page.locator("#grid thead")).toContainText("Waiting for review");
  await expect(page.locator("#revision")).toHaveText("Quote revision 2", { timeout: 10_000 });
  await expect(page.locator("#grid thead")).toContainText("SYN-WC-1001");
  await expect(page.locator("#live")).toHaveText("Loss information updated to quote revision 2.");
});
