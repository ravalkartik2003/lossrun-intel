import { test, expect } from "@playwright/test";
import { REVIEW_DOC, open } from "./helpers.ts";

// Brief 34 A3: the fill-down toggle in the review. Offered only when a review reason asks the question; the decision sends a typed boolean.

test("the fill-down toggle is offered only on a review that found blank policy cells", async ({ page }) => {
  await open(page, `/reviews/${REVIEW_DOC}`, { theme: "light", reducedMotion: true });
  await page.getByRole("button", { name: "Accept extracted values" }).waitFor();
  await expect(page.getByRole("checkbox", { name: /fills the policy down/ })).toHaveCount(0);

  await open(page, "/reviews/doc-5209d62bf384ad6a", { theme: "light", reducedMotion: true });
  const toggle = page.getByRole("checkbox", { name: /fills the policy down/ });
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAccessibleDescription(/Publishes a new version of the format/);
});

test("confirming with the toggle ticked sends periods.fillDown as a boolean", async ({ page }) => {
  await open(page, "/reviews/doc-5209d62bf384ad6a", { theme: "light", reducedMotion: true });
  await page.getByRole("checkbox", { name: /fills the policy down/ }).check();
  const sent = page.waitForRequest((r) => r.url().endsWith("/reviews/doc-5209d62bf384ad6a/decision") && r.method() === "POST");
  await page.getByRole("button", { name: "Confirm format and approve document" }).click();
  const body = (await sent).postDataJSON() as { action: string; formatChanges?: Record<string, unknown> };
  expect(body.action).toBe("accept");
  expect(body.formatChanges).toEqual({ "periods.fillDown": true });
  await expect(page.getByText(/Format confirmed for future/)).toBeVisible();
});
