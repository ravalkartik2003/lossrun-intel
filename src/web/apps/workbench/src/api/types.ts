// API contract types (docs/api/openapi.yaml; DTOs in src/LossRun.Api.Contracts). Only the fields the UI reads.

export interface Money {
  paid?: number | null;
  reserve?: number | null;
  incurred?: number | null;
}

export interface CodedValue {
  code?: string | null;
  text?: string | null;
  codeSystem?: string | null;
}

export interface Claim {
  claimNumber?: string | null;
  claimantName?: string | null;
  dateOfInjury?: string | null;
  dateReported?: string | null;
  dateClosed?: string | null;
  lastDayWorked?: string | null;
  status?: string | null;
  rawStatus?: string | null;
  claimType?: string | null;
  natureOfInjury?: CodedValue | null;
  causeOfInjury?: CodedValue | null;
  partOfBody?: CodedValue | null;
  indemnity?: Money | null;
  medical?: Money | null;
  expense?: Money | null;
  total?: Money | null;
  litigationFlag?: boolean | null;
  cumulativeTrauma?: boolean | null;
  jurisdictionState?: string | null;
  /** ADR 0022: recorded for reporting only, without financials (census-only status, incident indicator). */
  reportOnly?: boolean | null;
  /** ADR 0020 amendment: on a kept row only, why it was kept, not extracted. */
  keptReason?: string | null;
}

export interface Totals {
  claimCount?: number | null;
  openCount?: number | null;
  closedCount?: number | null;
  indemnity?: Money | null;
  medical?: Money | null;
  expense?: Money | null;
  total?: Money | null;
  recoverySubrogation?: number | null;
  /** ADR 0017: recoveries other than subrogation, by kind. */
  recoveries?: { deductible?: number | null; reinsurance?: number | null; sif?: number | null; other?: number | null } | null;
}

export interface PolicyPeriod {
  policyNumber?: string | null;
  effectiveDate?: string | null;
  expirationDate?: string | null;
  /** ADR 0017: policyTerm | fiscalYear | reportYear | accidentYear | calendarYear. */
  periodKind?: string | null;
  periodLabel?: string | null;
  /** ADR 0016: the period prints a "no losses" statement. */
  noLossesStatement?: boolean;
  detailLevel?: string | null;
  claims: Claim[];
  /** ADR 0020 amendment: rows of another line of business, kept, not extracted; never in claims or totals. */
  keptRows?: Claim[] | null;
  subtotals?: Totals | null;
}

export interface LineageRecord {
  documentId?: string;
  page?: number | null;
  polygon?: number[] | null;
  sheet?: string | null;
  cell?: string | null;
  sourceText?: string | null;
  extractor?: string | null;
  formatId?: string | null;
  formatVersion?: string | null;
  confidence?: number | null;
  /** ADR 0018: position of the value in a stacked cell; null for the whole cell. */
  part?: number | null;
  /** Brief 31 Answers 1: a repair applied to the printed text, e.g. "ocrRepair: l→I"; sourceText stays what was printed. */
  repair?: string | null;
}

export interface LossRunDocument {
  documentId?: string;
  carrierName?: string | null;
  insuredName?: string | null;
  valuationDate?: string | null;
  /** Brief 23 Answers 2: printed, or submission when the run prints none (R-32). */
  valuationDateSource?: string | null;
  reportRunDate?: string | null;
  expenseTreatment?: string | null;
  /** ADR 0018: what the printed totals sum (allComponents | indemnityMedical). */
  totalComposition?: string | null;
  /** Brief 19 follow-up: components the run prints no column for, e.g. { medical: { present: false } }. */
  componentPresence?: { indemnity?: { present: boolean } | null; medical?: { present: boolean } | null; expense?: { present: boolean } | null } | null;
  /** ADR 0018 / R-28: a "no losses" statement no policy period could take. */
  unattributedNoLossesStatement?: boolean;
  policyPeriods: PolicyPeriod[];
  reportTotals?: Totals | null;
  lineage?: Record<string, LineageRecord> | null;
}

export interface RuleOutcome {
  ruleId: string;
  severity: string;
  status: string;
  message: string;
  affectedFields: string[];
  claimId?: string | null;
  periodIndex?: number | null;
}

export interface RoutingDecision {
  tier: string;
  reasons?: string[];
  formatRef?: string | null;
  badge?: string | null;
  modelCalls?: number;
  formatStatus?: string | null;
}

export interface CostLine {
  service: string;
  meter: string;
  quantity: number;
  unit?: string;
  unitPrice?: number;
  costUsd: number;
}

export interface DocumentCost {
  path: string;
  llmCalls: number;
  pages: number;
  costUsd: number;
  breakdown: CostLine[];
}

export interface DocumentResult {
  documentId: string;
  uploadId: string;
  revision: number;
  document: LossRunDocument;
  ruleOutcomes: RuleOutcome[];
  routing?: RoutingDecision | null;
  formatRef?: string | null;
  reviewState?: string | null;
  cost?: DocumentCost | null;
}

export interface PageLayout {
  documentId: string;
  page: number;
  unit: string;
  pageInfo: { number: number; width: number; height: number; angle?: number; sheetName?: string | null };
  /** Spreadsheets: the workbook's sheets (a sheet is a layout page). Null for PDFs. */
  sheets?: { number: number; sheetName?: string | null }[] | null;
  cells?: LayoutCell[];
}

export interface LayoutCell {
  page: number;
  sheet?: string | null;
  a1?: string | null;
  row: number;
  column: number;
  rowSpan?: number;
  columnSpan?: number;
  rawValue?: string | null;
  content?: string | null;
}

export interface ManifestItem {
  documentId: string;
  fileName: string;
  parentDocumentId?: string | null;
  pageRange?: string | null;
  classification: string;
  status: string;
  reason?: string | null;
  routingTier?: string | null;
  badge?: string | null;
  formatRef?: string | null;
  formatStatus?: string | null;
  claimCount?: number | null;
  costUsd?: number | null;
  reviewKind?: string | null;
  /** ADR 0020 amendment: the document's rows of another line of business, kept, not extracted. */
  keptRows?: KeptRowItem[] | null;
}

export interface KeptRowItem {
  claimNumber?: string | null;
  policyNumber?: string | null;
  reason: string;
  page?: number | null;
  totalIncurred?: number | null;
}

export interface UploadManifest {
  uploadId: string;
  status: string;
  createdAt: string;
  completedAt?: string | null;
  submission: { quoteId?: string | null; carrierHint?: string | null; uploaderRole: string; uploaderId: string };
  files: { fileName: string; sizeBytes: number }[];
  items: ManifestItem[];
  cost?: { totalUsd: number; llmCalls: number; embeddingCalls: number; pages: number; byDocument: Record<string, number> } | null;
  firstClaimMs?: number | null;
  completedMs?: number | null;
}

export interface UploadProgress {
  step: string;
  documentId?: string | null;
  elapsedMs: number;
  costSoFar: number;
  detail?: string | null;
}

export interface UploadStatusView {
  uploadId: string;
  runtimeStatus: string;
  upload?: UploadProgress | null;
  documents: UploadProgress[];
}

export interface UploadAccepted {
  uploadId: string;
  statusUrl: string;
  eventsUrl: string;
  existing: boolean;
}

export interface ReviewItem {
  documentId: string;
  uploadId: string;
  kind: "document-review" | "confirm-format" | string;
  state: string;
  reasons: string[];
  badge?: string | null;
  formatRef?: string | null;
  openedAt: string;
  decidedAt?: string | null;
  /** A processed document sent back to review (brief 12b); its decision applies at once. */
  reopened?: boolean;
}

/**
 * Brief 30 item 3: typed by the canonical field type — number (money, counts), string (identifiers, text, enums; a
 * digit-only claim number stays a string), boolean (flags), "yyyy-MM-dd" (dates), { code, codeSystem } (*.code fields).
 */
export type OverrideValue = number | string | boolean | { code: string; codeSystem: string };

export interface FieldOverride {
  fieldPath: string;
  newValue: OverrideValue;
  rationale: string;
}

export interface ReviewDecisionRequest {
  action: "accept" | "override" | "reject";
  overrides?: FieldOverride[];
  promoteToFormat?: boolean;
  rationale?: string;
  /** ADR 0019, confirm-format reviews: a decision per learned alias; the ones not listed are accepted with the format. */
  aliases?: AliasDecision[];
  /** Brief 34 A3: changes to the document's format the reviewer confirms; today only { "periods.fillDown": true }. */
  formatChanges?: Record<string, boolean | string>;
}

/** ADR 0019: a reviewer's decision on one learned alias in the confirm-format review. */
export interface AliasDecision {
  header: string;
  canonicalField: string;
  action: "accept" | "edit" | "reject";
  newField?: string;
}

/** One verification of a learned alias: a document whose spec maps it verified (learn published or replay accepted). */
export interface AliasEvidence {
  documentId: string;
  formatRef: string;
  route: "learn" | "replay" | string;
  totalsTie?: boolean | null;
  atUtc: string;
}

/** GET /glossary/aliases (ADR 0019): a learned glossary alias, its lifecycle and evidence. */
export interface LearnedAlias {
  id?: string | null;
  header: string;
  canonicalField: string;
  status: "candidate" | "active" | "retired" | string;
  carrierName: string;
  formatId: string;
  formatVersion: string;
  firstSeenDocumentId: string;
  firstSeenUtc: string;
  learnedBy: { model: string; modelVersion?: string | null; promptVersion: string };
  confidence: number;
  totalsTie?: boolean | null;
  verificationGate?: string | null;
  evidence: AliasEvidence[];
  reviewedBy?: string | null;
  activatedBy?: string | null;
  activatedUtc?: string | null;
  activationReason?: string | null;
  retiredBy?: string | null;
  retiredUtc?: string | null;
  retiredReason?: string | null;
  retiredAuditEventId?: string | null;
  editedFrom?: string | null;
}

export interface FormatSummary {
  formatId: string;
  version: number;
  carrierName: string;
  fileKind: string;
  createdBy: string;
  createdAt: string;
  status: string;
  supersedes?: string | null;
  verificationGate?: string | null;
  documentsProcessed?: number | null;
  modelCallsSaved?: number | null;
}

export interface MeView {
  userId: string;
  role: "Applicant" | "Underwriter" | "UWManager" | "Admin" | "Engineer" | "QuoteSystem";
  /** Decides reviews, overrides values, acts on quotes. */
  canReview: boolean;
  /** Brief 26: reads what a reviewer reads (also true for Engineer, who cannot decide). */
  canRead?: boolean;
}

export interface QuoteSummary {
  quoteId: string;
  documents: number;
  claims: number;
  insuredName?: string | null;
  valuationDate?: string | null;
  pendingReview: number;
  updatedAt?: string | null;
  /** Display name set by a manager (PATCH /quotes/{id}); the id never changes. */
  displayName?: string | null;
  retiredAt?: string | null;
}

export interface PeriodExposure {
  effectiveDate: string;
  amount: number;
  policyNumber?: string | null;
}

export interface QuoteContext {
  quoteId: string;
  payrollByPeriod?: PeriodExposure[] | null;
  fteByPeriod?: PeriodExposure[] | null;
  lastDayWorked?: string | null;
  updatedAt?: string | null;
  updatedBy?: string | null;
  /** Brief 29: the effective date of the policy being quoted (R-15). */
  targetPolicyEffectiveDate?: string | null;
  /** Brief 29: the policy years the submission asks for (R-25). */
  requestedPolicyYears?: number[] | null;
}

export interface AuditTrailEntry {
  id?: number | null;
  at: string;
  user: string;
  role: string;
  action: string;
  uploadId?: string | null;
  documentId?: string | null;
  revision?: number | null;
  fieldPath?: string | null;
  oldValue?: string | null;
  newValue?: string | null;
  rationale?: string | null;
  ledgerTransactionId?: number | null;
  committedAt?: string | null;
  committedBy?: string | null;
}

export interface AuditTrailView {
  entries: AuditTrailEntry[];
  protection: string;
  lastDigestAt?: string | null;
}

export interface CostSummary {
  pocToDateUsd: number;
  documentsProcessed: number;
  avgCostPerDocumentUsd: number;
  zeroModelCallShare: number;
  byPath: { path: string; documents: number; totalUsd: number; meanUsd: number; modelCalls: number }[];
  byCarrier: { carrier: string; learnUsd: number; replayDocs: number; replayMeanUsd: number; replaySavingsUsd: number; breakEvenDoc?: number | null }[];
  documents: { documentId: string; carrier?: string | null; path: string; costUsd: number; pages: number; modelCalls: number; computedAt: string; lines: CostLine[] }[];
  reconciliation?: { meteredUsd: number; billedUsd: number; ratio: number; exportDate: string; from?: string | null; platformBilledUsd?: number | null; scope?: string | null } | null;
}

export interface FollowUp {
  id: string;
  quoteId: string;
  assignee: string;
  action: string;
  note?: string | null;
  claimIds: string[];
  due?: string | null;
  createdBy: string;
  createdAt: string;
}

export interface FlagPushResult {
  quoteId: string;
  claims: number;
  flags: Record<string, number>;
  delivery: string;
  at: string;
}

/** GET /quotes/{id}/claims: the engine input the twin recomputes from. */
export interface ClaimsPayload {
  catalogVersion: string;
  view: string;
  input: unknown;
  perClaimFlags: { claimId: string; flags: string[] }[];
}

/** Brief 24 step 2: quote readiness (openapi Readiness). */
export interface QuoteReadiness {
  ready: boolean;
  blockingFields: { documentId: string; fieldPath: string; status: "unresolved"; reason?: string | null }[];
  blockingFieldCount: number;
  rejectedDocuments: { documentId: string; revision: number; reason: string }[];
  pendingDocuments: { documentId: string; revision: number; reviewState: string }[];
}

/** GET /quotes/{id}/quote-payload (v2) — the documents list is what the Analytics footer needs. */
export interface QuotePayloadV2 {
  quoteId: string;
  /** Brief 24 step 3 (ADR 0024): the quote revision the payload reflects (ETag "q-<n>"); absent on captures made before brief 24. */
  quoteRevision?: number;
  /** Brief 24 Answers 2: "verified-only" — the payload never carries an unverified value. */
  featuresView?: string;
  catalogVersion: string;
  valuationDate?: string | null;
  documents: { documentId: string; uploadId: string; revision: number; carrierName?: string | null; insuredName?: string | null; valuationDate?: string | null; formatRef?: string | null; routingTier?: string | null; reviewState?: string | null; claimCount: number }[];
  claims: (Claim & { id: string; policyNumber?: string | null; flags: string[] })[];
  /** Brief 24 step 2: whether every value is approved; absent on captures made before brief 24. */
  readiness?: QuoteReadiness;
  /** Rejected documents: out of claims, features and periods. */
  excluded?: { documentId: string; uploadId: string; revision: number; reason: "rejected"; claimCount: number }[];
  lineage: { documents: number; pendingReview: number; overrides: { id: string; documentId: string; fieldPath: string; oldValue?: string | null; newValue?: string | null; rationale?: string | null; userId?: string | null; atUtc?: string | null }[] };
}

export interface ApiError {
  code: string;
  message: string;
}

// ------------------------------------------------------------------ chat (brief 12)

export type ChatScope = "org" | "quote" | "workspace";

export interface ChatTurnRequest {
  scope: ChatScope;
  question: string;
  quoteId?: string | null;
  workspaceId?: string | null;
  conversationId?: string | null;
}

export interface ChatCitation {
  ref: string;
  kind: "page" | "claim" | "metric";
  label: string;
  documentId?: string | null;
  page?: number | null;
  sheet?: string | null;
  fieldPath?: string | null;
  claimId?: string | null;
  metric?: string | null;
  fileName?: string | null;
  quoteId?: string | null;
}

export interface ChatToolCall {
  tool: "search_documents" | "query_claims" | "compute_metric";
  arguments: string;
  results: number;
  ms: number;
  dropped: number;
  note?: string | null;
}

export interface ChatTurnUsage {
  modelCalls: number;
  inputTokens: number;
  cachedInputTokens: number;
  outputTokens: number;
  embeddings: number;
  searchQueries: number;
  costUsd: number;
  firstTokenMs: number;
  totalMs: number;
  lines: CostLine[];
}

export interface ChatTurnResult {
  turnId: string;
  conversationId: string;
  userId: string;
  role: string;
  scope: ChatScope;
  quoteId?: string | null;
  workspaceId?: string | null;
  question: string;
  text: string;
  notFound: boolean;
  citations: ChatCitation[];
  toolTrace: ChatToolCall[];
  usage: ChatTurnUsage;
  at: string;
  invalidCitations?: string[] | null;
  /** Brief 24 step 5: names, dates or quotations in the answer no tool returned this turn (fact guard). */
  unverified?: string[] | null;
  /** Set when a per-turn cap (6 tool calls, 12 document chunks) stopped a tool. */
  capNote?: string | null;
}

export interface ChatWorkspace {
  workspaceId: string;
  ownerUserId: string;
  name?: string | null;
  createdAt: string;
  expiresAt: string;
  deletedAt?: string | null;
}

/** Brief 26 (ADR 0025): an engineering escalation — one per (reasonCode, formatFingerprint). The trace holds identifiers and counts only. */
export interface EscalationTrace {
  ruleIds: string[];
  reasons: string[];
  counts: Record<string, number>;
  step?: string | null;
  exceptionType?: string | null;
}

export type EscalationStatus = "new" | "investigating" | "resolved" | "wont-fix";

export interface Escalation {
  id: string;
  reasonCode: string;
  formatFingerprint: string;
  formatRef?: string | null;
  carrier?: string | null;
  documentIds: string[];
  uploadIds: string[];
  firstSeen: string;
  lastSeen: string;
  count: number;
  traceSummary: EscalationTrace;
  status: EscalationStatus;
  resolutionNote?: string | null;
  tuningItemRef?: string | null;
  version: number;
  updatedAt: string;
  updatedBy: string;
}

export interface TuningItem {
  id: string;
  category: string;
  escalationId: string;
  reasonCode: string;
  formatFingerprint: string;
  formatRef?: string | null;
  documentIds: string[];
  note: string;
  createdAt: string;
  createdBy: string;
}
