import { useState } from "react";
import type { Story } from "@ladle/react";
import { StatusBadge, RoutingBadge, type Status } from "../status.tsx";
import { CostLine } from "../cost.tsx";
import { ConfidenceMeter } from "../meter.tsx";
import { KpiStrip, type Kpi } from "../kpi.tsx";
import { ProcessingStepper, type Step } from "../stepper.tsx";
import { ManifestList, type ManifestRow } from "../manifest.tsx";
import { RuleOutcomeList, type RuleRow } from "../rules.tsx";
import { ScenarioPanel, BASELINE_CONTROLS, type ScenarioControls } from "../scenario.tsx";
import { AuditTrail, type AuditRow } from "../audit.tsx";
import { Citation, DensityToggle, StateMessage, ThemeToggle } from "../chrome.tsx";

// Stories for the small components of design-brief §4, each in its states. Data mirrors the API contracts.

export const Badges: Story = () => (
  <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
    {(["verified", "review", "fail", "learned", "kept", "unsupported", "pending"] as Status[]).map((s) => <StatusBadge key={s} status={s} />)}
  </div>
);
Badges.storyName = "StatusBadge — every status";

export const Routing: Story = () => (
  <div style={{ display: "grid", gap: 8, justifyItems: "start" }}>
    <RoutingBadge routing={{ tier: "ReplayAccepted", badge: "format memory hit: Synthetic Mutual Insurance Co. synthetic-mutual-pdf v5 · 0 LLM calls", formatStatus: "active" }} />
    <RoutingBadge routing={{ tier: "ReplayAccepted", badge: "format learned · shadow 2/3: Fictional Casualty Group fictional-casualty-group-pdf v1 · 0 LLM calls", formatStatus: "shadow" }} />
    <RoutingBadge routing={{ tier: "Learned", badge: "new format → learn path", modelCalls: 1 }} />
    <RoutingBadge routing={{ tier: "Review", badge: "needs review: Synthetic Mutual Insurance Co. synthetic-mutual-pdf v5" }} />
    <RoutingBadge routing={{ tier: "ReplayAccepted", formatRef: "example-carrier-pdf@2", modelCalls: 0 }} />
  </div>
);
Routing.storyName = "RoutingBadge — tiers, shadow, fallback text";

export const Cost: Story = () => (
  <div style={{ display: "grid", gap: 8, justifyItems: "start" }}>
    <CostLine usd={0.034} pages={6} modelCalls={0} />
    <CostLine usd={0.0075} pages={1} modelCalls={0} lines={[{ service: "ContentUnderstanding", meter: "documentPagesStandard", quantity: 1, costUsd: 0.0075 }]} label="Document cost" />
    <CostLine usd={null} />
  </div>
);
Cost.storyName = "CostLine — plain, with meter breakdown, unknown";

export const Confidence: Story = () => (
  <div style={{ display: "grid", gap: 8 }}>
    {[0.99, 0.91, 0.82, 0.71, 0.55, null].map((v, i) => <ConfidenceMeter key={i} value={v} />)}
  </div>
);
Confidence.storyName = "ConfidenceMeter — thresholds 0.9 / 0.7";

const kpis: Kpi[] = [
  { name: "claim_count", label: "Claim count", unit: "count", value: 318, scenario: 309 },
  { name: "incurred_total", label: "Total incurred", unit: "money", value: 4_812_233.12, scenario: 5_102_800 },
  { name: "frequency_per_100_payroll", label: "Frequency per $100 payroll", unit: "rate", value: null, notMeasurable: "no payroll in the submission context" },
  { name: "paid_to_incurred", label: "Paid-to-incurred", unit: "ratio", value: 0.734, scenario: 0.701 },
  { name: "reporting_lag_median_days", label: "Median reporting lag", unit: "days", value: 4.5, scenario: 4.5 },
];
export const Kpis: Story<{ scenario: boolean }> = ({ scenario }) => <KpiStrip items={kpis} scenarioActive={scenario} />;
Kpis.args = { scenario: false };
Kpis.storyName = "KpiStrip — baseline / scenario / not measurable";

const steps = (active: number): Step[] =>
  ["Classified", "Layout", "Format match", "Extracted", "Validated", "Ready"].map((label, i) => ({ key: label, label, state: i < active ? "done" : i === active ? "active" : "pending", ms: i < active ? 300 + i * 120 : null }));
export const Stepper: Story = () => (
  <div style={{ display: "grid", gap: 16 }}>
    <ProcessingStepper name="waiting.pdf" steps={steps(-1).map((s) => ({ ...s, state: "pending" }))} />
    <ProcessingStepper name="matching.pdf" steps={steps(2)} />
    <ProcessingStepper name="done.pdf" steps={steps(6)} />
  </div>
);
Stepper.storyName = "ProcessingStepper — waiting, running, ready";

const manifest: ManifestRow[] = [
  { documentId: "d1", fileName: "example-carrier-loss-run-2026.pdf", pageRange: "1-6", classification: "LossRun", status: "processed", routingTier: "ReplayAccepted", claimCount: 47 },
  { documentId: "d2", fileName: "pie-loss-run-2024.pdf", pageRange: "1-3", classification: "LossRun", status: "awaiting-review", reason: "Period 2023 subtotal does not match extracted rows (R-10)", claimCount: 12 },
  { documentId: "d3", fileName: "payroll-report-2025.pdf", pageRange: "1-2", classification: "Payroll", status: "kept-not-processed", reason: "Payroll report" },
  { documentId: "d4", fileName: "old-scan.tif", classification: "image/tiff", status: "unsupported", reason: "Image could not be read (0 words recognized)" },
];
export const Manifest: Story<{ applicant: boolean }> = ({ applicant }) => <ManifestList items={manifest} variant={applicant ? "applicant" : "underwriter"} />;
Manifest.args = { applicant: false };
Manifest.storyName = "ManifestList — underwriter / applicant";

const rules: RuleRow[] = [
  { ruleId: "R-10", severity: "hard", status: "fail", message: "Printed medical paid subtotal for policy SYN-WC-1001 does not equal the sum of its claims.", affectedFields: ["policyPeriods[0].subtotals.medical.paid"] },
  { ruleId: "R-04", severity: "soft", status: "fail", message: "Closed claim WC-22-0001 still carries an indemnity reserve.", claimId: "WC-22-0001", affectedFields: ["policyPeriods[0].claims[0].indemnity.reserve"] },
  { ruleId: "R-01", severity: "hard", status: "pass", message: "Paid does not exceed incurred", affectedFields: ["policyPeriods[*].claims[*].total"] },
  { ruleId: "R-20", severity: "hard", status: "notInformative", message: "Total equals components", affectedFields: ["policyPeriods[*].claims[*].total.incurred"] },
];
export const Rules: Story = () => <RuleOutcomeList outcomes={rules} onOpenInSource={() => undefined} onSendToReview={() => undefined} />;
Rules.storyName = "RuleOutcomeList — failed, review, passed, not informative";

export const Scenario: Story = () => {
  const [v, setV] = useState<ScenarioControls>(BASELINE_CONTROLS);
  return <div style={{ maxWidth: 320 }}><ScenarioPanel value={v} onChange={setV} onReset={() => setV(BASELINE_CONTROLS)} /></div>;
};
Scenario.storyName = "ScenarioPanel — interactive";

const audit: AuditRow[] = [
  { id: 2, at: "2026-10-21T19:52:40Z", user: "m.okafor", role: "Underwriter", action: "review.override", revision: 2, fieldPath: "policyPeriods[0].claims[3].claimNumber", oldValue: "WC-22-0156", newValue: "WC-22-0157", rationale: "Carrier typo; confirmed against claim detail page 4", ledgerTransactionId: 1842 },
  { id: 1, at: "2026-10-21T19:40:02Z", user: "system", role: "system", action: "extraction.persist", revision: 1, rationale: "47 claim(s); routing ReplayAccepted", ledgerTransactionId: 1790 },
];
export const Audit: Story<{ mode: "ledger" | "blob" | "empty" }> = ({ mode }) =>
  <AuditTrail entries={mode === "empty" ? [] : audit} protection={mode === "blob" ? "append-only-blob" : "ledger"} lastDigestAt="2026-10-22T23:00:00Z" />;
Audit.args = { mode: "ledger" };
Audit.argTypes = { mode: { options: ["ledger", "blob", "empty"], control: { type: "radio" } } };
Audit.storyName = "AuditTrail — ledger / Blob / empty";

export const Chrome: Story = () => {
  const [theme, setTheme] = useState<"light" | "dark">("light");
  const [density, setDensity] = useState<"dense" | "comfortable">("dense");
  return (
    <div style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap" }}>
      <ThemeToggle theme={theme} onChange={setTheme} />
      <DensityToggle density={density} onChange={setDensity} />
      <Citation label="loss-run-2024.pdf · p.4" onOpen={() => undefined} />
      <Citation label="claims.xlsx · Sheet1!F12" onOpen={() => undefined} />
    </div>
  );
};
Chrome.storyName = "ThemeToggle, DensityToggle, Citation";

export const States: Story = () => (
  <div style={{ display: "grid", gap: 8 }}>
    <StateMessage kind="loading">Loading the document…</StateMessage>
    <StateMessage kind="empty">No documents are waiting for review.</StateMessage>
    <StateMessage kind="error">This document could not be loaded (HTTP 404).</StateMessage>
  </div>
);
States.storyName = "Loading / empty / error";
