import { test, expect, type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { mkdirSync, writeFileSync } from "node:fs";
import { DOC, REVIEW_DOC, open, type Theme } from "./helpers.ts";

// Brief 11 acceptance / design-brief §6: axe-core, zero violations, all seven screens and their key states, both themes.
// WCAG 2.0/2.1 A and AA rule sets. Results are written to test-results/axe-summary.json for the report.

interface Screen {
  name: string;
  path: string;
  role?: string;
  ready: (page: Page) => Promise<void>;
}

const SCREENS: Screen[] = [
  { name: "Upload", path: "/upload", ready: async (p) => void (await p.getByRole("button", { name: "Replay demo" }).waitFor()) },
  {
    name: "Upload (replay finished)",
    path: "/upload",
    ready: async (p) => {
      await p.getByRole("button", { name: "Replay demo" }).click();
      await p.getByText("completed").first().waitFor({ timeout: 30_000 });
    },
  },
  { name: "Upload (applicant)", path: "/upload", role: "Applicant", ready: async (p) => void (await p.getByRole("heading", { name: "Upload your loss runs" }).waitFor()) },
  { name: "Document", path: `/documents/${DOC}`, ready: async (p) => void (await p.locator(".lr-source__page img").waitFor()) },
  {
    // Brief 23: a spreadsheet run with a report-only claim and a kept row of another line (Q-DEMO-23B capture).
    name: "Document (kept rows, report only)",
    path: "/documents/doc-163d6f090e74e933",
    ready: async (p) => void (await p.getByRole("heading", { name: /Rows of other lines kept, not extracted/ }).waitFor()),
  },
  {
    name: "Document (lineage panel)",
    path: `/documents/${DOC}`,
    ready: async (p) => {
      await p.locator(".lr-source__page img").waitFor();
      await p.locator(`[data-cell="0/0|medical.paid"]`).click();
      await p.getByRole("complementary", { name: "Value lineage" }).waitFor();
    },
  },
  {
    name: "Document (row trace)",
    path: `/documents/${DOC}`,
    ready: async (p) => {
      await p.locator(".lr-source__page img").waitFor();
      await p.locator(`[data-cell="0/0|claimNumber"]`).click();
      await p.getByRole("complementary", { name: "Row lineage" }).waitFor();
    },
  },
  {
    // ADR 0017: a summary-only loss run's period table, a printed value selected (design mock, not a capture).
    name: "Document (summary-only period table)",
    path: "/documents/doc-mock-summary-only",
    ready: async (p) => {
      await p.getByRole("table", { name: /Period summary/ }).waitFor();
      await p.locator(`[data-path="policyPeriods[1].subtotals.total.incurred"]`).click();
      await p.getByRole("complementary", { name: "Value lineage" }).waitFor();
    },
  },
  {
    name: "Document (reopen for review dialog)",
    path: `/documents/${DOC}`,
    ready: async (p) => {
      await p.locator(".lr-source__page img").waitFor();
      await p.getByRole("button", { name: "Reopen for review…" }).click();
      await p.getByRole("button", { name: "Reopen", exact: true }).click(); // empty reason: the error is shown and announced
      await p.getByText(/Say why it goes back to review/).waitFor();
    },
  },
  { name: "Review queue", path: "/reviews", ready: async (p) => void (await p.getByRole("tab", { name: /Documents needing review/ }).waitFor()) },
  { name: "Review detail", path: `/reviews/${REVIEW_DOC}`, ready: async (p) => void (await p.locator(".lr-source__page img").waitFor()) },
  {
    // Brief 24 step 4: dirty review — a staged change, a draft chip on another cell, the Save / Reset buttons.
    name: "Review detail (changes and a draft)",
    path: `/reviews/${REVIEW_DOC}`,
    ready: async (p) => {
      await p.locator(".lr-source__page img").waitFor();
      await p.locator(`[data-cell="0/0|medical.paid"]`).click();
      await p.getByLabel("New value").fill("1,234.00");
      await p.getByLabel("Rationale").fill("medical bill received");
      await p.getByRole("button", { name: "Add to decision" }).click();
      await p.locator(`[data-cell="0/1|medical.paid"]`).click();
      await p.getByLabel("New value").fill("99.00");
      await p.locator(`[data-cell="0/1|claimNumber"]`).click();
      await p.locator(".draft-chip").waitFor();
    },
  },
  {
    name: "Review detail (reset, undo offered)",
    path: `/reviews/${REVIEW_DOC}`,
    ready: async (p) => {
      await p.locator(".lr-source__page img").waitFor();
      await p.locator(`[data-cell="0/0|medical.paid"]`).click();
      await p.getByLabel("New value").fill("1,234.00");
      await p.getByRole("button", { name: "Reset changes" }).click();
      await p.getByRole("button", { name: "Undo" }).waitFor();
    },
  },
  {
    name: "Review detail (confirm format)",
    path: "/reviews/doc-5209d62bf384ad6a",
    ready: async (p) => void (await p.getByRole("button", { name: "Confirm format and approve document" }).waitFor()),
  },
  { name: "Analytics", path: "/analytics", ready: async (p) => void (await p.locator(".lr-kpis").waitFor()) },
  // Brief 24 step 2: the readiness banner in the Analytics header.
  { name: "Analytics (readiness banner)", path: "/analytics", ready: async (p) => void (await p.getByRole("region", { name: "Quote readiness" }).waitFor()) },
  {
    name: "Analytics (what-if active)",
    path: "/analytics",
    ready: async (p) => {
      await p.locator(".lr-kpis").waitFor();
      await p.getByLabel("Reserve adjustment, %").fill("20");
      await p.getByText(/Scenario applied/).waitFor();
    },
  },
  {
    name: "Chat",
    path: "/chat",
    ready: async (p) => {
      await p.getByRole("button", { name: "What is the largest open claim?" }).click();
      await p.getByLabel("Sources").waitFor();
    },
  },
  {
    name: "Chat (how this was answered open)",
    path: "/chat",
    ready: async (p) => {
      await p.getByRole("button", { name: "What is the largest open claim?" }).click();
      await p.getByText("How this was answered").click();
      await p.getByText(/model calls/).waitFor();
    },
  },
  {
    name: "Chat (citation open at the source)",
    path: "/chat",
    ready: async (p) => {
      await p.getByRole("button", { name: "What is the largest open claim?" }).click();
      await p.getByRole("button", { name: /C1 · 22-FC-00342/ }).click();
      await p.locator(".dialog .lr-source__page img").waitFor();
    },
  },
  { name: "Costs", path: "/costs", ready: async (p) => void (await p.getByText("Metered cost, POC to date").waitFor()) },
  { name: "Formats", path: "/formats", ready: async (p) => void (await p.getByRole("grid", { name: /Format registry/ }).waitFor()) },
  {
    // ADR 0019: the learned glossary aliases, filtered to one format.
    name: "Formats (learned aliases)",
    path: "/formats",
    ready: async (p) => {
      await p.getByRole("table", { name: /Learned aliases/ }).or(p.getByRole("grid", { name: /Learned aliases/ })).waitFor();
      await p.getByLabel("Format", { exact: true }).selectOption("fictional-casualty-group-pdf");
      await p.getByText(/Learned aliases \(3\)/).waitFor();
    },
  },
  {
    // ADR 0019: the confirm-format review lists the aliases its format introduced; one is being mapped to another field.
    name: "Review detail (confirm-format aliases)",
    path: "/reviews/doc-5209d62bf384ad6a",
    ready: async (p) => {
      await p.getByRole("group", { name: "Glossary aliases this format introduces" }).waitFor();
      await p.getByRole("radio", { name: "Map to another field" }).first().check();
      await p.getByLabel(/Canonical field for/).waitFor();
    },
  },
  {
    // Brief 34 A3: the review found blank policy cells; the reviewer confirms the carrier's export fills the policy down.
    name: "Review detail (fill-down toggle ticked)",
    path: "/reviews/doc-5209d62bf384ad6a",
    ready: async (p) => {
      await p.getByRole("checkbox", { name: /fills the policy down/ }).check();
      await p.getByText(/stays in shadow until it is approved or three documents replay cleanly/).waitFor();
    },
  },
  {
    // Brief 35 Answers 2: the page names the carrier differently; the reviewer confirms it is the same carrier.
    name: "Review detail (same-carrier toggle ticked)",
    path: "/reviews/doc-04777370247230a8",
    ready: async (p) => {
      await p.getByRole("button", { name: "Accept extracted values" }).waitFor({ timeout: 60_000 });
      await p.getByRole("checkbox", { name: /is the same carrier as/ }).check();
      await p.getByText(/Adds the name to the format's carrier names/).waitFor();
    },
  },
];

const results: { screen: string; theme: Theme; violations: number; passes: number; incomplete: number; details: unknown[] }[] = [];

for (const theme of ["light", "dark"] as Theme[]) {
  for (const s of SCREENS) {
    test(`axe: ${s.name} · ${theme}`, async ({ page }) => {
      await open(page, s.path, { theme, role: s.role, reducedMotion: true });
      await s.ready(page);
      await page.waitForTimeout(300);
      const r = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
      results.push({
        screen: s.name,
        theme,
        violations: r.violations.length,
        passes: r.passes.length,
        incomplete: r.incomplete.length,
        details: r.violations.map((v) => ({ id: v.id, impact: v.impact, help: v.help, nodes: v.nodes.slice(0, 5).map((n) => ({ target: n.target, summary: n.failureSummary })) })),
      });
      expect(r.violations, JSON.stringify(r.violations.map((v) => ({ id: v.id, nodes: v.nodes.slice(0, 3).map((n) => [n.target, n.failureSummary]) })), null, 1)).toEqual([]);
    });
  }
}

test.afterAll(() => {
  mkdirSync("test-results", { recursive: true });
  writeFileSync("test-results/axe-summary.json", JSON.stringify(results, null, 2));
});
