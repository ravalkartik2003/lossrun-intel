import { delay, http, HttpResponse } from "msw";
import { readInput, snapshotJson, writeInput, type AnalyticsInput, type Catalog } from "@lossrun/metrics";
import { perfAccount } from "@lossrun/metrics/test/perf-account.ts";
import memoUrl from "@docs/analytics/samples/memo.docx?url";
import { briefJson, captureJson, captureText, capturedDocumentIds, capturedQuoteIds, pageUrl } from "./data.ts";
import type {
  AuditTrailEntry,
  ChatTurnRequest,
  ChatTurnResult,
  ChatWorkspace,
  CostSummary,
  DocumentResult,
  Escalation,
  EscalationStatus,
  FieldOverride,
  FollowUp,
  FormatSummary,
  LearnedAlias,
  QuoteContext,
  QuotePayloadV2,
  QuoteReadiness,
  QuoteSummary,
  ReviewDecisionRequest,
  ReviewItem,
  TuningItem,
  UploadManifest,
  UploadStatusView,
} from "../api/types.ts";
import { fieldTypeOf } from "../review/fieldTypes.ts";

// MSW handlers for VITE_MOCK=1: the captured live slice, made interactive in memory (review decisions, context edits,
// follow-ups, flag pushes, format retirement, uploads replaying a recorded event stream). Role from X-Mock-Role.

const BASE = "*/api/v1";
const PERF = "Q-PERF-1K";

interface Replay {
  uploadId: string;
  quoteId: string;
  events: { atMs: number; event: string; data: unknown }[];
  manifest: UploadManifest;
}

// ------------------------------------------------------------------ in-memory state

const catalogText = captureText("catalog.json")!;
const catalog = JSON.parse(catalogText) as Catalog;
const documents = new Map<string, DocumentResult>(capturedDocumentIds().map((id) => [id, captureJson<DocumentResult>(`documents/${id}/result.json`)!]));
// ADR 0017: a summary-only loss run (design mock, not a capture) so the period table can be seen in mock mode.
const summaryOnly = briefJson<DocumentResult>("document-result-summary-only.json");
if (summaryOnly) documents.set(summaryOnly.documentId, summaryOnly);
const documentAudit = new Map<string, AuditTrailEntry[]>(capturedDocumentIds().map((id) => [id, captureJson<{ entries: AuditTrailEntry[] }>(`documents/${id}/audit.json`)?.entries ?? []]));
const quoteAudit = new Map<string, AuditTrailEntry[]>();
const contexts = new Map<string, QuoteContext>();
const followUps = new Map<string, FollowUp[]>();
const reviews: ReviewItem[] = captureJson<ReviewItem[]>("reviews.json") ?? [];
const formats: FormatSummary[] = captureJson<FormatSummary[]>("formats.json") ?? [];
// ADR 0019 learned glossary aliases (design mock, docs/ui/mock/glossary-aliases.json; synthetic).
const aliases: LearnedAlias[] = briefJson<LearnedAlias[]>("glossary-aliases.json") ?? [];
const uploads = new Map<string, UploadManifest>();
const replay = captureJson<Replay>("upload-replay.json")!;
const conversations = new Map<string, ChatTurnResult[]>();
const chat = briefJson<{ turns: { scope: string; question: string; events: { atMs: number; event: string; data: unknown }[] }[] }>("chat-turns.json")!;
let uploadCounter = 0;
let txCounter = 9000;

for (const q of capturedQuoteIds()) {
  const trail = captureJson<{ entries: AuditTrailEntry[] }>(`quotes/${q}/audit.json`)?.entries ?? [];
  quoteAudit.set(q, trail.filter((e) => e.fieldPath === `quotes/${q}`));
  contexts.set(q, captureJson<QuoteContext>(`quotes/${q}/context.json`) ?? { quoteId: q });
  followUps.set(q, captureJson<FollowUp[]>(`quotes/${q}/follow-ups.json`) ?? []);
}
for (const id of ["up-02981841d0c318be2226", "up-5ef86052f242da354666", "up-9d688fd56d7eca82c4d1", "up-a1b0b76c4558fac6c51b"]) {
  const m = captureJson<UploadManifest>(`uploads/${id}.json`);
  if (m) uploads.set(id, m);
}

// ------------------------------------------------------------------ helpers

const role = (req: Request) => req.headers.get("X-Mock-Role") ?? "Underwriter";
// Brief 26/25 Answers: Engineer reads what a reviewer reads but never decides; QuoteSystem only reads the payload.
const canReview = (req: Request) => !["Applicant", "QuoteSystem"].includes(role(req));
const canDecide = (req: Request) => ["Underwriter", "UWManager", "Admin"].includes(role(req));
const forbidden = () => HttpResponse.json({ code: "forbidden", message: "This is for underwriting roles." }, { status: 403 });
const notFound = (what: string) => HttpResponse.json({ code: "not-found", message: `${what} not found` }, { status: 404 });
const text = (body: string) => new HttpResponse(body, { headers: { "Content-Type": "application/json" } });
const user = (req: Request) => role(req).toLowerCase();
const now = () => new Date().toISOString();

/**
 * Brief 24 step 2, until the step-7 re-capture: readiness for a payload captured before brief 24, from the mock's current
 * document states. Approximate: pending and rejected documents, and the hard rules still failing on a decided document
 * (no R-33 check; the API computes the real one).
 */
function withReadiness(payload: QuotePayloadV2): QuotePayloadV2 {
  const state = (id: string, fallback?: string | null) => documents.get(id)?.reviewState ?? fallback ?? null;
  const rejected = payload.documents.filter((d) => state(d.documentId, d.reviewState) === "reject");
  const counted = payload.documents.filter((d) => state(d.documentId, d.reviewState) !== "reject");
  const pending = counted.filter((d) => ["pending", "reopened"].includes(state(d.documentId, d.reviewState) ?? ""));
  const blocking: QuoteReadiness["blockingFields"] = [];
  for (const d of counted.filter((x) => !pending.includes(x))) {
    const doc = documents.get(d.documentId);
    const lineage = doc?.document.lineage ?? {};
    for (const o of (doc?.ruleOutcomes ?? []).filter((r) => r.status === "fail" && r.severity === "hard")) {
      for (const pattern of o.affectedFields) {
        const re = new RegExp(`^${pattern.replace(/[.*+?^${}()|[\]\\]/g, "\\$&").replaceAll("\\[\\*\\]", "\\[\\d+\\]")}$`);
        for (const path of Object.keys(lineage).filter((k) => re.test(k) && lineage[k]?.extractor !== "manual")) {
          if (!blocking.some((b) => b.documentId === d.documentId && b.fieldPath === path)) blocking.push({ documentId: d.documentId, fieldPath: path, status: "unresolved", reason: o.ruleId });
        }
      }
    }
  }
  const readiness: QuoteReadiness = {
    ready: counted.length > 0 && pending.length === 0 && blocking.length === 0,
    blockingFields: blocking.slice(0, 200),
    blockingFieldCount: blocking.length,
    rejectedDocuments: rejected.map((d) => ({ documentId: d.documentId, revision: documents.get(d.documentId)?.revision ?? d.revision, reason: "rejected" })),
    pendingDocuments: pending.map((d) => ({ documentId: d.documentId, revision: documents.get(d.documentId)?.revision ?? d.revision, reviewState: state(d.documentId, d.reviewState)! })),
  };
  return {
    ...payload,
    documents: counted,
    claims: payload.claims.filter((c) => !rejected.some((d) => c.id.startsWith(`${d.documentId}/`))),
    readiness,
    excluded: rejected.map((d) => ({ documentId: d.documentId, uploadId: d.uploadId, revision: documents.get(d.documentId)?.revision ?? d.revision, reason: "rejected", claimCount: d.claimCount })),
  };
}

function claimsInput(quoteId: string, view: string): AnalyticsInput {
  if (quoteId === PERF) return perfAccount();
  const payload = JSON.parse(captureText(`quotes/${quoteId}/claims-${view}.json`)!) as { input: unknown };
  const input = readInput(payload.input);
  const ctx = contexts.get(quoteId);
  if (ctx) {
    const byYear = (list: { effectiveDate: string; amount: number }[] | null | undefined, scale: number) => {
      if (!list || list.length === 0) return null;
      const m: Record<string, number> = {};
      for (const p of list) m[p.effectiveDate.slice(0, 4)] = (m[p.effectiveDate.slice(0, 4)] ?? 0) + Math.round(p.amount * scale);
      return m;
    };
    input.context = { ...input.context, payrollCentsByYear: byYear(ctx.payrollByPeriod, 100), fteByYear: byYear(ctx.fteByPeriod, 1), lastDayWorked: ctx.lastDayWorked ?? null };
  }
  return input;
}

/** Features: the captured server snapshot while the context is unchanged, else the twin's (byte-identical engine). */
const contextChanged = new Set<string>();
function featuresText(quoteId: string, view: string): string {
  if (quoteId !== PERF && !contextChanged.has(quoteId)) return captureText(`quotes/${quoteId}/features-${view}.json`)!;
  return snapshotJson(claimsInput(quoteId, view), catalog, undefined, view);
}

function claimsText(quoteId: string, view: string): string {
  const input = claimsInput(quoteId, view);
  const flags = JSON.parse(featuresText(quoteId, view)).perClaimFlags;
  return `{"catalogVersion":"${catalog.catalogVersion}","view":"${view}","input":${writeInput(input)},"perClaimFlags":${JSON.stringify(flags)}}`;
}

function audit(entry: Omit<AuditTrailEntry, "at" | "ledgerTransactionId" | "committedAt">): AuditTrailEntry {
  const at = now();
  return { ...entry, at, ledgerTransactionId: ++txCounter, committedAt: at, committedBy: "app managed identity" };
}

function setPath(root: unknown, path: string, value: unknown): unknown {
  const parts = path.match(/[^.[\]]+/g) ?? [];
  let node = root as Record<string, unknown>;
  for (let i = 0; i < parts.length - 1; i++) {
    const k = /^\d+$/.test(parts[i]) ? Number(parts[i]) : parts[i];
    node[k] ??= {};
    node = node[k] as Record<string, unknown>;
  }
  const last = parts[parts.length - 1];
  const old = node[last];
  // A coded override ({ code, codeSystem } on *.code) sets the code and its system on the coded value.
  if (last === "code" && value && typeof value === "object") Object.assign(node, value);
  else node[last] = value;
  return old;
}

/**
 * As the API (brief 30 item 3): newValue must be the field's canonical JSON type; a wrong type is a 400 naming the path,
 * before anything is applied.
 */
function overrideTypeError(o: FieldOverride): string | null {
  const t = fieldTypeOf(o.fieldPath);
  const v = o.newValue as unknown;
  const ok =
    t.kind === "money" ? typeof v === "number" && Number.isFinite(v)
    : t.kind === "count" ? typeof v === "number" && Number.isInteger(v)
    : t.kind === "boolean" ? typeof v === "boolean"
    : t.kind === "date" ? typeof v === "string" && /^\d{4}-\d{2}-\d{2}$/.test(v)
    : t.kind === "code" ? !!v && typeof v === "object" && typeof (v as { code?: unknown }).code === "string" && (v as { codeSystem?: unknown }).codeSystem === t.codeSystem
    : t.kind === "enum" ? typeof v === "string" && (t.enum ?? []).includes(v)
    : typeof v === "string";
  return ok ? null : `${o.fieldPath}: newValue must be ${t.kind === "code" ? `{ code, codeSystem: "${t.codeSystem}" }` : t.kind === "date" ? "a yyyy-MM-dd string" : t.kind === "money" || t.kind === "count" ? "a number" : `a ${t.kind === "enum" ? "string" : t.kind}`}.`;
}

const auditText = (v: unknown) => (v && typeof v === "object" ? String((v as { code?: unknown }).code ?? JSON.stringify(v)) : v === undefined || v === null ? null : String(v));

function sse(events: { atMs: number; event: string; data: unknown }[], speed = 1): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    async start(controller) {
      let t = events[0]?.atMs ?? 0;
      for (const e of events) {
        await new Promise((r) => setTimeout(r, Math.min(2500, (e.atMs - t) / speed)));
        t = e.atMs;
        controller.enqueue(encoder.encode(`event: ${e.event}\ndata: ${JSON.stringify(e.data)}\n\n`));
      }
      controller.close();
    },
  });
}

function rewriteUploadId<T>(value: T, from: string, to: string, quoteId?: string): T {
  let s = JSON.stringify(value).split(from).join(to);
  if (quoteId) s = s.split(replay.quoteId).join(quoteId);
  return JSON.parse(s) as T;
}

/** A chat question with no recorded turn: answered "not found" (no tools), like the service when nothing in scope matches. */
function unrecordedTurn(body: ChatTurnRequest, req: Request): ChatTurnResult {
  return {
    turnId: `ct-mock${Date.now()}`, conversationId: body.conversationId ?? `cv-mock${Date.now()}`, userId: user(req), role: role(req), scope: body.scope,
    quoteId: body.quoteId ?? null, workspaceId: body.workspaceId ?? null, question: body.question,
    text: "Mock mode replays recorded answers only; try one of the suggested questions.", notFound: true, citations: [], toolTrace: [],
    usage: { modelCalls: 0, inputTokens: 0, cachedInputTokens: 0, outputTokens: 0, embeddings: 0, searchQueries: 0, costUsd: 0, firstTokenMs: 0, totalMs: 0, lines: [] },
    at: now(),
  };
}

// ------------------------------------------------------------------ handlers

// ------------------------------------------------------------------ brief 26: the investigation queue (synthetic seed, ADR 0025 shapes)

const escalationSeed = (id: string, e: Partial<Escalation> & Pick<Escalation, "reasonCode" | "formatFingerprint">): Escalation => ({
  id, formatRef: null, carrier: null, documentIds: [], uploadIds: [], firstSeen: "2026-10-24T15:02:11Z", lastSeen: "2026-10-25T09:41:37Z", count: 1,
  traceSummary: { ruleIds: [], reasons: [], counts: {}, step: null }, status: "new", resolutionNote: null, tuningItemRef: null, version: 1,
  updatedAt: "2026-10-25T09:41:37Z", updatedBy: "system", ...e,
});
const escalations: Escalation[] = [
  escalationSeed("esc-5b1f0c2a9e7d4410", {
    reasonCode: "learn-exhausted", formatFingerprint: "fp:3f9a1c0b7d22:a81e44c9f0b3", carrier: "Synthetic Mutual Insurance Co.", count: 3,
    documentIds: ["doc-04777370247230a8", "doc-5209d62bf384ad6a", "doc-9997d9e37c0fcb03-p1-1"], uploadIds: ["up-02981841d0c318be2226"],
    traceSummary: { ruleIds: ["R-02", "R-10"], reasons: ["verification failed: printed totals do not reconcile (…)", "header(s): …"], counts: { claims: 14, hardFailures: 2, unmappedHeaders: 3, learnModelCalls: 3, costUsd: 0.031 }, step: "learn" },
  }),
  escalationSeed("esc-91c2d7e0aa034b6f", {
    reasonCode: "rule-dominant", formatFingerprint: "carrier:7c1e0f3b92aa/R-14", carrier: "Fictional Casualty Group", count: 6, formatRef: "fictional-casualty-group-pdf@1",
    documentIds: ["doc-5209d62bf384ad6a"], traceSummary: { ruleIds: ["R-14"], reasons: [], counts: { documents: 7, failing: 6, share: 0.857 }, step: "validate" }, status: "investigating",
    resolutionNote: "checking the no-losses statement detection",
  }),
  escalationSeed("esc-0d4e6a1f73b25c98", {
    reasonCode: "unhandled-exception", formatFingerprint: "exception:ParseFile:System.IO.InvalidDataException", count: 1, uploadIds: ["up-5ef86052f242da354666"],
    traceSummary: { ruleIds: [], reasons: [], counts: {}, step: "ParseFile", exceptionType: "System.IO.InvalidDataException" }, status: "resolved", resolutionNote: "corrupt ZIP; parser now reports it as unsupported",
  }),
];
const tuningItems: TuningItem[] = [];
const canTriage = (req: Request) => ["Admin", "UWManager", "Engineer"].includes(role(req));
const ESCALATION_STATUSES: EscalationStatus[] = ["new", "investigating", "resolved", "wont-fix"];

export const handlers = [
  http.get(`${BASE}/me`, ({ request }) => HttpResponse.json({ userId: user(request), role: role(request), canReview: canDecide(request), canRead: canReview(request) })),

  http.get(`${BASE}/catalog`, () => text(catalogText)),

  http.get(`${BASE}/quotes`, ({ request }) => {
    if (!canReview(request)) return forbidden();
    const quotes = captureJson<QuoteSummary[]>("quotes.json") ?? [];
    return HttpResponse.json([...quotes, { quoteId: PERF, documents: 5, claims: 1000, insuredName: "Performance account (synthetic, 1,000 claims)", valuationDate: "2025-09-30", pendingReview: 0 }]);
  }),

  http.get(`${BASE}/quotes/:quoteId/features`, async ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    const view = new URL(request.url).searchParams.get("view") ?? "adjusted";
    const q = String(params.quoteId);
    if (q !== PERF && !captureText(`quotes/${q}/features-adjusted.json`)) return notFound(`quote ${q}`);
    await delay(60);
    return text(featuresText(q, view));
  }),

  http.get(`${BASE}/quotes/:quoteId/claims`, ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    const q = String(params.quoteId);
    if (q !== PERF && !captureText(`quotes/${q}/claims-adjusted.json`)) return notFound(`quote ${q}`);
    return text(claimsText(q, new URL(request.url).searchParams.get("view") ?? "adjusted"));
  }),

  http.post(`${BASE}/quotes/:quoteId/features/what-if`, async ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    const { whatIf, readScenario } = await import("@lossrun/metrics");
    const body = (await request.json()) as { scenario?: unknown; view?: string };
    const view = body.view ?? "adjusted";
    const r = whatIf(claimsInput(String(params.quoteId), view), catalog, readScenario(body.scenario ?? {}), view);
    return HttpResponse.json({ quoteId: params.quoteId, view, deltas: r.deltas });
  }),

  http.get(`${BASE}/quotes/:quoteId/quote-payload`, ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    const q = String(params.quoteId);
    if (q === PERF) return HttpResponse.json({ quoteId: PERF, catalogVersion: catalog.catalogVersion, valuationDate: "2025-09-30", documents: [], claims: [], lineage: { documents: 5, pendingReview: 0, overrides: [] } });
    const t = captureText(`quotes/${q}/quote-payload.json`);
    if (!t) return notFound(`quote ${q}`);
    const captured = JSON.parse(t) as QuotePayloadV2;
    return captured.readiness ? text(t) : HttpResponse.json(withReadiness(captured));
  }),

  http.get(`${BASE}/quotes/:quoteId/context`, ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    return HttpResponse.json(contexts.get(String(params.quoteId)) ?? { quoteId: params.quoteId });
  }),

  http.put(`${BASE}/quotes/:quoteId/context`, async ({ request, params }) => {
    if (!canDecide(request)) return forbidden();
    const q = String(params.quoteId);
    const body = (await request.json()) as QuoteContext;
    const old = contexts.get(q);
    const next = { ...body, quoteId: q, updatedAt: now(), updatedBy: user(request) };
    contexts.set(q, next);
    contextChanged.add(q);
    quoteAudit.set(q, [audit({ user: user(request), role: role(request), action: "quote.context", fieldPath: `quotes/${q}`, oldValue: old ? JSON.stringify(old) : null, newValue: JSON.stringify(body), rationale: "submission context (exposures, last day worked)" }), ...(quoteAudit.get(q) ?? [])]);
    return HttpResponse.json(next);
  }),

  http.get(`${BASE}/quotes/:quoteId/audit`, ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    const q = String(params.quoteId);
    const docs = (captureJson<{ documents: { documentId: string }[] }>(`quotes/${q}/quote-payload.json`)?.documents ?? []).map((d) => d.documentId);
    const entries = [...(quoteAudit.get(q) ?? []), ...docs.flatMap((d) => documentAudit.get(d) ?? [])].sort((a, b) => b.at.localeCompare(a.at));
    return HttpResponse.json({ entries, protection: "ledger", lastDigestAt: "2026-10-22T23:00:00Z" });
  }),

  http.get(`${BASE}/quotes/:quoteId/follow-ups`, ({ request, params }) => (canReview(request) ? HttpResponse.json(followUps.get(String(params.quoteId)) ?? []) : forbidden())),

  http.post(`${BASE}/quotes/:quoteId/follow-ups`, async ({ request, params }) => {
    if (!canDecide(request)) return forbidden();
    const q = String(params.quoteId);
    const body = (await request.json()) as Omit<FollowUp, "id" | "quoteId" | "createdBy" | "createdAt">;
    if (!body.assignee?.trim() || !body.action?.trim()) return HttpResponse.json({ code: "bad-follow-up", message: "assignee and action are required." }, { status: 400 });
    const f: FollowUp = { id: `fu-${Date.now().toString(16)}`, quoteId: q, assignee: body.assignee, action: body.action, note: body.note ?? null, claimIds: body.claimIds ?? [], due: body.due ?? null, createdBy: user(request), createdAt: now() };
    followUps.set(q, [f, ...(followUps.get(q) ?? [])]);
    quoteAudit.set(q, [audit({ user: user(request), role: role(request), action: "quote.followup", fieldPath: `quotes/${q}`, newValue: JSON.stringify(f), rationale: `${f.action} → ${f.assignee}` }), ...(quoteAudit.get(q) ?? [])]);
    return HttpResponse.json(f, { status: 201 });
  }),

  http.post(`${BASE}/quotes/:quoteId/flags`, async ({ request, params }) => {
    if (!canDecide(request)) return forbidden();
    const q = String(params.quoteId);
    const flags = JSON.parse(featuresText(q, "adjusted")).perClaimFlags as { flags: string[] }[];
    const counts: Record<string, number> = {};
    for (const c of flags) for (const f of c.flags) counts[f] = (counts[f] ?? 0) + 1;
    const result = { quoteId: q, claims: flags.length, flags: counts, delivery: "recorded; Service Bus delivery is brief 12b", at: now() };
    quoteAudit.set(q, [audit({ user: user(request), role: role(request), action: "quote.flags.pushed", fieldPath: `quotes/${q}`, newValue: JSON.stringify(result) }), ...(quoteAudit.get(q) ?? [])]);
    return HttpResponse.json(result);
  }),

  http.get(`${BASE}/quotes/:quoteId/memo`, async ({ request, params }) => {
    if (!canDecide(request)) return forbidden();
    const q = String(params.quoteId);
    quoteAudit.set(q, [audit({ user: user(request), role: role(request), action: "quote.memo.exported", fieldPath: `quotes/${q}`, rationale: "pricing memo (docx) — sample memo in mock mode" }), ...(quoteAudit.get(q) ?? [])]);
    const blob = await (await fetch(memoUrl)).blob();
    return new HttpResponse(blob, { headers: { "Content-Type": "application/vnd.openxmlformats-officedocument.wordprocessingml.document" } });
  }),

  http.get(`${BASE}/documents/:id`, async ({ params }) => {
    const d = documents.get(String(params.id));
    await delay(40);
    return d ? HttpResponse.json(d) : notFound(`document ${params.id}`);
  }),

  http.get(`${BASE}/documents/:id/audit`, ({ request, params }) =>
    canReview(request) ? HttpResponse.json({ entries: documentAudit.get(String(params.id)) ?? [], protection: "ledger", lastDigestAt: "2026-10-22T23:00:00Z" }) : forbidden()),

  http.get(`${BASE}/documents/:id/source/pages/:page/layout`, ({ params }) => {
    const t = captureText(`documents/${params.id}/pages/${params.page}.layout.json`);
    return t ? text(t) : notFound("page");
  }),

  http.get(`${BASE}/documents/:id/source/pages/:page`, async ({ params }) => {
    const url = pageUrl(String(params.id), Number(params.page));
    if (!url) return notFound("page");
    const blob = await (await fetch(url)).blob();
    return new HttpResponse(blob, { headers: { "Content-Type": "image/png" } });
  }),

  http.get(`${BASE}/reviews`, ({ request }) => {
    if (!canReview(request)) return forbidden();
    const state = new URL(request.url).searchParams.get("state") ?? "pending";
    return HttpResponse.json(reviews.filter((r) => r.state === state));
  }),

  http.post(`${BASE}/reviews/:id/reopen`, async ({ request, params }) => {
    if (!canDecide(request)) return forbidden();
    const id = String(params.id);
    const doc = documents.get(id);
    if (!doc) return notFound(`document ${id}`);
    const { reason } = (await request.json()) as { reason?: string };
    if (!reason?.trim()) return HttpResponse.json({ code: "no-reason", message: "Send { reason } — the reopen is audited." }, { status: 400 });
    if (reviews.some((r) => r.documentId === id && r.state === "pending")) {
      return HttpResponse.json({ code: "already-in-review", message: `Document ${id} already has an open review.` }, { status: 409 });
    }
    const item: ReviewItem = {
      documentId: id, uploadId: doc.uploadId, kind: "document-review", state: "pending", reasons: [`reopened by ${user(request)} (${role(request)}): ${reason.trim()}`],
      formatRef: doc.formatRef, openedAt: now(), reopened: true,
    };
    const at = reviews.findIndex((r) => r.documentId === id);
    if (at >= 0) reviews[at] = item;
    else reviews.push(item);
    doc.reviewState = "reopened";
    documentAudit.set(id, [audit({ user: user(request), role: role(request), action: "review.reopened", documentId: id, revision: doc.revision, rationale: reason.trim() }), ...(documentAudit.get(id) ?? [])]);
    return HttpResponse.json(item, { status: 201 });
  }),

  http.post(`${BASE}/reviews/:id/decision`, async ({ request, params }) => {
    if (!canDecide(request)) return forbidden();
    const id = String(params.id);
    const item = reviews.find((r) => r.documentId === id);
    if (!item) return notFound(`review ${id}`);
    if (item.state !== "pending") return HttpResponse.json({ code: "not-pending", message: `Review ${id} is ${item.state}.` }, { status: 409 });
    const body = (await request.json()) as ReviewDecisionRequest;
    // Brief 24 step 1: as the API, overrides belong to an override decision (never silently dropped or applied).
    if (body.action !== "override" && body.overrides?.length) {
      return HttpResponse.json({ code: "overrides-not-allowed", message: `overrides are sent only with action override, not ${body.action}.` }, { status: 400 });
    }
    if (body.action === "override" && (!body.overrides?.length || body.overrides.some((o) => !o.rationale?.trim()))) {
      return HttpResponse.json({ code: "bad-overrides", message: "override needs at least one { fieldPath, newValue, rationale } with a rationale." }, { status: 400 });
    }
    const typeError = (body.overrides ?? []).map(overrideTypeError).find((e) => e !== null);
    if (typeError) return HttpResponse.json({ code: "bad-override-type", message: typeError }, { status: 400 });
    // Brief 34 A3, brief 35 Answers 2: as the API, each format change answers the question its review asked.
    const changes = Object.entries(body.formatChanges ?? {});
    const asks = (prefix: string) => item.reasons.some((r) => r.startsWith(prefix));
    const allowed = ([k, v]: [string, unknown]) =>
      (k === "periods.fillDown" && v === true && asks("policy cell blank on "))
      || (k === "carrierNames.add" && typeof v === "string" && v.trim().length > 0 && v.length <= 200 && asks("carrier name on the page differs from the format's: "));
    if (changes.length > 0 && (body.action === "reject" || !changes.every(allowed))) {
      return HttpResponse.json({ code: "bad-format-change", message: "formatChanges answers the review's own question: { \"periods.fillDown\": true } for blank policy cells, { \"carrierNames.add\": \"<name>\" } for a carrier name the format does not know." }, { status: 400 });
    }
    await delay(400);
    const doc = documents.get(id);
    const trail = documentAudit.get(id) ?? [];
    if (doc) {
      const changed = new Set<string>();
      for (const o of body.overrides ?? []) {
        const old = setPath(doc.document, o.fieldPath, o.newValue);
        changed.add(o.fieldPath);
        const prior = doc.document.lineage?.[o.fieldPath];
        doc.document.lineage = { ...(doc.document.lineage ?? {}), [o.fieldPath]: { ...prior, extractor: "manual", sourceText: o.rationale, confidence: 1 } };
        trail.unshift(audit({ user: user(request), role: role(request), action: "override", uploadId: doc.uploadId, documentId: id, revision: doc.revision + 1, fieldPath: o.fieldPath, oldValue: auditText(old), newValue: auditText(o.newValue), rationale: o.rationale }));
      }
      for (const r of doc.ruleOutcomes) if (r.status === "fail" && r.affectedFields.some((f) => changed.has(f))) r.status = "pass";
      // As the API: every decision bumps the revision and records review.<action>; the state is the action itself.
      doc.revision += 1;
      doc.reviewState = body.action;
      trail.unshift(audit({ user: user(request), role: role(request), action: `review.${body.action}`, uploadId: doc.uploadId, documentId: id, revision: doc.revision, rationale: body.rationale ?? null }));
      const promote = body.action !== "reject" && (body.promoteToFormat ?? item.kind === "confirm-format");
      if (promote && item.formatRef) trail.unshift(audit({ user: user(request), role: role(request), action: "format.approve", documentId: id, rationale: item.formatRef }));
      if (promote && item.kind === "confirm-format" && item.formatRef) {
        // ADR 0019: the reviewer's alias decisions; the format's other candidates are accepted with it.
        const formatId = item.formatRef.split("@")[0];
        for (const a of aliases.filter((x) => x.formatId === formatId && x.status === "candidate")) {
          const d = body.aliases?.find((x) => x.header === a.header && x.canonicalField === a.canonicalField);
          if (d?.action === "reject") Object.assign(a, { status: "retired", retiredBy: user(request), retiredUtc: now(), retiredReason: "rejected in the confirm-format review" });
          else {
            if (d?.action === "edit" && d.newField) Object.assign(a, { editedFrom: a.canonicalField, canonicalField: d.newField });
            Object.assign(a, { status: "active", reviewedBy: user(request), activatedBy: user(request), activatedUtc: now(), activationReason: "reviewer" });
          }
          trail.unshift(audit({ user: user(request), role: role(request), action: `glossary.alias.${d?.action === "reject" ? "rejected" : d?.action === "edit" ? "edited" : "accepted"}`, documentId: id, fieldPath: `glossary/aliases/${a.id}`, rationale: `"${a.header}" → ${a.canonicalField}` }));
        }
      }
      documentAudit.set(id, trail);
    }
    item.state = "decided";
    item.decidedAt = now();
    // Echoes the typed overrides it applied (mock only; the API's 202 body is the decision receipt).
    return HttpResponse.json({ documentId: id, action: body.action, user: user(request), role: role(request), at: now(), overrides: body.overrides ?? [] }, { status: 202 });
  }),

  http.get(`${BASE}/formats`, () => HttpResponse.json(formats)),

  http.get(`${BASE}/escalations`, ({ request }) => (canTriage(request) ? HttpResponse.json(escalations) : forbidden())),
  http.patch(`${BASE}/escalations/:id`, async ({ request, params }) => {
    if (!canTriage(request)) return forbidden();
    const e = escalations.find((x) => x.id === params.id);
    if (!e) return notFound("escalation");
    const body = (await request.json()) as { status?: EscalationStatus; note?: string };
    if (!body.status || !ESCALATION_STATUSES.includes(body.status) || !body.note?.trim()) return HttpResponse.json({ code: "bad-status", message: "Send { status, note }." }, { status: 400 });
    Object.assign(e, { status: body.status, resolutionNote: body.note.trim(), version: e.version + 1, updatedAt: now(), updatedBy: user(request) });
    return HttpResponse.json(e);
  }),
  http.post(`${BASE}/escalations/:id/tuning-items`, async ({ request, params }) => {
    if (!canTriage(request)) return forbidden();
    const e = escalations.find((x) => x.id === params.id);
    if (!e) return notFound("escalation");
    const body = (await request.json()) as { category?: string; note?: string };
    if (!body.category || !"abcdefg".includes(body.category) || body.category.length !== 1 || !body.note?.trim()) return HttpResponse.json({ code: "bad-tuning-item", message: "Send { category: a–g, note }." }, { status: 400 });
    const item: TuningItem = { id: `ti-${Math.random().toString(16).slice(2, 14)}`, category: body.category, escalationId: e.id, reasonCode: e.reasonCode, formatFingerprint: e.formatFingerprint,
      formatRef: e.formatRef, documentIds: e.documentIds, note: body.note.trim(), createdAt: now(), createdBy: user(request) };
    tuningItems.push(item);
    Object.assign(e, { tuningItemRef: item.id, status: e.status === "new" ? "investigating" : e.status, version: e.version + 1, updatedAt: now() });
    return HttpResponse.json({ escalation: e, tuningItem: item }, { status: 201 });
  }),
  http.post(`${BASE}/documents/:id/flag`, async ({ request, params }) => {
    if (!canReview(request)) return forbidden();
    const { note } = (await request.json()) as { note?: string };
    if (!note?.trim()) return HttpResponse.json({ code: "bad-flag", message: "Send { note }." }, { status: 400 });
    const id = String(params.id);
    const key = `document:${id}`;
    let e = escalations.find((x) => x.reasonCode === "manual-flag" && x.formatFingerprint === key);
    if (!e) {
      e = escalationSeed(`esc-flag-${id.slice(-8)}`, { reasonCode: "manual-flag", formatFingerprint: key, documentIds: [id], firstSeen: now(), lastSeen: now(), updatedBy: user(request),
        traceSummary: { ruleIds: (documents.get(id)?.ruleOutcomes ?? []).filter((o) => o.status === "fail").map((o) => o.ruleId), reasons: [], counts: {}, step: "review" } });
      escalations.unshift(e);
    } else {
      Object.assign(e, { count: e.count + 1, lastSeen: now(), version: e.version + 1 });
    }
    return HttpResponse.json(e, { status: 201 });
  }),

  http.get(`${BASE}/glossary/aliases`, ({ request }) => {
    const url = new URL(request.url);
    const formatId = url.searchParams.get("formatId");
    const status = url.searchParams.get("status");
    return HttpResponse.json(aliases.filter((a) => (!formatId || a.formatId === formatId || a.evidence.some((e) => e.formatRef.split("@")[0] === formatId)) && (!status || a.status === status)));
  }),

  http.post(`${BASE}/formats/:id/versions/:version/retire`, async ({ request, params }) => {
    if (role(request) !== "Admin" && role(request) !== "UWManager") return HttpResponse.json({ code: "forbidden", message: "Retiring a format is for UWManager and Admin." }, { status: 403 });
    const f = formats.find((x) => x.formatId === params.id && String(x.version) === params.version);
    if (!f) return notFound("format");
    const { reason } = (await request.json()) as { reason?: string };
    if (!reason?.trim()) return HttpResponse.json({ code: "no-reason", message: "A reason is required." }, { status: 400 });
    f.status = "retired";
    return HttpResponse.json(f);
  }),

  http.get(`${BASE}/costs`, ({ request }) => {
    if (!canReview(request)) return forbidden();
    const live = captureJson<CostSummary>("costs.json")!;
    // Reconciliation is brief 13; the design-brief mock shows the intended line.
    return HttpResponse.json({ ...live, reconciliation: briefJson<{ reconciliation: CostSummary["reconciliation"] }>("costs.json")?.reconciliation ?? null });
  }),

  http.post(`${BASE}/chat/turns`, async ({ request }) => {
    const body = (await request.json()) as ChatTurnRequest;
    if (role(request) === "Applicant" && body.scope !== "workspace") {
      return HttpResponse.json({ code: "forbidden", message: "Applicants can chat only over their own workspace." }, { status: 403 });
    }
    const hit = chat.turns.find((t) => t.scope === body.scope && t.question.toLowerCase() === body.question.trim().toLowerCase());
    // As the API: the turn keeps the conversation id the client sent and is recorded (GET /chat/conversations/{id}).
    const events = (hit?.events ?? [{ atMs: 300, event: "final", data: unrecordedTurn(body, request) }]).map((e) =>
      e.event === "final" ? { ...e, data: { ...(e.data as ChatTurnResult), conversationId: body.conversationId ?? (e.data as ChatTurnResult).conversationId, question: body.question.trim() } } : e,
    );
    const final = events.find((e) => e.event === "final")?.data as ChatTurnResult | undefined;
    if (final) conversations.set(final.conversationId, [...(conversations.get(final.conversationId) ?? []), { ...final, userId: user(request) }]);
    return new HttpResponse(sse(events, 2), { headers: { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" } });
  }),

  http.get(`${BASE}/chat/conversations/:id`, ({ request, params }) => {
    const turns = (conversations.get(String(params.id)) ?? []).filter((t) => t.userId === user(request));
    return turns.length ? HttpResponse.json({ conversationId: String(params.id), turns }) : notFound(`conversation ${params.id}`);
  }),

  http.post(`${BASE}/workspaces`, async ({ request }) => {
    const created = new Date();
    const ws: ChatWorkspace = {
      workspaceId: `ws-mock${String(++uploadCounter).padStart(12, "0")}`, ownerUserId: user(request), name: "Chat workspace",
      createdAt: created.toISOString(), expiresAt: new Date(created.getTime() + 72 * 3600_000).toISOString(),
    };
    return HttpResponse.json(ws, { status: 201 });
  }),

  http.post(`${BASE}/uploads`, async ({ request }) => {
    const form = await request.formData();
    const quoteId = String(form.get("quoteId") ?? "") || replay.quoteId;
    const uploadId = `up-mock${String(++uploadCounter).padStart(15, "0")}`;
    const manifest = rewriteUploadId(replay.manifest, replay.uploadId, uploadId, quoteId);
    manifest.submission = { ...manifest.submission, uploaderRole: role(request), uploaderId: user(request) };
    uploads.set(uploadId, manifest);
    return HttpResponse.json({ uploadId, statusUrl: `/api/v1/uploads/${uploadId}`, eventsUrl: `/api/v1/uploads/${uploadId}/events`, existing: false }, { status: 202 });
  }),

  http.get(`${BASE}/uploads/:id/events`, ({ params }) => {
    const id = String(params.id);
    const events = rewriteUploadId(replay.events, replay.uploadId, id);
    return new HttpResponse(sse(events), { headers: { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" } });
  }),

  http.get(`${BASE}/uploads/:id/status`, ({ params }) => {
    const last = [...replay.events].reverse().find((e) => e.event === "progress");
    return HttpResponse.json(rewriteUploadId(last?.data as UploadStatusView, replay.uploadId, String(params.id)));
  }),

  http.get(`${BASE}/uploads/:id`, ({ params }) => {
    const m = uploads.get(String(params.id));
    return m ? HttpResponse.json(m) : notFound(`upload ${params.id}`);
  }),
];

export { replay as recordedReplay };
