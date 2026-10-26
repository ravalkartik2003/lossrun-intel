import type { Page } from "@playwright/test";

export type Theme = "light" | "dark";

/** Opens a route in mock mode with a theme, role and quote preset (per-viewer settings live in web storage). */
export async function open(page: Page, path: string, opts: { theme?: Theme; role?: string; quote?: string; reducedMotion?: boolean } = {}) {
  await page.addInitScript(({ theme, role, quote }) => {
    localStorage.setItem("lossrun.theme", theme);
    localStorage.setItem("lossrun.density", "dense");
    if (quote) localStorage.setItem("lossrun.quote", quote);
    sessionStorage.setItem("lossrun.mockRole", role);
  }, { theme: opts.theme ?? "light", role: opts.role ?? "Underwriter", quote: opts.quote ?? "Q-DEMO-11" });
  if (opts.reducedMotion) await page.emulateMedia({ reducedMotion: "reduce" });
  await page.goto(path);
  await page.locator("main h1").first().waitFor();
}

export const DOC = "doc-9997d9e37c0fcb03-p1-1";
export const REVIEW_DOC = "doc-04777370247230a8";
