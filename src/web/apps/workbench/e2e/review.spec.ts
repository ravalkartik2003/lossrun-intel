import { test, expect, type Page } from "@playwright/test";
import { REVIEW_DOC, open } from "./helpers.ts";

// Brief 24 step 4 (review plan 2026-10-25 §3): the review screen's states against the mock API. Each test is a fresh page,
// so the mock's in-memory decisions never leak between tests.

const CONFIRM_DOC = "doc-5209d62bf384ad6a";
const decision = (page: Page) => page.getByRole("region", { name: "Decision" });
const button = (page: Page, name: string) => decision(page).getByRole("button", { name, exact: true });

async function openReview(page: Page, documentId = REVIEW_DOC) {
  await open(page, `/reviews/${documentId}`);
  await page.locator(".lr-source__page img, .lr-sheet").first().waitFor();
}

async function typeCorrection(page: Page, cell: string, value: string, rationale = "") {
  await page.locator(`[data-cell="${cell}"]`).click();
  const form = page.getByRole("form", { name: /^Correct / });
  await form.getByLabel("New value").fill(value);
  if (rationale) await form.getByLabel("Rationale").fill(rationale);
}

function decisionBody(page: Page) {
  return page.waitForRequest((r) => r.method() === "POST" && /\/reviews\/[^/]+\/decision$/.test(r.url())).then((r) => r.postDataJSON() as Record<string, unknown>);
}

test("not dirty: accept the extracted values or reject", async ({ page }) => {
  await openReview(page);
  await expect(button(page, "Accept extracted values")).toBeVisible();
  await expect(button(page, "Reject document")).toBeDisabled(); // needs a note
  await expect(button(page, "Save changes and approve")).toHaveCount(0);
  await expect(button(page, "Reset changes")).toHaveCount(0);
});

test("typed text makes the review dirty, survives selecting another cell and shows a draft chip", async ({ page }) => {
  await openReview(page);
  await typeCorrection(page, "0/0|medical.paid", "1,234.00");
  await expect(button(page, "Save changes and approve")).toBeVisible();
  await expect(button(page, "Reset changes")).toBeVisible();
  await expect(button(page, "Accept extracted values")).toHaveCount(0);

  await page.locator(`[data-cell="0/1|medical.paid"]`).click();
  await expect(page.locator(`[data-cell="0/0|medical.paid"] .draft-chip`)).toBeVisible();
  await page.locator(`[data-cell="0/0|medical.paid"]`).click();
  await expect(page.getByRole("form", { name: /^Correct / }).getByLabel("New value")).toHaveValue("1,234.00");
});

test("reset clears every change and can be undone for five seconds", async ({ page }) => {
  await openReview(page);
  await typeCorrection(page, "0/0|medical.paid", "1,234.00", "medical bill received");
  await page.getByRole("button", { name: "Add to decision" }).click();
  await typeCorrection(page, "0/1|medical.paid", "99.00");

  await button(page, "Reset changes").click();
  await expect(button(page, "Accept extracted values")).toBeVisible();
  await expect(page.locator(".draft-chip")).toHaveCount(0);
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(button(page, "Save changes and approve")).toBeVisible();
  await expect(decision(page).getByText("Audit entries that will be recorded")).toBeVisible();

  await button(page, "Reset changes").click();
  await expect(page.getByRole("button", { name: "Undo" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Undo" })).toHaveCount(0, { timeout: 7_000 });
});

test("after Reset the open form shows the extracted value, after Undo the typed text (no stale draft)", async ({ page }) => {
  await openReview(page);
  const input = page.getByRole("form", { name: /^Correct / }).getByLabel("New value");
  await page.locator(`[data-cell="0/0|medical.paid"]`).click();
  const extracted = await input.inputValue();
  await input.fill("99.00");
  await button(page, "Reset changes").click();
  await expect(input).toHaveValue(extracted);
  await expect(button(page, "Accept extracted values")).toBeVisible();
  await page.getByRole("button", { name: "Undo" }).click();
  await expect(input).toHaveValue("99.00");
  await button(page, "Reset changes").click();
  // Typing again after a reset starts from the extracted value, not the discarded text.
  await input.press("End");
  await input.type("1");
  await expect(input).toHaveValue(`${extracted}1`);
});

test("an invalid draft blocks the save and is focused", async ({ page }) => {
  await openReview(page);
  await typeCorrection(page, "0/0|medical.paid", "twelve hundred", "medical bill received");
  await page.locator(`[data-cell="0/1|claimNumber"]`).click();
  await button(page, "Save changes and approve").click();
  await expect(page.getByRole("alert").filter({ hasText: "Not saved" })).toBeVisible();
  const input = page.getByRole("form", { name: /^Correct / }).getByLabel("New value");
  await expect(input).toBeFocused();
  await expect(input).toHaveAttribute("aria-invalid", "true");
});

test("save changes and approve auto-stages a valid draft into one override decision", async ({ page }) => {
  await openReview(page);
  await typeCorrection(page, "0/0|medical.paid", "1,234.00", "medical bill received");
  const body = decisionBody(page);
  await button(page, "Save changes and approve").click();
  const sent = await body;
  expect(sent.action).toBe("override");
  expect(sent.overrides).toEqual([{ fieldPath: "policyPeriods[0].claims[0].medical.paid", newValue: 1234, rationale: "medical bill received" }]);
  expect(sent).not.toHaveProperty("promoteToFormat");
  await expect(page.getByRole("status").filter({ hasText: /Saved 1 change and approved\. Revision \d+\./ })).toBeVisible();
});

test("overrides are sent typed: money as a number, dates as yyyy-MM-dd", async ({ page }) => {
  // The claim number cell opens the row trace, so the digit-only claim number case is in review/fieldTypes.test.ts.
  await openReview(page);
  await typeCorrection(page, "0/0|medical.paid", "1,234.00", "medical bill received");
  await page.getByRole("button", { name: "Add to decision" }).click();
  await typeCorrection(page, "0/0|dateOfInjury", "03/07/2022", "date of injury per the first report");
  const body = decisionBody(page);
  await button(page, "Save changes and approve").click();
  const sent = await body;
  expect(sent.overrides).toEqual([
    { fieldPath: "policyPeriods[0].claims[0].medical.paid", newValue: 1234, rationale: "medical bill received" },
    { fieldPath: "policyPeriods[0].claims[0].dateOfInjury", newValue: "2022-03-07", rationale: "date of injury per the first report" },
  ]);
  await expect(page.getByRole("status").filter({ hasText: /Saved 2 changes and approved\./ })).toBeVisible();
});

test("a date that does not parse is an inline error on the field", async ({ page }) => {
  await openReview(page);
  await typeCorrection(page, "0/0|dateOfInjury", "2022-02-30", "date of injury per the first report");
  await page.getByRole("button", { name: "Add to decision" }).click();
  const input = page.getByRole("form", { name: /^Correct / }).getByLabel("New value");
  await expect(input).toHaveAttribute("aria-invalid", "true");
  await expect(input).toHaveAccessibleDescription(/Enter a date like/);
});

test("reject sends no promoteToFormat and says the document is excluded", async ({ page }) => {
  await openReview(page, CONFIRM_DOC);
  await decision(page).getByLabel("Decision note").fill("another account's loss run");
  const body = decisionBody(page);
  await button(page, "Reject document").click();
  const sent = await body;
  expect(sent.action).toBe("reject");
  expect(sent).not.toHaveProperty("promoteToFormat");
  await expect(page.getByRole("status").filter({ hasText: "Rejected. Document kept in the upload, excluded from the quote." })).toBeVisible();
});

test("a new format: confirm it and approve, or approve the document only", async ({ page }) => {
  await openReview(page, CONFIRM_DOC);
  await expect(button(page, "Confirm format and approve document")).toBeVisible();
  await expect(decision(page).getByText(/This activates the learned format for future/)).toBeVisible();
  await expect(decision(page).getByRole("checkbox", { name: /future/ })).toHaveCount(0);
  const body = decisionBody(page);
  await button(page, "Approve document only").click();
  const sent = await body;
  expect(sent.promoteToFormat).toBe(false);
  expect(sent.action).toBe("accept");
  await expect(page.getByRole("status").filter({ hasText: "Approved as extracted. The new format stays in shadow." })).toBeVisible();
});

test("leaving with unsaved changes asks first", async ({ page }) => {
  await openReview(page);
  await typeCorrection(page, "0/0|medical.paid", "1,234.00");
  page.once("dialog", (d) => void d.dismiss());
  await page.getByRole("link", { name: "Open document view" }).click();
  await expect(page).toHaveURL(new RegExp(`/reviews/${REVIEW_DOC}$`));

  page.once("dialog", (d) => void d.accept());
  await page.getByRole("link", { name: "Open document view" }).click();
  await expect(page).toHaveURL(new RegExp(`/documents/${REVIEW_DOC}$`));
});
