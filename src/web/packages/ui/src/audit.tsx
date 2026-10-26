import { useState } from "react";
import { Check, Copy, Lock } from "lucide-react";
import { dateTime } from "./format.ts";

export interface AuditRow {
  id?: number | null;
  at: string;
  user: string;
  role: string;
  action: string;
  documentId?: string | null;
  revision?: number | null;
  fieldPath?: string | null;
  oldValue?: string | null;
  newValue?: string | null;
  rationale?: string | null;
  ledgerTransactionId?: number | null;
  committedAt?: string | null;
}

const ACTIONS: Record<string, string> = {
  "upload.create": "Upload received",
  "extraction.persist": "Extracted and stored",
  "review.decision": "Review decided",
  override: "Override",
  "review.override": "Saved changes and approved",
  "review.accept": "Approved as extracted",
  "review.reject": "Rejected document",
  "review.reopened": "Reopened for review",
  rederive: "Re-derived",
  revalidate: "Re-validated",
  "format.published": "Format published",
  "format.approve": "Format confirmed",
  "format.retired": "Format retired",
  "quote.context": "Submission context changed",
  "quote.flags.pushed": "Flags pushed to quote",
  "quote.followup": "Follow-up assigned",
  "quote.memo.exported": "Pricing memo exported",
};

/**
 * The append-only audit trail (design-brief §4): time, user, role, field, old → new, rationale, revision. Never
 * editable; each row can be copied. The header states how the trail is protected ("Ledger verified …").
 */
export function AuditTrail({ entries, protection, lastDigestAt, caption = "Audit trail" }: { entries: AuditRow[]; protection: string; lastDigestAt?: string | null; caption?: string }) {
  return (
    <section className="lr-audit" aria-label={caption}>
      <p className="lr-audit__seal">
        <Lock size={16} aria-hidden="true" />
        {protection === "ledger"
          ? `Append-only SQL ledger table${lastDigestAt ? ` · digest stored ${dateTime(lastDigestAt)}` : ""} · each row shows the ledger transaction that committed it`
          : "Append-only log (local Blob mode; the SQL ledger applies on the deployed host)"}
      </p>
      {entries.length === 0 ? (
        <p className="lr-meta">No audit events yet.</p>
      ) : (
        <ol className="lr-audit__list">
          {entries.map((e, i) => (
            <AuditItem key={e.id ?? i} e={e} />
          ))}
        </ol>
      )}
    </section>
  );
}

function AuditItem({ e }: { e: AuditRow }) {
  const [copied, setCopied] = useState(false);
  const text = [dateTime(e.at), `${e.user} (${e.role})`, ACTIONS[e.action] ?? e.action, e.fieldPath, e.oldValue || e.newValue ? `${e.oldValue ?? "—"} → ${e.newValue ?? "—"}` : null, e.rationale, e.revision ? `revision ${e.revision}` : null, e.ledgerTransactionId ? `ledger tx ${e.ledgerTransactionId}` : null]
    .filter(Boolean)
    .join(" · ");
  const isJson = (v?: string | null) => !!v && /^[[{]/.test(v);
  return (
    <li className="lr-audit__item">
      <div className="lr-audit__line">
        <time dateTime={e.at} className="lr-meta lr-num">{dateTime(e.at)}</time>
        <strong>{ACTIONS[e.action] ?? e.action}</strong>
        <span className="lr-meta">{e.user} · {e.role}</span>
        {e.revision ? <span className="lr-meta">revision {e.revision}</span> : null}
        {e.ledgerTransactionId ? (
          <span className="lr-meta lr-audit__tx"><Lock size={12} aria-hidden="true" /> ledger tx {e.ledgerTransactionId}</span>
        ) : null}
        <button
          type="button"
          className="lr-btn lr-btn--sm lr-btn--quiet lr-audit__copy"
          onClick={async () => {
            try {
              await navigator.clipboard.writeText(text);
              setCopied(true);
              setTimeout(() => setCopied(false), 1500);
            } catch {
              /* clipboard unavailable (insecure context): nothing to do */
            }
          }}
        >
          {copied ? <Check size={14} aria-hidden="true" /> : <Copy size={14} aria-hidden="true" />}
          <span className="lr-sr-only">{copied ? "Copied" : "Copy audit row"}</span>
        </button>
      </div>
      {e.fieldPath && !e.fieldPath.startsWith("quotes/") ? <code className="lr-code">{e.fieldPath}</code> : null}
      {(e.oldValue || e.newValue) && !isJson(e.newValue) ? (
        <p className="lr-num">
          <span className="lr-sr-only">Changed from </span><del>{e.oldValue ?? "—"}</del> → <span className="lr-sr-only">to </span><ins>{e.newValue ?? "—"}</ins>
        </p>
      ) : null}
      {e.rationale ? <p className="lr-audit__why">“{e.rationale}”</p> : null}
    </li>
  );
}
