import { useEffect, useMemo, useRef, useState, type DragEvent } from "react";
import { Link } from "@tanstack/react-router";
import { useForm, useFieldArray } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { CircleAlert, FileUp, Play, Plus, Trash2 } from "lucide-react";
import { CostLine, ManifestList, ProcessingStepper, RoutingBadge, StateMessage, classificationWord, type Step } from "@lossrun/ui";
import { getJson, postForm } from "../api/client.ts";
import { followUpload, type UploadEvent } from "../api/sse.ts";
import { useMe } from "../api/queries.ts";
import type { KeptRowItem, ManifestItem, UploadAccepted, UploadManifest, UploadStatusView } from "../api/types.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle } from "../shell/hooks.ts";
import { useUi } from "../state/ui.ts";
import recorded from "@mock/capture/upload-replay.json";

const ACCEPTED = ".pdf,.tif,.tiff,.png,.jpg,.jpeg,.xlsx,.xls,.csv,.zip";

const schema = z.object({
  quoteId: z.string().trim().max(64, "At most 64 characters.").optional(),
  carrierHint: z.string().trim().max(120).optional(),
  // Brief 23 Answers 2: used only when the loss run prints no valuation date; then R-32 flags it as stated by the submitter.
  valuationDate: z.union([z.literal(""), z.string().regex(/^\d{4}-\d{2}-\d{2}$/, "Use yyyy-mm-dd.")]).optional(),
  payroll: z
    .array(z.object({ effectiveDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, "Use yyyy-mm-dd."), amount: z.coerce.number({ message: "Enter an amount." }).min(0, "Payroll cannot be negative.") }))
    .optional(),
});
type Values = z.input<typeof schema>;

/** Stepper keys (design-brief §4) and the API progress steps that complete them. */
const STEPS: { key: string; label: string; api: string[] }[] = [
  { key: "classified", label: "Classified", api: [] },
  { key: "layout", label: "Layout", api: [] },
  { key: "match", label: "Format match", api: ["fingerprint"] },
  { key: "extracted", label: "Extracted", api: ["replay", "learn"] },
  { key: "validated", label: "Validated", api: ["validate"] },
  { key: "ready", label: "Ready", api: ["done"] },
];

interface DocProgress {
  /** API step → elapsed ms when first seen. */
  seen: Record<string, number>;
  last: string;
}

function stepsFor(p: DocProgress | undefined): Step[] {
  // The API's document steps: fingerprint (format match running; classified + layout are behind it), replay | learn
  // (extracting), validate, done. The active step is the one the last event names; everything before it is done. A
  // step's time is from its first event to the next step's first event.
  const RANK: Record<string, number> = { fingerprint: 2, replay: 3, learn: 3, validate: 4, done: 6 };
  const active = p ? RANK[p.last] ?? 0 : -1;
  const at = new Map<number, number>();
  for (const [step, ms] of Object.entries(p?.seen ?? {})) if (RANK[step] !== undefined) at.set(Math.min(RANK[step], 5), ms);
  const ranks = [...at.keys()].sort((a, b) => a - b);
  return STEPS.map((s, i) => {
    const next = ranks.find((r) => r > i);
    return {
      key: s.key,
      label: s.label,
      state: active < 0 ? "pending" : i < active ? "done" : i === active ? "active" : "pending",
      ms: at.has(i) && next !== undefined ? at.get(next)! - at.get(i)! : null,
    };
  });
}

export function UploadScreen() {
  useTitle("Upload");
  const me = useMe();
  const applicant = me.data?.role === "Applicant";
  const setContextCost = useUi((s) => s.setContextCost);
  const [files, setFiles] = useState<File[]>([]);
  const [dragging, setDragging] = useState(false);
  const [uploadId, setUploadId] = useState<string | null>(null);
  const [status, setStatus] = useState<UploadStatusView | null>(null);
  const [progress, setProgress] = useState<Record<string, DocProgress>>({});
  const [manifest, setManifest] = useState<UploadManifest | null>(null);
  const [running, setRunning] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [replaying, setReplaying] = useState(false);
  const [showExposures, setShowExposures] = useState(false);
  const abort = useRef<AbortController | null>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  const { register, control, handleSubmit, formState } = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { quoteId: "", carrierHint: "", payroll: [] } });
  const payroll = useFieldArray({ control, name: "payroll" });
  const err = formState.errors;

  useEffect(() => () => abort.current?.abort(), []);
  useEffect(() => {
    if (manifest?.cost) setContextCost({ label: "This upload", usd: manifest.cost.totalUsd, pages: manifest.cost.pages, modelCalls: manifest.cost.llmCalls });
    return () => setContextCost(null);
  }, [manifest?.cost?.totalUsd]);

  const onEvent = (e: UploadEvent, fetchManifest: () => Promise<void>) => {
    if (e.event === "progress") {
      setStatus(e.data);
      setProgress((prev) => {
        const next = { ...prev };
        for (const d of e.data.documents) {
          if (!d.documentId) continue;
          const p = next[d.documentId] ?? { seen: {}, last: d.step };
          next[d.documentId] = { seen: { ...p.seen, [d.step]: p.seen[d.step] ?? d.elapsedMs }, last: d.step };
        }
        return next;
      });
      void fetchManifest();
    } else {
      setRunning(false);
      setStatus((s) => (s ? { ...s, runtimeStatus: e.data.status } : s));
      void fetchManifest();
    }
  };

  async function start(values: Values) {
    setError(null);
    if (files.length === 0) {
      setError("Add at least one file to upload.");
      return;
    }
    reset();
    setRunning(true);
    const form = new FormData();
    for (const f of files) form.append("file", f, f.name);
    if (values.quoteId) form.append("quoteId", values.quoteId);
    if (values.carrierHint) form.append("carrierHint", values.carrierHint);
    if (values.valuationDate) form.append("valuationDate", values.valuationDate);
    if (values.payroll && values.payroll.length > 0) form.append("payrollByPeriod", JSON.stringify(values.payroll.map((p) => ({ effectiveDate: p.effectiveDate, amount: Number(p.amount) }))));
    try {
      const accepted = await postForm<UploadAccepted>("/uploads", form, `ui-${crypto.randomUUID()}`);
      setUploadId(accepted.uploadId);
      const ctl = new AbortController();
      abort.current = ctl;
      const fetchManifest = async () => setManifest(await getJson<UploadManifest>(`/uploads/${accepted.uploadId}`));
      // The stream closes after ~200 s on the host: followUpload then polls /status (and the manifest) until the end.
      const how = await followUpload(accepted.uploadId, (e) => onEvent(e, fetchManifest), ctl.signal);
      await fetchManifest();
      if (how === "timeout") setError("Still processing. The list below is the latest manifest; open the upload again later for the rest.");
    } catch (e) {
      if ((e as Error).name === "AbortError") return; // a new upload or the replay took over
      setError((e as Error).message);
    } finally {
      setRunning(false);
    }
  }

  function reset() {
    abort.current?.abort();
    setStatus(null);
    setProgress({});
    setManifest(null);
    setUploadId(null);
    setReplaying(false);
  }

  /** "Replay demo": plays a recorded live upload (two loss runs, a payroll page, a letter) through the same view. */
  async function replay() {
    reset();
    setReplaying(true);
    setRunning(true);
    const rec = recorded as unknown as { uploadId: string; events: { atMs: number; event: string; data: unknown }[]; manifest: UploadManifest };
    setUploadId(rec.uploadId);
    const ctl = new AbortController();
    abort.current = ctl;
    let t = rec.events[0].atMs;
    for (const e of rec.events) {
      await new Promise((r) => setTimeout(r, Math.min(2000, e.atMs - t)));
      if (ctl.signal.aborted) return;
      t = e.atMs;
      onEvent(e as UploadEvent, async () => {
        if (e.event === "end") setManifest(rec.manifest);
        else setManifest((m) => partialManifest(rec.manifest, (e.data as UploadStatusView).documents.map((d) => d.documentId!).filter(Boolean), m));
      });
    }
    setRunning(false);
  }

  const onDrop = (e: DragEvent) => {
    e.preventDefault();
    setDragging(false);
    setFiles((f) => [...f, ...Array.from(e.dataTransfer.files)]);
  };

  const items = manifest?.items ?? [];
  const lossRuns = items.filter((i) => i.classification === "LossRun");
  const others = items.filter((i) => i.classification !== "LossRun");

  return (
    <div
      className={`upload${dragging ? " upload--dragging" : ""}`}
      onDragEnter={(e) => {
        e.preventDefault();
        setDragging(true);
      }}
      onDragOver={(e) => e.preventDefault()}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target) setDragging(false);
      }}
      onDrop={onDrop}
    >
      <Page title={applicant ? "Upload your loss runs" : "Upload"} actions={<button type="button" className="lr-btn" onClick={() => void replay()} disabled={running}><Play size={16} aria-hidden="true" />Replay demo</button>}>
        <div className={`upload__grid${applicant ? " upload__grid--applicant" : ""}`}>
          <div className="upload__main">
            <form className="upload__form lr-panel" onSubmit={handleSubmit(start)} noValidate aria-label="New upload">
              <div className="dropzone" aria-describedby="drop-hint">
                <FileUp size={24} aria-hidden="true" />
                <p>
                  Drop files here or{" "}
                  <button type="button" className="linklike" onClick={() => fileInput.current?.click()}>choose files</button>
                </p>
                <p id="drop-hint" className="lr-hint">PDF (including scans and password-protected), images, Excel (.xlsx, .xls), CSV, or a ZIP of any of these.</p>
                <input
                  ref={fileInput}
                  id="upload-files"
                  type="file"
                  multiple
                  accept={ACCEPTED}
                  className="lr-sr-only"
                  aria-label="Choose files to upload"
                  onChange={(e) => setFiles((f) => [...f, ...Array.from(e.target.files ?? [])])}
                />
              </div>
              {files.length > 0 ? (
                <ul className="upload__files" aria-label="Files to upload">
                  {files.map((f, i) => (
                    <li key={i}>
                      <span>{f.name}</span>
                      <span className="lr-meta">{(f.size / 1024).toFixed(0)} KB</span>
                      <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => setFiles((all) => all.filter((_, j) => j !== i))} aria-label={`Remove ${f.name}`}>
                        <Trash2 size={14} aria-hidden="true" />
                      </button>
                    </li>
                  ))}
                </ul>
              ) : null}
              <div className="upload__fields">
                <div className="lr-field">
                  <label className="lr-label" htmlFor="up-quote">Quote number <span className="lr-meta">(optional)</span></label>
                  <input id="up-quote" className="lr-input" aria-invalid={!!err.quoteId} aria-describedby={err.quoteId ? "up-quote-err" : undefined} {...register("quoteId")} />
                  {err.quoteId ? <p id="up-quote-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.quoteId.message}</p> : null}
                </div>
                {!applicant ? (
                  <div className="lr-field">
                    <label className="lr-label" htmlFor="up-carrier">Carrier <span className="lr-meta">(optional)</span></label>
                    <input id="up-carrier" className="lr-input" aria-describedby="up-carrier-hint" {...register("carrierHint")} />
                    <p id="up-carrier-hint" className="lr-hint">When the report has no letterhead.</p>
                  </div>
                ) : null}
                <div className="lr-field">
                  <label className="lr-label" htmlFor="up-valuation">Valuation date <span className="lr-meta">(optional)</span></label>
                  <input id="up-valuation" type="date" className="lr-input" aria-invalid={!!err.valuationDate}
                    aria-describedby={err.valuationDate ? "up-valuation-hint up-valuation-err" : "up-valuation-hint"} {...register("valuationDate")} />
                  <p id="up-valuation-hint" className="lr-hint">Only used if the loss run prints no &ldquo;as of&rdquo; date. It is then shown as stated by you.</p>
                  {err.valuationDate ? <p id="up-valuation-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.valuationDate.message}</p> : null}
                </div>
              </div>
              {!applicant ? (
                <fieldset className="exposures">
                  <legend>
                    <button type="button" className="lr-disclosure" aria-expanded={showExposures} onClick={() => setShowExposures((s) => !s)}>Payroll by policy period <span className="lr-meta">(optional; makes frequency measurable)</span></button>
                  </legend>
                  {showExposures ? (
                    <>
                      {payroll.fields.map((f, i) => (
                        <div key={f.id} className="exposures__row">
                          <div className="lr-field">
                            <label className="lr-label" htmlFor={`pay-date-${i}`}>Effective date</label>
                            <input id={`pay-date-${i}`} className="lr-input" placeholder="2023-07-01" aria-invalid={!!err.payroll?.[i]?.effectiveDate} aria-describedby={err.payroll?.[i]?.effectiveDate ? `pay-date-${i}-err` : undefined} {...register(`payroll.${i}.effectiveDate`)} />
                            {err.payroll?.[i]?.effectiveDate ? <p id={`pay-date-${i}-err`} className="lr-error">{err.payroll[i]!.effectiveDate!.message}</p> : null}
                          </div>
                          <div className="lr-field">
                            <label className="lr-label" htmlFor={`pay-amt-${i}`}>Payroll ($)</label>
                            <input id={`pay-amt-${i}`} className="lr-input lr-input--num" inputMode="decimal" aria-invalid={!!err.payroll?.[i]?.amount} aria-describedby={err.payroll?.[i]?.amount ? `pay-amt-${i}-err` : undefined} {...register(`payroll.${i}.amount`)} />
                            {err.payroll?.[i]?.amount ? <p id={`pay-amt-${i}-err`} className="lr-error">{err.payroll[i]!.amount!.message}</p> : null}
                          </div>
                          <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => payroll.remove(i)} aria-label={`Remove payroll row ${i + 1}`}><Trash2 size={14} aria-hidden="true" /></button>
                        </div>
                      ))}
                      <button type="button" className="lr-btn lr-btn--sm" onClick={() => payroll.append({ effectiveDate: "", amount: "" as unknown as number })}><Plus size={14} aria-hidden="true" />Add a policy period</button>
                    </>
                  ) : null}
                </fieldset>
              ) : null}
              {error ? <p className="lr-error" role="alert"><CircleAlert size={14} aria-hidden="true" />{error}</p> : null}
              <div className="upload__submit">
                <button type="submit" className="lr-btn lr-btn--primary" disabled={running}>{running && !replaying ? "Processing…" : `Upload ${files.length || ""} file${files.length === 1 ? "" : "s"}`}</button>
                {uploadId ? <span className="lr-meta">Upload {uploadId}{replaying ? " (recorded)" : ""}{status ? ` · ${status.runtimeStatus.toLowerCase()}` : ""}</span> : null}
              </div>
            </form>

            {uploadId ? (
              <section aria-labelledby="manifest-title" className="upload__manifest">
                <h2 id="manifest-title">What we received</h2>
                {items.length === 0 ? (
                  <StateMessage kind="loading">{status?.upload?.detail ? `Opening files: ${status.upload.detail}` : "Opening the package…"}</StateMessage>
                ) : (
                  <ManifestList
                    items={items}
                    variant={applicant ? "applicant" : "underwriter"}
                    renderDetail={(item) => (item.classification === "LossRun" ? <LossRunDetail item={item as ManifestItem} progress={progress[item.documentId]} applicant={applicant} /> : null)}
                  />
                )}
              </section>
            ) : (
              <StateMessage kind="empty">Drop a loss-run package anywhere on this page to start. Each file and page is listed back to you as it is read.</StateMessage>
            )}
          </div>
          {applicant && manifest ? <ApplicantOutcome lossRuns={lossRuns} others={others} done={!running} /> : null}
        </div>
      </Page>
      {dragging ? <div className="drop-overlay" aria-hidden="true"><FileUp size={32} />Drop to add files</div> : null}
    </div>
  );
}

function LossRunDetail({ item, progress, applicant }: { item: ManifestItem; progress?: DocProgress; applicant: boolean }) {
  const steps = useMemo(() => stepsFor(progress ?? (item.status === "processed" || item.status === "awaiting-review" ? { seen: {}, last: "done" } : undefined)), [progress, item.status]);
  const done = steps[5].state === "done";
  return (
    <div className="lossrun-detail">
      <ProcessingStepper steps={steps} name={item.fileName} />
      {done && !applicant ? (
        <div className="lossrun-detail__result">
          {item.badge ? <RoutingBadge routing={{ tier: item.routingTier, badge: item.badge, formatRef: item.formatRef, formatStatus: item.formatStatus }} /> : null}
          <CostLine usd={item.costUsd ?? 0} modelCalls={/(\d+) LLM call/.exec(item.badge ?? "")?.[1] ? Number(/(\d+) LLM call/.exec(item.badge!)![1]) : undefined} label={`${item.fileName} cost`} />
          <span className="lr-meta">{item.claimCount ?? 0} claims</span>
          <Link to="/documents/$documentId" params={{ documentId: item.documentId }}>Open<span className="lr-sr-only"> {item.fileName}</span></Link>
        </div>
      ) : null}
      {done && !applicant && item.keptRows?.length ? <KeptRows rows={item.keptRows} fileName={item.fileName} /> : null}
    </div>
  );
}

/** ADR 0020 amendment: rows of another line of business in the document, listed and counted, never in claims or totals. */
function KeptRows({ rows, fileName }: { rows: KeptRowItem[]; fileName: string }) {
  return (
    <details className="kept-rows">
      <summary>
        {rows.length} row{rows.length === 1 ? "" : "s"} of other lines kept, not extracted<span className="lr-sr-only"> in {fileName}</span>
      </summary>
      <table className="lr-table">
        <caption className="lr-sr-only">Rows kept from {fileName}</caption>
        <thead>
          <tr>
            <th scope="col">Claim</th>
            <th scope="col">Policy</th>
            <th scope="col">Reason</th>
            <th scope="col">Page</th>
            <th scope="col" className="num">Total incurred</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={`${r.claimNumber ?? ""}-${i}`}>
              <td>{r.claimNumber ?? "—"}</td>
              <td>{r.policyNumber ?? "—"}</td>
              <td>{r.reason}</td>
              <td>{r.page ?? "—"}</td>
              <td className="num">{r.totalIncurred == null ? "—" : r.totalIncurred.toLocaleString("en-US", { style: "currency", currency: "USD" })}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </details>
  );
}

/** The applicant's right rail: plain-language outcome and next steps. */
function ApplicantOutcome({ lossRuns, others, done }: { lossRuns: ManifestItem[]; others: ManifestItem[]; done: boolean }) {
  const unreadable = others.filter((o) => o.status === "unsupported");
  const kept = others.filter((o) => o.status === "kept-not-processed");
  return (
    <aside className="outcome-rail lr-panel" aria-labelledby="outcome-title">
      <h2 id="outcome-title">{done ? "What happens next" : "Reading your files…"}</h2>
      <p>
        We found {lossRuns.length} loss run{lossRuns.length === 1 ? "" : "s"}
        {kept.length > 0 ? ` and ${kept.length} other document${kept.length === 1 ? "" : "s"} (${[...new Set(kept.map((k) => classificationWord(k.classification).toLowerCase()))].join(", ")})` : ""}.
      </p>
      {unreadable.length > 0 ? <p>{unreadable.length} file{unreadable.length === 1 ? "" : "s"} could not be read. Upload a clearer copy or a PDF export.</p> : null}
      {done ? <p>Check that every policy year you were asked for is here. If one is missing, upload it now, or continue without it — the underwriter will see what was provided.</p> : null}
    </aside>
  );
}

/** During a replay, show the recorded manifest's items as the events name them. */
function partialManifest(full: UploadManifest, seenIds: string[], prev: UploadManifest | null): UploadManifest {
  const ids = new Set([...(prev?.items.map((i) => i.documentId) ?? []), ...seenIds]);
  return { ...full, status: "processing", cost: null, items: full.items.filter((i) => ids.has(i.documentId) || i.classification !== "LossRun").map((i) => (seenIds.includes(i.documentId) || i.classification !== "LossRun" ? { ...i, status: i.classification === "LossRun" ? "processing" : i.status } : i)) };
}
