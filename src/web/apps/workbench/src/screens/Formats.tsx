import { useMemo, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { createColumnHelper } from "@tanstack/react-table";
import { useQueryClient } from "@tanstack/react-query";
import { CircleAlert, X } from "lucide-react";
import { DataTable, StateMessage, StatusBadge, dateTime, type Status } from "@lossrun/ui";
import { sendJson, HttpError } from "../api/client.ts";
import { useAliases, useFormats, useMe } from "../api/queries.ts";
import type { FormatSummary, LearnedAlias } from "../api/types.ts";
import { AliasStatusBadge, evidenceText, fieldLabel } from "../document/aliases.tsx";
import { Page } from "../shell/Shell.tsx";
import { useTitle } from "../shell/hooks.ts";

const helper = createColumnHelper<FormatSummary>();
const aliasHelper = createColumnHelper<LearnedAlias>();

const STATUS: Record<string, { status: Status; label: string }> = {
  active: { status: "verified", label: "Active" },
  shadow: { status: "learned", label: "Shadow (confirming)" },
  retired: { status: "kept", label: "Retired" },
};

function createdBy(s: string): string {
  const [kind, rest] = s.split(":");
  return kind === "hand" ? "Hand-written spec" : kind === "learn" ? `Learned (${rest?.split("@")[0] ?? "model"})` : kind === "delta" ? `Delta repair (${rest?.split("@")[0] ?? "model"})` : s;
}

/** Formats (design-brief §5.7): the registry — where "learned once" becomes visible. Retire with a reason (UWManager, Admin). */
export function FormatsScreen() {
  useTitle("Formats");
  const formats = useFormats();
  const me = useMe();
  const [retiring, setRetiring] = useState<FormatSummary | null>(null);
  const canRetire = me.data?.role === "Admin" || me.data?.role === "UWManager";

  const columns = useMemo(
    () => [
      helper.accessor("carrierName", { header: "Carrier", meta: { rowHeader: true } }),
      helper.accessor("formatId", { header: "Format" }),
      helper.accessor("version", { header: "Version", meta: { numeric: true } }),
      helper.accessor("status", { header: "Status", cell: (i) => { const s = STATUS[i.getValue()] ?? { status: "kept" as Status, label: i.getValue() }; return <StatusBadge status={s.status} label={s.label} />; } }),
      helper.accessor((r) => createdBy(r.createdBy), { id: "createdBy", header: "Created by" }),
      helper.accessor((r) => r.verificationGate ?? "—", { id: "gate", header: "Verification gate" }),
      helper.accessor((r) => r.documentsProcessed ?? null, { id: "docs", header: "Documents", meta: { numeric: true }, cell: (i) => i.getValue() ?? "—" }),
      helper.accessor((r) => r.modelCallsSaved ?? null, { id: "saved", header: "Model calls saved", meta: { numeric: true }, cell: (i) => i.getValue() ?? "—" }),
      helper.accessor("createdAt", { header: "Created", cell: (i) => dateTime(i.getValue()) }),
      helper.display({
        id: "actions",
        header: () => <span className="lr-sr-only">Actions</span>,
        cell: ({ row }) =>
          row.original.status !== "retired" && canRetire ? (
            <button type="button" className="lr-btn lr-btn--sm" onClick={() => setRetiring(row.original)}>
              Retire<span className="lr-sr-only"> {row.original.formatId} v{row.original.version}</span>
            </button>
          ) : null,
      }),
    ],
    [canRetire],
  );

  return (
    <Page title="Formats">
      <p className="lr-muted formats__intro">Each carrier layout is learned once, verified against its printed totals, then replayed with no model calls. Shadow formats need three confirmed documents before they become active.</p>
      {formats.isLoading ? <StateMessage kind="loading">Loading the registry…</StateMessage> : null}
      {formats.isError ? <StateMessage kind="error">The registry could not be loaded.</StateMessage> : null}
      {formats.data ? (
        <DataTable data={formats.data} columns={columns} caption={`Format registry (${formats.data.length} versions)`} getRowId={(r) => `${r.formatId}@${r.version}`} />
      ) : null}
      {!canRetire ? <p className="lr-meta">Retiring a format is for UWManager and Admin roles.</p> : null}
      <RetireDialog format={retiring} onClose={() => setRetiring(null)} />
      <LearnedAliases formatIds={[...new Set((formats.data ?? []).map((f) => f.formatId))]} />
    </Page>
  );
}

/**
 * Learned glossary aliases (ADR 0019): the printed headers learns mapped that the glossary did not know. A candidate is
 * used only once a reviewer confirms its format or it has verified on 3 documents across 2 formats; a correction retires it.
 */
function LearnedAliases({ formatIds }: { formatIds: string[] }) {
  const [formatId, setFormatId] = useState("");
  const aliases = useAliases(formatId || null);
  const columns = useMemo(
    () => [
      aliasHelper.accessor("header", { header: "Printed header", meta: { rowHeader: true }, cell: (i) => <span className="lr-num">“{i.getValue()}”</span> }),
      aliasHelper.accessor((a) => fieldLabel(a.canonicalField), { id: "field", header: "Maps to", cell: (i) => <>{i.getValue()} <span className="lr-meta">{i.row.original.canonicalField}</span></> }),
      aliasHelper.accessor("status", { header: "Status", cell: (i) => <AliasStatusBadge status={i.getValue()} /> }),
      aliasHelper.accessor((a) => `${a.formatId} v${a.formatVersion}`, { id: "introducedBy", header: "Learned by format" }),
      aliasHelper.accessor((a) => evidenceText(a), { id: "evidence", header: "Verified on" }),
      aliasHelper.accessor((a) => a.status === "retired" ? a.retiredReason ?? "retired" : a.activationReason ?? "awaiting a reviewer or more verifications", { id: "why", header: "Why" }),
    ],
    [],
  );
  return (
    <section className="aliases-registry" aria-labelledby="aliases-registry-title">
      <h2 id="aliases-registry-title">Learned glossary aliases</h2>
      <p className="lr-muted">Headers a verified learn mapped that the glossary did not know. Candidates are not used until a reviewer confirms their format or they verify on 3 documents across 2 formats; a correction retires one.</p>
      <div className="lr-field aliases-registry__filter">
        <label className="lr-label" htmlFor="alias-format">Format</label>
        <select id="alias-format" className="lr-select" value={formatId} onChange={(e) => setFormatId(e.target.value)}>
          <option value="">All formats</option>
          {formatIds.map((f) => <option key={f} value={f}>{f}</option>)}
        </select>
      </div>
      {aliases.isLoading ? <StateMessage kind="loading">Loading the learned aliases…</StateMessage> : null}
      {aliases.isError ? <StateMessage kind="error">The learned aliases could not be loaded.</StateMessage> : null}
      {aliases.data ? (
        <DataTable data={aliases.data} columns={columns} caption={`Learned aliases (${aliases.data.length})`} getRowId={(a) => a.id ?? `${a.header}|${a.canonicalField}`} />
      ) : null}
    </section>
  );
}

function RetireDialog({ format, onClose }: { format: FormatSummary | null; onClose: () => void }) {
  const [reason, setReason] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const client = useQueryClient();
  return (
    <Dialog.Root open={!!format} onOpenChange={(o) => { if (!o) { onClose(); setReason(""); setError(null); } }}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog__overlay" />
        <Dialog.Content className="dialog lr-panel" aria-describedby="retire-desc">
          <div className="dialog__head">
            <Dialog.Title>Retire {format?.formatId} v{format?.version}</Dialog.Title>
            <Dialog.Close className="lr-btn lr-btn--sm lr-btn--quiet" aria-label="Close"><X size={16} aria-hidden="true" /></Dialog.Close>
          </div>
          <Dialog.Description id="retire-desc">New documents from {format?.carrierName} will no longer replay this version; the next one goes to the learn path. Recorded in the audit trail.</Dialog.Description>
          <form
            className="dialog__form"
            noValidate
            onSubmit={async (e) => {
              e.preventDefault();
              if (reason.trim().length < 8) return setError("Give a reason of at least 8 characters.");
              setBusy(true);
              try {
                await sendJson("POST", `/formats/${encodeURIComponent(format!.formatId)}/versions/${format!.version}/retire`, { reason: reason.trim() });
                await client.invalidateQueries({ queryKey: ["formats"] });
                onClose();
                setReason("");
              } catch (err) {
                setError(err instanceof HttpError ? err.message : "The format could not be retired.");
              } finally {
                setBusy(false);
              }
            }}
          >
            <div className="lr-field">
              <label className="lr-label" htmlFor="retire-reason">Reason</label>
              <textarea id="retire-reason" className="lr-textarea" value={reason} onChange={(e) => setReason(e.target.value)} aria-invalid={!!error} aria-describedby={error ? "retire-err" : undefined} />
              {error ? <p id="retire-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{error}</p> : null}
            </div>
            <button type="submit" className="lr-btn lr-btn--danger" disabled={busy}>Retire format</button>
          </form>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
