// Records a real upload's Server-Sent Events from the live API for the Upload screen's "Replay demo" (brief 11 §6).
// Synthetic fixtures only. Credentials come from the environment and are never written out.
//   LOSSRUN_API_BASE=https://…/api/v1 LOSSRUN_API_USER=underwriter LOSSRUN_API_PASSWORD=… \
//   node scripts/record-upload.mjs <quoteId> <out.json> <file>...
import { readFileSync, writeFileSync } from "node:fs";
import { basename } from "node:path";

const [quoteId, out, ...files] = process.argv.slice(2);
const base = (process.env.LOSSRUN_API_BASE ?? (() => { throw new Error("Set LOSSRUN_API_BASE (https://<function app host>/api/v1: terraform output function_app_hostname)."); })()).replace(/\/$/, "");
const auth = "Basic " + Buffer.from(`${process.env.LOSSRUN_API_USER}:${process.env.LOSSRUN_API_PASSWORD}`).toString("base64");

const form = new FormData();
for (const f of files) form.append("file", new Blob([readFileSync(f)]), basename(f));
form.append("quoteId", quoteId);
if (process.env.PAYROLL_BY_PERIOD) form.append("payrollByPeriod", process.env.PAYROLL_BY_PERIOD);
if (process.env.FTE_BY_PERIOD) form.append("fteByPeriod", process.env.FTE_BY_PERIOD);

const t0 = Date.now();
const created = await fetch(`${base}/uploads`, { method: "POST", body: form, headers: { Authorization: auth, "Idempotency-Key": `record-${quoteId}-${t0}` } });
const accepted = await created.json();
if (!created.ok) throw new Error(`${created.status} ${JSON.stringify(accepted)}`);
console.log(`upload ${accepted.uploadId}`);

const events = [];
const res = await fetch(`${base}/uploads/${accepted.uploadId}/events`, { headers: { Authorization: auth, Accept: "text/event-stream" } });
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
    process.stdout.write(`${event} `);
  }
}
const manifest = await (await fetch(`${base}/uploads/${accepted.uploadId}`, { headers: { Authorization: auth } })).json();
writeFileSync(out, JSON.stringify({ recordedAt: new Date(t0).toISOString(), quoteId, uploadId: accepted.uploadId, files: files.map((f) => basename(f)), events, manifest }, null, 2));
console.log(`\n${events.length} events → ${out}`);
