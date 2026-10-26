import { useEffect, useMemo, useRef, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import * as Collapsible from "@radix-ui/react-collapsible";
import { ChevronRight, Send, Upload, X } from "lucide-react";
import { Citation, SheetViewer, SourceViewer, usd, type Zoom } from "@lossrun/ui";
import { HttpError, postForm, sendJson } from "../api/client.ts";
import { askChat } from "../api/sse.ts";
import { useDocument, useLayout, useMe, usePageImage } from "../api/queries.ts";
import type { ChatCitation, ChatScope, ChatToolCall, ChatTurnResult, ChatWorkspace, UploadAccepted } from "../api/types.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle } from "../shell/hooks.ts";
import { useUi } from "../state/ui.ts";
import { regionsFor, sheetRegionsFor } from "../document/model.ts";

interface Entry {
  question: string;
  text: string;
  tools: ChatToolCall[];
  result?: ChatTurnResult;
  error?: string;
}

const SUGGESTIONS: Record<ChatScope, string[]> = {
  org: ["How many claims across all documents are open?", "Which documents are kept but not processed?"],
  quote: ["What is the largest open claim?", "What is the total incurred, and how much is still reserved?", "What does the broker's letter say?"],
  workspace: ["Summarize the files in my workspace.", "What is the total incurred in these loss runs?"],
};

const TOOL_LABEL: Record<ChatToolCall["tool"], string> = {
  search_documents: "Searched documents",
  query_claims: "Queried claims",
  compute_metric: "Computed metric",
};

/**
 * Chat (brief 12, design-brief §5.5): scope first, then the thread. The server enforces the scope in every tool; the
 * answer streams in, its citation chips open the cited page (claim citations at the cited value), and "How this was
 * answered" shows the tool calls as executed and the turn's metered cost.
 */
export function ChatScreen() {
  useTitle("Chat");
  const me = useMe();
  const quoteId = useUi((s) => s.quoteId);
  const applicant = me.data?.role === "Applicant";
  // 12 Answers: org-wide questions are for UWManager and Admin; Underwriters ask within a quote or their workspace.
  const orgWide = me.data?.role === "UWManager" || me.data?.role === "Admin";
  const [scope, setScope] = useState<ChatScope>("quote");
  const [workspace, setWorkspace] = useState<ChatWorkspace | null>(null);
  const [files, setFiles] = useState<{ name: string; uploadId: string }[]>([]);
  const [uploading, setUploading] = useState(false);
  const [thread, setThread] = useState<Entry[]>([]);
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [status, setStatus] = useState("");
  const [draft, setDraft] = useState("");
  const [cite, setCite] = useState<ChatCitation | null>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const abort = useRef<AbortController | null>(null);

  const effectiveScope: ChatScope = applicant ? "workspace" : scope === "org" && !orgWide ? "quote" : scope;
  const scopeKey = `${effectiveScope}|${effectiveScope === "quote" ? quoteId : effectiveScope === "workspace" ? workspace?.workspaceId : ""}`;

  // A new scope (or quote, or workspace) starts a new conversation: the server refuses to change a conversation's scope.
  useEffect(() => {
    abort.current?.abort();
    setThread([]);
    setConversationId(null);
    setStatus("");
  }, [scopeKey]);
  useEffect(() => () => abort.current?.abort(), []);

  const blocked =
    effectiveScope === "quote" && !quoteId ? "Pick a quote in the top bar first."
    : effectiveScope === "workspace" && !workspace ? "Add files to your workspace first."
    : null;

  const update = (index: number, change: (e: Entry) => Entry) => setThread((th) => th.map((e, i) => (i === index ? change(e) : e)));

  const ask = async (question: string) => {
    const q = question.trim();
    if (!q || busy || blocked) return;
    const index = thread.length;
    setThread((th) => [...th, { question: q, text: "", tools: [] }]);
    setDraft("");
    setBusy(true);
    setStatus("Answering…");
    const controller = new AbortController();
    abort.current = controller;
    try {
      // If the stream closes before the answer (the host's ~200 s limit), askChat polls the conversation for the turn.
      const how = await askChat(
        {
          scope: effectiveScope,
          question: q,
          quoteId: effectiveScope === "quote" ? quoteId : null,
          workspaceId: effectiveScope === "workspace" ? workspace?.workspaceId : null,
          conversationId,
        },
        (e) => {
          if (e.event === "tool") update(index, (x) => ({ ...x, tools: [...x.tools, e.data] }));
          else if (e.event === "delta") update(index, (x) => ({ ...x, text: x.text + e.data.text }));
          else if (e.event === "final") {
            update(index, (x) => ({ ...x, text: e.data.text, result: e.data }));
            setConversationId(e.data.conversationId);
          } else update(index, (x) => ({ ...x, error: e.data.message }));
        },
        controller.signal,
        { knownTurnIds: thread.flatMap((x) => (x.result ? [x.result.turnId] : [])) },
      );
      setStatus(how === "timeout" ? "The answer failed." : "Answer ready.");
    } catch (err) {
      if (controller.signal.aborted) return;
      const message = err instanceof HttpError ? err.message : "The assistant could not answer; try again.";
      update(index, (x) => ({ ...x, error: message }));
      setStatus("The answer failed.");
    } finally {
      setBusy(false);
      inputRef.current?.focus();
    }
  };

  const addFiles = async (list: FileList | null) => {
    if (!list?.length) return;
    setUploading(true);
    try {
      const ws = workspace ?? (await sendJson<ChatWorkspace>("POST", "/workspaces", { name: "Chat workspace" }));
      setWorkspace(ws);
      const form = new FormData();
      for (const f of Array.from(list)) form.append("file", f, f.name);
      form.append("workspaceId", ws.workspaceId);
      const accepted = await postForm<UploadAccepted>("/uploads", form);
      setFiles((fs) => [...fs, ...Array.from(list).map((f) => ({ name: f.name, uploadId: accepted.uploadId }))]);
      setStatus(`${list.length} file${list.length === 1 ? "" : "s"} added; answers include them once processed (about a minute).`);
    } catch (err) {
      setStatus(err instanceof HttpError ? `Upload failed: ${err.message}` : "Upload failed.");
    } finally {
      setUploading(false);
    }
  };

  const scopes: { value: ChatScope; label: string }[] = applicant
    ? [{ value: "workspace", label: `My workspace (${files.length} file${files.length === 1 ? "" : "s"})` }]
    : [
        ...(orgWide ? [{ value: "org" as const, label: "All documents" }] : []),
        { value: "quote", label: quoteId ? `This quote (${quoteId})` : "This quote" },
        { value: "workspace", label: `My workspace (${files.length} file${files.length === 1 ? "" : "s"})` },
      ];

  return (
    <Page title="Chat">
      <div className="lr-segmented chat__scope" role="group" aria-label="Answer from">
        {scopes.map((s) => (
          <button key={s.value} type="button" className="lr-segmented__item" aria-pressed={effectiveScope === s.value} onClick={() => setScope(s.value)}>
            {s.label}
          </button>
        ))}
      </div>
      <p className="lr-meta">
        The server enforces the scope: answers can only use and cite documents inside it.
        {effectiveScope === "workspace" && workspace ? ` Workspace expires ${new Date(workspace.expiresAt).toLocaleString()}.` : ""}
      </p>
      {effectiveScope === "workspace" ? (
        <div className="chat__workspace">
          <label className="lr-btn lr-btn--sm chat__file">
            <Upload size={16} aria-hidden="true" />
            {uploading ? "Uploading…" : "Add files to my workspace"}
            <input type="file" multiple className="lr-sr-only" disabled={uploading} onChange={(e) => void addFiles(e.target.files).then(() => (e.target.value = ""))} />
          </label>
          {files.length > 0 ? <ul className="chat__files" aria-label="Workspace files">{files.map((f, i) => <li key={i}>{f.name}</li>)}</ul> : null}
        </div>
      ) : null}
      <ol className="chat__thread" aria-label="Conversation" aria-busy={busy}>
        {thread.map((t, i) => (
          <li key={i} className="chat__turn">
            <p className="chat__q"><span className="lr-sr-only">You asked: </span>{t.question}</p>
            <Answer entry={t} pending={busy && i === thread.length - 1} onCite={setCite} />
          </li>
        ))}
      </ol>
      <p className="lr-sr-only" role="status">{status}</p>
      {blocked ? <p className="note">{blocked}</p> : null}
      <div className="chat__suggest" aria-label="Suggested questions">
        {SUGGESTIONS[effectiveScope].map((s) => (
          <button key={s} type="button" className="lr-btn lr-btn--sm" disabled={busy || !!blocked} onClick={() => void ask(s)}>{s}</button>
        ))}
      </div>
      <form
        className="chat__form"
        onSubmit={(e) => {
          e.preventDefault();
          void ask(draft);
        }}
      >
        <label className="lr-sr-only" htmlFor="chat-input">Ask a question</label>
        <textarea
          id="chat-input"
          ref={inputRef}
          className="lr-textarea"
          rows={2}
          maxLength={2000}
          value={draft}
          placeholder="Ask about these loss runs…"
          onChange={(e) => setDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter" && !e.shiftKey) {
              e.preventDefault();
              void ask(draft);
            }
          }}
        />
        <button type="submit" className="lr-btn lr-btn--primary" disabled={!draft.trim() || busy || !!blocked}><Send size={16} aria-hidden="true" />Ask</button>
      </form>
      <CitationDialog cite={cite} onClose={() => setCite(null)} />
    </Page>
  );
}

function Answer({ entry, pending, onCite }: { entry: Entry; pending: boolean; onCite: (c: ChatCitation) => void }) {
  const r = entry.result;
  return (
    <div className="chat__a">
      {entry.error ? <p className="chat__error">{entry.error}</p> : null}
      {entry.text ? <p className={r?.notFound ? "lr-muted" : undefined}>{entry.text}</p> : pending && !entry.error ? <p className="lr-muted">{entry.tools.length ? `${TOOL_LABEL[entry.tools.at(-1)!.tool]}…` : "Thinking…"}</p> : null}
      {r && r.citations.length > 0 ? (
        <div className="chat__cites" aria-label="Sources">
          {r.citations.map((c) =>
            c.documentId ? (
              <Citation key={c.ref} label={`${c.ref} · ${c.label}`} onOpen={() => onCite(c)} />
            ) : (
              <span key={c.ref} className="chat__metric">{c.ref} · {c.label}</span>
            ),
          )}
        </div>
      ) : null}
      {r || entry.tools.length ? (
        <Collapsible.Root className="chat__how">
          <Collapsible.Trigger className="lr-disclosure"><ChevronRight size={16} aria-hidden="true" className="lr-disclosure__icon" />How this was answered</Collapsible.Trigger>
          <Collapsible.Content>
            <ol className="chat__tools">
              {(r?.toolTrace ?? entry.tools).map((t, j) => (
                <li key={j}>
                  {TOOL_LABEL[t.tool]} · {t.results} result{t.results === 1 ? "" : "s"} · {t.ms} ms
                  {t.dropped > 0 ? ` · ${t.dropped} outside the scope removed` : ""}
                  <code className="chat__args">{t.arguments}</code>
                </li>
              ))}
            </ol>
            {r ? (
              <p className="lr-meta">
                {r.usage.modelCalls} model call{r.usage.modelCalls === 1 ? "" : "s"} · {r.usage.inputTokens.toLocaleString()} input tokens ({r.usage.cachedInputTokens.toLocaleString()} cached) ·{" "}
                {r.usage.outputTokens.toLocaleString()} output · first words {(r.usage.firstTokenMs / 1000).toFixed(1)} s · {(r.usage.totalMs / 1000).toFixed(1)} s total ·{" "}
                <strong>{usd(r.usage.costUsd, 4)}</strong>
              </p>
            ) : null}
            {r?.invalidCitations?.length ? <p className="lr-meta">Removed references no tool returned: {r.invalidCitations.join(", ")}.</p> : null}
            {r?.capNote ? <p className="lr-meta chat__cap">Limit reached: {r.capNote}.</p> : null}
          </Collapsible.Content>
        </Collapsible.Root>
      ) : null}
    </div>
  );
}

/** A citation opens the SourceViewer at the cited page; a claim citation also highlights the cited value. */
export function CitationDialog({ cite, onClose }: { cite: Pick<ChatCitation, "label" | "documentId" | "page" | "fieldPath"> | null; onClose: () => void }) {
  return (
    <Dialog.Root open={!!cite} onOpenChange={(o) => !o && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog__overlay" />
        <Dialog.Content className="dialog dialog--source lr-panel" aria-describedby={undefined}>
          <div className="dialog__head">
            <Dialog.Title>{cite?.label}</Dialog.Title>
            <Dialog.Close className="lr-btn lr-btn--sm lr-btn--quiet" aria-label="Close"><X size={16} aria-hidden="true" /></Dialog.Close>
          </div>
          {cite?.documentId ? <CitedPage documentId={cite.documentId} page={cite.page ?? null} path={cite.fieldPath ?? null} /> : null}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

function CitedPage({ documentId, page: citedPage, path }: { documentId: string; page: number | null; path: string | null }) {
  const doc = useDocument(documentId);
  const lineage = path ? doc.data?.document.lineage?.[path] : undefined;
  // A workbook cites sheet + cell: the sheet's layout page comes from the workbook's sheet list (first layout).
  const sheet = lineage?.sheet ?? null;
  const first = useLayout(documentId, 1);
  const sheetPage = sheet ? first.data?.sheets?.find((s) => s.sheetName === sheet)?.number ?? 1 : null;
  const page = sheetPage ?? citedPage ?? lineage?.page ?? 1;
  const layout = useLayout(documentId, page);
  const image = usePageImage(documentId, page, !sheet && !!doc.data);
  const [zoom, setZoom] = useState<Zoom>("fit");
  const regions = useMemo(() => (doc.data && path ? regionsFor(doc.data, page, new Set()).filter((r) => r.id === path) : []), [doc.data, page, path]);
  const sheetRegions = useMemo(() => (doc.data && path && sheet ? sheetRegionsFor(doc.data, sheet, new Set()).filter((r) => r.id === path) : []), [doc.data, path, sheet]);
  if (sheet) {
    return (
      <div className="dialog__source">
        <SheetViewer
          title={`Cited source: ${doc.data?.document.carrierName ?? documentId}`}
          sheets={[{ number: page, name: sheet }]}
          sheet={page}
          onSheetChange={() => undefined}
          cells={(layout.data?.cells ?? []).map((c) => ({ row: c.row, column: c.column, rowSpan: c.rowSpan, columnSpan: c.columnSpan, text: c.rawValue ?? c.content ?? "" }))}
          regions={sheetRegions}
          activeRegionIds={new Set(path ? [path] : [])}
          loading={first.isLoading || layout.isLoading}
        />
      </div>
    );
  }
  return (
    <div className="dialog__source">
      <SourceViewer
        title={`Cited source: ${doc.data?.document.carrierName ?? documentId}`}
        pages={[{ number: page, src: image.data, width: layout.data?.pageInfo.width ?? 8.5, height: layout.data?.pageInfo.height ?? 11 }]}
        page={page}
        onPageChange={() => undefined}
        regions={regions}
        activeRegionIds={new Set(path ? [path] : [])}
        zoom={zoom}
        onZoomChange={setZoom}
        loading={image.isLoading}
      />
    </div>
  );
}
