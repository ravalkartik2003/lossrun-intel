import { useMemo, useState, type FormEvent } from "react";
import { Link } from "@tanstack/react-router";
import { createColumnHelper } from "@tanstack/react-table";
import { useQueryClient } from "@tanstack/react-query";
import { CircleAlert } from "lucide-react";
import { DataTable, StateMessage, StatusBadge, dateTime, type Status } from "@lossrun/ui";
import { HttpError, sendJson } from "../api/client.ts";
import { useEscalations, useMe } from "../api/queries.ts";
import type { Escalation, EscalationStatus } from "../api/types.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle } from "../shell/hooks.ts";

const helper = createColumnHelper<Escalation>();

/** Reason codes (config/escalation.json triggers) in words. */
export const REASONS: Record<string, string> = {
  "learn-exhausted": "Learn attempts exhausted",
  "delta-exhausted": "Delta repair did not publish",
  "replay-failed-active-format": "Replay failed on an active format",
  "unhandled-exception": "Unhandled exception",
  "too-many-segments": "Too many segments",
  "cost-over-limit": "Cost over the limit",
  "repeated-review": "Same layout to review repeatedly",
  "rule-dominant": "One rule fails on most of a carrier's documents",
  "review-rate-spike": "Review rate above the carrier's baseline",
  "repeated-reopen": "Document reopened repeatedly",
  "manual-flag": "Flagged by a reviewer",
};

const STATUS: Record<EscalationStatus, { status: Status; label: string }> = {
  new: { status: "fail", label: "New" },
  investigating: { status: "review", label: "Investigating" },
  resolved: { status: "verified", label: "Resolved" },
  "wont-fix": { status: "kept", label: "Won't fix" },
};

/** Tuning-loop cause categories (docs/operations/tuning-loop.md, brief 27). */
const CATEGORIES: [string, string][] = [
  ["a", "Glossary gap"], ["b", "Layout not expressible"], ["c", "Classifier miss"], ["d", "Scan quality"], ["e", "Rule too strict or loose"],
  ["f", "Source inconsistency"], ["g", "Tool bug"],
];

/**
 * Needs investigation (brief 26, ADR 0025): cases the pipeline, the hourly pattern sweep or a reviewer sent to engineering.
 * One row per (reason, layout fingerprint), counted. Detail shows the trace (identifiers and counts only), the documents and
 * the format; a status change needs a note; "Create tuning item" puts the case into the next tuning batch under a cause.
 */
export function EscalationsScreen() {
  useTitle("Needs investigation");
  const me = useMe();
  const canTriage = me.data?.role === "Admin" || me.data?.role === "UWManager" || me.data?.role === "Engineer";
  const escalations = useEscalations(canTriage);
  const [status, setStatus] = useState("open");
  const [reason, setReason] = useState("");
  const [carrier, setCarrier] = useState("");
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const all = escalations.data ?? [];
  const carriers = useMemo(() => [...new Set(all.map((e) => e.carrier).filter((c): c is string => !!c))].sort(), [all]);
  const rows = all.filter((e) => (status === "" || (status === "open" ? e.status === "new" || e.status === "investigating" : e.status === status))
    && (reason === "" || e.reasonCode === reason) && (carrier === "" || e.carrier === carrier)); // generic-guard: allow (a user-chosen filter value, not a carrier branch)
  const selected = all.find((e) => e.id === selectedId) ?? null;

  const columns = useMemo(
    () => [
      helper.accessor((e) => REASONS[e.reasonCode] ?? e.reasonCode, { id: "reason", header: "Reason", meta: { rowHeader: true } }),
      helper.accessor((e) => e.formatRef ?? e.formatFingerprint, { id: "key", header: "Format or layout", cell: (i) => <span className="lr-num">{i.getValue()}</span> }),
      helper.accessor((e) => e.carrier ?? "—", { id: "carrier", header: "Carrier" }),
      helper.accessor("count", { header: "Occurrences", meta: { numeric: true } }),
      helper.accessor("status", { header: "Status", cell: (i) => <StatusBadge status={STATUS[i.getValue()].status} label={STATUS[i.getValue()].label} /> }),
      helper.accessor("lastSeen", { header: "Last seen", cell: (i) => dateTime(i.getValue()) }),
      helper.display({
        id: "open",
        header: () => <span className="lr-sr-only">Details</span>,
        cell: ({ row }) => (
          <button type="button" className="lr-btn lr-btn--sm" onClick={() => setSelectedId(row.original.id)} aria-pressed={row.original.id === selectedId}>
            Details<span className="lr-sr-only"> of {REASONS[row.original.reasonCode] ?? row.original.reasonCode}, {row.original.formatRef ?? row.original.formatFingerprint}</span>
          </button>
        ),
      }),
    ],
    [selectedId],
  );

  if (me.data && !canTriage) {
    return (
      <Page title="Needs investigation">
        <StateMessage kind="empty">The investigation queue is for Engineer, UWManager and Admin roles. Use “Flag for engineering” on a review to add a case.</StateMessage>
      </Page>
    );
  }

  return (
    <Page title="Needs investigation">
      <p className="lr-muted formats__intro">
        Documents and patterns the pipeline could not handle on its own: learn attempts exhausted, replay failures on active formats, unhandled errors, repeated
        reviews of one layout, rules failing on most of a carrier's documents, and cases reviewers flagged. Traces carry identifiers and counts only.
      </p>
      <div className="escalations__filters" role="group" aria-label="Filters">
        <div className="lr-field">
          <label className="lr-label" htmlFor="esc-status">Status</label>
          <select id="esc-status" className="lr-select" value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="open">Open (new or investigating)</option>
            <option value="">All</option>
            {Object.entries(STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </select>
        </div>
        <div className="lr-field">
          <label className="lr-label" htmlFor="esc-reason">Reason</label>
          <select id="esc-reason" className="lr-select" value={reason} onChange={(e) => setReason(e.target.value)}>
            <option value="">All reasons</option>
            {Object.entries(REASONS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </select>
        </div>
        <div className="lr-field">
          <label className="lr-label" htmlFor="esc-carrier">Carrier</label>
          <select id="esc-carrier" className="lr-select" value={carrier} onChange={(e) => setCarrier(e.target.value)}>
            <option value="">All carriers</option>
            {carriers.map((c) => <option key={c} value={c}>{c}</option>)}
          </select>
        </div>
      </div>
      {escalations.isLoading ? <StateMessage kind="loading">Loading the queue…</StateMessage> : null}
      {escalations.isError ? <StateMessage kind="error">The queue could not be loaded.</StateMessage> : null}
      {escalations.data ? (
        rows.length === 0 ? <StateMessage kind="empty">Nothing matches these filters.</StateMessage>
          : <DataTable data={rows} columns={columns} caption={`Escalations (${rows.length} of ${all.length})`} getRowId={(e) => e.id} />
      ) : null}
      {selected ? <EscalationDetail key={`${selected.id}@${selected.version}`} escalation={selected} /> : null}
    </Page>
  );
}

function EscalationDetail({ escalation: e }: { escalation: Escalation }) {
  const client = useQueryClient();
  const [status, setStatus] = useState<EscalationStatus>(e.status === "new" ? "investigating" : e.status);
  const [note, setNote] = useState("");
  const [category, setCategory] = useState("a");
  const [tuningNote, setTuningNote] = useState("");
  const [errors, setErrors] = useState<{ status?: string; tuning?: string }>({});
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const counts = Object.entries(e.traceSummary.counts ?? {});

  async function send(kind: "status" | "tuning", ev: FormEvent) {
    ev.preventDefault();
    const text = (kind === "status" ? note : tuningNote).trim();
    if (!text) return setErrors({ [kind]: "A note is required: it is recorded in the audit trail." });
    setBusy(true);
    setErrors({});
    try {
      if (kind === "status") {
        await sendJson("PATCH", `/escalations/${encodeURIComponent(e.id)}`, { status, note: text });
        setDone(`Status set to ${STATUS[status].label}.`);
        setNote("");
      } else {
        const r = await sendJson<{ tuningItem: { id: string } }>("POST", `/escalations/${encodeURIComponent(e.id)}/tuning-items`, { category, note: text });
        setDone(`Tuning item ${r.tuningItem.id} created; the next tuning batch reads it.`);
        setTuningNote("");
      }
      await client.invalidateQueries({ queryKey: ["escalations"] });
    } catch (err) {
      setErrors({ [kind]: err instanceof HttpError ? err.message : "The change could not be saved." });
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="escalation lr-panel" aria-labelledby="escalation-title">
      <h2 id="escalation-title">{REASONS[e.reasonCode] ?? e.reasonCode}</h2>
      <dl className="escalation__facts">
        <dt>Status</dt><dd><StatusBadge status={STATUS[e.status].status} label={STATUS[e.status].label} />{e.resolutionNote ? <span className="lr-meta"> · “{e.resolutionNote}”</span> : null}</dd>
        <dt>Occurrences</dt><dd>{e.count} · first {dateTime(e.firstSeen)} · last {dateTime(e.lastSeen)}</dd>
        <dt>Layout fingerprint</dt><dd className="lr-num">{e.formatFingerprint}</dd>
        <dt>Format</dt><dd>{e.formatRef ? <Link to="/formats">{e.formatRef}</Link> : "none matched"}</dd>
        <dt>Carrier</dt><dd>{e.carrier ?? "—"}</dd>
        <dt>Pipeline step</dt><dd>{e.traceSummary.step ?? "—"}{e.traceSummary.exceptionType ? <> · <span className="lr-num">{e.traceSummary.exceptionType}</span></> : null}</dd>
        <dt>Rules failing</dt><dd>{e.traceSummary.ruleIds.length ? e.traceSummary.ruleIds.join(", ") : "none"}</dd>
        {e.tuningItemRef ? <><dt>Tuning item</dt><dd className="lr-num">{e.tuningItemRef}</dd></> : null}
      </dl>
      {e.traceSummary.reasons.length ? (
        <>
          <h3>Reasons (numbers and quoted text removed)</h3>
          <ul className="escalation__reasons">{e.traceSummary.reasons.map((r, i) => <li key={i}>{r}</li>)}</ul>
        </>
      ) : null}
      {counts.length ? (
        <>
          <h3>Counts</h3>
          <dl className="escalation__counts">{counts.map(([k, v]) => <div key={k}><dt>{k}</dt><dd className="lr-num">{v}</dd></div>)}</dl>
        </>
      ) : null}
      <h3>Documents ({e.documentIds.length})</h3>
      {e.documentIds.length ? (
        <ul className="escalation__docs">{e.documentIds.map((d) => <li key={d}><Link to="/documents/$documentId" params={{ documentId: d }} className="lr-num">{d}</Link></li>)}</ul>
      ) : <p className="lr-meta">No single document (an exception or a carrier-level pattern); uploads: {e.uploadIds.join(", ") || "—"}.</p>}
      {done ? <p className="lr-meta" role="status">{done}</p> : null}
      <div className="escalation__forms">
        <form className="dialog__form" aria-labelledby="esc-status-title" noValidate onSubmit={(ev) => void send("status", ev)}>
          <h3 id="esc-status-title">Change status</h3>
          <div className="lr-field">
            <label className="lr-label" htmlFor="esc-new-status">New status</label>
            <select id="esc-new-status" className="lr-select" value={status} onChange={(ev) => setStatus(ev.target.value as EscalationStatus)}>
              {Object.entries(STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
            </select>
          </div>
          <div className="lr-field">
            <label className="lr-label" htmlFor="esc-note">Note</label>
            <textarea id="esc-note" className="lr-textarea" value={note} onChange={(ev) => setNote(ev.target.value)} aria-invalid={!!errors.status} aria-describedby={errors.status ? "esc-note-err" : undefined} />
            {errors.status ? <p id="esc-note-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{errors.status}</p> : null}
          </div>
          <button type="submit" className="lr-btn lr-btn--primary" disabled={busy}>Save status</button>
        </form>
        <form className="dialog__form" aria-labelledby="esc-tuning-title" noValidate onSubmit={(ev) => void send("tuning", ev)}>
          <h3 id="esc-tuning-title">Create tuning item</h3>
          <div className="lr-field">
            <label className="lr-label" htmlFor="esc-category">Cause category</label>
            <select id="esc-category" className="lr-select" value={category} onChange={(ev) => setCategory(ev.target.value)}>
              {CATEGORIES.map(([k, v]) => <option key={k} value={k}>({k}) {v}</option>)}
            </select>
          </div>
          <div className="lr-field">
            <label className="lr-label" htmlFor="esc-tuning-note">Note</label>
            <textarea id="esc-tuning-note" className="lr-textarea" value={tuningNote} onChange={(ev) => setTuningNote(ev.target.value)} aria-invalid={!!errors.tuning} aria-describedby={errors.tuning ? "esc-tuning-err" : undefined} />
            {errors.tuning ? <p id="esc-tuning-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{errors.tuning}</p> : null}
          </div>
          <button type="submit" className="lr-btn" disabled={busy}>Create tuning item</button>
        </form>
      </div>
    </section>
  );
}
