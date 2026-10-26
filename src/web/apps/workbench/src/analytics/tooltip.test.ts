import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { costTooltip, escapeHtml, largeLossTooltip, tooltipHtml } from "./tooltip.ts";

// Brief 30 item 23: ECharts sets a formatter's return value as HTML; values from documents/users must be escaped.

const XSS = `<img src=x onerror="alert(1)">`;

describe("chart tooltips", () => {
  it("escape an <img onerror> label", () => {
    expect(escapeHtml(XSS)).toBe("&lt;img src=x onerror=&quot;alert(1)&quot;&gt;");
    const html = largeLossTooltip({ data: ["2024-03-07", 125000, XSS] });
    expect(html).not.toContain("<img");
    expect(html).toBe("&lt;img src=x onerror=&quot;alert(1)&quot;&gt;<br/>2024-03-07 · $125,000.00");
  });

  it("escape the series name and the document id in the cost tooltip", () => {
    const html = costTooltip({ data: ["2026-10-01T00:00:00Z", 0.0123, `doc-1'><script>x</script>`], seriesName: XSS });
    expect(html).not.toMatch(/<(img|script)/);
    expect(html.split("<br/>")).toHaveLength(2);
  });

  it("rationale text is escaped too", () => {
    expect(tooltipHtml(`rows sum to 8,000 & 1,200 <b>`)).toBe("rows sum to 8,000 &amp; 1,200 &lt;b&gt;");
  });

  it("no screen builds tooltip HTML inline", () => {
    for (const screen of ["Analytics.tsx", "Costs.tsx"]) {
      const src = readFileSync(new URL(`../screens/${screen}`, import.meta.url), "utf8");
      expect(src, screen).not.toMatch(/formatter:\s*\([^)]*\)\s*=>\s*`[^`]*<br/);
    }
  });
});
