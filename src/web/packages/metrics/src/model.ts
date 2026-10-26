// Engine input and parameters — the twin of src/LossRun.Analytics/Model.cs. Amounts are integer cents, dates ISO strings.

export interface AnalyticsClaim {
  id: string;
  claimNumber: string | null;
  policyNumber: string | null;
  policyYear: number | null;
  dateOfInjury: string | null;
  dateReported: string | null;
  dateClosed: string | null;
  status: string | null;
  claimType: string | null;
  natureCode: string | null;
  causeCode: string | null;
  bodyPartCode: string | null;
  typeOfLossCode: string | null;
  description: string | null;
  litigation: boolean | null;
  cumulativeTrauma: boolean | null;
  jurisdiction: string | null;
  lineOfBusiness: string | null;
  indemnityPaidCents: number;
  indemnityReserveCents: number;
  medicalPaidCents: number;
  medicalReserveCents: number;
  expensePaidCents: number;
  expenseReserveCents: number;
  recoveryCents: number | null;
  /** The claimant's last day worked when the carrier prints it; else context.lastDayWorked applies. */
  lastDayWorked: string | null;
  /** Recorded for reporting only, without financials (census-only status, incident indicator); absent when not printed. */
  reportOnly?: boolean | null;
}

export interface AnalyticsContext {
  quoteId: string | null;
  valuationDate: string | null;
  payrollCentsByYear: Record<string, number> | null;
  fteByYear: Record<string, number> | null;
  lastDayWorked: string | null;
}

export interface OverrideRef {
  id: string;
  claimId: string;
  field: string;
  /** Brief 32 item 5: the as-reported view could not restore this override's stored old value (it shows the current one). */
  unrevertable?: boolean | null;
}

export interface AnalyticsInput {
  context: AnalyticsContext;
  claims: AnalyticsClaim[];
  overrides: OverrideRef[];
}

/** What-if parameters (catalog.yaml scenario). Money in cents; factors in basis points. */
export interface Scenario {
  excludeClaimIds: string[];
  includeNonCA: boolean;
  includeCT: boolean;
  excludeCatastrophicAboveCents: number | null;
  trendFactorBpsByYear: Record<string, number>;
  reserveFactorBps: number;
  capPerClaimCents: number | null;
}

export const baseline = (): Scenario => ({
  excludeClaimIds: [],
  includeNonCA: true,
  includeCT: true,
  excludeCatastrophicAboveCents: null,
  trendFactorBpsByYear: {},
  reserveFactorBps: 0,
  capPerClaimCents: null,
});

export function isBaseline(s: Scenario): boolean {
  return s.excludeClaimIds.length === 0 && s.includeNonCA && s.includeCT && s.excludeCatastrophicAboveCents === null
    && Object.keys(s.trendFactorBpsByYear).length === 0 && s.reserveFactorBps === 0 && s.capPerClaimCents === null;
}

export interface CatalogMetric {
  name: string;
  label: string;
  type: string;
  unit: "count" | "money" | "ratio" | "rate" | "days";
  breakdown?: string;
  params?: Record<string, unknown>;
  tolerance: Record<string, unknown>;
  inputs: { field: string; transformation: string }[];
}

export interface Catalog {
  catalogVersion: string;
  metrics: CatalogMetric[];
}

export interface MetricResult {
  name: string;
  unit: string;
  scale: number;
  value: number | null;
  breakdown: [string, number | null][] | null;
  /** null = every included claim. */
  contributors: string[] | null;
  overrides: string[];
  /** Why value is null: a missing submission or carrier input, or no contributing claims. */
  notMeasurable?: string;
}

export interface PeriodAggregate {
  policyNumber: string | null;
  policyYear: number | null;
  claimCount: number;
  openCount: number;
  indemnity: [number, number, number];
  medical: [number, number, number];
  expense: [number, number, number];
  total: [number, number, number];
}

export interface FeatureSnapshot {
  catalogVersion: string;
  view: string;
  quoteId: string | null;
  valuationDate: string | null;
  scenario: Scenario;
  inputsHash: string;
  inputClaims: number;
  nonWcExcluded: number;
  scenarioExcluded: number;
  included: number;
  metrics: MetricResult[];
  perPeriod: PeriodAggregate[];
  perClaimFlags: { claimId: string; flags: string[] }[];
}

export function scaleOf(unit: string): number {
  return unit === "money" ? 2 : unit === "ratio" || unit === "rate" ? 6 : unit === "days" ? 1 : 0;
}
