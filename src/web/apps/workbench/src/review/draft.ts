import { create } from "zustand";
import { z } from "zod";
import type { ReviewDecisionRequest } from "../api/types.ts";
import { fieldTypeOf, isNumericKind, parseOverrideValue, type OverrideValue } from "./fieldTypes.ts";

// Brief 24 step 4 (review plan 2026-10-25 §3): the review screen's draft, per review id, outside the form components so
// typed text survives selecting another cell. dirty = something staged, or a form whose text differs from the extracted value.

/** A correction added to the decision. */
export interface StagedOverride {
  fieldPath: string;
  /** Typed by the canonical field type (brief 30 item 3), never the formatted input text. */
  newValue: OverrideValue;
  rationale: string;
  oldValue: string;
}

/** Typed text in a cell's form that was not added to the decision yet; `original` is the value the form opened with. */
export interface FormDraft {
  newValue: string;
  rationale: string;
  original: string;
}

export interface ReviewDraft {
  staged: StagedOverride[];
  forms: Record<string, FormDraft>;
}

export const EMPTY_DRAFT: ReviewDraft = { staged: [], forms: {} };

/** Money and counts (from the canonical schema: recoveries and net incurred included, not only paid/reserve/incurred). */
export const isNumericPath = (path: string) => isNumericKind(fieldTypeOf(path).kind);

/** The form's rules for a field: the new value must parse as the field's canonical type (amounts look like 9,200.00). */
export function overrideSchema(path: string) {
  return z.object({
    newValue: z.string().superRefine((v, ctx) => {
      const parsed = parseOverrideValue(path, v);
      if (!parsed.ok) ctx.addIssue({ code: z.ZodIssueCode.custom, message: parsed.message });
    }),
    rationale: z.string().trim().min(8, "Say why (at least 8 characters); it is stored in the audit trail."),
  });
}

export const formIsDirty = (f: FormDraft) => f.newValue.trim() !== f.original.trim() || f.rationale.trim() !== "";

export const isDirty = (d: ReviewDraft | undefined) => !!d && (d.staged.length > 0 || Object.values(d.forms).some(formIsDirty));

/** Paths with typed text not yet added to the decision (the cells that show a draft chip). */
export const draftPaths = (d: ReviewDraft | undefined) => new Set(Object.entries(d?.forms ?? {}).filter(([, f]) => formIsDirty(f)).map(([p]) => p));

/** A validated form as a staged override; throws on text that does not parse (the form's schema rejects it first). */
export function toOverride(path: string, f: Pick<FormDraft, "newValue" | "rationale">, oldValue: string): StagedOverride {
  const parsed = parseOverrideValue(path, f.newValue);
  if (!parsed.ok) throw new Error(`${path}: ${parsed.message}`);
  return { fieldPath: path, newValue: parsed.value, rationale: f.rationale.trim(), oldValue };
}

export type SavePlan = { ok: true; overrides: StagedOverride[] } | { ok: false; invalidPath: string; message: string };

/**
 * "Save changes and approve": the staged corrections plus every unsubmitted form that validates (auto-staged); the first
 * form that does not validate blocks the save and is named so the screen can focus it.
 */
export function planSave(d: ReviewDraft): SavePlan {
  const overrides = [...d.staged];
  for (const [path, f] of Object.entries(d.forms).filter(([, f]) => formIsDirty(f)).sort(([a], [b]) => a.localeCompare(b))) {
    const parsed = overrideSchema(path).safeParse({ newValue: f.newValue, rationale: f.rationale });
    if (!parsed.success) return { ok: false, invalidPath: path, message: parsed.error.issues[0]?.message ?? "This value is not valid." };
    const next = toOverride(path, parsed.data, f.original === "" ? "—" : f.original);
    const at = overrides.findIndex((o) => o.fieldPath === path);
    if (at >= 0) overrides[at] = next;
    else overrides.push(next);
  }
  return { ok: true, overrides };
}

export type DecisionKind = "save" | "accept" | "confirm" | "approve-only" | "reject";

/**
 * The body of POST /reviews/{id}/decision for the button the reviewer clicked. Only "Save changes and approve" (kind
 * "save") carries overrides; nothing is sent before a button is clicked ("Add to decision" stages locally).
 */
/** Brief 34 A3: how the API's fill-down review reason starts (LossRun.Formats.UnnormalizedReasonText.FillDownReasonPrefix). */
export const FILL_DOWN_REASON_PREFIX = "policy cell blank on ";

/** Brief 34 A3: the review asks whether the carrier's export fills the policy down; only then is the toggle offered. */
export function offersFillDown(item: { reasons?: string[] | null } | undefined): boolean {
  return (item?.reasons ?? []).some((r) => r.startsWith(FILL_DOWN_REASON_PREFIX));
}

/** Brief 35 Answers 2: how the API's review reason starts when the page names a carrier the format does not know (FormatRouter.CarrierOnPageReasonPrefix). */
export const CARRIER_NAME_REASON_PREFIX = "carrier name on the page differs from the format's: ";

/**
 * Brief 35 Answers 2: the review asks whether the printed carrier name is another name of the format's carrier. Returns both names from the
 * reason ("carrier name on the page differs from the format's: \"<printed>\" differs from the format's \"<known>\" (<ref>)"), or null.
 */
export function carrierNameQuestion(item: { reasons?: string[] | null } | undefined): { printed: string; known: string } | null {
  const reason = (item?.reasons ?? []).find((r) => r.startsWith(CARRIER_NAME_REASON_PREFIX));
  const match = reason?.match(/^[^"]*"([^"]+)" differs from the format's "([^"]+)"/);
  return match ? { printed: match[1], known: match[2] } : null;
}

export function decisionRequest(o: {
  kind: DecisionKind;
  staged: StagedOverride[];
  confirmFormat: boolean;
  offerFormat: boolean;
  promote: boolean;
  note: string;
  aliases?: ReviewDecisionRequest["aliases"];
  /** Brief 34 A3: the reviewer ticked "this carrier's export fills the policy down". */
  fillDown?: boolean;
  /** Brief 35 Answers 2: the reviewer confirmed this printed name is another name of the format's carrier. */
  addCarrierName?: string;
}): ReviewDecisionRequest {
  const action = o.kind === "save" ? "override" : o.kind === "reject" ? "reject" : "accept";
  // promoteToFormat: confirm-format items promote unless "Approve document only" (or saved corrections: the learned
  // format misread values, so it stays in shadow); document-review items only when the checkbox is ticked. Never on reject.
  const promoteToFormat = action === "reject" ? undefined : o.confirmFormat ? o.kind === "confirm" : o.offerFormat ? o.promote : undefined;
  return {
    action,
    overrides: action === "override" ? o.staged.map(({ fieldPath, newValue, rationale }) => ({ fieldPath, newValue, rationale })) : undefined,
    promoteToFormat,
    rationale: o.note.trim() || undefined,
    aliases: o.kind === "confirm" ? o.aliases : undefined,
    // Typed values, the changes the API allows; never with a reject (the document is not kept).
    formatChanges: formatChanges(o.fillDown, o.addCarrierName, action),
  };
}

/** What a cell's form opens with: its typed draft if any, else the extracted value (after Reset: the extracted value). */
export function formDefaults(d: ReviewDraft | undefined, path: string, original: string): { newValue: string; rationale: string } {
  const saved = d?.forms[path];
  return { newValue: saved?.newValue ?? original, rationale: saved?.rationale ?? "" };
}

interface DraftState {
  drafts: Record<string, ReviewDraft>;
  /**
   * Bumped per review when the draft is replaced from outside the forms (reset, undo, clear). The screen keys the open
   * cell's form by it, so the form re-opens on the store's state instead of keeping stale typed text (brief 30 item 23).
   */
  epochs: Record<string, number>;
  stage: (id: string, o: StagedOverride) => void;
  unstage: (id: string, path: string) => void;
  /** Records typed text (null: the form is back to its original value, or was added to the decision). */
  setForm: (id: string, path: string, f: FormDraft | null) => void;
  /** Clears the staged list and every form; returns what was cleared (for Undo). */
  reset: (id: string) => ReviewDraft;
  restore: (id: string, d: ReviewDraft) => void;
  clear: (id: string) => void;
}

export const useReviewDraft = create<DraftState>((set, get) => {
  const update = (id: string, change: (d: ReviewDraft) => ReviewDraft) =>
    set((s) => ({ drafts: { ...s.drafts, [id]: change(s.drafts[id] ?? EMPTY_DRAFT) } }));
  const bump = (id: string) => set((s) => ({ epochs: { ...s.epochs, [id]: (s.epochs[id] ?? 0) + 1 } }));
  return {
    drafts: {},
    epochs: {},
    stage: (id, o) =>
      update(id, (d) => {
        const forms = { ...d.forms };
        delete forms[o.fieldPath];
        return { staged: [...d.staged.filter((x) => x.fieldPath !== o.fieldPath), o], forms };
      }),
    unstage: (id, path) => update(id, (d) => ({ ...d, staged: d.staged.filter((x) => x.fieldPath !== path) })),
    setForm: (id, path, f) =>
      update(id, (d) => {
        const forms = { ...d.forms };
        if (f && formIsDirty(f)) forms[path] = f;
        else delete forms[path];
        return { ...d, forms };
      }),
    reset: (id) => {
      const previous = get().drafts[id] ?? EMPTY_DRAFT;
      update(id, () => EMPTY_DRAFT);
      bump(id);
      return previous;
    },
    restore: (id, d) => {
      update(id, () => d);
      bump(id);
    },
    clear: (id) => {
      set((s) => {
        const drafts = { ...s.drafts };
        delete drafts[id];
        return { drafts };
      });
      bump(id);
    },
  };
});

function formatChanges(fillDown: boolean | undefined, addCarrierName: string | undefined, action: string): ReviewDecisionRequest["formatChanges"] {
  if (action === "reject") return undefined;
  const changes: Record<string, boolean | string> = {};
  if (fillDown) changes["periods.fillDown"] = true;
  if (addCarrierName?.trim()) changes["carrierNames.add"] = addCarrierName.trim();
  return Object.keys(changes).length > 0 ? changes : undefined;
}
