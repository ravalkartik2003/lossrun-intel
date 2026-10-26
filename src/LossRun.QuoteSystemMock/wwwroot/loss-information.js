// Brief 25: the loss-information page's script (a file of its own, so the page runs under script-src 'self').
"use strict";
const params = new URLSearchParams(location.search);
// Brief 30 item 24: the grid and its stream are function-key routes; the page is opened with ?code=<key> and passes it on (EventSource sends no headers).
const keyQuery = params.get("code") ? `?code=${encodeURIComponent(params.get("code"))}` : "";
const quote = params.get("quote") || "";
const replay = params.has("replay");
const root = document.documentElement;
const themeButton = document.getElementById("theme");
function setTheme(t) {
  root.dataset.theme = t;
  themeButton.setAttribute("aria-pressed", String(t === "dark"));
  themeButton.textContent = t === "dark" ? "Light theme" : "Dark theme";
}
setTheme(params.get("theme") || (matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light"));
themeButton.addEventListener("click", () => setTheme(root.dataset.theme === "dark" ? "light" : "dark"));

const el = (tag, props = {}, ...children) => {
  const n = document.createElement(tag);
  for (const [k, v] of Object.entries(props)) {
    if (k === "class") n.className = v; else if (k in n) n[k] = v; else n.setAttribute(k, v);
  }
  for (const c of children) if (c != null) n.append(c);
  return n;
};
let tipId = 0;
function cell(c) {
  if (!c) return el("span", { class: "empty" }, "—");
  switch (c.state) {
    case "value": {
      const v = el("span", {}, c.value);
      return c.source ? el("span", {}, v, el("span", { class: "sub" }, c.source === "submission" ? "source: submission" : c.source)) : v;
    }
    case "needs-input": {
      const id = `tip-${++tipId}`;
      return el("span", { class: "needs", tabIndex: 0, "aria-describedby": id }, "Needs input", el("span", { class: "tip", role: "tooltip", id }, c.reason || "unresolved"));
    }
    case "not-from-loss-run":
      return el("span", { class: "marker", title: c.reason || "" }, "not from loss run");
    case "excluded":
      return el("span", { class: "marker" }, "excluded from quote");
    case "incomplete":
      // Brief 30 item 13: a total over claims that print no value is unknown, never 0; the count is shown.
      return el("span", { class: "empty" }, "—", el("span", { class: "sub" }, c.reason || "incomplete"));
    default:
      if (c.value) return el("span", { class: "marker", title: c.reason || "" }, c.reason || c.value); // e.g. "n/a (claim type not on the loss run)"
      return el("span", { class: "empty", title: c.reason || "" }, "—", el("span", { class: "sr-only" }, ` ${c.reason || "no value"}`));
  }
}

function render(g) {
  document.getElementById("title").textContent = `Loss Information · ${g.quoteId}`;
  const rev = document.getElementById("revision");
  rev.hidden = g.quoteRevision == null;
  rev.textContent = `Quote revision ${g.quoteRevision}`;
  const ready = document.getElementById("readiness");
  ready.hidden = g.ready == null;
  ready.className = `badge ${g.ready ? "ok" : "needs"}`;
  ready.textContent = g.ready ? "Ready" : `${g.blockingFieldCount} field${g.blockingFieldCount === 1 ? "" : "s"} need input`;
  document.getElementById("updated").textContent = g.updatedAt ? `Last update ${new Date(g.updatedAt).toLocaleString()}` : "";

  const grid = document.getElementById("grid");
  grid.replaceChildren();
  if (!g.columns.length) {
    grid.append(el("p", { class: "empty" }, "No loss runs received for this quote yet."));
  } else {
    const head = el("tr", {}, el("th", { scope: "col" }, "Policy period"));
    for (const c of g.columns) {
      const excluded = c.excludedReason === "rejected";
      head.append(el("th", { scope: "col", class: excluded ? "col-excluded" : "" }, c.title,
        excluded ? el("span", { class: "sub" }, "Excluded from quote (document rejected)") : null,
        c.excludedReason === "pending-review" ? el("span", { class: "sub" }, "Waiting for review") : null,
        c.effectiveDateNeedsInput ? el("span", { class: "sub" }, "Effective date needs input") : null));
    }
    const body = el("tbody");
    for (const row of g.rows) {
      const tr = el("tr", {}, el("th", { scope: "row" }, row.label));
      for (const c of g.columns) {
        const td = el("td", { class: c.excludedReason === "rejected" ? "col-excluded" : "" }, cell(c.cells[row.key]));
        if (row.key === "claims" && c.reportOnlyClaims > 0) td.append(el("span", { class: "sub" }, `+${c.reportOnlyClaims} report-only (no financials)`));
        tr.append(td);
      }
      body.append(tr);
    }
    const order = g.orderedByEffectiveDate === false
      ? "in the order the loss runs list them (an effective date needs input, so they are not sorted)"
      : "newest left";
    grid.append(el("table", {}, el("caption", {}, `One column per policy period, ${order}. Values come from the loss runs the integration sent; premium, payroll and Ex-Mod do not.`),
      el("thead", {}, head), body));
  }

  const att = document.getElementById("attachments");
  att.replaceChildren();
  if (g.attachments.length) {
    const tb = el("tbody");
    for (const a of g.attachments) tb.append(el("tr", {}, el("td", { class: "att" }, a.fileName), el("td", { class: "att" }, a.classification || "—"),
      el("td", { class: "att" }, a.status || "—"), el("td", { class: "att" }, a.reason || "")));
    att.append(el("table", {}, el("caption", {}, "Files of the uploads behind this quote"),
      el("thead", {}, el("tr", {}, ...["File", "Type", "Status", "Reason"].map(h => el("th", { scope: "col", class: "att" }, h)))), tb));
  } else {
    att.append(el("p", { class: "empty" }, "No attachments yet."));
  }

  const s = g.integration;
  const strip = document.getElementById("strip");
  strip.replaceChildren();
  for (const [k, v] of [["Last command", s.commandId], ["Idempotency key", s.idempotencyKey], ["Quote revision received", s.quoteRevision],
    ["Delivery", s.delivery], ["Received", s.receivedAt], ["Last event", s.lastEventType ? `${s.lastEventType}${s.lastEventRevision != null ? ` (revision ${s.lastEventRevision})` : ""}` : null],
    ["Last event at", s.lastEventAt], ["Older revisions refused", s.staleRefused]]) {
    strip.append(el("dt", {}, k), el("dd", {}, v == null || v === "" ? "—" : String(v)));
  }
  document.getElementById("live").textContent = g.quoteRevision != null ? `Loss information updated to quote revision ${g.quoteRevision}.` : "";
}

async function load() {
  const r = await fetch(`../quotes/${encodeURIComponent(quote)}/loss-information${keyQuery}`, { headers: { Accept: "application/json" } });
  if (r.status === 404) { document.getElementById("grid").replaceChildren(el("p", { class: "empty" }, `Quote ${quote} has received nothing yet.`)); return; }
  render(await r.json());
}

async function playRecording() {
  document.getElementById("mode").hidden = false;
  const steps = await (await fetch("replay.json")).json();
  for (const step of steps) {
    await new Promise(res => setTimeout(res, step.delayMs || 0));
    render(step.grid);
  }
}

if (replay) {
  playRecording();
} else {
  load();
  const stream = new EventSource(`../quotes/${encodeURIComponent(quote)}/loss-information/stream${keyQuery}`);
  stream.addEventListener("revision", () => load());
}
