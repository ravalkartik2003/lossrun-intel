import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

// Brief 13: a spreadsheet's source is its sheet grid (no page image). Live only, like live.spec.ts: LIVE_BASE_URL,
// LOSSRUN_API_USER, LOSSRUN_API_PASSWORD; LIVE_SHEET_DOC names a spreadsheet document (default: one of Q-LOAD-1000's).

const base = process.env.LIVE_BASE_URL;
const user = process.env.LOSSRUN_API_USER;
const password = process.env.LOSSRUN_API_PASSWORD;
test.skip(!base || !user || !password, "live: set LIVE_BASE_URL, LOSSRUN_API_USER, LOSSRUN_API_PASSWORD");
test.use({ baseURL: base });

const DOC = process.env.LIVE_SHEET_DOC ?? "doc-0feacbd9e91be8f7";

for (const theme of ["light", "dark"] as const) {
  test(`spreadsheet source renders as a grid and a ledger cell traces to its sheet cell (${theme})`, async ({ page }) => {
    await page.addInitScript(({ token, theme }) => {
      sessionStorage.setItem("lossrun.basic", token);
      localStorage.setItem("lossrun.theme", theme);
    }, { token: Buffer.from(`${user}:${password}`).toString("base64"), theme });
    const pageImages: string[] = [];
    page.on("request", (r) => {
      if (/\/source\/pages\/\d+$/.test(new URL(r.url()).pathname)) pageImages.push(r.url());
    });

    await page.goto(`/documents/${DOC}`);
    const grid = page.locator("table.lr-sheet");
    await grid.waitFor({ timeout: 60_000 });
    await expect(page.locator(".lr-source__page img")).toHaveCount(0);
    expect(pageImages).toEqual([]); // no page-image request (it answered 409 for workbooks)
    await expect(page.getByText(/Sheet 1 of \d+: /)).toBeVisible();

    // Ledger → sheet: the selected value's cell is the active, pressed region in the grid.
    await page.locator('[data-cell="0/0|medical.paid"]').click();
    const active = page.locator(".lr-sheet__value--active");
    await expect(active).toHaveCount(1);
    await expect(active).toHaveAttribute("data-region", "policyPeriods[0].claims[0].medical.paid");
    await expect(active).toHaveAttribute("aria-pressed", "true");

    const axe = await new AxeBuilder({ page }).include(".lr-source").withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
    expect(axe.violations.map((v) => `${v.id}: ${v.nodes.length}`)).toEqual([]);
    await page.screenshot({ path: `test-results/sheet-${theme}.png` });
  });
}
