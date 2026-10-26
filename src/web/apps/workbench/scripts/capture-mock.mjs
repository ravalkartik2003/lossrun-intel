// Captures a coherent slice of the live API (synthetic data only) into docs/ui/mock/capture/ so mock mode (VITE_MOCK=1)
// serves real contract shapes offline: quotes, features, claims, context, audit, documents with page images and layouts,
// reviews, formats, costs, manifests, catalog. Credentials come from the environment and are never written out.
//   LOSSRUN_API_USER=underwriter LOSSRUN_API_PASSWORD=… node scripts/capture-mock.mjs Q-DEMO-11 Q-DEMO-11B Q-SAMPLE-10
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const quotes = process.argv.slice(2);
const base = (process.env.LOSSRUN_API_BASE ?? (() => { throw new Error("Set LOSSRUN_API_BASE (https://<function app host>/api/v1: terraform output function_app_hostname)."); })()).replace(/\/$/, "");
const auth = "Basic " + Buffer.from(`${process.env.LOSSRUN_API_USER}:${process.env.LOSSRUN_API_PASSWORD}`).toString("base64");
const out = new URL("../../../../../docs/ui/mock/capture/", import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, "$1");
const GUID_AT_GUID = /\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}@[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b/gi;

async function get(path, binary = false) {
  const res = await fetch(`${base}${path}`, { headers: { Authorization: auth } });
  if (!res.ok) throw new Error(`${res.status} ${path}`);
  return binary ? Buffer.from(await res.arrayBuffer()) : res.text();
}

/**
 * Older stored rows can name the environment's resource group (cost lines priced before the price table's wording was made
 * neutral; brief 24 step 7): the demo pack refuses Azure resource names, so the capture writes the neutral wording.
 */
const RESOURCE_GROUP = /\b[a-z0-9]+(?:-[a-z0-9]+)*-rg\b/g;

/** Writes JSON text, refusing anything that looks like a tenant-qualified principal (never capture tenant ids). */
function save(rel, text) {
  const file = join(out, rel);
  mkdirSync(join(file, ".."), { recursive: true });
  writeFileSync(file, typeof text === "string" ? text.replace(GUID_AT_GUID, "app managed identity").replace(RESOURCE_GROUP, "the POC resource group") : text);
}

mkdirSync(out, { recursive: true });
const documents = new Set();
const uploads = new Set();
const allQuotes = JSON.parse(await get("/quotes"));
save("quotes.json", JSON.stringify(allQuotes.filter((q) => quotes.includes(q.quoteId)), null, 1));
save("catalog.json", await get("/catalog"));
save("formats.json", await get("/formats"));
save("costs.json", await get("/costs"));

for (const q of quotes) {
  const dir = `quotes/${q}/`;
  for (const view of ["adjusted", "reported"]) {
    save(`${dir}features-${view}.json`, await get(`/quotes/${q}/features?view=${view}`));
    save(`${dir}claims-${view}.json`, await get(`/quotes/${q}/claims?view=${view}`));
  }
  save(`${dir}context.json`, await get(`/quotes/${q}/context`));
  save(`${dir}audit.json`, await get(`/quotes/${q}/audit`));
  save(`${dir}follow-ups.json`, await get(`/quotes/${q}/follow-ups`));
  const payload = await get(`/quotes/${q}/quote-payload`);
  save(`${dir}quote-payload.json`, payload);
  for (const d of JSON.parse(payload).documents) {
    documents.add(d.documentId);
    uploads.add(d.uploadId);
  }
}

const reviews = [];
for (const state of ["pending", "decided"]) {
  for (const r of JSON.parse(await get(`/reviews?state=${state}`))) {
    if (documents.has(r.documentId) || uploads.has(r.uploadId)) reviews.push(r);
  }
}
save("reviews.json", JSON.stringify(reviews, null, 1));

for (const id of documents) {
  const result = await get(`/documents/${id}`);
  save(`documents/${id}/result.json`, result);
  save(`documents/${id}/audit.json`, await get(`/documents/${id}/audit`));
  const lineage = JSON.parse(result).document.lineage ?? {};
  const pages = Math.max(1, ...Object.values(lineage).map((l) => l.page ?? 1));
  for (let p = 1; p <= pages; p++) {
    try {
      save(`documents/${id}/pages/${p}.png`, await get(`/documents/${id}/source/pages/${p}`, true));
    } catch (e) {
      // A spreadsheet source has no page images (409): its lineage is sheet + cell, served by /source/sheets.
      if (String(e.message).startsWith("409 ")) break;
      throw e;
    }
    save(`documents/${id}/pages/${p}.layout.json`, await get(`/documents/${id}/source/pages/${p}/layout`));
  }
}

for (const u of uploads) save(`uploads/${u}.json`, await get(`/uploads/${u}`));
console.log(`captured ${quotes.length} quotes, ${documents.size} documents, ${uploads.size} uploads, ${reviews.length} reviews → ${out}`);
