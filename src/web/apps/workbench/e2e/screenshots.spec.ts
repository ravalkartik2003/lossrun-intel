import { test } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { DOC, REVIEW_DOC, open, type Theme } from "./helpers.ts";

// Deck screenshots (brief 11 acceptance): the single-cell and row-trace lineage shots in both themes, and every screen
// in both themes. Mock mode, 1440×900, synthetic data only. Written to docs/ui/screenshots/.

const dir = new URL("../../../../../docs/ui/screenshots/", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
mkdirSync(`${dir}screens`, { recursive: true });

for (const theme of ["light", "dark"] as Theme[]) {
  test(`lineage shots · ${theme}`, async ({ page }) => {
    await open(page, `/documents/${DOC}`, { theme });
    await page.locator(".lr-source__page img").waitFor();
    await page.locator(`[data-cell="0/0|medical.paid"]`).click();
    await page.getByRole("complementary", { name: "Value lineage" }).waitFor();
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${dir}lineage-single-cell-${theme}.png` });
    await page.keyboard.press("Escape");
    await page.locator(`[data-cell="0/0|claimNumber"]`).click();
    await page.getByRole("complementary", { name: "Row lineage" }).waitFor();
    await page.waitForTimeout(400);
    await page.screenshot({ path: `${dir}lineage-row-trace-${theme}.png` });
  });

  const screens: [string, string, (p: import("@playwright/test").Page) => Promise<void>][] = [
    ["upload", "/upload", async (p) => {
      await p.getByRole("button", { name: "Replay demo" }).click();
      await p.getByText("completed").first().waitFor({ timeout: 30_000 });
    }],
    ["document", `/documents/${DOC}`, async (p) => void (await p.locator(".lr-source__page img").waitFor())],
    ["review", `/reviews/${REVIEW_DOC}`, async (p) => void (await p.locator(".lr-source__page img").waitFor())],
    ["analytics", "/analytics", async (p) => {
      await p.locator(".lr-kpis").waitFor();
      await p.getByLabel("Reserve adjustment, %").fill("15");
      await p.getByText(/Scenario applied/).waitFor();
      await p.waitForTimeout(500);
    }],
    ["chat", "/chat", async (p) => {
      await p.getByRole("button", { name: "What is the largest open claim?" }).click();
      await p.getByLabel("Sources").waitFor();
    }],
    ["costs", "/costs", async (p) => {
      await p.getByText("Metered cost, POC to date").waitFor();
      await p.waitForTimeout(500);
    }],
    ["formats", "/formats", async (p) => void (await p.getByRole("grid", { name: /Format registry/ }).waitFor())],
  ];
  for (const [name, path, ready] of screens) {
    test(`screen ${name} · ${theme}`, async ({ page }) => {
      await open(page, path, { theme });
      await ready(page);
      await page.screenshot({ path: `${dir}screens/${name}-${theme}.jpg`, type: "jpeg", quality: 82 });
    });
  }

  test(`screen escalations · ${theme}`, async ({ page }) => {
    await open(page, "/escalations", { theme, role: "UWManager" });
    await page.getByRole("button", { name: /Details of Learn attempts exhausted/ }).click();
    await page.getByRole("region", { name: "Learn attempts exhausted" }).waitFor();
    await page.screenshot({ path: `${dir}screens/escalations-${theme}.jpg`, type: "jpeg", quality: 82, fullPage: true });
  });
}

test("small screens: upload at 320px, document summary at 375px", async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 720 });
  await open(page, "/upload", { role: "Applicant" });
  await page.screenshot({ path: `${dir}screens/upload-320.jpg`, type: "jpeg", quality: 82, fullPage: true });
  await page.setViewportSize({ width: 375, height: 812 });
  await page.goto(`/documents/${DOC}`);
  await page.getByText("Open this document on a larger screen").waitFor();
  await page.screenshot({ path: `${dir}screens/document-375.jpg`, type: "jpeg", quality: 82, fullPage: true });
});
