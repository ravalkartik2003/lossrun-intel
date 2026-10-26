import { test, expect } from "@playwright/test";

// Brief 13 addendum: the offline demo pack (`lossrun demo pack`) works with no network. Serve the pack with
// `node serve.mjs` and set PACK_BASE_URL (http://localhost:4173), or point it at the demo host. Every request to any
// other host is aborted and recorded; the walk must render every screen and record none.

const base = process.env.PACK_BASE_URL;
test.skip(!base, "pack: set PACK_BASE_URL to a served demo pack");
test.use({ baseURL: base, serviceWorkers: "allow" });

test("the demo pack renders every screen with no request beyond localhost", async ({ page }) => {
  const external: string[] = [];
  await page.route("**/*", (route) => {
    const host = new URL(route.request().url()).hostname;
    if (host === "localhost" || host === "127.0.0.1" || host === new URL(base!).hostname) return route.continue(); // the pack, or the demo host itself
    external.push(route.request().url());
    return route.abort("internetdisconnected");
  });

  await page.goto("/");
  await expect(page.getByText("Independent POC · synthetic data", { exact: false })).toBeVisible({ timeout: 30_000 });

  await page.goto("/upload");
  await page.getByRole("button", { name: /Replay demo/ }).click();
  await expect(page.getByText(/0 model calls/).first()).toBeVisible({ timeout: 30_000 });

  await page.goto("/documents/doc-9997d9e37c0fcb03-p1-1");
  await page.locator(".lr-source__page img").waitFor({ timeout: 30_000 });
  await page.locator('[data-cell="0/0|medical.paid"]').click();
  await expect(page.getByText("Printed text").first()).toBeVisible(); // the lineage panel opened on the selected value

  for (const [path, text] of [["/analytics", /Key indicators|Analytics/], ["/chat", /Ask a question|Chat/], ["/costs", /Metered cost/], ["/formats", /Formats/]] as const) {
    await page.goto(path);
    await expect(page.getByText(text).first()).toBeVisible({ timeout: 30_000 });
  }

  expect(external).toEqual([]);
});
