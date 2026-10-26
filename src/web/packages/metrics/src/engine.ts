// Feature engine — the TypeScript twin of src/LossRun.Analytics/FeatureEngine.cs (brief 10 §2–3). Pure and deterministic:
// integer arithmetic only (BigInt where a product can pass 2^53), fixed orderings, no clock. Follows the C# file step by
// step; test/golden.test.ts asserts byte-identical snapshots for every golden case.

import { day, medianTenths, mulBps, ordinal, percentileTenths, roundDiv, formatScaled } from "./canonical.ts";
import { inputsHash } from "./json.ts";
import type { AnalyticsClaim, AnalyticsInput, Catalog, CatalogMetric, FeatureSnapshot, MetricResult, PeriodAggregate, Scenario } from "./model.ts";
import { baseline, scaleOf } from "./model.ts";

interface Row {
  claim: AnalyticsClaim;
  indPaid: number; indRes: number; medPaid: number; medRes: number; expPaid: number; expRes: number;
  ct: boolean; postTermination: boolean; nonCA: boolean; open: boolean;
  doi: number | null; reported: number | null; closed: number | null; ldw: number | null;
}

const paid = (r: Row) => r.indPaid + r.medPaid + r.expPaid;
const reserve = (r: Row) => r.indRes + r.medRes + r.expRes;
const incurred = (r: Row) => paid(r) + reserve(r);
const sum = (rows: Row[], f: (r: Row) => number) => rows.reduce((a, r) => a + f(r), 0);

export function compute(input: AnalyticsInput, catalog: Catalog, scenario: Scenario = baseline(), view = "adjusted"): FeatureSnapshot {
  const lastDayWorked = day(input.context.lastDayWorked);
  const valuation = day(input.context.valuationDate);

  // 1. Non-WC rows are always excluded.
  const nonWc = input.claims.filter((c) => c.lineOfBusiness !== null && c.lineOfBusiness.trim().toUpperCase() !== "WC");
  const nonWcIds = new Set(nonWc.map((c) => c.id));
  const rows = input.claims.filter((c) => !nonWcIds.has(c.id)).map((c) => toRow(c, lastDayWorked));

  // 2. Scenario exclusions, on as-reported amounts.
  const excludedIds = new Set(scenario.excludeClaimIds);
  const excluded = new Set<string>();
  for (const r of rows) {
    if (excludedIds.has(r.claim.id) || (!scenario.includeNonCA && r.nonCA) || (!scenario.includeCT && r.ct)
      || (scenario.excludeCatastrophicAboveCents !== null && incurred(r) > scenario.excludeCatastrophicAboveCents)) {
      excluded.add(r.claim.id);
    }
  }
  const included = rows.filter((r) => !excluded.has(r.claim.id));

  // 3. Amount adjustments: trend, reserve factor, cap.
  for (const r of included) {
    const trend = r.claim.policyYear !== null ? scenario.trendFactorBpsByYear[String(r.claim.policyYear)] : undefined;
    if (trend !== undefined) {
      [r.indPaid, r.indRes, r.medPaid, r.medRes, r.expPaid, r.expRes] = [mulBps(r.indPaid, trend), mulBps(r.indRes, trend),
        mulBps(r.medPaid, trend), mulBps(r.medRes, trend), mulBps(r.expPaid, trend), mulBps(r.expRes, trend)];
    }
    if (scenario.reserveFactorBps !== 0) {
      const f = 10000 + scenario.reserveFactorBps;
      [r.indRes, r.medRes, r.expRes] = [mulBps(r.indRes, f), mulBps(r.medRes, f), mulBps(r.expRes, f)];
    }
    const cap = scenario.capPerClaimCents;
    if (cap !== null && incurred(r) > cap) {
      let remaining = cap;
      const take = (amount: number) => {
        const taken = Math.min(amount, Math.max(remaining, 0));
        remaining -= taken;
        return taken;
      };
      [r.indPaid, r.medPaid, r.expPaid] = [take(r.indPaid), take(r.medPaid), take(r.expPaid)];
      [r.indRes, r.medRes, r.expRes] = [take(r.indRes), take(r.medRes), take(r.expRes)];
    }
  }

  // 4. Metrics, in catalog order.
  const metrics = catalog.metrics.map((m) => metric(m, included, nonWc, input, valuation, lastDayWorked));

  // 5. Per period and per-claim flags.
  const periodGroups = new Map<string, Row[]>();
  for (const r of included) {
    const key = JSON.stringify([r.claim.policyYear, r.claim.policyNumber]);
    const list = periodGroups.get(key);
    if (list) list.push(r); else periodGroups.set(key, [r]);
  }
  const perPeriod: PeriodAggregate[] = [...periodGroups.values()]
    .sort((a, b) => yearOrder(a[0].claim.policyYear, b[0].claim.policyYear) || ordinal(a[0].claim.policyNumber ?? "", b[0].claim.policyNumber ?? ""))
    .map((g) => ({
      policyNumber: g[0].claim.policyNumber,
      policyYear: g[0].claim.policyYear,
      claimCount: g.length,
      openCount: g.filter((r) => r.open).length,
      indemnity: triplet(sum(g, (r) => r.indPaid), sum(g, (r) => r.indRes)),
      medical: triplet(sum(g, (r) => r.medPaid), sum(g, (r) => r.medRes)),
      expense: triplet(sum(g, (r) => r.expPaid), sum(g, (r) => r.expRes)),
      total: triplet(sum(g, paid), sum(g, reserve)),
    }));

  const largeThreshold = moneyParam(catalogMetric(catalog, "large_loss_count"), "threshold", 100000);
  const byId = new Map(rows.map((r) => [r.claim.id, r]));
  const ids = [...new Set(input.claims.map((c) => c.id))].sort(ordinal);
  const perClaimFlags = ids.map((id) => {
    const flags: string[] = [];
    if (nonWcIds.has(id)) {
      flags.push("nonWc");
    } else {
      const r = byId.get(id)!;
      if (excluded.has(id)) flags.push("excluded");
      if (r.ct) flags.push("ct");
      if (r.postTermination) flags.push("postTermination");
      if (incurred(r) >= largeThreshold) flags.push("large");
      if (r.claim.litigation === true) flags.push("litigated");
      if (r.nonCA) flags.push("nonCA");
      if (r.open) flags.push("open");
    }
    return { claimId: id, flags };
  }).filter((f) => f.flags.length > 0);

  return {
    catalogVersion: catalog.catalogVersion, view, quoteId: input.context.quoteId, valuationDate: input.context.valuationDate, scenario,
    inputsHash: inputsHash(input), inputClaims: input.claims.length, nonWcExcluded: nonWc.length, scenarioExcluded: excluded.size,
    included: included.length, metrics, perPeriod, perClaimFlags,
  };
}

function triplet(p: number, r: number): [number, number, number] {
  return [p, r, p + r];
}

function yearOrder(a: number | null, b: number | null): number {
  return (a === null ? 1 : 0) - (b === null ? 1 : 0) || (a ?? 0) - (b ?? 0);
}

function toRow(c: AnalyticsClaim, lastDayWorked: number | null): Row {
  const j = c.jurisdiction;
  const r: Row = {
    claim: c,
    indPaid: c.indemnityPaidCents, indRes: c.indemnityReserveCents,
    medPaid: c.medicalPaidCents, medRes: c.medicalReserveCents,
    expPaid: c.expensePaidCents, expRes: c.expenseReserveCents,
    doi: day(c.dateOfInjury), reported: day(c.dateReported), closed: day(c.dateClosed),
    ct: isCumulativeTrauma(c),
    nonCA: j !== null && j.trim().length > 0 && j.trim().toUpperCase() !== "CA",
    open: c.status === "open" || c.status === "reopened",
    postTermination: false,
    ldw: day(c.lastDayWorked) ?? lastDayWorked,
  };
  r.postTermination = r.ct && r.ldw !== null && r.doi !== null && r.doi >= r.ldw;
  return r;
}

const ctText = /(?<![A-Za-z0-9_])(cumulative|ct|repetitive)(?![A-Za-z0-9_])/i;

/** catalog.yaml conventions.ct. */
export function isCumulativeTrauma(c: AnalyticsClaim): boolean {
  const nature = code(c.natureCode);
  const cause = code(c.causeCode);
  const loss = code(c.typeOfLossCode);
  return c.cumulativeTrauma === true
    || (nature !== null && nature >= 60 && nature <= 83)
    || cause === 97 || cause === 98
    || loss === 2 || loss === 3
    || (c.description !== null && ctText.test(c.description));
}

function code(value: string | null): number | null {
  const s = value?.trim();
  return s !== undefined && s.length > 0 && s.length <= 6 && /^[0-9]+$/.test(s) ? Number(s) : null;
}

// ------------------------------------------------------------------ metrics

function catalogMetric(catalog: Catalog, name: string): CatalogMetric {
  return catalog.metrics.find((m) => m.name === name)!;
}

function moneyParam(m: CatalogMetric, name: string, fallbackDollars: number): number {
  const v = m.params?.[name];
  return (typeof v === "number" ? v : fallbackDollars) * 100;
}

function moneyParams(m: CatalogMetric, name: string): number[] {
  const v = m.params?.[name];
  return Array.isArray(v) ? v.map((x) => (x as number) * 100) : [];
}

function intParam(m: CatalogMetric, name: string, fallback: number): number {
  const v = m.params?.[name];
  return typeof v === "number" ? v : fallback;
}

/** numerator / denominator in millionths (catalog conventions.ratio). */
function ratio(numerator: number, denominator: number): number | null {
  return roundDiv(BigInt(numerator) * 1000000n, denominator);
}

const dollars = (cents: number) => formatScaled(cents, 2);

function byYear(rows: Row[]): [string, Row[]][] {
  const groups = new Map<number | null, Row[]>();
  for (const r of rows) {
    const list = groups.get(r.claim.policyYear);
    if (list) list.push(r); else groups.set(r.claim.policyYear, [r]);
  }
  return [...groups.entries()].sort((a, b) => yearOrder(a[0], b[0])).map(([y, g]) => [y === null ? "unknown" : String(y), g]);
}

function frequency(rows: Row[], exposure: Record<string, number> | null, scale: bigint): [number | null, [string, number | null][] | null] {
  if (exposure === null || Object.keys(exposure).length === 0) {
    return [null, null];
  }
  const years = Object.keys(exposure).map(Number).sort((a, b) => a - b);
  const set = new Set(years);
  const claims = rows.filter((r) => r.claim.policyYear !== null && set.has(r.claim.policyYear)).length;
  const value = roundDiv(BigInt(claims) * scale, years.reduce((a, y) => a + exposure[String(y)], 0));
  const breakdown: [string, number | null][] = years.map((y) => [String(y), roundDiv(BigInt(rows.filter((r) => r.claim.policyYear === y).length) * scale, exposure[String(y)])]);
  return [value, breakdown];
}

function drivers(rows: Row[], codeOf: (r: Row) => string | null, byIncurred: boolean, top: number): [number | null, [string, number | null][] | null] {
  if (!rows.some((r) => { const c = codeOf(r); return c !== null && c.length > 0; })) {
    return [null, null];
  }
  const groups = new Map<string, Row[]>();
  for (const r of rows) {
    const c = codeOf(r);
    const key = c !== null && c.length > 0 ? c.trim() : "unknown";
    const list = groups.get(key);
    if (list) list.push(r); else groups.set(key, [r]);
  }
  const ranked = [...groups.entries()].map(([key, g]) => ({ key, value: byIncurred ? sum(g, incurred) : g.length }))
    .sort((a, b) => b.value - a.value || ordinal(a.key, b.key));
  return [ranked[0].value, ranked.slice(0, top).map((g) => [g.key, g.value])];
}

function lags(rows: Row[]): number[] {
  return rows.map((r) => r.reported! - r.doi!);
}

function metric(m: CatalogMetric, rows: Row[], nonWc: AnalyticsClaim[], input: AnalyticsInput, valuation: number | null, lastDayWorked: number | null): MetricResult {
  const all = rows;
  const lostTime = rows.filter((r) => r.claim.claimType === "indemnity");
  let value: number | null;
  let breakdown: [string, number | null][] | null = null;
  let contributors: Row[] = all;
  let explicitContributors: string[] | null = null;

  switch (m.name) {
    case "claim_count":
      value = rows.length;
      break;
    case "claim_count_with_financials":
      contributors = rows.filter((r) => r.claim.reportOnly !== true);
      value = contributors.length;
      break;
    case "open_claim_count":
      contributors = rows.filter((r) => r.open);
      value = contributors.length;
      break;
    case "closed_claim_count":
      contributors = rows.filter((r) => r.claim.status === "closed");
      value = contributors.length;
      break;
    case "reopened_claim_count":
      contributors = rows.filter((r) => r.claim.status === "reopened");
      value = contributors.length;
      break;
    case "medical_only_count":
      contributors = rows.filter((r) => r.claim.claimType === "medicalOnly");
      value = contributors.length;
      break;
    case "lost_time_count":
      contributors = lostTime;
      value = lostTime.length;
      break;
    case "lost_time_share":
      value = ratio(lostTime.length, rows.length);
      break;
    case "paid_total":
      value = sum(rows, paid);
      breakdown = [["indemnity", sum(rows, (r) => r.indPaid)], ["medical", sum(rows, (r) => r.medPaid)], ["expense", sum(rows, (r) => r.expPaid)]];
      break;
    case "reserve_total":
      value = sum(rows, reserve);
      breakdown = [["indemnity", sum(rows, (r) => r.indRes)], ["medical", sum(rows, (r) => r.medRes)], ["expense", sum(rows, (r) => r.expRes)]];
      break;
    case "incurred_total":
      value = sum(rows, incurred);
      breakdown = [["indemnity", sum(rows, (r) => r.indPaid + r.indRes)], ["medical", sum(rows, (r) => r.medPaid + r.medRes)], ["expense", sum(rows, (r) => r.expPaid + r.expRes)]];
      break;
    case "frequency_per_100_payroll":
      [value, breakdown] = frequency(rows, input.context.payrollCentsByYear, 10000000000n);
      break;
    case "frequency_per_100_fte":
      [value, breakdown] = frequency(rows, input.context.fteByYear, 100000000n);
      break;
    case "claim_count_by_policy_year":
      breakdown = byYear(rows).map(([k, g]) => [k, g.length]);
      value = breakdown.length;
      break;
    case "severity_all":
      value = roundDiv(sum(rows, incurred), rows.length);
      break;
    case "severity_lost_time":
      contributors = lostTime;
      value = roundDiv(sum(lostTime, incurred), lostTime.length);
      break;
    case "paid_to_incurred":
      value = ratio(sum(rows, paid), sum(rows, incurred));
      break;
    case "large_loss_count": {
      const threshold = moneyParam(m, "threshold", 100000);
      contributors = rows.filter((r) => incurred(r) >= threshold);
      value = contributors.length;
      breakdown = moneyParams(m, "thresholds").map((t) => [dollars(t), rows.filter((r) => incurred(r) >= t).length]);
      break;
    }
    case "large_loss_share": {
      const threshold = moneyParam(m, "threshold", 100000);
      const total = sum(rows, incurred);
      value = ratio(sum(rows.filter((r) => incurred(r) >= threshold), incurred), total);
      breakdown = moneyParams(m, "thresholds").map((t) => [dollars(t), ratio(sum(rows.filter((r) => incurred(r) >= t), incurred), total)]);
      break;
    }
    case "capped_incurred": {
      const cap = moneyParam(m, "cap", 250000);
      value = sum(rows, (r) => Math.min(incurred(r), cap));
      breakdown = moneyParams(m, "caps").map((c) => [dollars(c), sum(rows, (r) => Math.min(incurred(r), c))]);
      break;
    }
    case "loss_drivers_cause_incurred":
      [value, breakdown] = drivers(rows, (r) => r.claim.causeCode, true, intParam(m, "top", 5));
      break;
    case "loss_drivers_cause_count":
      [value, breakdown] = drivers(rows, (r) => r.claim.causeCode, false, intParam(m, "top", 5));
      break;
    case "loss_drivers_body_part_incurred":
      [value, breakdown] = drivers(rows, (r) => r.claim.bodyPartCode, true, intParam(m, "top", 5));
      break;
    case "loss_drivers_body_part_count":
      [value, breakdown] = drivers(rows, (r) => r.claim.bodyPartCode, false, intParam(m, "top", 5));
      break;
    case "loss_drivers_nature_incurred":
      [value, breakdown] = drivers(rows, (r) => r.claim.natureCode, true, intParam(m, "top", 5));
      break;
    case "loss_drivers_nature_count":
      [value, breakdown] = drivers(rows, (r) => r.claim.natureCode, false, intParam(m, "top", 5));
      break;
    case "litigation_rate":
      contributors = lostTime;
      value = rows.some((r) => r.claim.litigation !== null) ? ratio(lostTime.filter((r) => r.claim.litigation === true).length, lostTime.length) : null;
      break;
    case "ct_share":
      value = ratio(rows.filter((r) => r.ct).length, rows.length);
      break;
    case "ct_indemnity_share":
      contributors = lostTime;
      value = ratio(lostTime.filter((r) => r.ct).length, lostTime.length);
      break;
    case "post_termination_ct_count":
      contributors = rows.filter((r) => r.postTermination);
      value = rows.some((r) => r.ldw !== null) ? contributors.length : null;
      break;
    case "subrogation_recovery_rate":
      contributors = rows.filter((r) => r.claim.recoveryCents !== null && r.claim.recoveryCents > 0);
      value = contributors.length === 0 ? null : ratio(sum(contributors, (r) => r.claim.recoveryCents!), sum(contributors, incurred));
      break;
    case "closure_median_days":
      contributors = rows.filter((r) => r.claim.status === "closed" && r.doi !== null && r.closed !== null);
      value = medianTenths(contributors.map((r) => r.closed! - r.doi!));
      break;
    case "open_claim_age": {
      contributors = rows.filter((r) => r.open && r.doi !== null);
      if (valuation !== null) {
        const ages = contributors.map((r) => valuation - r.doi!);
        breakdown = [["<1y", ages.filter((a) => a < 365).length], ["1-2y", ages.filter((a) => a >= 365 && a < 730).length],
          ["2-3y", ages.filter((a) => a >= 730 && a < 1095).length], ["3y+", ages.filter((a) => a >= 1095).length]];
        value = contributors.length;
      } else {
        value = null;
      }
      break;
    }
    case "reporting_lag_median_days":
      contributors = rows.filter((r) => r.doi !== null && r.reported !== null);
      value = medianTenths(lags(contributors));
      break;
    case "reporting_lag_p90_days":
      contributors = rows.filter((r) => r.doi !== null && r.reported !== null);
      value = percentileTenths(lags(contributors), intParam(m, "percentile", 90));
      break;
    case "reporting_lag_over_30_share": {
      contributors = rows.filter((r) => r.doi !== null && r.reported !== null);
      const days = intParam(m, "days", 30);
      value = ratio(lags(contributors).filter((l) => l > days).length, contributors.length);
      break;
    }
    case "incurred_by_policy_year":
      value = sum(rows, incurred);
      breakdown = byYear(rows).map(([k, g]) => [k, sum(g, incurred)]);
      break;
    case "paid_by_policy_year":
      value = sum(rows, paid);
      breakdown = byYear(rows).map(([k, g]) => [k, sum(g, paid)]);
      break;
    case "severity_by_policy_year":
      value = roundDiv(sum(rows, incurred), rows.length);
      breakdown = byYear(rows).map(([k, g]) => [k, roundDiv(sum(g, incurred), g.length)]);
      break;
    case "paid_to_incurred_by_policy_year":
      value = ratio(sum(rows, paid), sum(rows, incurred));
      breakdown = byYear(rows).map(([k, g]) => [k, ratio(sum(g, paid), sum(g, incurred))]);
      break;
    case "non_ca_share": {
      value = ratio(rows.filter((r) => r.nonCA).length, rows.length);
      const counts = new Map<string, number>();
      for (const r of rows) {
        const j = r.claim.jurisdiction;
        const key = j !== null && j.trim().length > 0 ? j.trim().toUpperCase() : "unknown";
        counts.set(key, (counts.get(key) ?? 0) + 1);
      }
      breakdown = [...counts.entries()].sort((a, b) => b[1] - a[1] || ordinal(a[0], b[0])).map(([k, n]) => [k, ratio(n, rows.length)]);
      break;
    }
    case "non_wc_excluded_count":
      value = nonWc.length;
      explicitContributors = nonWc.map((c) => c.id).sort(ordinal);
      break;
    default:
      throw new Error(`catalog metric '${m.name}' has no implementation`);
  }

  const contributorIds = explicitContributors ?? (contributors === all || contributors.length === all.length ? null
    : contributors.map((r) => r.claim.id).sort(ordinal));
  const contributing = new Set(contributorIds ?? all.map((r) => r.claim.id));
  const inputFields = m.inputs.map((i) => i.field);
  const overrides = [...new Set(input.overrides.filter((o) => contributing.has(o.claimId) && inputFields.some((f) => fieldMatches(f, o.field))).map((o) => o.id))].sort(ordinal);
  const result: MetricResult = { name: m.name, unit: m.unit, scale: scaleOf(m.unit), value, breakdown, contributors: contributorIds, overrides };
  if (value === null) result.notMeasurable = notMeasurable(m.name, rows, input, valuation);
  return result;
}

/** Why a metric has no value (catalog.yaml conventions.notMeasurable): the missing input, else no contributing claims. */
function notMeasurable(name: string, rows: Row[], input: AnalyticsInput, valuation: number | null): string {
  const has = (m: Record<string, number> | null) => m !== null && Object.keys(m).length > 0;
  switch (name) {
    case "frequency_per_100_payroll":
      if (!has(input.context.payrollCentsByYear)) return "no payroll in the submission context";
      break;
    case "frequency_per_100_fte":
      if (!has(input.context.fteByYear)) return "no FTE count in the submission context";
      break;
    case "post_termination_ct_count":
      return "no last day worked in the submission context or on the claims";
    case "litigation_rate":
      if (rows.every((r) => r.claim.litigation === null)) return "no litigation flag printed";
      break;
    case "subrogation_recovery_rate":
      return "no recoveries printed";
    case "loss_drivers_cause_incurred":
    case "loss_drivers_cause_count":
      return "no cause-of-injury codes printed";
    case "loss_drivers_body_part_incurred":
    case "loss_drivers_body_part_count":
      return "no body-part codes printed";
    case "loss_drivers_nature_incurred":
    case "loss_drivers_nature_count":
      return "no nature-of-injury codes printed";
    case "open_claim_age":
      if (valuation === null) return "no valuation date";
      break;
  }
  return "no contributing claims";
}

/** Whether an override of claim field `field` changes catalog input `input`. */
export function fieldMatches(input: string, field: string): boolean {
  if (!input.startsWith("claim.")) {
    return false;
  }
  const i = input.slice("claim.".length);
  return i === field || field.startsWith(i + ".") || ((i.startsWith("total.") || i.endsWith(".incurred")) && isMoney(field));
}

function isMoney(field: string): boolean {
  return field.startsWith("indemnity.") || field.startsWith("medical.") || field.startsWith("expense.") || field.startsWith("total.");
}
