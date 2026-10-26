import { afterEach, describe, expect, it, vi } from "vitest";
import { askChat, followUpload, type ChatEvent, type UploadEvent } from "./sse.ts";
import type { ChatTurnResult, UploadStatusView } from "./types.ts";

// Brief 30 item 23: the Functions host closes Server-Sent Events after ~200 s. The upload and chat streams must then fall
// back to polling the status endpoint instead of showing nothing. The event source is mocked: a fetch whose stream
// sends some events and closes (or errors) before `end` / `final`.

const enc = new TextEncoder();
const sseBody = (blocks: string[], fail = false) =>
  new ReadableStream<Uint8Array>({
    start(c) {
      for (const b of blocks) c.enqueue(enc.encode(b));
      if (fail) c.error(new TypeError("network error"));
      else c.close();
    },
  });
const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
const stream = (blocks: string[], fail = false) => new Response(sseBody(blocks, fail), { headers: { "Content-Type": "text/event-stream" } });

const status = (runtimeStatus: string, step: string): UploadStatusView => ({ uploadId: "up-1", runtimeStatus, documents: [{ step, documentId: "doc-1", elapsedMs: 10, costSoFar: 0 }] });

/** Routes fetch by path; each route returns its responses in order (the last one repeats). */
function routes(table: Record<string, (() => Response)[]>) {
  const calls: { path: string; init?: RequestInit }[] = [];
  const left = Object.fromEntries(Object.entries(table).map(([k, v]) => [k, [...v]]));
  vi.stubGlobal("fetch", async (url: string, init?: RequestInit) => {
    const path = new URL(url, "http://x").pathname.replace(/^\/api\/v1/, "");
    calls.push({ path, init });
    const list = left[path];
    if (!list) return json({ code: "not-found", message: path }, 404);
    return (list.length > 1 ? list.shift()! : list[0])();
  });
  return calls;
}

afterEach(() => vi.unstubAllGlobals());

describe("upload events", () => {
  it("fall back to polling /status when the stream closes without end, until the upload is done", async () => {
    const calls = routes({
      "/uploads/up-1/events": [() => stream([`event: progress\ndata: ${JSON.stringify(status("Running", "classified"))}\n\n`])],
      "/uploads/up-1/status": [() => json(status("Running", "extracted")), () => json(status("Completed", "ready"))],
      "/uploads/up-1": [() => json({ uploadId: "up-1", status: "processing" }), () => json({ uploadId: "up-1", status: "completed" })],
    });
    const events: UploadEvent[] = [];
    const how = await followUpload("up-1", (e) => events.push(e), new AbortController().signal, { pollMs: 1 });
    expect(how).toBe("poll");
    expect(events.map((e) => (e.event === "progress" ? e.data.documents[0].step : `end:${e.data.status}`))).toEqual(["classified", "extracted", "ready", "end:completed"]);
    expect(calls.filter((c) => c.path === "/uploads/up-1/status")).toHaveLength(2);
  });

  it("fall back to polling when the stream errors", async () => {
    routes({
      "/uploads/up-1/events": [() => stream([], true)],
      "/uploads/up-1/status": [() => json(status("Running", "validated"))],
      "/uploads/up-1": [() => json({ uploadId: "up-1", status: "awaiting-review" })],
    });
    const events: UploadEvent[] = [];
    expect(await followUpload("up-1", (e) => events.push(e), new AbortController().signal, { pollMs: 1 })).toBe("poll");
    expect(events.at(-1)).toEqual({ event: "end", data: { status: "awaiting-review" } });
  });

  it("do not poll when the stream delivered end", async () => {
    const calls = routes({ "/uploads/up-1/events": [() => stream([`event: end\ndata: {"status":"completed"}\n\n`])] });
    expect(await followUpload("up-1", () => {}, new AbortController().signal, { pollMs: 1 })).toBe("stream");
    expect(calls.map((c) => c.path)).toEqual(["/uploads/up-1/events"]);
  });

  it("stop when aborted", async () => {
    routes({ "/uploads/up-1/events": [() => stream([])], "/uploads/up-1/status": [() => json(status("Running", "x"))], "/uploads/up-1": [() => json({ status: "processing" })] });
    const ctl = new AbortController();
    const run = followUpload("up-1", () => ctl.abort(), ctl.signal, { pollMs: 5 });
    await expect(run).rejects.toBeDefined();
  });
});

describe("chat turns", () => {
  const turn = (over: Partial<ChatTurnResult>): ChatTurnResult =>
    ({ turnId: "turn-2", conversationId: "conv-x", userId: "u", role: "Underwriter", scope: "quote", question: "What is the loss ratio?", text: "42%.", notFound: false, citations: [], toolTrace: [], usage: {}, at: "2026-10-25T00:00:00Z", ...over }) as ChatTurnResult;

  it("poll the conversation for the turn when the stream closes before final", async () => {
    const calls = routes({
      "/chat/turns": [() => stream([`event: delta\ndata: {"text":"4"}\n\n`])],
      "/chat/conversations/conv-x": [() => json({ code: "not-found", message: "" }, 404), () => json({ conversationId: "conv-x", turns: [turn({ turnId: "turn-1", question: "What is the loss ratio?" }), turn({})] })],
    });
    const events: ChatEvent[] = [];
    const how = await askChat({ scope: "quote", question: " What is the loss ratio? ", quoteId: "Q-1", conversationId: "conv-x" }, (e) => events.push(e), new AbortController().signal, { pollMs: 1, knownTurnIds: ["turn-1"] });
    expect(how).toBe("poll");
    expect(events.map((e) => e.event)).toEqual(["delta", "final"]);
    expect((events[1] as { data: ChatTurnResult }).data.turnId).toBe("turn-2");
    expect(calls.filter((c) => c.path === "/chat/turns")).toHaveLength(1); // never asks again (no second model run)
  });

  it("send a new conversation id with a first turn so it can be found again", async () => {
    const calls = routes({ "/chat/turns": [() => stream([], true)] });
    const events: ChatEvent[] = [];
    const how = await askChat({ scope: "quote", question: "Q", quoteId: "Q-1", conversationId: null }, (e) => events.push(e), new AbortController().signal, { pollMs: 1, maxPolls: 2 });
    const sent = JSON.parse(String(calls[0].init?.body)) as { conversationId: string };
    expect(sent.conversationId).toMatch(/^conv-[0-9a-f]{32}$/);
    expect(calls.slice(1).every((c) => c.path === `/chat/conversations/${sent.conversationId}`)).toBe(true);
    expect(how).toBe("timeout");
    expect(events.at(-1)).toMatchObject({ event: "error", data: { code: "stream-closed" } });
  });

  it("a refused turn is thrown, not polled", async () => {
    const calls = routes({ "/chat/turns": [() => json({ code: "forbidden", message: "no" }, 403)] });
    await expect(askChat({ scope: "org", question: "Q" }, () => {}, new AbortController().signal, { pollMs: 1 })).rejects.toMatchObject({ status: 403 });
    expect(calls).toHaveLength(1);
  });
});
