// Number and date presentation (design-brief §3 Type): tabular figures, right-aligned money with two decimals and
// thousands separators, a true minus for negatives, ISO dates in tables and "Mar 17, 2024" in prose.

const MINUS = "−";

const money2 = new Intl.NumberFormat("en-US", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const int = new Intl.NumberFormat("en-US", { maximumFractionDigits: 0 });

function signed(text: string, negative: boolean): string {
  return negative ? MINUS + text : text;
}

/** 1234.5 → "1,234.50"; −1234.5 → "−1,234.50"; null → "—". */
export function money(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return signed(money2.format(Math.abs(value)), value < 0);
}

/** "$1,234.50" for prose and KPI tiles. */
export function usd(value: number | null | undefined, digits = 2): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  const f = new Intl.NumberFormat("en-US", { minimumFractionDigits: digits, maximumFractionDigits: digits });
  return signed("$" + f.format(Math.abs(value)), value < 0);
}

/** Compact dollars for KPI values and axes: $1.23M, $482.1K, $8.1K, $950 (tables show full precision). */
export function usdCompact(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  const a = Math.abs(value);
  const text = a >= 1e6 ? `$${(a / 1e6).toFixed(2)}M` : a >= 1e3 ? `$${(a / 1e3).toFixed(1)}K` : `$${int.format(Math.round(a))}`;
  return signed(text, value < 0);
}

export function count(value: number | null | undefined): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return signed(int.format(Math.abs(value)), value < 0);
}

/** 0.4567 → "45.7 %". */
export function percent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || Number.isNaN(value)) return "—";
  return signed(`${(Math.abs(value) * 100).toFixed(digits)} %`, value < 0);
}

/** ISO date for tables (unchanged; "—" when absent). */
export function isoDate(value: string | null | undefined): string {
  return value ? value.slice(0, 10) : "—";
}

/** "Mar 17, 2024" for prose. */
export function proseDate(value: string | null | undefined): string {
  if (!value) return "—";
  const d = new Date(value.length === 10 ? value + "T00:00:00Z" : value);
  return d.toLocaleDateString("en-US", { month: "short", day: "numeric", year: "numeric", timeZone: "UTC" });
}

/** "2026-10-22 14:03" (UTC) for audit rows. */
export function dateTime(value: string | null | undefined): string {
  if (!value) return "—";
  const d = new Date(value);
  return `${d.toISOString().slice(0, 10)} ${d.toISOString().slice(11, 16)} UTC`;
}

export type MetricUnit = "count" | "money" | "ratio" | "rate" | "days";

/** A catalog metric value (already in display units: dollars, 0–1 ratios, days) as text. */
export function metricValue(value: number | null | undefined, unit: string, compact = false): string {
  switch (unit) {
    case "money":
      return compact ? usdCompact(value) : usd(value);
    case "ratio":
      return percent(value);
    case "rate":
      return value === null || value === undefined ? "—" : value.toFixed(value !== 0 && Math.abs(value) < 0.01 ? 6 : 2);
    case "days":
      return value === null || value === undefined ? "—" : `${value.toFixed(1)} d`;
    default:
      return count(value);
  }
}

/** A delta with an explicit sign: "+$1,200.00", "−2.0 pts", "+3". */
export function metricDelta(delta: number | null | undefined, unit: string): string {
  if (delta === null || delta === undefined || Number.isNaN(delta)) return "—";
  if (delta === 0) return "no change";
  const sign = delta > 0 ? "+" : MINUS;
  const a = Math.abs(delta);
  switch (unit) {
    case "money":
      return sign + usdCompact(a);
    case "ratio":
      return `${sign}${(a * 100).toFixed(1)} pts`;
    case "days":
      return `${sign}${a.toFixed(1)} d`;
    case "rate":
      return sign + a.toFixed(a < 0.01 ? 6 : 2);
    default:
      return sign + int.format(a);
  }
}
