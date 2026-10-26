import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "@tanstack/react-router";
import * as Collapsible from "@radix-ui/react-collapsible";
import * as Dialog from "@radix-ui/react-dialog";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { ChevronRight, CircleAlert, RotateCcw, X } from "lucide-react";
import { CostLine, RoutingBadge, RuleOutcomeList, StateMessage, isoDate, proseDate, type RuleRow } from "@lossrun/ui";
import { useAssignFollowUp, useDocument, useDocumentAudit, useMe, useReopen, useReviews, useUpload } from "../api/queries.ts";
import type { DocumentResult } from "../api/types.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle, useWidth } from "../shell/hooks.ts";
import { useUi } from "../state/ui.ts";
import { Workbench } from "../document/Workbench.tsx";
import { LEDGER_COLUMNS, asReported, cellText, displayOutcomes, overridesOf } from "../document/model.ts";

export function DocumentScreen() {
  const { documentId } = useParams({ from: "/documents/$documentId" });
  const doc = useDocument(documentId);
  const me = useMe();
  const audit = useDocumentAudit(documentId, me.data?.canRead ?? me.data?.canReview ?? false);
  const width = useWidth();
  const setContextCost = useUi((s) => s.setContextCost);
  const [view, setView] = useState<"adjusted" | "reported">("adjusted");
  const [focusPath, setFocusPath] = useState<{ path: string; nonce: number } | null>(null);
  useTitle(doc.data ? `${doc.data.document.carrierName ?? "Document"} · ${documentId}` : "Document");

  const overrides = useMemo(() => overridesOf(audit.data?.entries), [audit.data]);
  const shown = useMemo(() => (doc.data && view === "reported" ? asReported(doc.data, overrides) : doc.data), [doc.data, view, overrides]);

  useEffect(() => {
    if (!doc.data) return;
    const c = doc.data.cost;
    setContextCost({ label: "This document", usd: c?.costUsd ?? null, pages: c?.pages || null, modelCalls: c?.llmCalls ?? doc.data.routing?.modelCalls ?? null });
    return () => setContextCost(null);
  }, [doc.data]);

  if (doc.isLoading) return <Page title="Document"><StateMessage kind="loading">Loading the document…</StateMessage></Page>;
  if (doc.isError || !shown) return <Page title="Document"><StateMessage kind="error">This document could not be loaded ({(doc.error as Error)?.message ?? "not found"}).</StateMessage></Page>;

  const d = shown;
  const overrideCount = [...overrides.values()].reduce((a, l) => a + l.length, 0);
  return (
    <Page
      wide
      title={<>{d.document.carrierName ?? "Loss run"} <span className="lr-meta">· {d.document.insuredName ?? ""}</span></>}
      actions={
        <>
          {overrideCount > 0 ? <ViewToggle view={view} onChange={setView} /> : null}
          {me.data?.canReview ? <ReopenDialog documentId={documentId} reviewState={d.reviewState} /> : null}
        </>
      }
    >
      <div className="doc-head">
        <RoutingBadge routing={{ tier: d.routing?.tier, badge: d.routing?.badge, formatRef: d.formatRef, formatStatus: d.routing?.formatStatus, modelCalls: d.routing?.modelCalls }} />
        <span>Valued {proseDate(d.document.valuationDate)}{d.document.valuationDateSource === "submission" ? " (as stated by the submitter)" : ""}</span>
        <span>Revision {d.revision}{overrideCount > 0 ? ` — ${overrideCount} override${overrideCount === 1 ? "" : "s"}` : ""}</span>
        {view === "reported" ? <span className="chip chip--learned">Showing as reported (before overrides)</span> : null}
        <CostLine usd={d.cost?.costUsd ?? null} pages={d.cost?.pages || null} modelCalls={d.cost?.llmCalls ?? d.routing?.modelCalls ?? 0} lines={d.cost?.breakdown} label="Document cost" />
      </div>
      {width === "small" ? (
        <SmallSummary doc={d} />
      ) : (
        <>
          <Workbench doc={d} overrides={overrides} focusPath={focusPath} />
          <RulesDock doc={d} onOpen={(r) => setFocusPath({ path: r.affectedFields.find((f) => !f.includes("*"))!, nonce: Date.now() })} />
          <KeptRowsPanel doc={d} />
        </>
      )}
    </Page>
  );
}

const reopenSchema = z.object({ reason: z.string().trim().min(8, "Say why it goes back to review (at least 8 characters).") });

/**
 * "Reopen for review…" (11 Answers 2, brief 12b): a processed document goes back to the review queue with a reason;
 * nothing changes until the reviewer decides. Underwriting roles only; hidden while a review is open.
 */
function ReopenDialog({ documentId, reviewState }: { documentId: string; reviewState?: string | null }) {
  const [open, setOpen] = useState(false);
  const reopen = useReopen(documentId);
  const pending = useReviews("pending");
  const navigate = useNavigate();
  const { register, handleSubmit, formState, reset } = useForm<z.infer<typeof reopenSchema>>({ resolver: zodResolver(reopenSchema), defaultValues: { reason: "" } });
  if (reviewState === "pending" || reviewState === "reopened" || pending.data?.some((r) => r.documentId === documentId)) return null;
  const err = formState.errors.reason;
  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger className="lr-btn"><RotateCcw size={16} aria-hidden="true" />Reopen for review…</Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog__overlay" />
        <Dialog.Content className="dialog lr-panel" aria-describedby="reopen-desc">
          <div className="dialog__head">
            <Dialog.Title>Reopen for review</Dialog.Title>
            <Dialog.Close className="lr-btn lr-btn--sm lr-btn--quiet" aria-label="Close"><X size={16} aria-hidden="true" /></Dialog.Close>
          </div>
          <Dialog.Description id="reopen-desc" className="lr-meta">
            The document goes back to the review queue. Its values stay as they are until a reviewer decides; the reason is recorded in the audit trail.
          </Dialog.Description>
          <form
            className="dialog__form"
            noValidate
            onSubmit={handleSubmit(async (v) => {
              await reopen.mutateAsync(v.reason);
              reset();
              setOpen(false);
              void navigate({ to: "/reviews/$documentId", params: { documentId } });
            })}
          >
            <div className="lr-field">
              <label className="lr-label" htmlFor="reopen-reason">Reason</label>
              <textarea id="reopen-reason" className="lr-textarea" placeholder="The broker sent a corrected 2023 loss run" aria-invalid={!!err}
                aria-describedby={err ? "reopen-reason-err" : undefined} {...register("reason")} />
              {err ? <p id="reopen-reason-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.message}</p> : null}
            </div>
            {reopen.isError ? <p className="lr-error" role="alert"><CircleAlert size={14} aria-hidden="true" />{(reopen.error as Error).message}</p> : null}
            <button type="submit" className="lr-btn lr-btn--primary" disabled={reopen.isPending}>Reopen</button>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function ViewToggle({ view, onChange }: { view: "adjusted" | "reported"; onChange: (v: "adjusted" | "reported") => void }) {
  return (
    <div className="lr-segmented" role="group" aria-label="Values shown">
      <button type="button" className="lr-segmented__item" aria-pressed={view === "reported"} onClick={() => onChange("reported")}>As reported</button>
      <button type="button" className="lr-segmented__item" aria-pressed={view === "adjusted"} onClick={() => onChange("adjusted")}>As adjusted</button>
    </div>
  );
}

/** Rule outcomes docked below the ledger, collapsible; failures offer "Send to review". */
function RulesDock({ doc, onOpen }: { doc: DocumentResult; onOpen: (r: RuleRow) => void }) {
  const [open, setOpen] = useState(true);
  const failures = doc.ruleOutcomes.filter((r) => r.status === "fail").length;
  const pending = useReviews("pending");
  const upload = useUpload(doc.uploadId);
  const navigate = useNavigate();
  const followUp = useAssignFollowUp(upload.data?.submission.quoteId ?? "");
  const [sent, setSent] = useState<string | null>(null);
  const inReview = pending.data?.some((r) => r.documentId === doc.documentId);

  const send = async (r: RuleRow) => {
    if (inReview) {
      await navigate({ to: "/reviews/$documentId", params: { documentId: doc.documentId } });
      return;
    }
    // Processing has finished, so the document cannot re-enter the review queue (brief 11 report, Questions); the
    // failure becomes an assigned follow-up on the quote instead, which is audited.
    const quoteId = upload.data?.submission.quoteId;
    if (!quoteId) return setSent("This upload has no quote, so there is nowhere to assign the follow-up.");
    await followUp.mutateAsync({ assignee: "review-queue", action: `Review ${r.ruleId} on ${doc.documentId}${r.claimId ? ` (${r.claimId})` : ""}`, note: r.message });
    setSent(`Follow-up assigned on ${quoteId}: review ${r.ruleId}.`);
  };

  return (
    <Collapsible.Root open={open} onOpenChange={setOpen} className="dock">
      <h2 className="dock__title">
        <Collapsible.Trigger className="lr-disclosure">
          <ChevronRight size={16} aria-hidden="true" className="lr-disclosure__icon" />
          Validation rules <span className="lr-meta">({failures} failing of {new Set(doc.ruleOutcomes.map((r) => r.ruleId)).size} rules)</span>
        </Collapsible.Trigger>
      </h2>
      <Collapsible.Content>
        {sent ? <p className="lr-meta" role="status">{sent}</p> : null}
        <RuleOutcomeList outcomes={displayOutcomes(doc)} onOpenInSource={onOpen} onSendToReview={(r) => void send(r)} />
      </Collapsible.Content>
    </Collapsible.Root>
  );
}

/** ADR 0020 amendment: rows of another line of business, per policy; listed with the reason, never in claims or totals. */
function KeptRowsPanel({ doc }: { doc: DocumentResult }) {
  const total = LEDGER_COLUMNS.find((c) => c.id === "total.incurred")!;
  const periods = doc.document.policyPeriods.map((p, i) => ({ p, i })).filter(({ p }) => (p.keptRows?.length ?? 0) > 0);
  if (periods.length === 0) return null;
  const count = periods.reduce((n, { p }) => n + p.keptRows!.length, 0);
  return (
    <section className="kept-rows lr-panel" aria-labelledby="kept-rows-title">
      <h2 id="kept-rows-title">Rows of other lines kept, not extracted ({count})</h2>
      <p className="lr-meta">Listed and counted. They are not claims: no metric, total or quote payload includes them.</p>
      {periods.map(({ p, i }) => (
        <table key={i} className="lr-table">
          <caption>Policy {p.policyNumber ?? "—"}</caption>
          <thead>
            <tr>
              <th scope="col">Claim</th>
              <th scope="col">Injury date</th>
              <th scope="col">Reason</th>
              <th scope="col" className="num">Total incurred</th>
            </tr>
          </thead>
          <tbody>
            {p.keptRows!.map((c, j) => (
              <tr key={j}>
                <td>{c.claimNumber ?? "—"}</td>
                <td>{isoDate(c.dateOfInjury)}</td>
                <td>{c.keptReason ?? "kept"}</td>
                <td className="num">{cellText(c, total)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ))}
    </section>
  );
}

/** Below 768px: a read-only claims list and the failing rules (brief 11 §5). */
function SmallSummary({ doc }: { doc: DocumentResult }) {
  const total = LEDGER_COLUMNS.find((c) => c.id === "total.incurred")!;
  return (
    <div className="small-summary">
      <p className="note">Open this document on a larger screen to trace values to the source page.</p>
      {doc.document.policyPeriods.map((p, i) => (
        <section key={i} aria-label={`Policy ${p.policyNumber}`}>
          <h2>Policy {p.policyNumber} · {isoDate(p.effectiveDate)}</h2>
          <ul className="small-summary__list">
            {p.claims.map((c, j) => (
              <li key={j}>
                <strong>{c.claimNumber}</strong> <span className="lr-meta">{isoDate(c.dateOfInjury)} · {c.status}</span>
                <span className="lr-num">Incurred {cellText(c, total)}</span>
              </li>
            ))}
          </ul>
        </section>
      ))}
      <KeptRowsPanel doc={doc} />
      <h2>Failing rules</h2>
      <ul className="small-summary__list">
        {doc.ruleOutcomes.filter((r) => r.status === "fail").map((r, i) => <li key={i}><strong>{r.ruleId}</strong> {r.message}</li>)}
      </ul>
      <p><Link to="/upload">Back to upload</Link></p>
    </div>
  );
}
