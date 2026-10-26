import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useBlocker, useParams } from "@tanstack/react-router";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import type { z } from "zod";
import { CircleAlert, Lock, Plus, Trash2, Undo2 } from "lucide-react";
import { AuditTrail, RoutingBadge, StateMessage, TabSet, dateTime } from "@lossrun/ui";
import { useAliases, useDecide, useDocument, useDocumentAudit, useMe, useReviews } from "../api/queries.ts";
import type { DocumentResult } from "../api/types.ts";
import { HttpError } from "../api/client.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle, useWidth } from "../shell/hooks.ts";
import { Workbench } from "../document/Workbench.tsx";
import { labelOf, contextOf, overridesOf, valueText } from "../document/model.ts";
import { AliasReview, type AliasChoice } from "../document/aliases.tsx";
import { EMPTY_DRAFT, decisionRequest, draftPaths, formDefaults, isDirty, isNumericPath, overrideSchema, planSave, toOverride, useReviewDraft, type DecisionKind, type ReviewDraft, offersFillDown, carrierNameQuestion } from "../review/draft.ts";
import { overrideText } from "../review/fieldTypes.ts";
import { plainReason } from "./ReviewQueue.tsx";
import { FlagForEngineering } from "../review/FlagForEngineering.tsx";

/** How long "Reset changes" can be undone. */
const UNDO_MS = 5000;

type Kind = DecisionKind;

/**
 * Review detail (design-brief §5.3; brief 24 step 4): the workbench with failing cells outlined, a correction form on the
 * selected value (new value + required rationale), and the decision. With nothing changed: accept the extracted values
 * (on a new format: confirm it and approve the document, or approve the document only) or reject. With changes (staged, or
 * typed and not yet added): save them and approve, reset them (undo for 5 s), or reject. Typed text is kept per review
 * when another cell is selected, shown as a draft on its cell, and leaving the screen with it asks first.
 */
export function ReviewDetailScreen() {
  const { documentId } = useParams({ from: "/reviews/$documentId" });
  const doc = useDocument(documentId);
  const audit = useDocumentAudit(documentId);
  const pending = useReviews("pending");
  const decided = useReviews("decided");
  const me = useMe();
  const width = useWidth();
  const decide = useDecide(documentId);
  const draft = useReviewDraft((s) => s.drafts[documentId]) ?? EMPTY_DRAFT;
  // Reset / Undo replace the draft from outside the open form: a new key re-opens it on the store's state.
  const epoch = useReviewDraft((s) => s.epochs[documentId] ?? 0);
  const store = useReviewDraft.getState;
  const [tab, setTab] = useState("document");
  const [promote, setPromote] = useState(false);
  const [fillDown, setFillDown] = useState(false);
  const [sameCarrier, setSameCarrier] = useState(false);
  const [decisionNote, setDecisionNote] = useState("");
  const [outcome, setOutcome] = useState<{ text: string; tone: "done" | "error" } | null>(null);
  const [undo, setUndo] = useState<ReviewDraft | null>(null);
  const [invalid, setInvalid] = useState<{ path: string; nonce: number } | null>(null);
  const undoTimer = useRef<number | undefined>(undefined);
  const [aliasChoices, setAliasChoices] = useState<Record<string, AliasChoice>>({});
  useTitle(`Review · ${documentId}`);

  const item = pending.data?.find((r) => r.documentId === documentId) ?? decided.data?.find((r) => r.documentId === documentId);
  const confirmFormatId = item?.kind === "confirm-format" ? item.formatRef?.split("@")[0] ?? null : null;
  const aliases = useAliases(confirmFormatId);
  const introduced = (aliases.data ?? []).filter((a) => a.formatId === confirmFormatId && a.status === "candidate");
  const overrides = useMemo(() => overridesOf(audit.data?.entries), [audit.data]);
  const drafts = useMemo(() => draftPaths(draft), [draft]);
  // Brief 26 Answers 3: an Engineer reads the review but cannot decide it or correct values.
  const canDecide = me.data?.canReview ?? false;
  const isPending = item?.state === "pending" && canDecide;
  const dirty = isPending && isDirty(draft);

  // Leaving with unsaved changes asks first; leaving anyway discards them.
  useBlocker({
    shouldBlockFn: () => {
      if (!dirty) return false;
      const leave = window.confirm("This review has unsaved changes. Leave and discard them?");
      if (leave) store().clear(documentId);
      return !leave;
    },
    enableBeforeUnload: () => dirty,
  });
  useEffect(() => () => window.clearTimeout(undoTimer.current), []);

  if (width === "small") return <Page title="Review"><p className="note">Reviewing needs a larger screen (768px or wider).</p></Page>;
  if (doc.isLoading || pending.isLoading) return <Page title="Review"><StateMessage kind="loading">Loading…</StateMessage></Page>;
  if (!doc.data) return <Page title="Review"><StateMessage kind="error">This document could not be loaded.</StateMessage></Page>;

  const d = doc.data;
  const carrier = d.document.carrierName ?? "this carrier";
  const confirmFormat = item?.kind === "confirm-format";
  // The format checkbox only where the decision can promote a format the reviewer did not come to confirm.
  const offerFormat = item?.kind === "document-review" && !!d.formatRef;
  // Brief 34 A3: the review found blank policy cells under a format that does not fill down.
  const askFillDown = offersFillDown(item);
  // Brief 35 Answers 2: the page names the carrier differently from the format.
  const carrierQuestion = carrierNameQuestion(item);

  const resetChanges = () => {
    const previous = store().reset(documentId);
    setInvalid(null);
    setUndo(previous);
    window.clearTimeout(undoTimer.current);
    undoTimer.current = window.setTimeout(() => setUndo(null), UNDO_MS);
  };
  const undoReset = () => {
    if (undo) store().restore(documentId, undo);
    window.clearTimeout(undoTimer.current);
    setUndo(null);
  };

  const submit = async (kind: Kind) => {
    setOutcome(null);
    setInvalid(null);
    let staged = draft.staged;
    if (kind === "save") {
      const plan = planSave(draft);
      if (!plan.ok) {
        setTab("document");
        setInvalid({ path: plan.invalidPath, nonce: Date.now() });
        setOutcome({ tone: "error", text: `Not saved: ${labelOf(plan.invalidPath)} · ${contextOf(d, plan.invalidPath)} has a change that is not valid (${plan.message}).` });
        return;
      }
      staged = plan.overrides;
    }
    try {
      await decide.mutateAsync(
        decisionRequest({
          kind,
          staged,
          confirmFormat,
          offerFormat,
          promote,
          note: decisionNote,
          fillDown: askFillDown && fillDown,
          addCarrierName: carrierQuestion && sameCarrier ? carrierQuestion.printed : undefined,
          aliases: confirmFormatId
            ? Object.entries(aliasChoices).map(([key, c]) => {
                const [header, canonicalField] = key.split("|");
                return { header, canonicalField, action: c.action, newField: c.action === "edit" ? c.newField : undefined };
              })
            : undefined,
        }),
      );
      store().clear(documentId);
      const revision = d.revision + 1;
      setOutcome({
        tone: "done",
        text:
          // Brief 24 Answers 3: corrections on a new-format review leave the format in shadow until a clean document confirms it.
          kind === "save" && confirmFormat ? `Approved with ${staged.length} change${staged.length === 1 ? "" : "s"}. The format stays in shadow until a clean document confirms it.`
          : kind === "save" ? `Saved ${staged.length} change${staged.length === 1 ? "" : "s"} and approved. Revision ${revision}.`
          : kind === "confirm" ? `Format confirmed for future ${carrier} documents and document approved. Revision ${revision}.`
          : kind === "approve-only" ? `Approved as extracted. The new format stays in shadow. Revision ${revision}.`
          : kind === "accept" ? `Approved as extracted. Revision ${revision}.`
          : "Rejected. Document kept in the upload, excluded from the quote.",
      });
      setTab("audit");
      setTimeout(() => void audit.refetch(), 1500);
    } catch (e) {
      setOutcome({ tone: "error", text: e instanceof HttpError ? e.message : "The decision could not be saved." });
    }
  };

  const busy = decide.isPending;
  const rejectButton = (
    <button type="button" className="lr-btn lr-btn--danger" disabled={busy || decisionNote.trim().length < 8} onClick={() => void submit("reject")} aria-describedby="decision-note-hint">
      Reject document
    </button>
  );

  return (
    <Page
      wide
      title={<>Review · {carrier} <span className="lr-meta">· {d.document.insuredName}</span></>}
      actions={<><FlagForEngineering documentId={documentId} /><Link to="/documents/$documentId" params={{ documentId }}>Open document view</Link></>}
    >
      <div className="doc-head">
        <RoutingBadge routing={{ tier: d.routing?.tier, badge: item?.badge ?? d.routing?.badge, formatRef: d.formatRef, formatStatus: d.routing?.formatStatus }} />
        {item ? <span>{confirmFormat ? "Confirm new format" : "Document review"} · opened {dateTime(item.openedAt)}</span> : null}
        <span>Revision {d.revision}</span>
      </div>
      {item ? (
        <ul className="reasons">
          {item.reasons.map((r, i) => <li key={i}><CircleAlert size={14} aria-hidden="true" className="lr-tone-review" /> {plainReason(r)}</li>)}
        </ul>
      ) : null}
      {outcome ? <p className={`lr-meta outcome${outcome.tone === "error" ? " outcome--error" : ""}`} role={outcome.tone === "error" ? "alert" : "status"}>{outcome.text}</p> : null}
      {undo ? (
        <div className="undo-toast" role="status">
          <span>Changes reset.</span>
          <button type="button" className="lr-btn lr-btn--sm" onClick={undoReset}><Undo2 size={14} aria-hidden="true" />Undo</button>
        </div>
      ) : null}
      <TabSet
        label="Review"
        value={tab}
        onValueChange={setTab}
        tabs={[
          {
            value: "document",
            label: "Document",
            content: (
              <>
                <Workbench
                  doc={d}
                  overrides={overrides}
                  drafts={drafts}
                  outlineFailures
                  focusPath={invalid}
                  panelExtras={isPending ? (path) => <OverrideForm key={`${path}#${epoch}`}documentId={documentId} doc={d} path={path} showErrors={invalid?.path === path ? invalid.nonce : 0} /> : undefined}
                />
                {isPending ? (
                  <section className="decision lr-panel" aria-labelledby="decision-title">
                    <h2 id="decision-title">Decision</h2>
                    {draft.staged.length > 0 ? (
                      <>
                        <h3>Audit entries that will be recorded</h3>
                        <ul className="decision__preview">
                          {draft.staged.map((o) => (
                            <li key={o.fieldPath}>
                              <Lock size={14} aria-hidden="true" />
                              <span><strong>{labelOf(o.fieldPath)}</strong> · {contextOf(d, o.fieldPath)}: <span className="lr-num">{o.oldValue} → {overrideText(o.newValue)}</span> · {me.data?.userId} ({me.data?.role}) · “{o.rationale}”</span>
                              <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => store().unstage(documentId, o.fieldPath)} aria-label={`Remove change of ${labelOf(o.fieldPath)}`}>
                                <Trash2 size={14} aria-hidden="true" />
                              </button>
                            </li>
                          ))}
                        </ul>
                      </>
                    ) : null}
                    {drafts.size > 0 ? (
                      <p className="lr-meta">
                        {drafts.size} value{drafts.size === 1 ? " has" : "s have"} typed changes not yet added ({[...drafts].map((p) => `${labelOf(p)} · ${contextOf(d, p)}`).join("; ")}). “Save changes and approve” adds {drafts.size === 1 ? "it" : "them"}.
                      </p>
                    ) : null}
                    {!dirty ? <p className="lr-meta">Select a value in the ledger or on the page to correct it. Failing values are outlined.</p> : null}
                    <div className="lr-field">
                      <label className="lr-label" htmlFor="decision-note">Decision note</label>
                      <textarea id="decision-note" className="lr-textarea" value={decisionNote} onChange={(e) => setDecisionNote(e.target.value)} aria-describedby="decision-note-hint" />
                      <p id="decision-note-hint" className="lr-hint">Required to reject; optional otherwise. Stored with the decision.</p>
                    </div>
                    {confirmFormatId && !dirty ? (
                      aliases.isLoading ? <StateMessage kind="loading">Loading the learned aliases…</StateMessage>
                        : <AliasReview aliases={introduced} choices={aliasChoices} onChoose={(key, c) => setAliasChoices((s) => ({ ...s, [key]: c }))} />
                    ) : null}
                    {askFillDown ? (
                      <div className="lr-field">
                        <label className="lr-check-row">
                          <input type="checkbox" className="lr-check" checked={fillDown} onChange={(e) => setFillDown(e.target.checked)} aria-describedby="fill-down-hint" />
                          This carrier's export fills the policy down (blank cells continue the period above)
                        </label>
                        <p id="fill-down-hint" className="lr-hint">
                          Publishes a new version of the format that reads a blank policy cell as the period above, and re-reads this document with it. The version stays in shadow until it is approved or three documents replay cleanly.
                        </p>
                      </div>
                    ) : null}
                    {carrierQuestion ? (
                      <div className="lr-field">
                        <label className="lr-check-row">
                          <input type="checkbox" className="lr-check" checked={sameCarrier} onChange={(e) => setSameCarrier(e.target.checked)} aria-describedby="carrier-name-hint" />
                          “{carrierQuestion.printed}” is the same carrier as “{carrierQuestion.known}”
                        </label>
                        <p id="carrier-name-hint" className="lr-hint">
                          Adds the name to the format's carrier names and re-reads this document. Later documents that print it are no longer sent to review for it. Leave it unticked if this is another company's name, such as the insured's.
                        </p>
                      </div>
                    ) : null}
                    {offerFormat ? (
                      <label className="lr-check-row">
                        <input type="checkbox" className="lr-check" checked={promote} onChange={(e) => setPromote(e.target.checked)} />
                        Also apply this document's format to future {carrier} documents
                      </label>
                    ) : null}
                    {confirmFormat && !dirty ? <p className="lr-meta" id="confirm-format-hint">This activates the learned format for future {carrier} documents.</p> : null}
                    {confirmFormat && dirty ? <p className="lr-meta">Saving corrections approves this document only: the learned format misread these values, so it stays in shadow until a clean document confirms it.</p> : null}
                    <div className="decision__actions">
                      {dirty ? (
                        <>
                          <button type="button" className="lr-btn lr-btn--primary" disabled={busy} onClick={() => void submit("save")}>Save changes and approve</button>
                          <button type="button" className="lr-btn" disabled={busy} onClick={resetChanges}>Reset changes</button>
                        </>
                      ) : confirmFormat ? (
                        <>
                          <button type="button" className="lr-btn lr-btn--primary" disabled={busy} onClick={() => void submit("confirm")} aria-describedby="confirm-format-hint">Confirm format and approve document</button>
                          <button type="button" className="lr-btn" disabled={busy} onClick={() => void submit("approve-only")}>Approve document only</button>
                        </>
                      ) : (
                        <button type="button" className="lr-btn lr-btn--primary" disabled={busy} onClick={() => void submit("accept")}>Accept extracted values</button>
                      )}
                      {rejectButton}
                    </div>
                  </section>
                ) : (
                  item?.state === "pending"
                    ? <p className="note">Read-only: this review is open. Deciding it and correcting values are for underwriting roles; “Flag for engineering” is available.</p>
                    : <p className="note">This review is decided{item?.decidedAt ? ` (${dateTime(item.decidedAt)})` : ""}. The audit trail shows what changed.</p>
                )}
              </>
            ),
          },
          {
            value: "audit",
            label: "Audit trail",
            content: audit.data ? <AuditTrail entries={audit.data.entries} protection={audit.data.protection} lastDigestAt={audit.data.lastDigestAt} /> : <StateMessage kind="loading">Loading the audit trail…</StateMessage>,
          },
        ]}
      />
    </Page>
  );
}

/**
 * The correction form for the selected value. Its text lives in the review's draft (not in this component), so selecting
 * another cell keeps it; "Add to decision" stages it.
 */
function OverrideForm({ documentId, doc, path, showErrors }: { documentId: string; doc: DocumentResult; path: string; showErrors: number }) {
  const current = valueText(doc, path);
  const original = current === "—" ? "" : current.replace(/[−]/, "-");
  const numeric = isNumericPath(path);
  const { register, handleSubmit, formState, reset, watch, trigger, setFocus } = useForm<z.infer<ReturnType<typeof overrideSchema>>>({
    resolver: zodResolver(overrideSchema(path)),
    defaultValues: formDefaults(useReviewDraft.getState().drafts[documentId], path, original),
  });
  useEffect(() => {
    const sub = watch((v) => useReviewDraft.getState().setForm(documentId, path, { newValue: v.newValue ?? "", rationale: v.rationale ?? "", original }));
    return () => sub.unsubscribe();
  }, [watch, documentId, path, original]);
  useEffect(() => {
    if (showErrors) void trigger().then(() => setFocus("newValue"));
  }, [showErrors, trigger, setFocus]);
  const err = formState.errors;
  return (
    <form
      className="override"
      noValidate
      onSubmit={handleSubmit((v) => {
        useReviewDraft.getState().stage(documentId, toOverride(path, v, current));
        reset({ newValue: v.newValue, rationale: "" });
        useReviewDraft.getState().setForm(documentId, path, null);
      })}
      aria-label={`Correct ${labelOf(path)}`}
    >
      <h3 className="lr-lineage__sub">Correct this value</h3>
      <div className="lr-field">
        <label className="lr-label" htmlFor="ov-value">New value</label>
        <input id="ov-value" className={`lr-input${numeric ? " lr-input--num" : ""}`} inputMode={numeric ? "decimal" : undefined} aria-invalid={!!err.newValue} aria-describedby={err.newValue ? "ov-value-err" : undefined} {...register("newValue")} />
        {err.newValue ? <p id="ov-value-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.newValue.message}</p> : null}
      </div>
      <div className="lr-field">
        <label className="lr-label" htmlFor="ov-why">Rationale</label>
        <textarea id="ov-why" className="lr-textarea" aria-invalid={!!err.rationale} aria-describedby={err.rationale ? "ov-why-err" : "ov-why-hint"} {...register("rationale")} />
        {err.rationale ? <p id="ov-why-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.rationale.message}</p> : <p id="ov-why-hint" className="lr-hint">Recorded in the audit trail with your name and role.</p>}
      </div>
      <button type="submit" className="lr-btn lr-btn--sm"><Plus size={14} aria-hidden="true" />Add to decision</button>
    </form>
  );
}
