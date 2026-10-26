// Brief 10 §7: time the twin on the 1,000-claim / 5-year account (target ≤ 20 ms in Node). Run: node test/bench.ts

import { readFileSync } from "node:fs";
import { compute, readScenario, writeSnapshot } from "../src/index.ts";
import { sha256Hex } from "../src/sha256.ts";
import type { Catalog } from "../src/model.ts";
import { perfAccount } from "./perf-account.ts";

const catalog = JSON.parse(readFileSync(new URL("../catalog.json", import.meta.url), "utf8")) as Catalog;
const input = perfAccount();
const scenario = readScenario({ reserveFactorBps: 1000, capPerClaim: 250000, includeCT: false });

function time(label: string, f: () => unknown, runs = 60): void {
  for (let i = 0; i < 10; i++) f(); // warm up the JIT
  const ms: number[] = [];
  for (let i = 0; i < runs; i++) {
    const t = performance.now();
    f();
    ms.push(performance.now() - t);
  }
  ms.sort((a, b) => a - b);
  const p = (q: number) => ms[Math.min(ms.length - 1, Math.ceil(q * ms.length) - 1)];
  console.log(`${label}: p50 ${p(0.5).toFixed(2)} ms · p95 ${p(0.95).toFixed(2)} ms · max ${ms[ms.length - 1].toFixed(2)} ms (${runs} runs, ${input.claims.length} claims)`);
}

time("cold: new payload each time (serialise + hash input + compute)", () => compute(structuredClone(input), catalog), 20);
time("baseline compute", () => compute(input, catalog));
time("what-if compute (reserve +10 %, cap $250K, no CT)", () => compute(input, catalog, scenario));
time("compute + canonical JSON", () => writeSnapshot(compute(input, catalog, scenario)));
console.log(`snapshot sha256 ${sha256Hex(writeSnapshot(compute(input, catalog)))}`);
