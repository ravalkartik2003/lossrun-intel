import { useId } from "react";
import { RotateCcw } from "lucide-react";

/** What-if controls in UI units; the app converts them to the catalog's Scenario (basis points, cents). */
export interface ScenarioControls {
  /** Reserve adjustment, percent (−50…+100). */
  reservePct: number;
  /** Annual loss trend, percent per year, applied from each policy year to the latest one. */
  trendPct: number;
  /** Per-claim cap in dollars; null = off. */
  capPerClaim: number | null;
  /** Exclude claims with incurred above this (dollars); null = off. */
  excludeAbove: number | null;
  includeNonCA: boolean;
  includeCT: boolean;
}

export const BASELINE_CONTROLS: ScenarioControls = { reservePct: 0, trendPct: 0, capPerClaim: null, excludeAbove: null, includeNonCA: true, includeCT: true };

export function isBaselineControls(c: ScenarioControls): boolean {
  return c.reservePct === 0 && c.trendPct === 0 && c.capPerClaim === null && c.excludeAbove === null && c.includeNonCA && c.includeCT;
}

/**
 * ScenarioPanel (design-brief §4): native range inputs (arrows, Page keys, Home/End) each paired with a visible numeric
 * input; optional thresholds are switched on with a checkbox. "Reset to as-reported" returns to the baseline.
 */
export function ScenarioPanel({ value, onChange, onReset, resetLabel = "Reset to baseline" }: {
  value: ScenarioControls;
  onChange: (next: ScenarioControls) => void;
  onReset: () => void;
  resetLabel?: string;
}) {
  const set = (patch: Partial<ScenarioControls>) => onChange({ ...value, ...patch });
  return (
    <section className="lr-scenario" aria-labelledby="scenario-title">
      <h2 id="scenario-title">What-if</h2>
      <p className="lr-meta">Recomputed in your browser with the same engine as the server.</p>
      <RangeField label="Reserve adjustment" unit="%" min={-50} max={100} step={1} value={value.reservePct} onChange={(v) => set({ reservePct: v })} describe={(v) => `${v > 0 ? "+" : ""}${v} % on open reserves`} />
      <RangeField label="Annual trend" unit="% / yr" min={0} max={15} step={0.5} value={value.trendPct} onChange={(v) => set({ trendPct: v })} describe={(v) => `${v} % per year to the latest policy year`} />
      <ThresholdField label="Cap each claim at" value={value.capPerClaim} defaultValue={250000} min={10000} max={2000000} step={10000} onChange={(v) => set({ capPerClaim: v })} />
      <ThresholdField label="Exclude catastrophic claims above" value={value.excludeAbove} defaultValue={500000} min={50000} max={5000000} step={50000} onChange={(v) => set({ excludeAbove: v })} />
      <fieldset className="lr-scenario__toggles">
        <legend className="lr-label">Include</legend>
        <label className="lr-check-row"><input type="checkbox" className="lr-check" checked={value.includeNonCA} onChange={(e) => set({ includeNonCA: e.target.checked })} /> Claims outside California</label>
        <label className="lr-check-row"><input type="checkbox" className="lr-check" checked={value.includeCT} onChange={(e) => set({ includeCT: e.target.checked })} /> Cumulative-trauma claims</label>
      </fieldset>
      <button type="button" className="lr-btn" onClick={onReset} disabled={isBaselineControls(value)}>
        <RotateCcw size={16} aria-hidden="true" />{resetLabel}
      </button>
    </section>
  );
}

function RangeField({ label, unit, min, max, step, value, onChange, describe }: { label: string; unit: string; min: number; max: number; step: number; value: number; onChange: (v: number) => void; describe: (v: number) => string }) {
  const id = useId();
  return (
    <div className="lr-range">
      <label htmlFor={`${id}-r`} className="lr-label">{label}</label>
      <div className="lr-range__row">
        <input id={`${id}-r`} type="range" min={min} max={max} step={step} value={value} aria-valuetext={describe(value)} onChange={(e) => onChange(Number(e.target.value))} />
        <input
          type="number"
          className="lr-input lr-input--num lr-range__num"
          aria-label={`${label}, ${unit}`}
          min={min}
          max={max}
          step={step}
          value={value}
          onChange={(e) => {
            const v = Number(e.target.value);
            if (!Number.isNaN(v)) onChange(Math.max(min, Math.min(max, v)));
          }}
        />
        <span className="lr-meta" aria-hidden="true">{unit}</span>
      </div>
    </div>
  );
}

function ThresholdField({ label, value, defaultValue, min, max, step, onChange }: { label: string; value: number | null; defaultValue: number; min: number; max: number; step: number; onChange: (v: number | null) => void }) {
  const id = useId();
  const on = value !== null;
  return (
    <div className="lr-range">
      <label className="lr-check-row lr-label">
        <input type="checkbox" className="lr-check" checked={on} onChange={(e) => onChange(e.target.checked ? defaultValue : null)} />
        {label}
      </label>
      <div className="lr-range__row">
        <input id={`${id}-r`} type="range" min={min} max={max} step={step} value={value ?? defaultValue} disabled={!on} aria-label={`${label}, dollars`} aria-valuetext={`$${(value ?? defaultValue).toLocaleString("en-US")}`} onChange={(e) => onChange(Number(e.target.value))} />
        <input
          type="number"
          className="lr-input lr-input--num lr-range__num"
          aria-label={`${label}, dollars`}
          disabled={!on}
          min={min}
          max={max}
          step={step}
          value={value ?? defaultValue}
          onChange={(e) => {
            const v = Number(e.target.value);
            if (!Number.isNaN(v)) onChange(Math.max(min, Math.min(max, v)));
          }}
        />
        <span className="lr-meta" aria-hidden="true">$</span>
      </div>
    </div>
  );
}
