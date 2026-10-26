import { API_BASE } from "../env.ts";
import { authorizationHeader } from "../auth/auth.ts";
import { HttpError, getJson } from "./client.ts";
import type { ApiError, ChatToolCall, ChatTurnRequest, ChatTurnResult, UploadManifest, UploadStatusView } from "./types.ts";

export type UploadEvent = { event: "progress"; data: UploadStatusView } | { event: "end"; data: { status: string } };

/**
 * Server-Sent Events over fetch. EventSource cannot send an Authorization header (or POST a body), so this parses the
 * stream itself; the same code reads the mock streams (MSW) and the live Function App. Return true from onEvent to stop.
 * Resolves true when onEvent stopped it, false when the server closed the stream first.
 */
async function readEvents(res: Response, onEvent: (event: string, data: string) => boolean | void): Promise<boolean> {
  if (!res.body) throw new Error("no response body");
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { done, value } = await reader.read();
    if (done) return false;
    buffer += decoder.decode(value, { stream: true });
    let i: number;
    while ((i = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, i);
      buffer = buffer.slice(i + 2);
      const event = /^event: (.*)$/m.exec(block)?.[1] ?? "message";
      const data = block.split("\n").filter((l) => l.startsWith("data: ")).map((l) => l.slice(6)).join("\n");
      if (data && onEvent(event, data) === true) {
        await reader.cancel();
        return true;
      }
    }
  }
}

/** Resolves after ms, or rejects when the signal aborts. */
function wait(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal.aborted) return reject(signal.reason);
    const t = setTimeout(() => {
      signal.removeEventListener("abort", stop);
      resolve();
    }, ms);
    const stop = () => {
      clearTimeout(t);
      reject(signal.reason);
    };
    signal.addEventListener("abort", stop, { once: true });
  });
}

/** Polling cadence once a stream closed early (the Functions host closes SSE at ~200 s; front-end limit 230 s). */
export interface PollOptions {
  pollMs?: number;
  /** Polls before giving up (default: 15 minutes at 3 s). */
  maxPolls?: number;
}

/** GET /uploads/{id}/events. Resolves true when the stream delivered `end`, false when it closed without it. */
export async function readUploadEvents(uploadId: string, onEvent: (e: UploadEvent) => void, signal: AbortSignal): Promise<boolean> {
  const res = await fetch(`${API_BASE}/uploads/${encodeURIComponent(uploadId)}/events`, {
    headers: { ...(await authorizationHeader()), Accept: "text/event-stream" },
    signal,
  });
  if (!res.ok) throw new Error(`events: HTTP ${res.status}`);
  return readEvents(res, (event, data) => {
    if (event === "progress" || event === "end") onEvent({ event, data: JSON.parse(data) } as UploadEvent);
    return event === "end";
  });
}

const RUNTIME_DONE = new Set(["Completed", "Failed", "Terminated"]);
const MANIFEST_DONE = new Set(["completed", "awaiting-review", "closed", "failed"]);

/**
 * Follows an upload to its end (brief 30 item 23): the event stream while it is open; when it closes without `end` (the
 * host closes it after ~200 s) or errors, polls GET /uploads/{id}/status and the manifest's status until either says the
 * upload is done, emitting the same progress/end events. Resolves "stream" | "poll", or "timeout" after maxPolls.
 */
export async function followUpload(uploadId: string, onEvent: (e: UploadEvent) => void, signal: AbortSignal, opts: PollOptions = {}): Promise<"stream" | "poll" | "timeout"> {
  try {
    if (await readUploadEvents(uploadId, onEvent, signal)) return "stream";
  } catch (e) {
    if (signal.aborted) throw e;
  }
  const id = encodeURIComponent(uploadId);
  for (let i = 0; i < (opts.maxPolls ?? 300); i++) {
    await wait(i === 0 ? 0 : opts.pollMs ?? 3000, signal);
    try {
      const status = await getJson<UploadStatusView>(`/uploads/${id}/status`, signal);
      onEvent({ event: "progress", data: status });
      const manifest = await getJson<UploadManifest>(`/uploads/${id}`, signal);
      if (RUNTIME_DONE.has(status.runtimeStatus) || MANIFEST_DONE.has(manifest.status)) {
        onEvent({ event: "end", data: { status: manifest.status } });
        return "poll";
      }
    } catch (e) {
      if (signal.aborted) throw e; // a failed poll is retried on the next tick
    }
  }
  return "timeout";
}

export type ChatEvent =
  | { event: "tool"; data: ChatToolCall }
  | { event: "delta"; data: { text: string } }
  | { event: "final"; data: ChatTurnResult }
  | { event: "error"; data: ApiError };

/**
 * POST /chat/turns as a stream: tool calls as they finish, answer text as it is generated, then the final turn.
 * Resolves true when `final` or `error` arrived, false when the server closed the stream before either.
 */
export async function streamChatTurn(request: ChatTurnRequest, onEvent: (e: ChatEvent) => void, signal: AbortSignal): Promise<boolean> {
  const res = await fetch(`${API_BASE}/chat/turns`, {
    method: "POST",
    headers: { ...(await authorizationHeader()), Accept: "text/event-stream", "Content-Type": "application/json" },
    body: JSON.stringify(request),
    signal,
  });
  if (!res.ok) {
    let body: ApiError | null = null;
    try {
      body = (await res.json()) as ApiError;
    } catch {
      /* not JSON */
    }
    throw new HttpError(res.status, body);
  }

  return readEvents(res, (event, data) => {
    if (event === "tool" || event === "delta" || event === "final" || event === "error") onEvent({ event, data: JSON.parse(data) } as ChatEvent);
    return event === "final" || event === "error";
  });
}

/** A new conversation id, sent with the first turn so the turn can be found again if its stream closes early. */
export const newConversationId = () => `conv-${crypto.randomUUID().replace(/-/g, "")}`;

/**
 * A chat turn that survives its stream closing (brief 30 item 23): the turn is sent with a conversation id (a new one
 * for a first turn; the API accepts it); when the stream closes or errors before `final`, polls
 * GET /chat/conversations/{id} for the recorded turn (the API writes every turn to dbo.ChatTurn) and emits it as
 * `final`. A refusal (HTTP error) is thrown as before; nothing found after maxPolls is an `error` event.
 */
export async function askChat(
  request: ChatTurnRequest,
  onEvent: (e: ChatEvent) => void,
  signal: AbortSignal,
  opts: PollOptions & { knownTurnIds?: readonly string[] } = {},
): Promise<"stream" | "poll" | "timeout"> {
  const conversationId = request.conversationId ?? newConversationId();
  try {
    if (await streamChatTurn({ ...request, conversationId }, onEvent, signal)) return "stream";
  } catch (e) {
    if (signal.aborted || e instanceof HttpError) throw e;
  }
  const known = new Set(opts.knownTurnIds ?? []);
  const question = request.question.trim();
  for (let i = 0; i < (opts.maxPolls ?? 40); i++) {
    await wait(opts.pollMs ?? 3000, signal);
    try {
      const conversation = await getJson<{ conversationId: string; turns: ChatTurnResult[] }>(`/chat/conversations/${encodeURIComponent(conversationId)}`, signal);
      const turn = [...conversation.turns].reverse().find((t) => !known.has(t.turnId) && t.question.trim() === question);
      if (turn) {
        onEvent({ event: "final", data: turn });
        return "poll";
      }
    } catch (e) {
      if (signal.aborted) throw e; // 404 until the turn is recorded
    }
  }
  onEvent({ event: "error", data: { code: "stream-closed", message: "The answer did not arrive before the connection closed; try again." } });
  return "timeout";
}
