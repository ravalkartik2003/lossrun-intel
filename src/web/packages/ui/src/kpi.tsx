import { metricDelta, metricValue } from "./format.ts";

export interface Kpi {
  name: string;
  label: string;
  unit: string;
  /** Baseline value in display units (dollars, 0–1, days). */
  value: number | null;
  /** Scenario value when a what-if is active. */
  scenario?: number | null;
  /** Why the value is missing ("no payroll in the submission context"). */
  notMeasurable?: string | null;
  /** True when a larger value is better (paid-to-incurred), for the delta's wording only. */
  higherIsBetter?: boolean;
}

/**
 * The KPI strip (design-brief §4 KpiTile): tiles in one bordered strip separated by rules, not a card kit. With a
 * scenario active each tile reads baseline → scenario → delta.
 */
export function KpiStrip({ items, scenarioActive, label = "Key indicators" }: { items: Kpi[]; scenarioActive: boolean; label?: string }) {
  return (
    <ul className="lr-kpis" aria-label={label}>
      {items.map((k) => {
        const delta = scenarioActive && k.value !== null && k.scenario !== null && k.scenario !== undefined ? k.scenario - k.value : null;
        return (
          <li key={k.name} className="lr-kpi" data-metric={k.name}>
            <span className="lr-kpi__label">{k.label}</span>
            {k.value === null && k.notMeasurable ? (
              <>
                <span className="lr-kpi__value lr-kpi__value--na">—</span>
                <span className="lr-kpi__note">Not measurable: {k.notMeasurable}</span>
              </>
            ) : scenarioActive ? (
              <>
                <span className={`lr-kpi__value lr-num${metricValue(k.scenario ?? null, k.unit, true).length > 7 ? " lr-kpi__value--long" : ""}`}>{metricValue(k.scenario ?? null, k.unit, true)}</span>
                <span className="lr-kpi__note lr-num">
                  <span className="lr-sr-only">Baseline </span>
                  {metricValue(k.value, k.unit, true)} → <span className="lr-sr-only">scenario </span>{metricValue(k.scenario ?? null, k.unit, true)}
                  <span className={`lr-kpi__delta${delta && delta !== 0 ? (delta > 0 ? " lr-kpi__delta--up" : " lr-kpi__delta--down") : ""}`}>
                    {" "}({metricDelta(delta, k.unit)})
                  </span>
                </span>
              </>
            ) : (
              <span className={`lr-kpi__value lr-num${metricValue(k.value, k.unit, true).length > 7 ? " lr-kpi__value--long" : ""}`}>{metricValue(k.value, k.unit, true)}</span>
            )}
          </li>
        );
      })}
    </ul>
  );
}
