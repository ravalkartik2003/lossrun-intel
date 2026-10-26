import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import type { DocumentResult } from "../api/types.ts";
import { asReported, covers, factsFor, moneyOf, overridesOf, regionsFor, rowPaths, rulesFor, sheetRegionsFor, totalsCheck, traceable } from "./model.ts";
import { toScenario } from "../analytics/useWhatIf.ts";
import { BASELINE_CONTROLS, columnName, metricDelta, metricValue, money, parseA1, usdCompact } from "@lossrun/ui";

const capture = new URL("../../../../../../docs/ui/mock/capture/", import.meta.url);
const doc = JSON.parse(readFileSync(new URL("documents/doc-9997d9e37c0fcb03-p1-1/result.json", capture), "utf8")) as DocumentResult;
const bad = JSON.parse(readFileSync(new URL("documents/doc-04777370247230a8/result.json", capture), "utf8")) as DocumentResult;

describe("ledger ↔ lineage model", () => {
  it("rule patterns cover concrete paths", () => {
    expect(covers("policyPeriods[*].claims[*].total", "policyPeriods[0].claims[1].total.incurred")).toBe(true);
    expect(covers("policyPeriods[1].claims[0]", "policyPeriods[1].claims[0].status")).toBe(true);
    expect(covers("policyPeriods[1].claims[0]", "policyPeriods[1].claims[01].status")).toBe(false);
    expect(covers("policyPeriods[0].claims[1].medical.paid", "policyPeriods[0].claims[1].medical.paidX")).toBe(false);
  });

  it("a printed value has a region, a page and its rules; derived values are not drawn", () => {
    const f = factsFor(doc, "policyPeriods[0].claims[0].medical.paid", new Map());
    expect(f.sourceText).toBe("8,000.00");
    expect(f.extractor).toBe("replay");
    expect(f.format).toBe("synthetic-mutual-pdf@5");
    expect(f.source).toMatch(/^Page 1 · x 7\.00–7\.81 in/);
    expect(f.rules.map((r) => r.ruleId)).toContain("R-01");
    const derived = factsFor(doc, "policyPeriods[0].claims[0].indemnity.incurred", new Map());
    expect(derived.derivedFrom).toEqual(["Indemnity paid", "Indemnity reserve"]);
    expect(f.repair).toBeNull();
    expect(f.part).toBeNull();
    expect(regionsFor(doc, 1, new Set()).some((r) => r.id.endsWith("indemnity.incurred"))).toBe(false);
  });

  it("a repaired scanned name shows the printed text and the repair (brief 31 Answers 1)", () => {
    const repaired: DocumentResult = structuredClone(doc);
    repaired.document.insuredName = "Iota Staffing (synthetic)";
    repaired.document.lineage = { ...repaired.document.lineage, insuredName: { documentId: "d", page: 1, sourceText: "lota Staffing (synthetic)", extractor: "replay", repair: "ocrRepair: l→I", part: 1 } };
    const f = factsFor(repaired, "insuredName", new Map());
    expect(f.value).toBe("Iota Staffing (synthetic)");
    expect(f.sourceText).toBe("lota Staffing (synthetic)");
    expect(f.repair).toBe("OCR read the capital I as l");
    expect(f.part).toBe(2);
  });

  it("row trace steps through the printed values in column order", () => {
    const paths = rowPaths(doc, { periodIndex: 0, claimIndex: 0 });
    expect(paths[0]).toBe("policyPeriods[0].claims[0].claimNumber");
    expect(paths).toContain("policyPeriods[0].claims[0].medical.paid");
    expect(paths.indexOf("policyPeriods[0].claims[0].indemnity.paid")).toBeLessThan(paths.indexOf("policyPeriods[0].claims[0].medical.paid"));
  });

  it("totals strip: the misprinted subtotal is off by 100.00", () => {
    const med = totalsCheck(bad, 0).find((c) => c.id === "medical.paid")!;
    expect(med.printed! - med.extracted).toBeCloseTo(100, 2);
    expect(totalsCheck(doc, 0).every((c) => c.printed === null || Math.abs(c.printed - c.extracted) < 0.005)).toBe(true);
    expect(rulesFor(bad, "policyPeriods[0].subtotals.medical.paid").some((r) => r.status === "fail")).toBe(true);
  });

  it("incurred falls back to paid + reserve", () => {
    expect(moneyOf({ medical: { paid: 1200, reserve: 800 } }, "medical.incurred")).toBe(2000);
    expect(moneyOf({ indemnity: { paid: 1 }, medical: { paid: 2, reserve: 3 }, expense: { paid: 4 } }, "total.incurred")).toBe(10);
  });

  it("as reported reverts each overridden field to its first old value", () => {
    // The API's shape (brief 24 step 1): one "override" row per field with its fieldPath; "review.override" closes the decision.
    const audit = [
      { at: "2026-01-02", user: "u", role: "Underwriter", action: "review.override", fieldPath: null },
      { at: "2026-01-02", user: "u", role: "Underwriter", action: "override", fieldPath: "policyPeriods[0].subtotals.medical.paid", oldValue: "9200", newValue: "9300" },
      { at: "2026-01-01", user: "u", role: "Underwriter", action: "override", fieldPath: "policyPeriods[0].subtotals.medical.paid", oldValue: "9300", newValue: "9200" },
    ];
    const reverted = asReported(doc, overridesOf(audit));
    expect(reverted.document.policyPeriods[0].subtotals!.medical!.paid).toBe(9300);
    expect(doc.document.policyPeriods[0].subtotals!.medical!.paid).toBe(9200);
  });

  it("as reported keeps a digit-only claim number a string (typed by the schema, not by the digits)", () => {
    const path = "policyPeriods[0].claims[0].claimNumber";
    const audit = [{ at: "2026-01-01", user: "u", role: "Underwriter", action: "override", fieldPath: path, oldValue: "0012345", newValue: "WC-1" }];
    const reverted = asReported(doc, overridesOf(audit));
    expect(reverted.document.policyPeriods[0].claims[0].claimNumber).toBe("0012345");
  });

  it("reads the override rows the live API writes (committed capture)", () => {
    const live = JSON.parse(readFileSync(new URL("documents/doc-ebb00898278dd541/audit.json", capture), "utf8"));
    const rows = Array.isArray(live) ? live : live.entries;
    const overrides = overridesOf(rows);
    expect(overrides.size).toBeGreaterThan(0);
    expect(rows.some((r: { action: string; fieldPath?: string | null }) => r.action === "review.override" && !r.fieldPath)).toBe(true);
    expect(overridesOf(rows.filter((r: { action: string }) => r.action === "review.override")).size).toBe(0);
  });
});

describe("what-if scenario from the panel", () => {
  it("baseline controls and no filters are the baseline (null)", () => {
    expect(toScenario(BASELINE_CONTROLS, [2022, 2023], [])).toBeNull();
  });
  it("trend compounds to the latest year; money in cents; reserve in bps", () => {
    const s = toScenario({ ...BASELINE_CONTROLS, trendPct: 5, reservePct: 10, capPerClaim: 250000 }, [2021, 2022, 2023], ["a"])!;
    expect(s.trendFactorBpsByYear).toEqual({ "2021": 11025, "2022": 10500 });
    expect(s.reserveFactorBps).toBe(1000);
    expect(s.capPerClaimCents).toBe(25_000_000);
    expect(s.excludeClaimIds).toEqual(["a"]);
  });
});

describe("formatting", () => {
  it("money uses a true minus and two decimals", () => {
    expect(money(-1234.5)).toBe("−1,234.50");
    expect(money(null)).toBe("—");
    expect(usdCompact(8050)).toBe("$8.1K");
    expect(metricValue(0.4567, "ratio")).toBe("45.7 %");
    expect(metricDelta(-0.015, "ratio")).toBe("−1.5 pts");
  });
});

describe("spreadsheet sources (brief 13)", () => {
  const sheetDoc = {
    ...doc,
    document: {
      ...doc.document,
      lineage: {
        "policyPeriods[0].claims[0].medical.paid": { sheet: "Claims Detail", cell: "Q7", sourceText: "18822.87", extractor: "replay" },
        "policyPeriods[0].claims[0].total.incurred": { sheet: "Claims Detail", cell: "W7", extractor: "derived" },
      },
    },
  } as DocumentResult;

  it("A1 addresses and column letters round-trip", () => {
    expect(parseA1("Q7")).toEqual({ row: 6, column: 16 });
    expect(parseA1("$AA$10")).toEqual({ row: 9, column: 26 });
    expect([0, 25, 26, 701].map(columnName)).toEqual(["A", "Z", "AA", "ZZ"]);
  });

  it("a cell-read value is traceable and highlighted on its sheet; derived values are not", () => {
    expect(traceable(sheetDoc.document.lineage!["policyPeriods[0].claims[0].medical.paid"])).toBe(true);
    expect(traceable(sheetDoc.document.lineage!["policyPeriods[0].claims[0].total.incurred"])).toBe(false);
    const regions = sheetRegionsFor(sheetDoc, "Claims Detail", new Set(["policyPeriods[0].claims[0].medical.paid"]));
    expect(regions.map((r) => [r.cell, r.tone])).toEqual([["Q7", "fail"]]);
    expect(sheetRegionsFor(sheetDoc, "Policy Summary", new Set())).toEqual([]);
    expect(rowPaths(sheetDoc, { periodIndex: 0, claimIndex: 0 })).toContain("policyPeriods[0].claims[0].medical.paid");
  });
});
