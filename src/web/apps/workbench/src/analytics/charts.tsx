import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import * as echarts from "echarts/core";
import { BarChart, LineChart, ScatterChart } from "echarts/charts";
import { GridComponent, LegendComponent, MarkLineComponent, TooltipComponent } from "echarts/components";
import { CanvasRenderer } from "echarts/renderers";
import { Table2, ChartColumn } from "lucide-react";
import { useUi } from "../state/ui.ts";
import { useReducedMotion } from "../shell/hooks.ts";

echarts.use([BarChart, LineChart, ScatterChart, GridComponent, LegendComponent, MarkLineComponent, TooltipComponent, CanvasRenderer]);

/** Token values for canvas charts (one palette: action series, ink-3 comparison; status colors only for status). */
export function chartTokens() {
  const css = getComputedStyle(document.documentElement);
  const v = (n: string) => css.getPropertyValue(n).trim();
  return { action: v("--action"), ink: v("--ink"), ink2: v("--ink-2"), ink3: v("--ink-3"), line: v("--line"), fail: v("--fail"), review: v("--review"), learned: v("--learned"), surface: v("--surface-raised"), font: v("--font") };
}

/** ECharts on canvas, themed from the tokens, redrawn on theme change; the container is an img with a text summary. */
export function EChart({ option, height = 240, summary }: { option: (t: ReturnType<typeof chartTokens>) => echarts.EChartsCoreOption; height?: number; summary: string }) {
  const ref = useRef<HTMLDivElement>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const theme = useUi((s) => s.theme);
  const reduced = useReducedMotion();
  useEffect(() => {
    if (!ref.current) return;
    chart.current = echarts.init(ref.current, undefined, { renderer: "canvas" });
    const ro = new ResizeObserver(() => chart.current?.resize());
    ro.observe(ref.current);
    return () => {
      ro.disconnect();
      chart.current?.dispose();
    };
  }, []);
  useEffect(() => {
    const t = chartTokens();
    chart.current?.setOption({ animation: !reduced, animationDuration: 160, textStyle: { fontFamily: t.font, color: t.ink2 }, ...option(t) }, true);
  }, [option, theme, reduced]);
  return <div ref={ref} style={{ height }} role="img" aria-label={summary} />;
}

/**
 * A chart with its data-table alternative (design-brief §5.4: every chart has one, for screen readers and copying).
 * The toggle is a pressed button; the table is a real table with a caption.
 */
export function ChartFrame({ title, children, table, note }: { title: string; children: ReactNode; table: { columns: string[]; rows: (string | number)[][]; numeric?: boolean[] }; note?: string }) {
  const [asTable, setAsTable] = useState(false);
  const id = useId();
  return (
    <section className="chart lr-panel" aria-labelledby={id}>
      <div className="chart__head">
        <h3 id={id}>{title}</h3>
        <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" aria-pressed={asTable} onClick={() => setAsTable((v) => !v)}>
          {asTable ? <ChartColumn size={14} aria-hidden="true" /> : <Table2 size={14} aria-hidden="true" />}
          {asTable ? "View as chart" : "View as table"}
          <span className="lr-sr-only"> for {title}</span>
        </button>
      </div>
      {note ? <p className="lr-meta">{note}</p> : null}
      {asTable ? (
        <div className="chart__table">
          <table className="lr-table">
            <caption className="lr-sr-only">{title}</caption>
            <thead>
              <tr>{table.columns.map((c, i) => <th key={i} scope="col" className={table.numeric?.[i] ? "num" : undefined}>{c}</th>)}</tr>
            </thead>
            <tbody>
              {table.rows.map((r, i) => (
                <tr key={i}>{r.map((v, j) => (j === 0 ? <th key={j} scope="row">{v}</th> : <td key={j} className={table.numeric?.[j] ? "num" : undefined}>{v}</td>))}</tr>
              ))}
            </tbody>
          </table>
        </div>
      ) : (
        children
      )}
    </section>
  );
}
