import { usd } from "@lossrun/ui";

// Brief 30 item 23: an ECharts tooltip formatter's return value is set as HTML. Every value put into one (claim numbers,
// document ids, series names, rationale, labels) comes from documents or users, so it is escaped here. ECharts' own
// default tooltips escape names already; only our formatters need this.

const ENTITIES: Record<string, string> = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

export const escapeHtml = (value: unknown): string => String(value ?? "").replace(/[&<>"']/g, (c) => ENTITIES[c]);

/** Tooltip lines, each escaped, joined with <br/> (the only markup). */
export const tooltipHtml = (...lines: unknown[]): string => lines.map(escapeHtml).join("<br/>");

/** Large-loss scatter: [date of injury, incurred, claim number]. */
export const largeLossTooltip = (p: { data: [string, number, string] }) => tooltipHtml(p.data[2], `${p.data[0]} · ${usd(p.data[1])}`);

/** Cost-per-document scatter: [metered at, cost, document id] in a path series. */
export const costTooltip = (p: { data: [string, number, string]; seriesName: string }) => tooltipHtml(p.data[2], `${p.seriesName} · ${usd(p.data[1], 4)}`);
