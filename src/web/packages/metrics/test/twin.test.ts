// Brief 10: the TypeScript twin against the hand-computed account and the golden parity cases (same files the C# tests read).

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { compute, readInput, readScenario, snapshotJson, whatIf } from "../src/index.ts";
import { sha256Hex } from "../src/sha256.ts";
import type { Catalog } from "../src/model.ts";

const here = new URL(".", import.meta.url);
const catalog = JSON.parse(readFileSync(new URL("../catalog.json", here), "utf8")) as Catalog;
const hand = JSON.parse(readFileSync(new URL("hand-account.json", here), "utf8"));
const golden = JSON.parse(readFileSync(new URL("golden.json", here), "utf8"));

const printed = (snapshot: string, name: string) => JSON.parse(snapshot).metrics.find((m: any) => m.name === name);
const literal = (json: string, name: string, field: string) => {
  // the canonical number exactly as written (not re-formatted by JSON.parse)
  const start = json.indexOf(`{"name":"${name}"`);
  const slice = json.slice(start, json.indexOf(`"contributors"`, start));
  const match = slice.match(new RegExp(`"${field}":(null|-?[0-9.]+)`));
  return match ? match[1] : undefined;
};

test("every catalog metric is implemented and has a hand-computed expectation", () => {
  const snapshot = compute(readInput(hand.input), catalog);
  assert.deepEqual(snapshot.metrics.map((m) => m.name), catalog.metrics.map((m) => m.name));
  assert.deepEqual(Object.keys(hand.expected).sort(), catalog.metrics.map((m) => m.name).sort());
});

for (const [name, expected] of Object.entries<any>(hand.expected)) {
  test(`hand-computed: ${name}`, () => {
    const json = snapshotJson(readInput(hand.input), catalog);
    assert.equal(literal(json, name, "value"), expected.value);
    const m = printed(json, name);
    if (expected.breakdown) {
      const section = json.slice(json.indexOf(`{"name":"${name}"`));
      const keys = m.breakdown.map((b: any) => b.key);
      const values = keys.map((k: string) => section.match(new RegExp(`"key":"${k.replace(/[<+]/g, "\\$&")}","value":(null|-?[0-9.]+)`))![1]);
      assert.deepEqual(keys.map((k: string, i: number) => [k, values[i]]), expected.breakdown);
    }
    if (expected.contributors) assert.deepEqual(m.contributors, expected.contributors);
    if (expected.overrides) assert.deepEqual(m.overrides, expected.overrides);
  });
}

hand.scenarios.forEach((s: any, i: number) => {
  test(`hand-computed scenario ${i}: ${s.name}`, () => {
    const json = snapshotJson(readInput(hand.input), catalog, readScenario(s.scenario));
    assert.equal(JSON.parse(json).claims.scenarioExcluded, s.claims.scenarioExcluded);
    assert.equal(JSON.parse(json).claims.included, s.claims.included);
    for (const [name, value] of Object.entries(s.expected)) {
      assert.equal(literal(json, name, "value"), value, name);
    }
  });
});

test(`golden parity: ${golden.cases.length} cases byte-identical to the C# engine`, () => {
  assert.ok(golden.cases.length >= 50);
  assert.equal(golden.catalogVersion, catalog.catalogVersion);
  for (const c of golden.cases) {
    const actual = snapshotJson(readInput(c.input), catalog, c.scenario ? readScenario(c.scenario) : undefined, c.view);
    assert.equal(actual, c.expected, `golden case ${c.name}`);
  }
});

test("the 1,000-claim performance account reproduces the C# snapshot hash", async () => {
  const { perfAccount } = await import("./perf-account.ts");
  assert.equal(sha256Hex(snapshotJson(perfAccount(), catalog)), golden.perfAccountSnapshotSha256);
});

test("sha256 matches node:crypto", () => {
  for (const text of ["", "abc", "é ü 漢字 🎉", "x".repeat(1000), JSON.stringify(hand.input)]) {
    assert.equal(sha256Hex(text), createHash("sha256").update(text, "utf8").digest("hex"));
  }
});

test("what-if: baseline → scenario → delta per metric", () => {
  const { deltas } = whatIf(readInput(hand.input), catalog, readScenario({ reserveFactorBps: 1000 }));
  const reserve = deltas.find((d) => d.name === "reserve_total")!;
  assert.equal(reserve.baseline, 24000000);
  assert.equal(reserve.scenario, 26400000);
  assert.equal(reserve.delta, 2400000);
  assert.equal(deltas.find((d) => d.name === "claim_count")!.delta, 0);
});

test("not measurable: the reason is printed; a claim's last day worked is enough for post-termination CT", () => {
  const input = readInput(hand.input);
  const bare = { ...input, context: { ...input.context, payrollCentsByYear: null, fteByYear: null, lastDayWorked: null } };
  const s = compute(bare, catalog);
  const m = (snap: typeof s, name: string) => snap.metrics.find((x) => x.name === name)!;
  assert.equal(m(s, "frequency_per_100_payroll").notMeasurable, "no payroll in the submission context");
  assert.equal(m(s, "post_termination_ct_count").notMeasurable, "no last day worked in the submission context or on the claims");
  assert.equal(m(s, "claim_count").notMeasurable, undefined);
  const printed = { ...bare, claims: bare.claims.map((c) => (c.id === "d/1/1" ? { ...c, lastDayWorked: "2024-01-31" } : c)) };
  const t = compute(printed, catalog);
  assert.equal(m(t, "post_termination_ct_count").value, 1);
  assert.ok(t.perClaimFlags.find((f) => f.claimId === "d/1/1")!.flags.includes("postTermination"));
});
