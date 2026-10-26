import { useCallback, useMemo } from "react";
import { createColumnHelper } from "@tanstack/react-table";
import { DataTable, StateMessage, count, dateTime, percent, usd } from "@lossrun/ui";
import { useCosts } from "../api/queries.ts";
import type { CostSummary } from "../api/types.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle } from "../shell/hooks.ts";
import { ChartFrame, EChart, chartTokens } from "../analytics/charts.tsx";
import { costTooltip } from "../analytics/tooltip.ts";

type Tokens = ReturnType<typeof chartTokens>;
type Line = CostSummary["documents"][number];
const helper = createColumnHelper<Line>();

const PATH_WORD: Record<string, string> = { replay: "Replay (format memory)", learn: "Learn", delta: "Delta repair", review: "Review" };

/** Costs (design-brief §5.6): the one 40px number, cost per document by path over time, learn-once savings, the cost lines. */
export function CostsScreen() {
  useTitle("Costs");
  const costs = useCosts();
  const c = costs.data;

  const columns = useMemo(
    () => [
      helper.accessor("documentId", { header: "Document", meta: { rowHeader: true } }),
      helper.accessor((r) => r.carrier ?? "—", { id: "carrier", header: "Carrier" }),
      helper.accessor((r) => PATH_WORD[r.path] ?? r.path, { id: "path", header: "Path" }),
      helper.accessor("pages", { header: "Pages", meta: { numeric: true } }),
      helper.accessor("modelCalls", { header: "Model calls", meta: { numeric: true } }),
      helper.accessor("costUsd", { header: "Cost", meta: { numeric: true }, cell: (i) => usd(i.getValue(), 4) }),
      helper.accessor((r) => r.lines.map((l) => `${l.service} ${l.meter} × ${l.quantity}`).join("; ") || "—", { id: "meters", header: "Meters" }),
      helper.accessor("computedAt", { header: "Metered", cell: (i) => (i.getValue().startsWith("0001") ? "—" : dateTime(i.getValue())) }),
    ],
    [],
  );

  const byTime = useCallback(
    (t: Tokens) => {
      const paths = [...new Set((c?.documents ?? []).map((d) => d.path))];
      const color: Record<string, string> = { replay: t.action, learn: t.learned, delta: t.review, review: t.ink3 };
      return {
        grid: { left: 64, right: 16, top: 32, bottom: 28 },
        legend: { top: 0, textStyle: { color: t.ink2 } },
        tooltip: { trigger: "item", formatter: costTooltip },
        xAxis: { type: "time", axisLabel: { color: t.ink2 } },
        yAxis: { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usd(v, 3) }, splitLine: { lineStyle: { color: t.line } } },
        series: paths.map((p) => ({ name: PATH_WORD[p] ?? p, type: "scatter", symbolSize: 6, data: (c?.documents ?? []).filter((d) => d.path === p && !d.computedAt.startsWith("0001")).map((d) => [d.computedAt, d.costUsd, d.documentId]), itemStyle: { color: color[p] ?? t.ink2 } })),
      };
    },
    [c],
  );

  if (costs.isLoading) return <Page title="Costs"><StateMessage kind="loading">Loading metered costs…</StateMessage></Page>;
  if (!c) return <Page title="Costs"><StateMessage kind="error">Costs could not be loaded.</StateMessage></Page>;

  const carriers = c.byCarrier.filter((x) => x.replayDocs > 0 || x.learnUsd > 0);
  return (
    <Page title="Costs">
      <section className="costs-hero" aria-label="POC to date">
        <p className="costs-hero__label">Metered cost, POC to date</p>
        <p className="costs-hero__value lr-num">{usd(c.pocToDateUsd)}</p>
        <ul className="lr-kpis costs-hero__kpis" aria-label="Cost indicators">
          <li className="lr-kpi"><span className="lr-kpi__label">Documents processed</span><span className="lr-kpi__value lr-num">{count(c.documentsProcessed)}</span></li>
          <li className="lr-kpi"><span className="lr-kpi__label">Average per document</span><span className="lr-kpi__value lr-num">{usd(c.avgCostPerDocumentUsd, 4)}</span></li>
          <li className="lr-kpi"><span className="lr-kpi__label">Zero model calls</span><span className="lr-kpi__value lr-num">{percent(c.zeroModelCallShare)}</span></li>
          {c.byPath.map((p) => (
            <li key={p.path} className="lr-kpi"><span className="lr-kpi__label">{PATH_WORD[p.path] ?? p.path}: mean</span><span className="lr-kpi__value lr-num">{usd(p.meanUsd, 4)}</span><span className="lr-kpi__note">{count(p.documents)} documents · {count(p.modelCalls)} model calls</span></li>
          ))}
        </ul>
        <p className="costs-hero__recon">
          {c.reconciliation
            ? `AI services, ${c.reconciliation.from ? `${c.reconciliation.from} to ` : ""}${c.reconciliation.exportDate}: metered ${usd(c.reconciliation.meteredUsd)} · billed ${usd(c.reconciliation.billedUsd)} · ${percent(c.reconciliation.ratio, 0)} of the bill${c.reconciliation.platformBilledUsd != null ? ` · platform (standing) ${usd(c.reconciliation.platformBilledUsd)}` : ""}.${c.reconciliation.scope ? ` ${c.reconciliation.scope}` : ""}`
            : "No reconciliation to the Azure bill has been published yet; this page shows metered cost only."}
        </p>
      </section>
      <div className="charts charts--two">
        <ChartFrame
          title="Cost per document over time, by path"
          table={{ columns: ["Document", "Path", "Metered", "Cost"], rows: c.documents.slice(0, 200).map((d) => [d.documentId, PATH_WORD[d.path] ?? d.path, dateTime(d.computedAt), usd(d.costUsd, 4)]), numeric: [false, false, false, true] }}
        >
          <EChart option={byTime} summary={`${c.documents.length} documents; ${c.byPath.map((p) => `${PATH_WORD[p.path] ?? p.path} mean ${usd(p.meanUsd, 4)}`).join(", ")}.`} />
        </ChartFrame>
        <ChartFrame
          title="Learn once vs replay, by carrier"
          note="Savings = replays × (learn cost − replay cost). The break-even is the document where the learn has paid for itself."
          table={{ columns: ["Carrier", "Learn", "Replay mean", "Replays", "Saved", "Break-even doc"], rows: carriers.map((x) => [x.carrier, usd(x.learnUsd, 4), usd(x.replayMeanUsd, 4), x.replayDocs, usd(x.replaySavingsUsd, 4), x.breakEvenDoc ?? "—"]), numeric: [false, true, true, true, true, true] }}
        >
          {carriers.length === 0 ? (
            <p className="chart__na">No carrier has both a learn and replays yet.</p>
          ) : (
            <EChart
              summary={`Replay savings by carrier: ${carriers.map((x) => `${x.carrier} ${usd(x.replaySavingsUsd, 4)} over ${x.replayDocs} replays`).join("; ")}.`}
              option={(t: Tokens) => ({
                grid: { left: 180, right: 32, top: 8, bottom: 28 },
                tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
                xAxis: { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usd(v, 2) }, splitLine: { lineStyle: { color: t.line } } },
                yAxis: { type: "category", inverse: true, data: carriers.map((x) => x.carrier), axisLabel: { color: t.ink2, width: 170, overflow: "truncate" } },
                series: [
                  { name: "Saved by replay", type: "bar", data: carriers.map((x) => x.replaySavingsUsd), itemStyle: { color: t.action }, label: { show: true, position: "right", color: t.ink2, formatter: (p: { dataIndex: number }) => (carriers[p.dataIndex].breakEvenDoc ? `break-even at doc ${carriers[p.dataIndex].breakEvenDoc}` : "") } },
                ],
              })}
            />
          )}
        </ChartFrame>
      </div>
      <h2 className="costs__lines-title">Cost lines</h2>
      <DataTable data={c.documents} columns={columns} caption={`Per-document cost lines (${c.documents.length}), newest first`} getRowId={(r) => r.documentId} maxHeight={480} virtualizeAt={200} />
    </Page>
  );
}
