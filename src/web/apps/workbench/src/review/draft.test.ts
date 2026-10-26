import { beforeEach, describe, expect, it } from "vitest";
import { EMPTY_DRAFT, draftPaths, formDefaults, isDirty, planSave, useReviewDraft, type FormDraft } from "./draft.ts";

// Brief 24 step 4: the reviewDraft slice (review plan 2026-10-25 §3 acceptance tests).

const PAID = "policyPeriods[0].claims[1].medical.paid";
const NAME = "policyPeriods[0].claims[0].claimantName";
const form = (newValue: string, rationale: string, original = "1,200.00"): FormDraft => ({ newValue, rationale, original });

describe("reviewDraft", () => {
  beforeEach(() => useReviewDraft.setState({ drafts: {} }));
  const draft = () => useReviewDraft.getState().drafts["doc-1"];

  it("is dirty with typed text in any cell's form, even after selecting another cell", () => {
    expect(isDirty(undefined)).toBe(false);
    useReviewDraft.getState().setForm("doc-1", PAID, form("1,200.00", "")); // opened, nothing typed
    expect(isDirty(draft())).toBe(false);
    useReviewDraft.getState().setForm("doc-1", PAID, form("1,250.00", ""));
    useReviewDraft.getState().setForm("doc-1", NAME, form("Synthetic Claimant A", "", "Synthetic Claimant A"));
    expect(isDirty(draft())).toBe(true);
    expect([...draftPaths(draft())]).toEqual([PAID]);
  });

  it("staging a value replaces its form draft and keeps one entry per field", () => {
    const s = useReviewDraft.getState();
    s.setForm("doc-1", PAID, form("1,250.00", "carrier letter"));
    s.stage("doc-1", { fieldPath: PAID, newValue: 1250, rationale: "carrier letter dated 3 March", oldValue: "1,200.00" });
    s.stage("doc-1", { fieldPath: PAID, newValue: 1260, rationale: "carrier letter dated 4 March", oldValue: "1,200.00" });
    expect(draft()!.staged.map((o) => o.newValue)).toEqual([1260]);
    expect(draftPaths(draft()).size).toBe(0);
    expect(isDirty(draft())).toBe(true);
  });

  it("reset clears the staged list and every form, and restore undoes it", () => {
    const s = useReviewDraft.getState();
    s.stage("doc-1", { fieldPath: PAID, newValue: 1250, rationale: "carrier letter dated 3 March", oldValue: "1,200.00" });
    s.setForm("doc-1", NAME, form("Synthetic Claimant One", "", "Synthetic Claimant A"));
    const previous = s.reset("doc-1");
    expect(draft()).toEqual(EMPTY_DRAFT);
    expect(isDirty(draft())).toBe(false);
    s.restore("doc-1", previous);
    expect(isDirty(draft())).toBe(true);
    expect(draft()!.staged).toHaveLength(1);
  });

  it("after Reset the open form re-opens on the extracted value, after Undo on the restored text (no stale draft)", () => {
    // Brief 30 item 23: the screen keys the open cell's form by epochs[id]; a form keyed by path alone kept its typed text.
    const s = useReviewDraft.getState();
    const epoch = () => useReviewDraft.getState().epochs["doc-1"] ?? 0;
    s.setForm("doc-1", PAID, form("99.00", "typed then reset"));
    const before = epoch();
    expect(formDefaults(draft(), PAID, "1,200.00")).toEqual({ newValue: "99.00", rationale: "typed then reset" });

    const previous = s.reset("doc-1");
    expect(epoch()).toBe(before + 1);
    expect(formDefaults(draft(), PAID, "1,200.00")).toEqual({ newValue: "1,200.00", rationale: "" });

    s.restore("doc-1", previous);
    expect(epoch()).toBe(before + 2);
    expect(formDefaults(draft(), PAID, "1,200.00").newValue).toBe("99.00");

    s.clear("doc-1");
    expect(epoch()).toBe(before + 3);
    // Typing and staging do not re-key the form (it would lose focus mid-edit).
    s.setForm("doc-1", PAID, form("98.00", ""));
    s.stage("doc-1", { fieldPath: PAID, newValue: 98, rationale: "medical bill received", oldValue: "1,200.00" });
    expect(epoch()).toBe(before + 3);
  });

  it("save auto-stages a valid unsubmitted form", () => {
    const s = useReviewDraft.getState();
    s.stage("doc-1", { fieldPath: NAME, newValue: "Synthetic Claimant One", rationale: "name per carrier letter", oldValue: "Synthetic Claimant A" });
    s.setForm("doc-1", PAID, form("1,250.00", "medical bill received"));
    const plan = planSave(draft()!);
    expect(plan).toEqual({
      ok: true,
      overrides: [
        { fieldPath: NAME, newValue: "Synthetic Claimant One", rationale: "name per carrier letter", oldValue: "Synthetic Claimant A" },
        { fieldPath: PAID, newValue: 1250, rationale: "medical bill received", oldValue: "1,200.00" },
      ],
    });
  });

  it("an invalid draft blocks the save and names the field to focus", () => {
    useReviewDraft.getState().setForm("doc-1", PAID, form("twelve hundred", "medical bill received"));
    expect(planSave(draft()!)).toEqual({ ok: false, invalidPath: PAID, message: "Enter an amount like 9,200.00." });
    useReviewDraft.getState().setForm("doc-1", PAID, form("1,250.00", "short"));
    expect(planSave(draft()!)).toMatchObject({ ok: false, invalidPath: PAID });
  });

  it("drafts are kept per review", () => {
    useReviewDraft.getState().setForm("doc-1", PAID, form("1,250.00", ""));
    expect(isDirty(useReviewDraft.getState().drafts["doc-2"])).toBe(false);
    useReviewDraft.getState().clear("doc-1");
    expect(draft()).toBeUndefined();
  });
});

describe("brief 34 A3: the fill-down toggle", () => {
  it("is offered only when a review reason asks the fill-down question", async () => {
    const { offersFillDown } = await import("./draft.ts");
    expect(offersFillDown({ reasons: ["policy cell blank on 2 row(s), each attached to the period above: a fill-down export, or a dropped cell? …"] })).toBe(true);
    expect(offersFillDown({ reasons: ["4 hard rule failure(s) with every header mapped"] })).toBe(false);
    expect(offersFillDown({ reasons: [] })).toBe(false);
    expect(offersFillDown(undefined)).toBe(false);
  });

  it("sends a typed boolean, and never with a reject", async () => {
    const { decisionRequest } = await import("./draft.ts");
    const base = { staged: [], confirmFormat: true, offerFormat: false, promote: false, note: "" };
    const confirm = JSON.parse(JSON.stringify(decisionRequest({ ...base, kind: "confirm", fillDown: true })));
    expect(confirm.formatChanges).toEqual({ "periods.fillDown": true });
    expect(typeof confirm.formatChanges["periods.fillDown"]).toBe("boolean");
    expect(decisionRequest({ ...base, kind: "confirm", fillDown: false }).formatChanges).toBeUndefined();
    expect(decisionRequest({ ...base, kind: "reject", note: "not this insured", fillDown: true }).formatChanges).toBeUndefined();
  });
});

describe("brief 35 Answers 2: the same-carrier toggle", () => {
  const reason = 'carrier name on the page differs from the format\'s: "Synthetic Property Casualty Co of America" differs from the format\'s "Synthetic Mutual Insurance Co." (synthetic-mutual-pdf@5)';

  it("reads both names from the review reason, and nothing from another reason", async () => {
    const { carrierNameQuestion } = await import("./draft.ts");
    expect(carrierNameQuestion({ reasons: ["4 hard rule failure(s) with every header mapped", reason] }))
      .toEqual({ printed: "Synthetic Property Casualty Co of America", known: "Synthetic Mutual Insurance Co." });
    expect(carrierNameQuestion({ reasons: ["4 hard rule failure(s) with every header mapped"] })).toBeNull();
    expect(carrierNameQuestion(undefined)).toBeNull();
  });

  it("sends the printed name as a string, with fill-down when both are ticked, and never with a reject", async () => {
    const { decisionRequest } = await import("./draft.ts");
    const base = { staged: [], confirmFormat: false, offerFormat: false, promote: false, note: "" };
    expect(decisionRequest({ ...base, kind: "accept", addCarrierName: "Synthetic Property Casualty Co of America" }).formatChanges)
      .toEqual({ "carrierNames.add": "Synthetic Property Casualty Co of America" });
    expect(decisionRequest({ ...base, kind: "accept", fillDown: true, addCarrierName: "X Indemnity Co" }).formatChanges)
      .toEqual({ "periods.fillDown": true, "carrierNames.add": "X Indemnity Co" });
    expect(decisionRequest({ ...base, kind: "accept", addCarrierName: "   " }).formatChanges).toBeUndefined();
    expect(decisionRequest({ ...base, kind: "reject", note: "another company", addCarrierName: "X Indemnity Co" }).formatChanges).toBeUndefined();
  });
});
