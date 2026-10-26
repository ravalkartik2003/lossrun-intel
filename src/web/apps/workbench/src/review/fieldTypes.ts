import fieldTypes from "virtual:field-types";
import codeSets from "@docs/evaluation/code-sets.json";
import type { OverrideValue } from "../api/types.ts";

// Brief 30 item 3(a): an override's newValue is typed JSON by the canonical field type (docs/evaluation/canonical-schema.json,
// generated into virtual:field-types): number for money and counts; string for identifiers and text (a digit-only claim
// number stays a string); boolean for flags; "yyyy-MM-dd" for dates; { code, codeSystem } for coded fields (*.code).
// The reviewer's formatted text is parsed here; text that does not parse blocks the save with the message shown inline.

export type FieldKind = "money" | "count" | "string" | "boolean" | "date" | "enum" | "code";
export interface FieldType {
  kind: FieldKind;
  enum?: string[];
  codeSystem?: string;
}
export type { OverrideValue };

const TYPES = fieldTypes as Record<string, FieldType>;

/** "policyPeriods[0].claims[3].medical.paid" → "policyPeriods[].claims[].medical.paid" (the schema's path). */
export const schemaPath = (path: string) => path.replace(/\[\d+\]/g, "[]");

/** The canonical type of a field path; a path the schema does not know is text. */
export function fieldTypeOf(path: string): FieldType {
  return TYPES[schemaPath(path)] ?? { kind: "string" };
}

export const isNumericKind = (kind: FieldKind) => kind === "money" || kind === "count";

/** Labels the ledger shows for enum values (cellText in document/model.ts), accepted back as input. */
const ENUM_LABELS: Record<string, string> = { "medical only": "medicalOnly", "lost time": "indemnity" };

const MONEY = /^-?\$?\d{1,3}(,?\d{3})*(\.\d{1,2})?$/;
const COUNT = /^\d{1,3}(,?\d{3})*$/;

interface CodeTable {
  codeWidth: number;
  codes: { code: string; description: string }[];
}
const TABLES = (codeSets as unknown as { tables: Record<string, CodeTable> }).tables;

function isoDateOf(text: string): string | null {
  let y: number, m: number, d: number;
  const iso = /^(\d{4})-(\d{1,2})-(\d{1,2})$/.exec(text);
  const us = /^(\d{1,2})\/(\d{1,2})\/(\d{4})$/.exec(text);
  if (iso) [y, m, d] = [Number(iso[1]), Number(iso[2]), Number(iso[3])];
  else if (us) [y, m, d] = [Number(us[3]), Number(us[1]), Number(us[2])];
  else return null;
  // Calendar check without the clock or the machine's time zone (UTC arithmetic only).
  const t = new Date(Date.UTC(y, m - 1, d));
  if (t.getUTCFullYear() !== y || t.getUTCMonth() !== m - 1 || t.getUTCDate() !== d) return null;
  return `${String(y).padStart(4, "0")}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`;
}

export type Parsed = { ok: true; value: OverrideValue } | { ok: false; message: string };

/** Parses the reviewer's text into the field's JSON type. */
export function parseOverrideValue(path: string, raw: string): Parsed {
  const text = raw.trim().replace(/[−]/g, "-");
  if (!text) return { ok: false, message: "Enter the corrected value." };
  const type = fieldTypeOf(path);
  switch (type.kind) {
    case "money":
      return MONEY.test(text) ? { ok: true, value: Number(text.replace(/[$,]/g, "")) } : { ok: false, message: "Enter an amount like 9,200.00." };
    case "count":
      return COUNT.test(text) ? { ok: true, value: Number(text.replace(/,/g, "")) } : { ok: false, message: "Enter a whole number like 12." };
    case "boolean": {
      const t = text.toLowerCase();
      if (["yes", "y", "true"].includes(t)) return { ok: true, value: true };
      if (["no", "n", "false"].includes(t)) return { ok: true, value: false };
      return { ok: false, message: "Enter yes or no." };
    }
    case "date": {
      const iso = isoDateOf(text);
      return iso ? { ok: true, value: iso } : { ok: false, message: "Enter a date like 2024-03-17 or 03/17/2024." };
    }
    case "enum": {
      const t = text.toLowerCase();
      const hit = (type.enum ?? []).find((v) => v.toLowerCase() === t) ?? (ENUM_LABELS[t] && type.enum?.includes(ENUM_LABELS[t]) ? ENUM_LABELS[t] : undefined);
      return hit ? { ok: true, value: hit } : { ok: false, message: `Enter one of: ${(type.enum ?? []).join(", ")}.` };
    }
    case "code": {
      const system = type.codeSystem!;
      const table = TABLES[system];
      const m = /^(\d{1,4})\b/.exec(text);
      const code = m && table ? m[1].padStart(table.codeWidth, "0") : null;
      return code && table!.codes.some((c) => c.code === code)
        ? { ok: true, value: { code, codeSystem: system } }
        : { ok: false, message: `Enter a ${system} code (for example ${table?.codes[0]?.code ?? "01"}).` };
    }
    default:
      return { ok: true, value: text };
  }
}

/** The value as the decision preview shows it. */
export function overrideText(v: OverrideValue): string {
  if (typeof v === "object") return `${v.code} (${v.codeSystem})`;
  if (typeof v === "boolean") return v ? "Yes" : "No";
  return String(v);
}

/**
 * The as-reported value of an audited `oldValue` string, typed by the field (never by "looks like a number": a digit-only
 * claim number stays a string; brief 30 item 3(b) on the client).
 */
export function typedOldValue(path: string, old: string | null | undefined): unknown {
  if (old === null || old === undefined) return null;
  const kind = fieldTypeOf(path).kind;
  if (isNumericKind(kind)) {
    const n = Number(old.replace(/,/g, ""));
    return Number.isNaN(n) ? old : n;
  }
  if (kind === "boolean") return old === "true" ? true : old === "false" ? false : old;
  return old;
}
