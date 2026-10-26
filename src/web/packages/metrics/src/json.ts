// Canonical JSON for engine input and feature snapshots — the twin of src/LossRun.Analytics/AnalyticsJson.cs.

import { CanonicalWriter, ordinal } from "./canonical.ts";
import { sha256Hex } from "./sha256.ts";
import type { AnalyticsClaim, AnalyticsInput, FeatureSnapshot, Scenario } from "./model.ts";
import { baseline, isBaseline } from "./model.ts";

export function writeInput(input: AnalyticsInput): string {
  const w = new CanonicalWriter();
  w.startObject();
  w.name("context");
  const c = input.context;
  w.startObject().name("quoteId").string(c.quoteId).name("valuationDate").string(c.valuationDate).name("payrollCentsByYear");
  writeYearMap(w, c.payrollCentsByYear);
  w.name("fteByYear");
  writeYearMap(w, c.fteByYear);
  w.name("lastDayWorked").string(c.lastDayWorked).endObject();
  w.name("claims").startArray();
  for (const claim of input.claims) {
    writeClaim(w, claim);
  }
  w.endArray();
  w.name("overrides").startArray();
  for (const o of input.overrides) {
    w.startObject().name("id").string(o.id).name("claimId").string(o.claimId).name("field").string(o.field).endObject();
  }
  w.endArray();
  w.endObject();
  return w.toString();
}

function writeClaim(w: CanonicalWriter, c: AnalyticsClaim): void {
  w.startObject()
    .name("id").string(c.id)
    .name("claimNumber").string(c.claimNumber)
    .name("policyNumber").string(c.policyNumber)
    .name("policyYear").scaled(c.policyYear, 0)
    .name("dateOfInjury").string(c.dateOfInjury)
    .name("dateReported").string(c.dateReported)
    .name("dateClosed").string(c.dateClosed)
    .name("status").string(c.status)
    .name("claimType").string(c.claimType)
    .name("natureCode").string(c.natureCode)
    .name("causeCode").string(c.causeCode)
    .name("bodyPartCode").string(c.bodyPartCode)
    .name("typeOfLossCode").string(c.typeOfLossCode)
    .name("description").string(c.description)
    .name("litigation").bool(c.litigation)
    .name("cumulativeTrauma").bool(c.cumulativeTrauma)
    .name("jurisdiction").string(c.jurisdiction)
    .name("lineOfBusiness").string(c.lineOfBusiness)
    .name("indemnityPaidCents").scaled(c.indemnityPaidCents, 0)
    .name("indemnityReserveCents").scaled(c.indemnityReserveCents, 0)
    .name("medicalPaidCents").scaled(c.medicalPaidCents, 0)
    .name("medicalReserveCents").scaled(c.medicalReserveCents, 0)
    .name("expensePaidCents").scaled(c.expensePaidCents, 0)
    .name("expenseReserveCents").scaled(c.expenseReserveCents, 0)
    .name("recoveryCents").scaled(c.recoveryCents, 0)
    .name("lastDayWorked").string(c.lastDayWorked);
  // Written only when printed, so snapshots of runs without the flag are unchanged (as in AnalyticsJson.WriteClaim).
  if (c.reportOnly !== undefined && c.reportOnly !== null) w.name("reportOnly").bool(c.reportOnly);
  w.endObject();
}

function writeYearMap(w: CanonicalWriter, map: Record<string, number> | null): void {
  if (map === null) {
    w.null();
    return;
  }
  w.startObject();
  for (const year of Object.keys(map).map(Number).sort((a, b) => a - b)) {
    w.name(String(year)).scaled(map[String(year)], 0);
  }
  w.endObject();
}

const hashes = new WeakMap<AnalyticsInput, string>();

/**
 * Lower-case hex SHA-256 of the canonical input JSON (same bytes as the C# engine hashes). Memoised per input object: the
 * client loads the claims payload once and every slider move recomputes with the same input, so serialising and hashing
 * ~0.6 MB per move (≈ 39 ms of a 45 ms compute in Node, brief 10 §7) is paid once. Treat inputs as immutable.
 */
export function inputsHash(input: AnalyticsInput): string {
  let hash = hashes.get(input);
  if (hash === undefined) {
    hash = sha256Hex(writeInput(input));
    hashes.set(input, hash);
  }
  return hash;
}

const str = (v: unknown): string | null => (typeof v === "string" ? v : null);
const num = (v: unknown): number | null => (typeof v === "number" ? v : null);
const bool = (v: unknown): boolean | null => (typeof v === "boolean" ? v : null);

function yearMap(v: unknown): Record<string, number> | null {
  if (v === null || typeof v !== "object" || Array.isArray(v)) {
    return null;
  }
  const out: Record<string, number> = {};
  for (const [k, x] of Object.entries(v as Record<string, unknown>)) {
    out[String(Number(k))] = x as number;
  }
  return out;
}

/** Reads the per-account claims payload (GET /quotes/{id}/claims) or a golden input. */
export function readInput(json: string | unknown): AnalyticsInput {
  const root = (typeof json === "string" ? JSON.parse(json) : json) as Record<string, any>;
  const ctx = root.context ?? {};
  return {
    context: {
      quoteId: str(ctx.quoteId), valuationDate: str(ctx.valuationDate), payrollCentsByYear: yearMap(ctx.payrollCentsByYear),
      fteByYear: yearMap(ctx.fteByYear), lastDayWorked: str(ctx.lastDayWorked),
    },
    claims: (root.claims ?? []).map((e: Record<string, unknown>) => ({
      id: e.id as string, claimNumber: str(e.claimNumber), policyNumber: str(e.policyNumber), policyYear: num(e.policyYear),
      dateOfInjury: str(e.dateOfInjury), dateReported: str(e.dateReported), dateClosed: str(e.dateClosed), status: str(e.status),
      claimType: str(e.claimType), natureCode: str(e.natureCode), causeCode: str(e.causeCode), bodyPartCode: str(e.bodyPartCode),
      typeOfLossCode: str(e.typeOfLossCode), description: str(e.description), litigation: bool(e.litigation), cumulativeTrauma: bool(e.cumulativeTrauma),
      jurisdiction: str(e.jurisdiction), lineOfBusiness: str(e.lineOfBusiness),
      indemnityPaidCents: num(e.indemnityPaidCents) ?? 0, indemnityReserveCents: num(e.indemnityReserveCents) ?? 0,
      medicalPaidCents: num(e.medicalPaidCents) ?? 0, medicalReserveCents: num(e.medicalReserveCents) ?? 0,
      expensePaidCents: num(e.expensePaidCents) ?? 0, expenseReserveCents: num(e.expenseReserveCents) ?? 0,
      recoveryCents: num(e.recoveryCents),
      lastDayWorked: str(e.lastDayWorked), reportOnly: bool(e.reportOnly),
    })),
    overrides: (root.overrides ?? []).map((o: Record<string, unknown>) => ({ id: o.id as string, claimId: o.claimId as string, field: o.field as string })),
  };
}

/** Reads a scenario: excludeCatastrophicAbove / capPerClaim in dollars (or …Cents), factors in basis points. */
export function readScenario(json: string | unknown): Scenario {
  const e = (typeof json === "string" ? JSON.parse(json) : json) as Record<string, any> | null;
  const s = baseline();
  if (!e) {
    return s;
  }
  const cents = (key: string) => (typeof e[key + "Cents"] === "number" ? e[key + "Cents"] : typeof e[key] === "number" ? Math.round(e[key] * 100) : null);
  return {
    excludeClaimIds: Array.isArray(e.excludeClaimIds) ? e.excludeClaimIds.map(String) : s.excludeClaimIds,
    includeNonCA: typeof e.includeNonCA === "boolean" ? e.includeNonCA : true,
    includeCT: typeof e.includeCT === "boolean" ? e.includeCT : true,
    excludeCatastrophicAboveCents: cents("excludeCatastrophicAbove"),
    trendFactorBpsByYear: yearMap(e.trendFactorBpsByYear) ?? {},
    reserveFactorBps: typeof e.reserveFactorBps === "number" ? e.reserveFactorBps : 0,
    capPerClaimCents: cents("capPerClaim"),
  };
}

export function writeScenario(w: CanonicalWriter, s: Scenario): void {
  w.startObject().name("excludeClaimIds").startArray();
  for (const id of [...s.excludeClaimIds].sort(ordinal)) {
    w.string(id);
  }
  w.endArray()
    .name("includeNonCA").bool(s.includeNonCA)
    .name("includeCT").bool(s.includeCT)
    .name("excludeCatastrophicAbove").scaled(s.excludeCatastrophicAboveCents, 2)
    .name("trendFactorBpsByYear");
  writeYearMap(w, s.trendFactorBpsByYear);
  w.name("reserveFactorBps").scaled(s.reserveFactorBps, 0)
    .name("capPerClaim").scaled(s.capPerClaimCents, 2)
    .endObject();
}

export function writeSnapshot(s: FeatureSnapshot): string {
  const w = new CanonicalWriter();
  w.startObject()
    .name("catalogVersion").string(s.catalogVersion)
    .name("view").string(s.view)
    .name("quoteId").string(s.quoteId)
    .name("valuationDate").string(s.valuationDate)
    .name("scenario");
  if (isBaseline(s.scenario)) {
    w.null();
  } else {
    writeScenario(w, s.scenario);
  }
  w.name("inputsHash").string(s.inputsHash)
    .name("claims").startObject()
    .name("input").scaled(s.inputClaims, 0)
    .name("nonWcExcluded").scaled(s.nonWcExcluded, 0)
    .name("scenarioExcluded").scaled(s.scenarioExcluded, 0)
    .name("included").scaled(s.included, 0)
    .endObject();
  w.name("metrics").startArray();
  for (const m of s.metrics) {
    w.startObject().name("name").string(m.name).name("unit").string(m.unit).name("value").scaled(m.value, m.scale);
    if (m.notMeasurable !== undefined) w.name("notMeasurable").string(m.notMeasurable);
    if (m.breakdown !== null) {
      w.name("breakdown").startArray();
      for (const [key, value] of m.breakdown) {
        w.startObject().name("key").string(key).name("value").scaled(value, m.scale).endObject();
      }
      w.endArray();
    }
    w.name("contributors");
    if (m.contributors === null) {
      w.string("all");
    } else {
      w.startArray();
      for (const id of m.contributors) {
        w.string(id);
      }
      w.endArray();
    }
    w.name("overrides").startArray();
    for (const id of m.overrides) {
      w.string(id);
    }
    w.endArray().endObject();
  }
  w.endArray();
  w.name("perPeriod").startArray();
  for (const p of s.perPeriod) {
    w.startObject().name("policyNumber").string(p.policyNumber).name("policyYear").scaled(p.policyYear, 0)
      .name("claimCount").scaled(p.claimCount, 0).name("openCount").scaled(p.openCount, 0);
    for (const [name, t] of [["indemnity", p.indemnity], ["medical", p.medical], ["expense", p.expense], ["total", p.total]] as const) {
      w.name(name).startObject().name("paid").scaled(t[0], 2).name("reserve").scaled(t[1], 2).name("incurred").scaled(t[2], 2).endObject();
    }
    w.endObject();
  }
  w.endArray();
  w.name("perClaimFlags").startArray();
  for (const f of s.perClaimFlags) {
    w.startObject().name("claimId").string(f.claimId).name("flags").startArray();
    for (const flag of f.flags) {
      w.string(flag);
    }
    w.endArray().endObject();
  }
  w.endArray();
  w.endObject();
  return w.toString();
}
