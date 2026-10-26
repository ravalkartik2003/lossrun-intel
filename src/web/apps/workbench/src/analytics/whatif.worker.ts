/// <reference lib="webworker" />
// What-if in a web worker (brief 11 §3): the TypeScript twin of the C# engine recomputes every catalog metric for the
// account on each scenario change, off the main thread. Inputs arrive once per quote/view; scenarios stream in.
import { compute, readInput, readScenario, writeSnapshot, type AnalyticsInput, type Catalog, type FeatureSnapshot } from "@lossrun/metrics";

export type WorkerRequest =
  | { type: "load"; key: string; claimsText: string; catalogText: string }
  | { type: "compute"; id: number; key: string; scenario: Record<string, unknown> | null; view: string }
  | { type: "snapshot"; id: number; key: string; scenario: Record<string, unknown> | null; view: string };

export type WorkerResponse =
  | { type: "loaded"; key: string; claims: number; ms: number }
  | { type: "result"; id: number; key: string; baseline: FeatureSnapshot; scenario: FeatureSnapshot | null; ms: number }
  | { type: "snapshot"; id: number; json: string; ms: number }
  | { type: "error"; id?: number; message: string };

let loaded: { key: string; input: AnalyticsInput; catalog: Catalog; baseline: Map<string, FeatureSnapshot> } | null = null;

self.onmessage = (e: MessageEvent<WorkerRequest>) => {
  const msg = e.data;
  try {
    if (msg.type === "load") {
      const t = performance.now();
      const payload = JSON.parse(msg.claimsText) as { input: unknown };
      loaded = { key: msg.key, input: readInput(payload.input), catalog: JSON.parse(msg.catalogText) as Catalog, baseline: new Map() };
      post({ type: "loaded", key: msg.key, claims: loaded.input.claims.length, ms: performance.now() - t });
      return;
    }
    if (!loaded || loaded.key !== msg.key) {
      post({ type: "error", id: msg.id, message: "account not loaded" });
      return;
    }
    const t = performance.now();
    if (msg.type === "snapshot") {
      const json = writeSnapshot(compute(loaded.input, loaded.catalog, msg.scenario ? readScenario(msg.scenario) : undefined, msg.view));
      post({ type: "snapshot", id: msg.id, json, ms: performance.now() - t });
      return;
    }
    let baseline = loaded.baseline.get(msg.view);
    if (!baseline) {
      baseline = compute(loaded.input, loaded.catalog, undefined, msg.view);
      loaded.baseline.set(msg.view, baseline);
    }
    const scenario = msg.scenario ? compute(loaded.input, loaded.catalog, readScenario(msg.scenario), msg.view) : null;
    post({ type: "result", id: msg.id, key: msg.key, baseline, scenario, ms: performance.now() - t });
  } catch (err) {
    post({ type: "error", id: "id" in msg ? msg.id : undefined, message: (err as Error).message });
  }
};

function post(m: WorkerResponse) {
  (self as unknown as DedicatedWorkerGlobalScope).postMessage(m);
}
