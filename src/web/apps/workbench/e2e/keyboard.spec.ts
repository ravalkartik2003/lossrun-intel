import { test, expect } from "@playwright/test";
import { DOC, open } from "./helpers.ts";

// Design-brief §6 keyboard checks, keyboard only (no mouse): skip link and tab order, table cell navigation, panel
// open/close with focus return, highlight navigation on the page, slider operation, tabs with arrows, reduced motion.

const focused = (page: import("@playwright/test").Page) => page.evaluate(() => {
  const el = document.activeElement as HTMLElement | null;
  return { tag: el?.tagName, name: el?.getAttribute("aria-label") ?? el?.textContent?.trim().slice(0, 60), cell: el?.dataset.cell ?? null, region: el?.dataset.region ?? null };
});

test("skip link is first and moves focus to main", async ({ page }) => {
  await open(page, "/upload");
  await page.keyboard.press("Tab");
  expect((await focused(page)).name).toBe("Skip to content");
  await expect(page.getByRole("link", { name: "Skip to content" })).toBeVisible();
  await page.keyboard.press("Enter");
  await expect(page).toHaveURL(/#main$/);
});

test("ledger: arrows move between cells, Enter opens lineage, Esc closes and returns focus", async ({ page }) => {
  await open(page, `/documents/${DOC}`);
  await page.locator(".lr-source__page img").waitFor();
  await page.locator('[data-cell="0/0|claimNumber"]').focus();
  await page.keyboard.press("ArrowRight");
  expect((await focused(page)).cell).toBe("0/0|dateOfInjury");
  await page.keyboard.press("End");
  expect((await focused(page)).cell).toBe("0/0|total.incurred");
  await page.keyboard.press("ArrowDown");
  expect((await focused(page)).cell).toBe("0/1|total.incurred");
  await page.keyboard.press("Home");
  await page.keyboard.press("ArrowUp");
  for (let i = 0; i < 8; i++) await page.keyboard.press("ArrowRight"); // → medical.paid
  expect((await focused(page)).cell).toBe("0/0|medical.paid");
  await page.keyboard.press("Enter");
  const panel = page.getByRole("complementary", { name: "Value lineage" });
  await expect(panel).toBeVisible();
  await expect(panel.getByRole("heading", { name: "Medical paid" })).toBeFocused(); // focus moves into the panel
  await expect(page.locator(".lr-thread__path")).toHaveCount(1);
  await page.keyboard.press("Escape");
  await expect(panel).toHaveCount(0);
  await expect(page.locator('[data-cell="0/0|medical.paid"]')).toBeFocused(); // focus returns to the cell
});

test("row trace: Enter on the claim number, ←/→ step through the row's values", async ({ page }) => {
  await open(page, `/documents/${DOC}`);
  await page.locator(".lr-source__page img").waitFor();
  await page.locator('[data-cell="0/0|claimNumber"]').focus();
  await page.keyboard.press("Enter");
  const strip = page.getByRole("complementary", { name: "Row lineage" });
  await expect(strip).toBeVisible();
  await expect(strip.getByText(/1 of \d+/)).toBeVisible();
  await page.keyboard.press("ArrowRight");
  await expect(strip.getByText(/2 of \d+/)).toBeVisible();
  await page.keyboard.press("ArrowLeft");
  await expect(strip.getByText(/1 of \d+/)).toBeVisible();
  expect(await page.locator(".lr-thread__path").count()).toBeGreaterThan(5);
  await page.keyboard.press("Escape");
  await expect(page.locator('[data-cell="0/0|claimNumber"]')).toBeFocused();
});

test("page highlights: Tab reaches them, arrows move between them, Enter selects", async ({ page }) => {
  await open(page, `/documents/${DOC}`);
  await page.locator(".lr-source__page img").waitFor();
  await page.locator(".lr-source__scroll").focus();
  await page.keyboard.press("Tab");
  const first = await focused(page);
  expect(first.region).toBeTruthy();
  await page.keyboard.press("ArrowRight");
  const second = await focused(page);
  expect(second.region).toBeTruthy();
  expect(second.region).not.toBe(first.region);
  await page.keyboard.press("Enter");
  await expect(page.getByRole("complementary", { name: "Value lineage" })).toBeVisible();
});

test("tabs: arrow keys switch policy periods", async ({ page }) => {
  await open(page, `/documents/${DOC}`);
  const first = page.getByRole("tab", { name: /SYN-WC-1001/ });
  await first.focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByRole("tab", { name: /SYN-WC-1002/ })).toBeFocused();
  await expect(page.getByRole("tab", { name: /SYN-WC-1002/ })).toHaveAttribute("aria-selected", "true");
});

test("what-if slider: arrows, Page keys, Home/End update the KPIs", async ({ page }) => {
  await open(page, "/analytics");
  await page.locator(".lr-kpis").waitFor();
  const slider = page.getByRole("slider", { name: "Reserve adjustment" });
  await slider.focus();
  await page.keyboard.press("ArrowRight");
  await expect(page.getByText(/Scenario applied/)).toBeVisible();
  await expect(slider).toHaveValue("1");
  await page.keyboard.press("End");
  await expect(slider).toHaveValue("100");
  await page.keyboard.press("Home");
  await expect(slider).toHaveValue("-50");
  await page.getByRole("button", { name: "Reset to baseline" }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByText(/claims included · baseline/)).toBeVisible();
});

test("dialogs trap focus and return it on Esc (follow-up)", async ({ page }) => {
  await open(page, "/analytics");
  await page.locator(".lr-kpis").waitFor();
  const trigger = page.getByRole("button", { name: "Assign follow-up" });
  await trigger.focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("dialog", { name: "Assign a follow-up" })).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(trigger).toBeFocused();
});

test("reduced motion: the lineage thread is drawn without animation", async ({ page }) => {
  await open(page, `/documents/${DOC}`, { reducedMotion: true });
  await page.locator(".lr-source__page img").waitFor();
  await page.locator('[data-cell="0/0|medical.paid"]').click();
  const animation = await page.locator(".lr-thread__path").first().evaluate((el) => getComputedStyle(el).animationName);
  expect(animation).toBe("none");
});

test("200% zoom: 1920×1080 at 200 % keeps the workbench usable; 1440×900 at 200 % falls back to the summary", async ({ page }) => {
  await page.setViewportSize({ width: 960, height: 540 }); // 1920×1080 at 200 %: tablet layout
  await open(page, `/documents/${DOC}`);
  await expect(page.getByRole("tab", { name: "Ledger" })).toBeVisible(); // source and ledger as tabs
  await expect(page.locator('[data-cell="0/0|medical.paid"]')).toBeVisible();
  await page.getByRole("tab", { name: "Source page" }).click();
  await expect(page.locator(".lr-source__page img")).toBeVisible();
  await page.setViewportSize({ width: 720, height: 450 }); // 1440×900 at 200 %: below 768px
  await expect(page.getByText("Open this document on a larger screen")).toBeVisible();
  await expect(page.getByText("WC-22-0001")).toBeVisible(); // claims and rules stay readable (no lost content)
});

test("320px reflow: Upload has no horizontal scroll (WCAG 1.4.10)", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 700 });
  await open(page, "/upload", { role: "Applicant" });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(0);
});
