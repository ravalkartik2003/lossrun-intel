import { describe, expect, it } from "vitest";
import { fieldLabel, reasonLabel } from "./readiness.ts";

describe("readiness labels (brief 24 step 2)", () => {
  it("names a claim field by policy and claim, one-based", () => {
    expect(fieldLabel("policyPeriods[0].claims[2].total.incurred")).toBe("Policy 1, claim 3 · total.incurred");
    expect(fieldLabel("policyPeriods[1].effectiveDate")).toBe("Policy 2 · effectiveDate");
    expect(fieldLabel("valuationDate")).toBe("valuationDate");
  });

  it("explains R-33, review states and failing rules", () => {
    expect(reasonLabel("R-33")).toBe("required and missing");
    expect(reasonLabel("R-10")).toBe("rule R-10 still fails");
    expect(reasonLabel("review-pending")).toBe("waiting for review");
    expect(reasonLabel(null)).toBe("unresolved");
  });
});
