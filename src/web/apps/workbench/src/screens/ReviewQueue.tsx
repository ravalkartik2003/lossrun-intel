import { Link, useNavigate, useSearch } from "@tanstack/react-router";
import { useQueries } from "@tanstack/react-query";
import { RoutingBadge, StateMessage, TabSet, dateTime } from "@lossrun/ui";
import { getJson } from "../api/client.ts";
import { useReviews } from "../api/queries.ts";
import type { DocumentResult, ReviewItem } from "../api/types.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle, useWidth } from "../shell/hooks.ts";

/** Review queue (design-brief §5.3): documents needing review and new formats to confirm. */
export function ReviewQueueScreen() {
  useTitle("Review queue");
  const width = useWidth();
  const search = useSearch({ from: "/reviews" });
  const navigate = useNavigate();
  const pending = useReviews("pending");
  const decided = useReviews("decided");
  const tab = search.tab ?? "documents";

  if (width === "small") {
    return <Page title="Review queue"><p className="note">The review queue needs a larger screen (768px or wider) to compare values with the source page.</p></Page>;
  }

  const docs = (pending.data ?? []).filter((r) => r.kind === "document-review");
  const formats = (pending.data ?? []).filter((r) => r.kind === "confirm-format");
  return (
    <Page title="Review queue">
      {pending.isError ? <StateMessage kind="error">The review queue could not be loaded.</StateMessage> : null}
      <TabSet
        label="Review queue"
        value={tab}
        onValueChange={(v) => void navigate({ to: "/reviews", search: v === "documents" ? {} : { tab: v as "formats" | "decided" } })}
        tabs={[
          { value: "documents", label: `Documents needing review (${docs.length})`, content: <ReviewTable items={docs} loading={pending.isLoading} empty="No documents are waiting for review." /> },
          { value: "formats", label: `Confirm new formats (${formats.length})`, content: <ReviewTable items={formats} loading={pending.isLoading} empty="No newly learned formats are waiting for confirmation." /> },
          { value: "decided", label: "Decided", content: <ReviewTable items={decided.data ?? []} loading={decided.isLoading} empty="Nothing decided yet." /> },
        ]}
      />
    </Page>
  );
}

function ReviewTable({ items, loading, empty }: { items: ReviewItem[]; loading: boolean; empty: string }) {
  const docs = useQueries({ queries: items.map((r) => ({ queryKey: ["document", r.documentId], queryFn: () => getJson<DocumentResult>(`/documents/${encodeURIComponent(r.documentId)}`) })) });
  if (loading) return <StateMessage kind="loading">Loading the queue…</StateMessage>;
  if (items.length === 0) return <StateMessage kind="empty">{empty}</StateMessage>;
  return (
    <div className="lr-datatable">
      <table className="lr-table review-table">
        <caption className="lr-sr-only">Review items</caption>
        <thead>
          <tr>
            <th scope="col">Document</th>
            <th scope="col">Why it is here</th>
            <th scope="col">Routing</th>
            <th scope="col">Opened</th>
            <th scope="col" className="num">Claims</th>
          </tr>
        </thead>
        <tbody>
          {items.map((r, i) => {
            const d = docs[i]?.data;
            return (
              <tr key={r.documentId}>
                <th scope="row">
                  <Link to="/reviews/$documentId" params={{ documentId: r.documentId }}>{d?.document.carrierName ?? r.documentId}</Link>
                  <div className="lr-meta">{d?.document.insuredName ?? r.documentId}</div>
                </th>
                <td className="review-table__reasons">{r.reasons.map(plainReason).join("; ")}</td>
                <td><RoutingBadge routing={{ tier: d?.routing?.tier ?? (r.kind === "confirm-format" ? "Learned" : "Review"), badge: r.badge, formatRef: r.formatRef, formatStatus: d?.routing?.formatStatus }} /></td>
                <td className="lr-num">{dateTime(r.decidedAt ?? r.openedAt)}</td>
                <td className="num">{d ? d.document.policyPeriods.reduce((a, p) => a + p.claims.length, 0) : "…"}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

/** Server reasons in plain words. */
export function plainReason(reason: string): string {
  return reason
    .replace(/^(\d+) hard rule failure\(s\) with every header mapped$/, (_, n) => `${n} printed total${n === "1" ? "" : "s"} disagree${n === "1" ? "s" : ""} with the claim rows`)
    .replace(/^(\d+) routing flag\(s\) raised$/, (_, n) => `${n} large-loss or data flag${n === "1" ? "" : "s"}`)
    .replace(/^deterministic fingerprint hit; all hard and soft rules pass$/, "New carrier format learned; every rule passes — confirm the column mapping")
    .replace(/^fingerprint miss \(best score ([\d.]+)\)$/, "Unknown layout (closest known format scored $1) — confirm the learned mapping");
}
