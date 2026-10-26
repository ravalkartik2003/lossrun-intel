// Records real chat turns (POST /chat/turns as Server-Sent Events) from the live API for the Chat screen's mock mode
// (brief 12). Synthetic fixtures only. Credentials come from the environment and are never written out.
//   LOSSRUN_API_BASE=https://…/api/v1 LOSSRUN_API_USER=underwriter LOSSRUN_API_PASSWORD=… \
//   APPLICANT_PASSWORD=… node scripts/record-chat.mjs <quoteId> <out.json> <workspace-file>...
// Quote and org turns run as LOSSRUN_API_USER; workspace turns as the applicant, in a fresh workspace with the given files.
import { readFileSync, writeFileSync } from "node:fs";
import { basename } from "node:path";

const [quoteId, out, ...files] = process.argv.slice(2);
const base = (process.env.LOSSRUN_API_BASE ?? (() => { throw new Error("Set LOSSRUN_API_BASE (https://<function app host>/api/v1: terraform output function_app_hostname)."); })()).replace(/\/$/, "");
const basic = (user, password) => "Basic " + Buffer.from(`${user}:${password}`).toString("base64");
const underwriter = basic(process.env.LOSSRUN_API_USER, process.env.LOSSRUN_API_PASSWORD);
const applicant = basic("applicant", process.env.APPLICANT_PASSWORD);

const suggestions = {
  org: ["How many claims across all documents are open?", "Which documents are kept but not processed?"],
  quote: ["What is the largest open claim?", "What is the total incurred, and how much is still reserved?", "What does the broker's letter say?"],
  workspace: ["Summarize the files in my workspace.", "What is the total incurred in these loss runs?"],
};

async function turn(auth, request) {
  const t0 = Date.now();
  const res = await fetch(`${base}/chat/turns`, {
    method: "POST",
    headers: { Authorization: auth, Accept: "text/event-stream", "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
  const events = [];
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    let i;
    while ((i = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, i);
      buffer = buffer.slice(i + 2);
      const event = /^event: (.*)$/m.exec(block)?.[1] ?? "message";
      const data = block.split("\n").filter((l) => l.startsWith("data: ")).map((l) => l.slice(6)).join("\n");
      if (data) events.push({ atMs: Date.now() - t0, event, data: JSON.parse(data) });
    }
  }
  const final = events.find((e) => e.event === "final")?.data;
  console.log(`${request.scope} · ${request.question}\n  → ${final?.text ?? JSON.stringify(events.at(-1)?.data)}\n  $${final?.usage.costUsd}`);
  return { scope: request.scope, question: request.question, events };
}

const turns = [];
for (const question of suggestions.quote) turns.push(await turn(underwriter, { scope: "quote", quoteId, question }));
for (const question of suggestions.org) turns.push(await turn(underwriter, { scope: "org", question }));

if (files.length) {
  const ws = await (await fetch(`${base}/workspaces`, { method: "POST", headers: { Authorization: applicant, "Content-Type": "application/json" }, body: JSON.stringify({ name: "Recorded workspace" }) })).json();
  const form = new FormData();
  for (const f of files) form.append("file", new Blob([readFileSync(f)]), basename(f));
  form.append("workspaceId", ws.workspaceId);
  const accepted = await (await fetch(`${base}/uploads`, { method: "POST", body: form, headers: { Authorization: applicant } })).json();
  console.log(`workspace ${ws.workspaceId} upload ${accepted.uploadId}`);
  for (;;) {
    await new Promise((r) => setTimeout(r, 3000));
    const m = await (await fetch(`${base}/uploads/${accepted.uploadId}`, { headers: { Authorization: applicant } })).json();
    if (["completed", "awaiting-review", "failed"].includes(m.status)) break;
  }
  await new Promise((r) => setTimeout(r, 45_000)); // the chat-index queue message follows completion
  for (const question of suggestions.workspace) turns.push(await turn(applicant, { scope: "workspace", workspaceId: ws.workspaceId, question }));
}

writeFileSync(out, JSON.stringify({
  note: "Chat turns recorded from the live API (scripts/record-chat.mjs, brief 12) over synthetic documents; the mock replays their events. Ids are the live ones.",
  recordedAt: new Date().toISOString(),
  quoteId,
  suggestions,
  turns,
}, null, 2) + "\n");
console.log(`${turns.length} turns → ${out}`);
