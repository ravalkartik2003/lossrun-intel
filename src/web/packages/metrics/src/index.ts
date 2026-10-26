// @lossrun/metrics — the client what-if engine (brief 10 §3). Pure functions, no UI: the React app loads the per-account
// claims payload (GET /quotes/{id}/claims) and the catalog (GET /catalog) once, then recomputes every metric in a web worker
// on each slider move. The C# engine (src/LossRun.Analytics) produces byte-identical snapshots for the same input.

import { compute } from "./engine.ts";
import { writeSnapshot } from "./json.ts";
import type { AnalyticsInput, Catalog, FeatureSnapshot, MetricResult, Scenario } from "./model.ts";

export { compute, isCumulativeTrauma, fieldMatches } from "./engine.ts";
export { readInput, readScenario, writeInput, writeSnapshot, inputsHash } from "./json.ts";
export { formatScaled } from "./canonical.ts";
export * from "./model.ts";

export interface MetricDelta {
  name: string;
  unit: string;
  scale: number;
  baseline: number | null;
  scenario: number | null;
  /** scenario − baseline at the metric's scale; null when either side is null. */
  delta: number | null;
}

/** Baseline → scenario → delta per metric (brief 10 §3 delta view). */
export function whatIf(input: AnalyticsInput, catalog: Catalog, scenario: Scenario, view = "adjusted"): { baseline: FeatureSnapshot; scenario: FeatureSnapshot; deltas: MetricDelta[] } {
  const base = compute(input, catalog, undefined, view);
  const next = compute(input, catalog, scenario, view);
  const deltas = base.metrics.map((b: MetricResult, i: number) => {
    const s = next.metrics[i];
    return { name: b.name, unit: b.unit, scale: b.scale, baseline: b.value, scenario: s.value, delta: b.value === null || s.value === null ? null : s.value - b.value };
  });
  return { baseline: base, scenario: next, deltas };
}

/** The snapshot as canonical JSON (what the server returns for the same request). */
export function snapshotJson(input: AnalyticsInput, catalog: Catalog, scenario?: Scenario, view = "adjusted"): string {
  return writeSnapshot(compute(input, catalog, scenario, view));
}
