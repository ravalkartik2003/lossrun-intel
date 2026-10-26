import { test, expect } from "@playwright/test";
import { REVIEW_DOC, open } from "./helpers.ts";

// Brief 35 Answers 2: the same-carrier toggle. Offered only when a review reason found a carrier name the format does not know; the decision
// sends the printed name as carrierNames.add.

const DOC = REVIEW_DOC; // the pending document review; the confirm-format review asks no carrier question

test("the same-carrier toggle is offered only on a review that found an unknown carrier name", async ({ page }) => {
  await open(page, "/reviews/doc-5209d62bf384ad6a", { theme: "light", reducedMotion: true });
  await page.getByRole("checkbox", { name: /fills the policy down/ }).waitFor();
  await expect(page.getByRole("checkbox", { name: /is the same carrier as/ })).toHaveCount(0);

  await open(page, `/reviews/${DOC}`, { theme: "light", reducedMotion: true });
  await page.getByRole("button", { name: "Accept extracted values" }).waitFor({ timeout: 60_000 });
  const toggle = page.getByRole("checkbox", { name: "“Synthetic Property Casualty Co of America” is the same carrier as “Synthetic Mutual Insurance Co.”" });
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAccessibleDescription(/Adds the name to the format's carrier names/);
});

test("accepting with the toggle ticked sends carrierNames.add with the printed name", async ({ page }) => {
  await open(page, `/reviews/${DOC}`, { theme: "light", reducedMotion: true });
  await page.getByRole("button", { name: "Accept extracted values" }).waitFor({ timeout: 60_000 });
  await page.getByRole("checkbox", { name: /is the same carrier as/ }).check();
  const sent = page.waitForRequest((r) => r.url().endsWith(`/reviews/${DOC}/decision`) && r.method() === "POST");
  await page.getByRole("button", { name: "Accept extracted values" }).click();
  const body = (await sent).postDataJSON() as { action: string; formatChanges?: Record<string, unknown> };
  expect(body.action).toBe("accept");
  expect(body.formatChanges).toEqual({ "carrierNames.add": "Synthetic Property Casualty Co of America" });
});
