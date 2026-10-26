import { money, ruleStatus, isoDate, type LineageFacts, type SheetRegion, type SourceRegion } from "@lossrun/ui";
import ruleNames from "virtual:rule-names";
import { typedOldValue } from "../review/fieldTypes.ts";
import type { AuditTrailEntry, Claim, DocumentResult, LineageRecord, Money, RuleOutcome, Totals } from "../api/types.ts";

// The ledger ↔ lineage model: which canonical field path each ledger cell shows, the value, the page region, and the
// rules that read it. Paths are the document's own (policyPeriods[p].claims[c].medical.paid), as in document.lineage.

export interface LedgerColumn {
  id: string;
  /** Path below the claim: "medical.paid", "dateOfInjury". */
  field: string;
  label: string;
  group?: "Indemnity" | "Medical" | "Expense" | "Total";
  numeric?: boolean;
  kind: "money" | "date" | "text" | "status";
}

const MONEY_GROUPS = ["indemnity", "medical", "expense", "total"] as const;
const GROUP_LABEL = { indemnity: "Indemnity", medical: "Medical", expense: "Expense", total: "Total" } as const;
const PART_LABEL = { paid: "Paid", reserve: "Reserve", incurred: "Incurred" } as const;

export const LEDGER_COLUMNS: LedgerColumn[] = [
  { id: "claimNumber", field: "claimNumber", label: "Claim", kind: "text" },
  { id: "dateOfInjury", field: "dateOfInjury", label: "Injury date", kind: "date" },
  { id: "dateReported", field: "dateReported", label: "Reported", kind: "date" },
  { id: "status", field: "status", label: "Status", kind: "status" },
  { id: "claimType", field: "claimType", label: "Type", kind: "status" },
  ...MONEY_GROUPS.flatMap((g) =>
    (["paid", "reserve", "incurred"] as const).map((p) => ({ id: `${g}.${p}`, field: `${g}.${p}`, label: PART_LABEL[p], group: GROUP_LABEL[g], numeric: true, kind: "money" as const })),
  ),
];

export const FIELD_LABEL: Record<string, string> = {
  ...Object.fromEntries(LEDGER_COLUMNS.map((c) => [c.field, c.group ? `${c.group} ${c.label.toLowerCase()}` : c.label])),
  claimNumber: "Claim number",
  claimantName: "Claimant",
  rawStatus: "Status as printed",
  dateClosed: "Closed date",
  lastDayWorked: "Last day worked",
  "natureOfInjury": "Nature of injury",
  "causeOfInjury": "Cause of injury",
  "partOfBody": "Part of body",
  litigationFlag: "Litigation",
  reportOnly: "Report only (no financials)",
};

export interface LedgerRow {
  id: string;
  periodIndex: number;
  claimIndex: number;
  claim: Claim;
}

export function rowsOf(doc: DocumentResult, periodIndex: number): LedgerRow[] {
  return (doc.document.policyPeriods[periodIndex]?.claims ?? []).map((claim, claimIndex) => ({ id: `${periodIndex}/${claimIndex}`, periodIndex, claimIndex, claim }));
}

export function claimPath(row: { periodIndex: number; claimIndex: number }, field: string): string {
  return `policyPeriods[${row.periodIndex}].claims[${row.claimIndex}].${field}`;
}

export function get(obj: unknown, field: string): unknown {
  return field.split(".").reduce<unknown>((o, k) => (o && typeof o === "object" ? (o as Record<string, unknown>)[k] : undefined), obj);
}

/** A claim's money part; incurred falls back to paid + reserve, total to the sum of components (as the engine reads it). */
export function moneyOf(claim: Claim, field: string): number | null {
  const [group, part] = field.split(".") as [keyof Claim, keyof Money];
  const m = claim[group] as Money | null | undefined;
  const direct = m?.[part];
  if (direct !== null && direct !== undefined) return direct;
  if (part === "incurred" && m && (m.paid != null || m.reserve != null)) return (m.paid ?? 0) + (m.reserve ?? 0);
  if (group === "total") {
    const parts = (["indemnity", "medical", "expense"] as const).map((g) => claim[g]?.[part === "incurred" ? "incurred" : part] ?? (part === "incurred" ? (claim[g]?.paid ?? 0) + (claim[g]?.reserve ?? 0) : null));
    if (parts.some((p) => p !== null && p !== undefined)) return parts.reduce<number>((a, b) => a + (b ?? 0), 0);
  }
  return null;
}

/** Display text for a cell. */
export function cellText(claim: Claim, col: LedgerColumn): string {
  if (col.kind === "money") return money(moneyOf(claim, col.field));
  const v = get(claim, col.field);
  if (v === null || v === undefined || v === "") return "—";
  if (col.kind === "date") return isoDate(String(v));
  if (col.field === "claimType") return v === "medicalOnly" ? "Medical only" : v === "indemnity" ? "Lost time" : String(v);
  if (col.field === "status") return String(v).charAt(0).toUpperCase() + String(v).slice(1) + (claim.reportOnly ? " · report only" : "");
  return String(v);
}

/**
 * Overrides from the audit trail, oldest first per path (as-reported = the value before the first). The API writes one
 * `override` row per field with its `fieldPath`; the `review.override` row closes the decision and names no field
 * (brief 24 step 1: the UI read `review.override` + fieldPath, which only the old mock wrote).
 */
export function overridesOf(audit: AuditTrailEntry[] | undefined): Map<string, AuditTrailEntry[]> {
  const map = new Map<string, AuditTrailEntry[]>();
  for (const e of [...(audit ?? [])].reverse()) {
    if (e.action !== "override" || !e.fieldPath) continue;
    map.set(e.fieldPath, [...(map.get(e.fieldPath) ?? []), e]);
  }
  return map;
}

/** As-reported view: the document with every overridden field put back to its first old value. */
export function asReported(doc: DocumentResult, overrides: Map<string, AuditTrailEntry[]>): DocumentResult {
  if (overrides.size === 0) return doc;
  const copy = structuredClone(doc);
  for (const [path, list] of overrides) {
    const old = list[0].oldValue;
    const parts = path.match(/[^.[\]]+/g) ?? [];
    let node = copy.document as unknown as Record<string, unknown>;
    for (let i = 0; i < parts.length - 1 && node; i++) node = node[/^\d+$/.test(parts[i]) ? Number(parts[i]) : parts[i]] as Record<string, unknown>;
    // Typed by the canonical field type, never by "looks like a number" (a digit-only claim number stays a string).
    if (node) node[parts[parts.length - 1]] = typedOldValue(path, old);
  }
  return copy;
}

export function lineageOf(doc: DocumentResult, path: string): LineageRecord | undefined {
  return doc.document.lineage?.[path] ?? undefined;
}

/** Whether a rule's affected field pattern covers a path ("policyPeriods[*].claims[*].total" covers …total.incurred). */
export function covers(pattern: string, path: string): boolean {
  const re = new RegExp("^" + pattern.replace(/[.+?^${}()|\\]/g, "\\$&").replace(/\[\*\]/g, "\\[\\d+\\]").replace(/\[(\d+)\]/g, "\\[$1\\]") + "(\\.|$)");
  return re.test(path);
}

const RULE_NAMES: Record<string, string> = ruleNames;

/**
 * Rule outcomes as the UI shows them: a failure keeps the engine's message ("medical paid exceeds medical incurred on
 * claim …"); any other outcome shows the rule's name from docs/evaluation/rule-catalog.json, because the engine's
 * message is phrased as the failure.
 */
export function displayOutcomes(doc: DocumentResult): RuleOutcome[] {
  return doc.ruleOutcomes.map((r) => (r.status === "fail" ? r : { ...r, message: RULE_NAMES[r.ruleId] ?? r.message }));
}

export function rulesFor(doc: DocumentResult, path: string): RuleOutcome[] {
  const hits = displayOutcomes(doc).filter((r) => r.affectedFields.some((f) => covers(f, path)));
  const seen = new Set<string>();
  return hits.filter((r) => {
    const k = `${r.ruleId}|${r.status}|${r.message}`;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });
}

/** Paths of hard/soft failures (outlined in Review). */
export function failingPaths(doc: DocumentResult): Set<string> {
  return new Set(doc.ruleOutcomes.filter((r) => r.status === "fail" && r.severity !== "flag").flatMap((r) => r.affectedFields.filter((f) => !f.includes("*"))));
}

export function describeSource(l: LineageRecord | undefined): string | null {
  if (!l) return null;
  if (l.sheet) return `${l.sheet}!${l.cell ?? ""}`;
  if (l.page && l.polygon && l.polygon.length >= 8) {
    const xs = l.polygon.filter((_, i) => i % 2 === 0);
    const ys = l.polygon.filter((_, i) => i % 2 === 1);
    return `Page ${l.page} · x ${Math.min(...xs).toFixed(2)}–${Math.max(...xs).toFixed(2)} in, y ${Math.min(...ys).toFixed(2)}–${Math.max(...ys).toFixed(2)} in`;
  }
  return l.page ? `Page ${l.page}` : null;
}

/** Human label for any path (claim fields, subtotals, header fields). */
export function labelOf(path: string): string {
  const claim = /claims\[\d+\]\.(.+)$/.exec(path);
  if (claim) return FIELD_LABEL[claim[1]] ?? claim[1];
  const sub = /(subtotals|reportTotals)\.(.+)$/.exec(path);
  if (sub) {
    const [g, p] = sub[2].split(".");
    const what = g in GROUP_LABEL ? `${GROUP_LABEL[g as keyof typeof GROUP_LABEL]} ${p ?? ""}`.trim() : sub[2].replace(/([A-Z])/g, " $1").toLowerCase();
    return `${sub[1] === "subtotals" ? "Printed subtotal" : "Printed grand total"}: ${what}`;
  }
  return path.replace(/^policyPeriods\[\d+\]\./, "").replace(/([A-Z])/g, " $1").replace(/^./, (c) => c.toUpperCase());
}

export function contextOf(doc: DocumentResult, path: string): string {
  const m = /^policyPeriods\[(\d+)\](?:\.claims\[(\d+)\])?/.exec(path);
  if (!m) return doc.document.carrierName ?? "Document";
  const period = doc.document.policyPeriods[Number(m[1])];
  if (m[2] !== undefined) return `Claim ${period?.claims[Number(m[2])]?.claimNumber ?? "?"} · policy ${period?.policyNumber ?? "?"}`;
  return `Policy ${period?.policyNumber ?? "?"}`;
}

export function valueText(doc: DocumentResult, path: string): string {
  const m = /^policyPeriods\[(\d+)\]\.claims\[(\d+)\]\.(.+)$/.exec(path);
  if (m) {
    const claim = doc.document.policyPeriods[Number(m[1])]?.claims[Number(m[2])];
    const col = LEDGER_COLUMNS.find((c) => c.field === m[3]);
    if (claim && col) return cellText(claim, col);
  }
  const parts = path.match(/[^.[\]]+/g) ?? [];
  const v = parts.reduce<unknown>((o, k) => (o && typeof o === "object" ? (o as Record<string, unknown>)[/^\d+$/.test(k) ? Number(k) : k] : undefined), doc.document);
  return typeof v === "number" ? (Number.isInteger(v) && /count/i.test(path) ? String(v) : money(v)) : v === null || v === undefined ? "—" : String(v);
}

/** Brief 31 Answers 1: the lineage repair code as the reviewer reads it ("ocrRepair: l→I" → "OCR read the capital I as l"). */
export function repairText(repair: string | null | undefined): string | null {
  if (!repair) return null;
  const ocr = /^ocrRepair:\s*(\S)→(\S)$/.exec(repair);
  return ocr ? `OCR read the capital ${ocr[2]} as ${ocr[1]}` : repair;
}

export function factsFor(doc: DocumentResult, path: string, overrides: Map<string, AuditTrailEntry[]>): LineageFacts {
  const l = lineageOf(doc, path);
  const derivedFrom = l?.extractor === "derived" && l.sourceText ? l.sourceText.split(" + ").map((p) => labelOf(p)) : undefined;
  return {
    field: labelOf(path),
    path,
    context: contextOf(doc, path),
    value: valueText(doc, path),
    sourceText: l?.extractor === "derived" || l?.extractor === "manual" ? null : l?.sourceText,
    confidence: l?.confidence ?? null,
    source: l?.extractor === "derived" ? "Not printed — computed" : describeSource(l),
    format: l?.formatId ? `${l.formatId}@${l.formatVersion ?? "?"}` : doc.formatRef ?? null,
    extractor: l?.extractor ?? null,
    derivedFrom,
    part: l?.part === null || l?.part === undefined ? null : l.part + 1,
    repair: repairText(l?.repair),
    rules: rulesFor(doc, path).map((r) => ({ ruleId: r.ruleId, status: ruleStatus(r), message: r.message })),
    overrides: (overrides.get(path) ?? []).map((o) => ({ at: o.at.slice(0, 16).replace("T", " "), user: o.user, oldValue: o.oldValue, newValue: o.newValue, rationale: o.rationale })),
  };
}

/** Highlight regions for one page: every value with a polygon there. */
export function regionsFor(doc: DocumentResult, page: number, failing: Set<string>): SourceRegion[] {
  const out: SourceRegion[] = [];
  for (const [path, l] of Object.entries(doc.document.lineage ?? {})) {
    if (l.page !== page || !l.polygon || l.polygon.length < 8 || l.extractor === "derived") continue;
    const value = l.sourceText ?? valueText(doc, path);
    out.push({ id: path, page, polygon: l.polygon, label: `${labelOf(path)}, ${contextOf(doc, path)}: ${value}`, short: labelOf(path), tone: failing.has(path) ? "fail" : "default" });
  }
  return out;
}

/** A value that can be shown in its source: a page region (PDF) or a sheet cell (spreadsheet); derived values cannot. */
export function traceable(l: LineageRecord | undefined): boolean {
  return !!l && l.extractor !== "derived" && (!!(l.polygon && l.polygon.length >= 8) || !!(l.sheet && l.cell));
}

/** Highlighted cells of one sheet: every value read from a cell there. */
export function sheetRegionsFor(doc: DocumentResult, sheet: string, failing: Set<string>): SheetRegion[] {
  const out: SheetRegion[] = [];
  for (const [path, l] of Object.entries(doc.document.lineage ?? {})) {
    if (l.sheet !== sheet || !l.cell || l.extractor === "derived") continue;
    const value = l.sourceText ?? valueText(doc, path);
    out.push({ id: path, cell: l.cell, label: `${labelOf(path)}, ${contextOf(doc, path)}: ${value}`, tone: failing.has(path) ? "fail" : "default" });
  }
  return out;
}

/** The traceable values of a ledger row, in column order (row trace steps through these). */
export function rowPaths(doc: DocumentResult, row: { periodIndex: number; claimIndex: number }): string[] {
  return LEDGER_COLUMNS.map((c) => claimPath(row, c.field)).filter((p) => {
    return traceable(lineageOf(doc, p));
  });
}

/** Printed subtotals vs the sum of extracted rows, per money column (the totals strip). */
export function totalsCheck(doc: DocumentResult, periodIndex: number): { id: string; label: string; printed: number | null; extracted: number }[] {
  const period = doc.document.policyPeriods[periodIndex];
  const printed: Totals | null | undefined = period?.subtotals;
  return LEDGER_COLUMNS.filter((c) => c.kind === "money").map((c) => {
    const extracted = (period?.claims ?? []).reduce((a, claim) => a + (moneyOf(claim, c.field) ?? 0), 0);
    const [g, p] = c.field.split(".") as [keyof Totals, keyof Money];
    const pv = (printed?.[g] as Money | undefined)?.[p];
    return { id: c.id, label: FIELD_LABEL[c.field], printed: pv ?? null, extracted: Math.round(extracted * 100) / 100 };
  });
}
