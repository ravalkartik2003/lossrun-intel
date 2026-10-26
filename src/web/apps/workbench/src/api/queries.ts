import { useMutation, useQuery, useQueryClient, type QueryClient } from "@tanstack/react-query";
import { getBlob, getJson, getText, sendJson } from "./client.ts";
import type {
  AuditTrailView,
  ClaimsPayload,
  CostSummary,
  DocumentResult,
  Escalation,
  FlagPushResult,
  FollowUp,
  FormatSummary,
  LearnedAlias,
  MeView,
  PageLayout,
  QuoteContext,
  QuotePayloadV2,
  QuoteSummary,
  ReviewDecisionRequest,
  ReviewItem,
  UploadManifest,
} from "./types.ts";

const q = (id: string) => encodeURIComponent(id);

export const useMe = (enabled = true) => useQuery({ queryKey: ["me"], queryFn: ({ signal }) => getJson<MeView>("/me", signal), enabled, staleTime: 5 * 60_000 });

export const useQuotes = (enabled = true) => useQuery({ queryKey: ["quotes"], queryFn: ({ signal }) => getJson<QuoteSummary[]>("/quotes", signal), enabled });

export const useDocument = (id: string) => useQuery({ queryKey: ["document", id], queryFn: ({ signal }) => getJson<DocumentResult>(`/documents/${q(id)}`, signal) });

export const useLayout = (id: string, page: number) =>
  useQuery({ queryKey: ["layout", id, page], queryFn: ({ signal }) => getJson<PageLayout>(`/documents/${q(id)}/source/pages/${page}/layout`, signal), staleTime: Infinity });

/** The page image as an object URL (the request carries the Authorization header, which an <img src> cannot). */
export const usePageImage = (id: string, page: number, enabled = true) =>
  useQuery({
    queryKey: ["page", id, page],
    enabled,
    queryFn: async ({ signal }) => URL.createObjectURL(await getBlob(`/documents/${q(id)}/source/pages/${page}`, signal)),
    staleTime: Infinity,
    gcTime: 10 * 60_000,
  });

export const useDocumentAudit = (id: string, enabled = true) =>
  useQuery({ queryKey: ["audit", "document", id], queryFn: ({ signal }) => getJson<AuditTrailView>(`/documents/${q(id)}/audit`, signal), enabled });

export const useQuoteAudit = (id: string | null) =>
  useQuery({ queryKey: ["audit", "quote", id], queryFn: ({ signal }) => getJson<AuditTrailView>(`/quotes/${q(id!)}/audit`, signal), enabled: !!id });

export const useUpload = (id: string | null) =>
  useQuery({ queryKey: ["upload", id], queryFn: ({ signal }) => getJson<UploadManifest>(`/uploads/${q(id!)}`, signal), enabled: !!id });

export const useReviews = (state: "pending" | "decided") => useQuery({ queryKey: ["reviews", state], queryFn: ({ signal }) => getJson<ReviewItem[]>(`/reviews?state=${state}`, signal) });

export const useFormats = () => useQuery({ queryKey: ["formats"], queryFn: ({ signal }) => getJson<FormatSummary[]>("/formats", signal) });

/** Brief 26: the "Needs investigation" queue (UWManager and Admin). */
export const useEscalations = (enabled: boolean) =>
  useQuery({ queryKey: ["escalations"], queryFn: ({ signal }) => getJson<Escalation[]>("/escalations", signal), enabled });

/** GET /glossary/aliases (ADR 0019): learned aliases, optionally those a format introduced or verified. */
export const useAliases = (formatId?: string | null) =>
  useQuery({
    queryKey: ["aliases", formatId ?? "all"],
    queryFn: ({ signal }) => getJson<LearnedAlias[]>(`/glossary/aliases${formatId ? `?formatId=${q(formatId)}` : ""}`, signal),
  });

export const useCosts = () => useQuery({ queryKey: ["costs"], queryFn: ({ signal }) => getJson<CostSummary>("/costs", signal) });

export const useCatalogText = () => useQuery({ queryKey: ["catalog"], queryFn: ({ signal }) => getText("/catalog", signal), staleTime: Infinity });

export const useQuotePayload = (id: string | null) =>
  useQuery({ queryKey: ["quote-payload", id], queryFn: ({ signal }) => getJson<QuotePayloadV2>(`/quotes/${q(id!)}/quote-payload`, signal), enabled: !!id });

/** The claims payload as text (the twin's reader keeps integer cents exact). */
export const useClaimsPayload = (id: string | null, view: "adjusted" | "reported") =>
  useQuery({ queryKey: ["claims", id, view], queryFn: ({ signal }) => getText(`/quotes/${q(id!)}/claims?view=${view}`, signal), enabled: !!id });

export const useFeaturesText = (id: string | null, view: "adjusted" | "reported") =>
  useQuery({ queryKey: ["features", id, view], queryFn: ({ signal }) => getText(`/quotes/${q(id!)}/features?view=${view}`, signal), enabled: !!id });

export const useQuoteContext = (id: string | null) =>
  useQuery({ queryKey: ["context", id], queryFn: ({ signal }) => getJson<QuoteContext>(`/quotes/${q(id!)}/context`, signal), enabled: !!id });

export const useFollowUps = (id: string | null) =>
  useQuery({ queryKey: ["follow-ups", id], queryFn: ({ signal }) => getJson<FollowUp[]>(`/quotes/${q(id!)}/follow-ups`, signal), enabled: !!id });

/**
 * Everything a review decision, an override or a reopen can change (brief 30 item 23): the review lists, the document and
 * its audit trail, the learned aliases, and every quote-level read built from verified documents — the quote payload,
 * the analytics claims and feature snapshots (both views), the quote list's readiness and the upload manifest. Quote
 * keys are invalidated for every quote: the document's quote is not in the decision response.
 */
export const DECISION_INVALIDATES = ["reviews", "audit", "aliases", "quote-payload", "claims", "features", "quotes", "upload"] as const;

export function invalidateAfterDecision(client: QueryClient, documentId: string): Promise<void> {
  return Promise.all([
    client.invalidateQueries({ queryKey: ["document", documentId] }),
    ...DECISION_INVALIDATES.map((key) => client.invalidateQueries({ queryKey: [key] })),
  ]).then(() => undefined);
}

export function useDecide(documentId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: ReviewDecisionRequest) => sendJson<unknown>("POST", `/reviews/${q(documentId)}/decision`, body),
    onSuccess: () => void invalidateAfterDecision(client, documentId),
  });
}

/** POST /reviews/{documentId}/reopen (brief 12b): a processed document back to review, with a reason (audited). */
export function useReopen(documentId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (reason: string) => sendJson<ReviewItem>("POST", `/reviews/${q(documentId)}/reopen`, { reason }),
    onSuccess: () => void invalidateAfterDecision(client, documentId),
  });
}

export function usePutContext(quoteId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: QuoteContext) => sendJson<QuoteContext>("PUT", `/quotes/${q(quoteId)}/context`, body),
    onSuccess: () => {
      for (const key of ["context", "features", "claims", "quote-payload", "audit"]) void client.invalidateQueries({ queryKey: [key] });
    },
  });
}

export function usePushFlags(quoteId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { flags?: string[]; rationale?: string }) => sendJson<FlagPushResult>("POST", `/quotes/${q(quoteId)}/flags`, body),
    onSuccess: () => void client.invalidateQueries({ queryKey: ["audit"] }),
  });
}

export function useAssignFollowUp(quoteId: string) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: (body: { assignee: string; action: string; note?: string; claimIds?: string[]; due?: string }) => sendJson<FollowUp>("POST", `/quotes/${q(quoteId)}/follow-ups`, body),
    onSuccess: () => {
      void client.invalidateQueries({ queryKey: ["follow-ups"] });
      void client.invalidateQueries({ queryKey: ["audit"] });
    },
  });
}

export async function downloadMemo(quoteId: string): Promise<void> {
  const blob = await getBlob(`/quotes/${q(quoteId)}/memo?format=docx`);
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `pricing-memo-${quoteId}.docx`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 5000);
}
