import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { REVIEW_DOC, open, type Theme } from "./helpers.ts";

// Brief 26: the Needs investigation queue (UWManager/Admin) and "Flag for engineering" on the review screen, on the mock API.

const axe = async (page: import("@playwright/test").Page) =>
  (await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze()).violations.map((v) => `${v.id}: ${v.nodes.length}`);

for (const theme of ["light", "dark"] as Theme[]) {
  test(`needs investigation: filters, detail, status with a note, tuning item · ${theme}`, async ({ page }) => {
    await open(page, "/escalations", { theme, role: "UWManager" });
    const table = page.getByRole("grid", { name: /Escalations/ });
    await expect(table).toContainText("Learn attempts exhausted");
    await expect(table).not.toContainText("Unhandled exception"); // resolved: not open
    await page.getByLabel("Status", { exact: true }).selectOption("");
    await expect(table).toContainText("Unhandled exception");
    await page.getByLabel("Reason").selectOption("rule-dominant");
    await expect(page.getByRole("grid", { name: "Escalations (1 of 3)" })).toBeVisible();
    await page.getByLabel("Reason").selectOption("");

    await table.getByRole("button", { name: /Details of Learn attempts exhausted/ }).click();
    const detail = page.getByRole("region", { name: "Learn attempts exhausted" });
    await expect(detail).toContainText("R-02, R-10");
    await expect(detail).toContainText("learnModelCalls");
    await expect(detail.getByRole("link", { name: REVIEW_DOC })).toBeVisible();
    expect(await axe(page)).toEqual([]);

    const status = detail.getByRole("form", { name: "Change status" });
    await status.getByRole("button", { name: "Save status" }).click();
    await expect(status.getByText("A note is required")).toBeVisible();
    await expect(status.getByLabel("Note")).toHaveAttribute("aria-invalid", "true");
    await status.getByLabel("Note").fill("looking at the learn trace with the spec author");
    await status.getByRole("button", { name: "Save status" }).click();
    await expect(page.getByRole("region", { name: "Learn attempts exhausted" }).getByText("Investigating").first()).toBeVisible();

    const tuning = page.getByRole("region", { name: "Learn attempts exhausted" }).getByRole("form", { name: "Create tuning item" });
    await tuning.getByLabel("Cause category").selectOption("b");
    await tuning.getByLabel("Note").fill("two-line records the grammar cannot express");
    await tuning.getByRole("button", { name: "Create tuning item" }).click();
    await expect(page.getByRole("region", { name: "Learn attempts exhausted" })).toContainText(/Tuning item\s*ti-/);
    expect(await axe(page)).toEqual([]);
  });
}

test("the queue is for Engineer, UWManager and Admin; an underwriter flags a document from the review screen", async ({ page }) => {
  await open(page, "/escalations", { role: "Underwriter" });
  await expect(page.getByText("The investigation queue is for Engineer, UWManager and Admin roles.")).toBeVisible();

  await open(page, `/reviews/${REVIEW_DOC}`);
  await page.getByRole("button", { name: "Flag for engineering" }).click();
  const dialog = page.getByRole("dialog", { name: "Flag for engineering" });
  await dialog.getByRole("button", { name: "Flag document" }).click();
  await expect(dialog.getByText("at least 8 characters")).toBeVisible();
  expect(await axe(page)).toEqual([]);
  await dialog.getByLabel("What looks wrong").fill("the subtotal row is read as a claim on every page");
  await dialog.getByRole("button", { name: "Flag document" }).click();
  await expect(dialog).toBeHidden();
  await expect(page.getByRole("status").filter({ hasText: "Flagged for engineering." })).toBeVisible();
});

test("an Engineer works the queue and reads a review, but has no decision controls", async ({ page }) => {
  await open(page, "/escalations", { role: "Engineer" });
  await expect(page.getByRole("grid", { name: /Escalations/ })).toContainText("Learn attempts exhausted");
  await open(page, `/reviews/${REVIEW_DOC}`, { role: "Engineer" });
  await expect(page.getByText(/Read-only: this review is open/)).toBeVisible();
  await expect(page.getByRole("region", { name: "Decision" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Flag for engineering" })).toBeVisible();
  expect(await axe(page)).toEqual([]);
});
