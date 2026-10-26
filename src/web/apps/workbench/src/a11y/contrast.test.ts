import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";

// Design-brief §6: contrast ≥ 4.5:1 for text and ≥ 3:1 for UI components and focus indicators, in both themes, for
// every token pair the components use. Parses packages/ui/src/tokens.css (the single token file).

const css = readFileSync(new URL("../../../../packages/ui/src/tokens.css", import.meta.url), "utf8");

function block(selector: string): Record<string, string> {
  const start = css.indexOf(selector);
  const body = css.slice(css.indexOf("{", start) + 1, css.indexOf("}", start));
  return Object.fromEntries([...body.matchAll(/--([\w-]+):\s*(#[0-9a-f]{6})/gi)].map((m) => [m[1], m[2].toLowerCase()]));
}

const themes = { light: block('[data-theme="light"]'), dark: block('[data-theme="dark"]') };

function luminance(hex: string): number {
  const c = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((v) => (v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4));
  return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}

export function ratio(a: string, b: string): number {
  const [x, y] = [luminance(a), luminance(b)];
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

/** Text pairs: foreground on every background it is drawn on in the components. */
const TEXT: [string, string[]][] = [
  ["ink", ["surface", "surface-raised", "surface-sunken", "action-tint", "verified-tint", "review-tint", "fail-tint", "learned-tint"]],
  ["ink-2", ["surface", "surface-raised", "surface-sunken", "action-tint", "verified-tint", "review-tint", "fail-tint", "learned-tint"]],
  ["ink-3", ["surface", "surface-raised", "surface-sunken"]],
  ["action", ["surface", "surface-raised", "surface-sunken", "action-tint"]],
  ["verified", ["surface", "surface-raised", "verified-tint"]],
  ["review", ["surface", "surface-raised", "review-tint"]],
  ["fail", ["surface", "surface-raised", "fail-tint"]],
  ["learned", ["surface", "surface-raised", "learned-tint"]],
  ["action-ink", ["action"]],
  ["rail-ink", ["rail", "rail-active"]],
  ["rail-ink-2", ["rail", "rail-active"]],
];

/** Non-text: component boundaries and the focus ring (WCAG 1.4.11, ≥ 3:1). */
const UI: [string, string[]][] = [
  ["line-strong", ["surface", "surface-raised"]],
  ["action", ["surface", "surface-raised", "surface-sunken"]],
  ["thread", ["surface", "surface-raised", "surface-sunken"]],
  ["highlight-edge", ["surface-raised"]],
];

describe.each(Object.entries(themes))("%s theme", (_, t) => {
  it.each(TEXT.flatMap(([fg, bgs]) => bgs.map((bg) => [fg, bg] as const)))("text %s on %s ≥ 4.5:1", (fg, bg) => {
    expect(t[fg], fg).toBeDefined();
    expect(t[bg], bg).toBeDefined();
    expect(ratio(t[fg], t[bg])).toBeGreaterThanOrEqual(4.5);
  });
  it.each(UI.flatMap(([fg, bgs]) => bgs.map((bg) => [fg, bg] as const)))("UI %s on %s ≥ 3:1", (fg, bg) => {
    expect(ratio(t[fg], t[bg])).toBeGreaterThanOrEqual(3);
  });
});
