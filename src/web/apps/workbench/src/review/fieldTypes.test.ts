import { beforeEach, describe, expect, it } from "vitest";
import { fieldTypeOf, parseOverrideValue } from "./fieldTypes.ts";
import { decisionRequest, overrideSchema, planSave, useReviewDraft } from "./draft.ts";

// Brief 30 item 3(a): the override newValue is typed JSON by the canonical field type (docs/evaluation/canonical-schema.json).

const C = "policyPeriods[0].claims[2]";
const PAID = `${C}.medical.paid`;
const RECOVERY = `${C}.recoverySubrogation`;
const CLAIM_NO = `${C}.claimNumber`;
const DOI = `${C}.dateOfInjury`;
const LITIGATED = `${C}.litigationFlag`;
const NATURE = `${C}.natureOfInjury.code`;
const COUNT = "policyPeriods[0].subtotals.claimCount";
const STATUS = `${C}.status`;

describe("canonical field types", () => {
  it("come from the schema, indices ignored", () => {
    expect(fieldTypeOf(PAID).kind).toBe("money");
    expect(fieldTypeOf(RECOVERY).kind).toBe("money");
    expect(fieldTypeOf("reportTotals.recoveries.sif").kind).toBe("money");
    expect(fieldTypeOf(COUNT).kind).toBe("count");
    expect(fieldTypeOf(CLAIM_NO).kind).toBe("string");
    expect(fieldTypeOf("policyPeriods[1].policyNumber").kind).toBe("string");
    expect(fieldTypeOf(DOI).kind).toBe("date");
    expect(fieldTypeOf(LITIGATED).kind).toBe("boolean");
    expect(fieldTypeOf(NATURE)).toEqual({ kind: "code", codeSystem: "WCIO-Nature" });
    expect(fieldTypeOf(`${C}.partOfBody.code`).codeSystem).toBe("WCIO-PartOfBody");
    expect(fieldTypeOf(STATUS).enum).toContain("reopened");
  });
});

describe("parseOverrideValue", () => {
  it.each([
    [PAID, "9,200.00", 9200],
    [PAID, "−1,200.50", -1200.5],
    [RECOVERY, "$350", 350],
    [COUNT, "12", 12],
    [CLAIM_NO, " 0012345 ", "0012345"],
    [DOI, "2024-03-07", "2024-03-07"],
    [DOI, "3/7/2024", "2024-03-07"],
    [LITIGATED, "Yes", true],
    [LITIGATED, "false", false],
    [NATURE, "52", { code: "52", codeSystem: "WCIO-Nature" }],
    [STATUS, "Closed", "closed"],
    [`${C}.claimType`, "Medical only", "medicalOnly"],
  ])("%s ← %j", (path, text, value) => {
    expect(parseOverrideValue(path, text)).toEqual({ ok: true, value });
  });

  it.each([
    [PAID, "twelve hundred"],
    [COUNT, "1.5"],
    [DOI, "2024-02-30"],
    [DOI, "next tuesday"],
    [LITIGATED, "maybe"],
    [NATURE, "STRAIN OR TEAR"],
    [NATURE, "99"],
    [STATUS, "pending"],
  ])("%s rejects %j", (path, text) => {
    const r = parseOverrideValue(path, text);
    expect(r.ok).toBe(false);
    expect(overrideSchema(path).safeParse({ newValue: text, rationale: "per carrier letter" }).success).toBe(false);
  });
});

describe("decision request body", () => {
  beforeEach(() => useReviewDraft.setState({ drafts: {} }));

  it("carries typed values: money → number, digit-only claim number → string, date → ISO, boolean, coded → object", () => {
    const s = useReviewDraft.getState();
    const why = "per the carrier's corrected letter";
    s.setForm("doc-1", PAID, { newValue: "9,200.00", rationale: why, original: "9,300.00" });
    s.setForm("doc-1", CLAIM_NO, { newValue: "0012345", rationale: why, original: "WC-22-0001" });
    s.setForm("doc-1", DOI, { newValue: "03/07/2024", rationale: why, original: "2024-03-08" });
    s.setForm("doc-1", LITIGATED, { newValue: "yes", rationale: why, original: "false" });
    s.setForm("doc-1", NATURE, { newValue: "52", rationale: why, original: "40" });
    const plan = planSave(useReviewDraft.getState().drafts["doc-1"]!);
    if (!plan.ok) throw new Error(plan.message);
    const body = JSON.parse(JSON.stringify(decisionRequest({ kind: "save", staged: plan.overrides, confirmFormat: false, offerFormat: false, promote: false, note: "" })));
    expect(body.action).toBe("override");
    const byPath = Object.fromEntries((body.overrides as { fieldPath: string; newValue: unknown }[]).map((o) => [o.fieldPath, o.newValue]));
    expect(byPath).toEqual({
      [PAID]: 9200,
      [CLAIM_NO]: "0012345",
      [DOI]: "2024-03-07",
      [LITIGATED]: true,
      [NATURE]: { code: "52", codeSystem: "WCIO-Nature" },
    });
    expect(typeof byPath[CLAIM_NO]).toBe("string");
  });

  it("a value that does not parse blocks the save and names the field", () => {
    useReviewDraft.getState().setForm("doc-1", DOI, { newValue: "2024-13-01", rationale: "per the carrier's letter", original: "2024-03-08" });
    const plan = planSave(useReviewDraft.getState().drafts["doc-1"]!);
    expect(plan).toMatchObject({ ok: false, invalidPath: DOI });
  });

  it("only Save changes and approve carries overrides; accept and reject never do", () => {
    const staged = [{ fieldPath: PAID, newValue: 9200, rationale: "per the carrier's letter", oldValue: "9,300.00" }];
    expect(decisionRequest({ kind: "accept", staged, confirmFormat: false, offerFormat: false, promote: false, note: "" }).overrides).toBeUndefined();
    expect(decisionRequest({ kind: "reject", staged, confirmFormat: true, offerFormat: false, promote: true, note: "wrong insured" })).toMatchObject({ action: "reject", promoteToFormat: undefined });
    expect(decisionRequest({ kind: "save", staged, confirmFormat: true, offerFormat: false, promote: false, note: "" }).promoteToFormat).toBe(false);
  });
});
