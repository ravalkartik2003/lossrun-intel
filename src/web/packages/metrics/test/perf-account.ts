// The brief 10 §7 synthetic account — the twin of src/LossRun.Analytics/PerfAccount.cs (same 32-bit LCG, same draws in the
// same order), so both engines time identical input and their snapshot hashes match.

import type { AnalyticsClaim, AnalyticsInput } from "../src/model.ts";

const statuses = ["open", "closed", "closed", "reopened"];
const types = ["indemnity", "medicalOnly", "medicalOnly"];
const causes = ["56", "29", "97", "31", "45", "60", "81", "98"];
const parts = ["42", "36", "38", "53", "90", "21"];
const natures = ["52", "40", "60", "78", "10", "49"];
const jurisdictions = ["CA", "CA", "CA", "CA", "CA", "CA", "CA", "NV", "AZ"];

export function perfAccount(claims = 1000, years = 5, firstYear = 2020): AnalyticsInput {
  let x = 20261022 >>> 0;
  const next = () => (x = (Math.imul(x, 1664525) + 1013904223) >>> 0);
  const pick = (n: number) => next() % n;
  const date = (year: number, dayOfYear: number) => new Date(Date.UTC(year, 0, 1 + dayOfYear)).toISOString().slice(0, 10);

  const list: AnalyticsClaim[] = [];
  for (let i = 0; i < claims; i++) {
    const year = firstYear + pick(years);
    const doi = pick(360);
    const status = statuses[pick(statuses.length)];
    const type = types[pick(types.length)];
    const indPaid = type === "indemnity" ? pick(6000000) : 0;
    const medPaid = pick(2500000);
    const open = status !== "closed";
    const claimNumber = `PF-${String(year % 100).padStart(2, "0")}-${String(i).padStart(4, "0")}`;
    const dateOfInjury = date(year, doi);
    const dateReported = date(year, doi + 1 + pick(60));
    const dateClosed = open ? null : date(year, doi + 30 + pick(600));
    const natureCode = pick(6) === 0 ? null : natures[pick(natures.length)];
    const causeCode = pick(5) === 0 ? null : causes[pick(causes.length)];
    const bodyPartCode = pick(6) === 0 ? null : parts[pick(parts.length)];
    const description = pick(4) === 0 ? "cumulative strain" : null;
    const litigation = pick(3) === 0 ? null : pick(5) === 0;
    const jurisdiction = jurisdictions[pick(jurisdictions.length)];
    const lineOfBusiness = pick(100) === 0 ? "GL" : "WC";
    const indRes = open && type === "indemnity" ? pick(4000000) : 0;
    const medRes = open ? pick(1500000) : 0;
    const expPaid = pick(400000);
    const expRes = open ? pick(200000) : 0;
    const recovery = pick(15) === 0 ? pick(300000) : null;
    list.push({
      id: `perf/${Math.floor(i / 200)}/${i % 200}`, claimNumber, policyNumber: `PERF-${year}`, policyYear: year,
      dateOfInjury, dateReported, dateClosed, status, claimType: type, natureCode, causeCode, bodyPartCode, typeOfLossCode: null, description,
      litigation, cumulativeTrauma: null, jurisdiction, lineOfBusiness,
      indemnityPaidCents: indPaid, indemnityReserveCents: indRes, medicalPaidCents: medPaid, medicalReserveCents: medRes,
      expensePaidCents: expPaid, expenseReserveCents: expRes, recoveryCents: recovery, lastDayWorked: null,
    });
  }
  const payroll: Record<string, number> = {};
  const fte: Record<string, number> = {};
  for (let y = firstYear; y < firstYear + years; y++) {
    payroll[String(y)] = 500000000 + (y - firstYear) * 25000000;
    fte[String(y)] = 120 + (y - firstYear) * 5;
  }
  return { context: { quoteId: "Q-PERF", valuationDate: "2025-09-30", payrollCentsByYear: payroll, fteByYear: fte, lastDayWorked: "2024-06-30" }, claims: list, overrides: [] };
}
