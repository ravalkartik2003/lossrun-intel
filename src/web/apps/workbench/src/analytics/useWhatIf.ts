import { useEffect, useRef, useState } from "react";
import type { FeatureSnapshot } from "@lossrun/metrics";
import type { ScenarioControls } from "@lossrun/ui";
import type { WorkerRequest, WorkerResponse } from "./whatif.worker.ts";

export interface WhatIfResult {
  baseline: FeatureSnapshot;
  scenario: FeatureSnapshot | null;
  ms: number;
}

/** The catalog Scenario (basis points, cents) from the panel's controls and the filter bar's exclusions. */
export function toScenario(c: ScenarioControls, years: number[], excludeClaimIds: string[]): Record<string, unknown> | null {
  const latest = years.length ? Math.max(...years) : 0;
  const trend: Record<string, number> = {};
  if (c.trendPct > 0) for (const y of years) if (y < latest) trend[String(y)] = Math.round(10000 * Math.pow(1 + c.trendPct / 100, latest - y));
  const s = {
    excludeClaimIds,
    includeNonCA: c.includeNonCA,
    includeCT: c.includeCT,
    excludeCatastrophicAboveCents: c.excludeAbove === null ? null : Math.round(c.excludeAbove * 100),
    trendFactorBpsByYear: trend,
    reserveFactorBps: Math.round(c.reservePct * 100),
    capPerClaimCents: c.capPerClaim === null ? null : Math.round(c.capPerClaim * 100),
  };
  const baseline = excludeClaimIds.length === 0 && c.includeNonCA && c.includeCT && s.excludeCatastrophicAboveCents === null && Object.keys(trend).length === 0 && s.reserveFactorBps === 0 && s.capPerClaimCents === null;
  return baseline ? null : s;
}

/**
 * Loads the account into the worker once per (quote, view) and recomputes on every scenario change. Requests are
 * coalesced: while one compute runs, only the latest scenario is queued (a fast slider never builds a backlog).
 */
export function useWhatIf(key: string | null, claimsText: string | undefined, catalogText: string | undefined, view: string, scenario: Record<string, unknown> | null) {
  const worker = useRef<Worker | null>(null);
  const [loadedKey, setLoadedKey] = useState<string | null>(null);
  const [result, setResult] = useState<WhatIfResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const busy = useRef(false);
  const queued = useRef<WorkerRequest | null>(null);
  const nextId = useRef(0);

  useEffect(() => {
    const w = new Worker(new URL("./whatif.worker.ts", import.meta.url), { type: "module" });
    worker.current = w;
    w.onmessage = (e: MessageEvent<WorkerResponse>) => {
      const m = e.data;
      if (m.type === "loaded") setLoadedKey(m.key);
      else if (m.type === "result") {
        setResult({ baseline: m.baseline, scenario: m.scenario, ms: m.ms });
        busy.current = false;
        if (queued.current) {
          const q = queued.current;
          queued.current = null;
          busy.current = true;
          w.postMessage(q);
        }
      } else if (m.type === "error") {
        busy.current = false;
        setError(m.message);
      }
    };
    return () => w.terminate();
  }, []);

  useEffect(() => {
    if (!key || !claimsText || !catalogText || !worker.current) return;
    setResult(null);
    worker.current.postMessage({ type: "load", key, claimsText, catalogText } satisfies WorkerRequest);
  }, [key, claimsText, catalogText]);

  useEffect(() => {
    if (!key || loadedKey !== key || !worker.current) return;
    const req: WorkerRequest = { type: "compute", id: ++nextId.current, key, scenario, view };
    if (busy.current) queued.current = req;
    else {
      busy.current = true;
      worker.current.postMessage(req);
    }
  }, [loadedKey, key, view, JSON.stringify(scenario)]);

  /** One canonical snapshot (for the golden test and "copy JSON"). */
  const snapshot = (s: Record<string, unknown> | null, v = view) =>
    new Promise<{ json: string; ms: number }>((resolve) => {
      const id = ++nextId.current;
      const w = new Worker(new URL("./whatif.worker.ts", import.meta.url), { type: "module" });
      w.onmessage = (e: MessageEvent<WorkerResponse>) => {
        if (e.data.type === "loaded") w.postMessage({ type: "snapshot", id, key: key!, scenario: s, view: v } satisfies WorkerRequest);
        else if (e.data.type === "snapshot") {
          resolve({ json: e.data.json, ms: e.data.ms });
          w.terminate();
        }
      };
      w.postMessage({ type: "load", key: key!, claimsText: claimsText!, catalogText: catalogText! } satisfies WorkerRequest);
    });

  return { result, ready: loadedKey === key, error, snapshot };
}

/**
 * One canonical snapshot from any engine input, computed in a fresh what-if worker exactly as the Analytics screen does.
 * Exposed as window.__lossrun.snapshot for the browser golden test (e2e/golden.spec.ts); it reads nothing from the API.
 */
export function workerSnapshot(claimsText: string, catalogText: string, scenario: Record<string, unknown> | null, view = "adjusted"): Promise<{ json: string; ms: number }> {
  return new Promise((resolve, reject) => {
    const w = new Worker(new URL("./whatif.worker.ts", import.meta.url), { type: "module" });
    w.onmessage = (e: MessageEvent<WorkerResponse>) => {
      if (e.data.type === "loaded") w.postMessage({ type: "snapshot", id: 1, key: "golden", scenario, view } satisfies WorkerRequest);
      else if (e.data.type === "snapshot") {
        resolve({ json: e.data.json, ms: e.data.ms });
        w.terminate();
      } else if (e.data.type === "error") {
        reject(new Error(e.data.message));
        w.terminate();
      }
    };
    w.postMessage({ type: "load", key: "golden", claimsText, catalogText } satisfies WorkerRequest);
  });
}
