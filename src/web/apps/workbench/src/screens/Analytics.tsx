import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "@tanstack/react-router";
import * as Dialog from "@radix-ui/react-dialog";
import { useForm, useFieldArray } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { Bar, BarChart, CartesianGrid, Legend, ResponsiveContainer, Tooltip as RTooltip, XAxis, YAxis } from "recharts";
import { CircleAlert, CircleCheck, FileDown, Plus, Send, Trash2, UserPlus, X } from "lucide-react";
import { BASELINE_CONTROLS, KpiStrip, ScenarioPanel, StateMessage, count, isBaselineControls, metricValue, proseDate, usd, usdCompact, type Kpi, type ScenarioControls } from "@lossrun/ui";
import type { AnalyticsClaim, Catalog, FeatureSnapshot, MetricResult } from "@lossrun/metrics";
import codeSets from "@docs/evaluation/code-sets.json";
import { downloadMemo, useAssignFollowUp, useMe, useCatalogText, useClaimsPayload, useFollowUps, usePushFlags, usePutContext, useQuoteContext, useQuotePayload, useQuotes } from "../api/queries.ts";
import type { QuoteReadiness } from "../api/types.ts";
import { fieldLabel, reasonLabel } from "../analytics/readiness.ts";
import { Page } from "../shell/Shell.tsx";
import { useTitle, useWidth } from "../shell/hooks.ts";
import { useUi } from "../state/ui.ts";
import { ChartFrame, EChart, chartTokens } from "../analytics/charts.tsx";
import { largeLossTooltip } from "../analytics/tooltip.ts";

type Tokens = ReturnType<typeof chartTokens>;
import { toScenario, useWhatIf } from "../analytics/useWhatIf.ts";

const KPIS = ["claim_count", "claim_count_with_financials", "incurred_total", "frequency_per_100_payroll", "severity_all", "paid_to_incurred", "litigation_rate", "ct_share", "open_claim_count", "reporting_lag_median_days", "closure_median_days"];

type Filters = { years: Set<number>; injury: "all" | "medicalOnly" | "indemnity"; bodyPart: string; jurisdiction: "all" | "CA" | "other"; ctOnly: boolean };

const CODE_NAMES: Record<string, Record<string, string>> = Object.fromEntries(
  (["WCIO-Cause", "WCIO-PartOfBody", "WCIO-Nature"] as const).map((t) => [t, Object.fromEntries(((codeSets as any).tables[t]?.codes ?? []).map((c: { code: string; description: string }) => [c.code, c.description]))]),
);

const display = (m: MetricResult | undefined) => (m && m.value !== null ? m.value / 10 ** m.scale : null);
const bdValue = (m: MetricResult | undefined, scale: number) => (m?.breakdown ?? []).map(([k, v]) => [k, v === null ? null : v / 10 ** scale] as const);

export function AnalyticsScreen() {
  useTitle("Analytics");
  const width = useWidth();
  const quoteId = useUi((s) => s.quoteId);
  const [view, setView] = useState<"adjusted" | "reported">("adjusted");
  const [controls, setControls] = useState<ScenarioControls>(BASELINE_CONTROLS);
  const [filters, setFilters] = useState<Filters>({ years: new Set(), injury: "all", bodyPart: "", jurisdiction: "all", ctOnly: false });
  const catalogText = useCatalogText();
  const claims = useClaimsPayload(quoteId, view);
  const payload = useQuotePayload(quoteId);
  const quotes = useQuotes();
  const setContextCost = useUi((s) => s.setContextCost);

  const catalog = useMemo(() => (catalogText.data ? (JSON.parse(catalogText.data) as Catalog & { metrics: { name: string; label: string; unit: string }[] }) : null), [catalogText.data]);
  const input = useMemo(() => (claims.data ? (JSON.parse(claims.data).input as { claims: AnalyticsClaim[] }) : null), [claims.data]);
  const years = useMemo(() => [...new Set((input?.claims ?? []).map((c) => c.policyYear).filter((y): y is number => y !== null))].sort(), [input]);
  const bodyParts = useMemo(() => [...new Set((input?.claims ?? []).map((c) => c.bodyPartCode).filter((b): b is string => !!b))].sort(), [input]);

  const excluded = useMemo(() => {
    if (!input) return [];
    return input.claims
      .filter((c) =>
        (filters.years.size > 0 && (c.policyYear === null || !filters.years.has(c.policyYear))) ||
        (filters.injury !== "all" && c.claimType !== filters.injury) ||
        (filters.bodyPart && c.bodyPartCode !== filters.bodyPart) ||
        (filters.jurisdiction === "CA" && c.jurisdiction !== null && c.jurisdiction.trim().toUpperCase() !== "CA") ||
        (filters.jurisdiction === "other" && (c.jurisdiction === null || c.jurisdiction.trim().toUpperCase() === "CA")) ||
        (filters.ctOnly && !c.cumulativeTrauma && !/^(6\d|7\d|8[0-3])$/.test(c.natureCode ?? "") && !/^(97|98)$/.test(c.causeCode ?? "")),
      )
      .map((c) => c.id);
  }, [input, filters]);

  const scenario = useMemo(() => toScenario(controls, years, excluded), [controls, years, excluded]);
  const whatIf = useWhatIf(quoteId ? `${quoteId}|${view}|${claims.dataUpdatedAt}` : null, claims.data, catalogText.data, view, scenario);
  const base = whatIf.result?.baseline;
  const scen = whatIf.result?.scenario ?? null;
  const active = scen !== null;
  const shown = scen ?? base;

  useEffect(() => {
    if (!payload.data) return;
    setContextCost(null);
  }, [payload.data]);

  if (width === "small") return <Page title="Analytics"><p className="note">Analytics needs a screen at least 768px wide.</p></Page>;
  if (!quoteId) return <Page title="Analytics"><StateMessage kind="empty">Choose a quote in the top bar.</StateMessage></Page>;
  if (claims.isError) return <Page title="Analytics"><StateMessage kind="error">The claims for {quoteId} could not be loaded ({(claims.error as Error).message}).</StateMessage></Page>;

  const metric = (s: FeatureSnapshot | null | undefined, name: string) => s?.metrics.find((m) => m.name === name);
  const label = (name: string) => catalog?.metrics.find((m) => m.name === name)?.label ?? name;
  const kpis: Kpi[] = KPIS.map((name) => {
    const b = metric(base, name);
    return { name, label: label(name), unit: b?.unit ?? "count", value: display(b), scenario: active ? display(metric(scen, name)) : undefined, notMeasurable: b?.notMeasurable ?? null };
  });

  const overrides = payload.data?.lineage.overrides.length ?? 0;
  return (
    <Page
      wide
      title={<>Analytics <span className="lr-meta">· {quotes.data?.find((q) => q.quoteId === quoteId)?.displayName ?? quoteId}{payload.data?.documents[0]?.insuredName ? ` · ${payload.data.documents[0].insuredName}` : ""}</span></>}
      actions={<QuoteActions quoteId={quoteId} />}
    >
      {payload.data?.readiness ? <ReadinessBanner readiness={payload.data.readiness} /> : null}
      <QuoteTerms quoteId={quoteId} />
      <FilterBar filters={filters} onChange={setFilters} years={years} bodyParts={bodyParts} />
      <div className="analytics">
        <div className="analytics__main">
          {!base ? (
            <StateMessage kind="loading">{whatIf.error ? `The what-if engine failed: ${whatIf.error}` : "Computing the account's metrics…"}</StateMessage>
          ) : (
            <>
              <KpiStrip items={kpis} scenarioActive={active} label={active ? "Key indicators: baseline → scenario" : "Key indicators"} />
              <p className="lr-meta analytics__timing" aria-live="polite">
                {active ? `Scenario applied: ${scen!.included} of ${scen!.inputClaims} claims included · recomputed in ${whatIf.result!.ms.toFixed(1)} ms in your browser` : `${base.included} claims included · baseline`}
              </p>
              <div className="charts">
                <TrendChart base={base} shown={shown!} active={active} />
                <TypeChart claims={input?.claims ?? []} snapshot={shown!} />
                <DriversChart snapshot={shown!} metric="loss_drivers_cause_incurred" table="WCIO-Cause" title="Loss drivers by cause (incurred)" />
                <DriversChart snapshot={shown!} metric="loss_drivers_body_part_incurred" table="WCIO-PartOfBody" title="Loss drivers by body part (incurred)" />
                <LargeLossChart claims={input?.claims ?? []} snapshot={shown!} threshold={100000} />
                <DevelopmentChart snapshot={shown!} />
              </div>
            </>
          )}
        </div>
        <aside className="analytics__side lr-panel" aria-label="Scenario">
          <ScenarioPanel value={controls} onChange={setControls} onReset={() => setControls(BASELINE_CONTROLS)} resetLabel="Reset to baseline" />
          <ContextEditor quoteId={quoteId} />
        </aside>
      </div>
      <footer className="analytics__footer">
        <span>
          From {count(base?.inputClaims ?? 0)} claims in {payload.data?.documents.length ?? "…"} document{payload.data?.documents.length === 1 ? "" : "s"} · valued {proseDate(base?.valuationDate)} · {overrides} override{overrides === 1 ? "" : "s"} applied · catalog v{catalog?.catalogVersion ?? "…"}
        </span>
        <div className="lr-segmented" role="group" aria-label="Values">
          <button type="button" className="lr-segmented__item" aria-pressed={view === "reported"} onClick={() => setView("reported")}>As reported</button>
          <button type="button" className="lr-segmented__item" aria-pressed={view === "adjusted"} onClick={() => setView("adjusted")}>As adjusted</button>
        </div>
        {!isBaselineControls(controls) || excluded.length > 0 ? <span className="chip">What-if active</span> : null}
      </footer>
    </Page>
  );
}

// ------------------------------------------------------------------ filters

function FilterBar({ filters, onChange, years, bodyParts }: { filters: Filters; onChange: (f: Filters) => void; years: number[]; bodyParts: string[] }) {
  return (
    <form className="filters" aria-label="Filters" onSubmit={(e) => e.preventDefault()}>
      <fieldset className="filters__years">
        <legend className="lr-label">Policy years</legend>
        {years.map((y) => (
          <label key={y} className="lr-check-row">
            <input
              type="checkbox"
              className="lr-check"
              checked={filters.years.size === 0 || filters.years.has(y)}
              onChange={(e) => {
                const all = filters.years.size === 0 ? new Set(years) : new Set(filters.years);
                if (e.target.checked) all.add(y);
                else all.delete(y);
                onChange({ ...filters, years: all.size === years.length ? new Set() : all });
              }}
            />
            {y}
          </label>
        ))}
      </fieldset>
      <label className="lr-field">
        <span className="lr-label">Injury type</span>
        <select className="lr-select" value={filters.injury} onChange={(e) => onChange({ ...filters, injury: e.target.value as Filters["injury"] })}>
          <option value="all">All</option>
          <option value="indemnity">Lost time</option>
          <option value="medicalOnly">Medical only</option>
        </select>
      </label>
      <label className="lr-field">
        <span className="lr-label">Body part</span>
        <select className="lr-select" value={filters.bodyPart} onChange={(e) => onChange({ ...filters, bodyPart: e.target.value })} disabled={bodyParts.length === 0}>
          <option value="">{bodyParts.length === 0 ? "Not printed" : "All"}</option>
          {bodyParts.map((b) => <option key={b} value={b}>{b} {CODE_NAMES["WCIO-PartOfBody"][b] ?? ""}</option>)}
        </select>
      </label>
      <label className="lr-field">
        <span className="lr-label">Jurisdiction</span>
        <select className="lr-select" value={filters.jurisdiction} onChange={(e) => onChange({ ...filters, jurisdiction: e.target.value as Filters["jurisdiction"] })}>
          <option value="all">All</option>
          <option value="CA">California</option>
          <option value="other">Other states</option>
        </select>
      </label>
      <label className="lr-check-row filters__ct">
        <input type="checkbox" className="lr-check" checked={filters.ctOnly} onChange={(e) => onChange({ ...filters, ctOnly: e.target.checked })} />
        Cumulative trauma only
      </label>
      <p className="lr-meta filters__note">Class code is not printed on these loss runs, so it is not offered as a filter.</p>
    </form>
  );
}

// ------------------------------------------------------------------ charts

function TrendChart({ base, shown, active }: { base: FeatureSnapshot; shown: FeatureSnapshot; active: boolean }) {
  const m = (s: FeatureSnapshot, n: string) => s.metrics.find((x) => x.name === n);
  const inc = bdValue(m(shown, "incurred_by_policy_year"), 2);
  const incBase = bdValue(m(base, "incurred_by_policy_year"), 2);
  const sev = bdValue(m(shown, "severity_by_policy_year"), 2);
  const cnt = bdValue(m(shown, "claim_count_by_policy_year"), 0);
  const years = inc.map(([k]) => k);
  const option = useCallback(
    (t: Tokens) => ({
      grid: { left: 64, right: 56, top: 36, bottom: 28 },
      legend: { top: 0, textStyle: { color: t.ink2 } },
      tooltip: { trigger: "axis" },
      xAxis: { type: "category", data: years, axisLine: { lineStyle: { color: t.line } }, axisLabel: { color: t.ink2 } },
      yAxis: [
        { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usdCompact(v) }, splitLine: { lineStyle: { color: t.line } } },
        { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usdCompact(v) }, splitLine: { show: false } },
      ],
      series: [
        ...(active ? [{ name: "Incurred (baseline)", type: "bar", data: incBase.map(([, v]) => v), itemStyle: { color: t.ink3 }, barGap: "8%" }] : []),
        { name: active ? "Incurred (scenario)" : "Incurred", type: "bar", data: inc.map(([, v]) => v), itemStyle: { color: t.action } },
        { name: "Severity", type: "line", yAxisIndex: 1, data: sev.map(([, v]) => v), lineStyle: { color: t.ink, width: 2 }, itemStyle: { color: t.ink }, symbolSize: 6 },
      ],
    }),
    [JSON.stringify(inc), JSON.stringify(incBase), JSON.stringify(sev), active],
  );
  return (
    <ChartFrame
      title="Trend by policy year"
      table={{ columns: ["Policy year", "Claims", "Incurred", "Severity"], rows: years.map((y, i) => [y, count(cnt[i]?.[1] ?? null), usd(inc[i]?.[1] ?? null), usd(sev[i]?.[1] ?? null)]), numeric: [false, true, true, true] }}
    >
      <EChart option={option} summary={`Incurred and severity by policy year: ${years.map((y, i) => `${y} ${usdCompact(inc[i]?.[1] ?? null)}`).join(", ")}.`} />
    </ChartFrame>
  );
}

function TypeChart({ claims, snapshot }: { claims: AnalyticsClaim[]; snapshot: FeatureSnapshot }) {
  const excluded = new Set(snapshot.perClaimFlags.filter((f) => f.flags.includes("excluded") || f.flags.includes("nonWc")).map((f) => f.claimId));
  const byYear = new Map<string, { year: string; medOnly: number; lostTime: number }>();
  for (const c of claims) {
    if (excluded.has(c.id) || c.policyYear === null) continue;
    const y = String(c.policyYear);
    const row = byYear.get(y) ?? { year: y, medOnly: 0, lostTime: 0 };
    if (c.claimType === "medicalOnly") row.medOnly++;
    else if (c.claimType === "indemnity") row.lostTime++;
    byYear.set(y, row);
  }
  const data = [...byYear.values()].sort((a, b) => a.year.localeCompare(b.year));
  return (
    <ChartFrame title="Medical-only vs lost-time claims" table={{ columns: ["Policy year", "Medical only", "Lost time"], rows: data.map((d) => [d.year, d.medOnly, d.lostTime]), numeric: [false, true, true] }}>
      <div role="img" aria-label={`Claims by type and policy year: ${data.map((d) => `${d.year} ${d.medOnly} medical only, ${d.lostTime} lost time`).join("; ")}.`} style={{ height: 240 }}>
        <ResponsiveContainer width="100%" height="100%">
          <BarChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
            <CartesianGrid stroke="var(--line)" vertical={false} />
            <XAxis dataKey="year" stroke="var(--ink-2)" tick={{ fill: "var(--ink-2)", fontSize: 12 }} />
            <YAxis allowDecimals={false} stroke="var(--ink-2)" tick={{ fill: "var(--ink-2)", fontSize: 12 }} />
            <RTooltip contentStyle={{ background: "var(--surface-raised)", border: "1px solid var(--line)", color: "var(--ink)" }} />
            <Legend wrapperStyle={{ color: "var(--ink-2)", fontSize: 12 }} />
            <Bar dataKey="lostTime" name="Lost time" stackId="t" fill="var(--action)" isAnimationActive={false} />
            <Bar dataKey="medOnly" name="Medical only" stackId="t" fill="var(--ink-3)" isAnimationActive={false} />
          </BarChart>
        </ResponsiveContainer>
      </div>
    </ChartFrame>
  );
}

function DriversChart({ snapshot, metric, table, title }: { snapshot: FeatureSnapshot; metric: string; table: string; title: string }) {
  const m = snapshot.metrics.find((x) => x.name === metric);
  const rows = bdValue(m, 2).map(([code, v]) => [code === "unknown" ? "Not coded" : `${code} ${CODE_NAMES[table][code] ?? ""}`.trim(), v] as const);
  const option = useCallback(
    (t: Tokens) => ({
      grid: { left: 160, right: 24, top: 8, bottom: 24 },
      tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
      xAxis: { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usdCompact(v) }, splitLine: { lineStyle: { color: t.line } } },
      yAxis: { type: "category", inverse: true, data: rows.map(([k]) => k), axisLabel: { color: t.ink2, width: 150, overflow: "truncate" } },
      series: [{ type: "bar", data: rows.map(([, v]) => v), itemStyle: { color: t.action } }],
    }),
    [JSON.stringify(rows)],
  );
  if (!m || m.value === null) {
    return (
      <ChartFrame title={title} table={{ columns: ["Code", "Incurred"], rows: [] }}>
        <p className="chart__na">Not measurable: {m?.notMeasurable ?? "no codes printed"}.</p>
      </ChartFrame>
    );
  }
  return (
    <ChartFrame title={title} table={{ columns: ["Code", "Incurred"], rows: rows.map(([k, v]) => [k, usd(v)]), numeric: [false, true] }}>
      <EChart option={option} summary={`${title}: ${rows.map(([k, v]) => `${k} ${usdCompact(v)}`).join(", ")}.`} />
    </ChartFrame>
  );
}

function LargeLossChart({ claims, snapshot, threshold }: { claims: AnalyticsClaim[]; snapshot: FeatureSnapshot; threshold: number }) {
  const excluded = new Set(snapshot.perClaimFlags.filter((f) => f.flags.includes("excluded") || f.flags.includes("nonWc")).map((f) => f.claimId));
  const points = claims
    .filter((c) => c.dateOfInjury)
    .map((c) => {
      const inc = (c.indemnityPaidCents + c.indemnityReserveCents + c.medicalPaidCents + c.medicalReserveCents + c.expensePaidCents + c.expenseReserveCents) / 100;
      return { id: c.id, number: c.claimNumber ?? c.id, doi: c.dateOfInjury!, inc, excluded: excluded.has(c.id) };
    });
  const large = points.filter((p) => p.inc >= threshold && !p.excluded).sort((a, b) => b.inc - a.inc);
  const option = useCallback(
    (t: Tokens) => ({
      grid: { left: 64, right: 24, top: 16, bottom: 28 },
      tooltip: { trigger: "item", formatter: largeLossTooltip },
      xAxis: { type: "time", axisLabel: { color: t.ink2 }, splitLine: { show: false } },
      yAxis: { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usdCompact(v) }, splitLine: { lineStyle: { color: t.line } } },
      series: [
        { name: "Included", type: "scatter", symbolSize: 7, data: points.filter((p) => !p.excluded).map((p) => [p.doi, p.inc, p.number]), itemStyle: { color: t.action, opacity: 0.75 }, markLine: { silent: true, symbol: "none", lineStyle: { color: t.fail, type: "dashed" }, label: { color: t.fail, formatter: `Large loss ${usdCompact(threshold)}` }, data: [{ yAxis: threshold }] } },
        { name: "Excluded by scenario", type: "scatter", symbolSize: 7, data: points.filter((p) => p.excluded).map((p) => [p.doi, p.inc, p.number]), itemStyle: { color: "transparent", borderColor: t.ink3, borderWidth: 1 } },
      ],
    }),
    [points.length, JSON.stringify(points.map((p) => p.excluded)), threshold],
  );
  return (
    <ChartFrame
      title="Large losses"
      note={`Each dot is a claim (incurred as reported vs date of injury); the line is the ${usdCompact(threshold)} large-loss threshold.`}
      table={{ columns: ["Claim", "Date of injury", "Incurred"], rows: large.map((p) => [p.number, p.doi, usd(p.inc)]), numeric: [false, false, true] }}
    >
      <EChart option={option} summary={`${points.length} claims plotted; ${large.length} at or above ${usdCompact(threshold)}.`} />
    </ChartFrame>
  );
}

function DevelopmentChart({ snapshot }: { snapshot: FeatureSnapshot }) {
  const paid = bdValue(snapshot.metrics.find((m) => m.name === "paid_by_policy_year"), 2);
  const inc = bdValue(snapshot.metrics.find((m) => m.name === "incurred_by_policy_year"), 2);
  const ratio = bdValue(snapshot.metrics.find((m) => m.name === "paid_to_incurred_by_policy_year"), 6);
  const years = inc.map(([k]) => k);
  const option = useCallback(
    (t: Tokens) => ({
      grid: { left: 64, right: 24, top: 36, bottom: 28 },
      legend: { top: 0, textStyle: { color: t.ink2 } },
      tooltip: { trigger: "axis" },
      xAxis: { type: "category", data: years, axisLabel: { color: t.ink2 } },
      yAxis: { type: "value", axisLabel: { color: t.ink2, formatter: (v: number) => usdCompact(v) }, splitLine: { lineStyle: { color: t.line } } },
      series: [
        { name: "Paid", type: "bar", data: paid.map(([, v]) => v), itemStyle: { color: t.action } },
        { name: "Incurred", type: "bar", data: inc.map(([, v]) => v), itemStyle: { color: t.ink3 } },
      ],
    }),
    [JSON.stringify(paid), JSON.stringify(inc)],
  );
  return (
    <ChartFrame title="Paid vs incurred by policy year" table={{ columns: ["Policy year", "Paid", "Incurred", "Paid-to-incurred"], rows: years.map((y, i) => [y, usd(paid[i]?.[1] ?? null), usd(inc[i]?.[1] ?? null), metricValue(ratio[i]?.[1] ?? null, "ratio")]), numeric: [false, true, true, true] }}>
      <EChart option={option} summary={`Paid vs incurred by policy year: ${years.map((y, i) => `${y} paid ${usdCompact(paid[i]?.[1] ?? null)} of ${usdCompact(inc[i]?.[1] ?? null)}`).join("; ")}.`} />
    </ChartFrame>
  );
}

// ------------------------------------------------------------------ actions

function QuoteActions({ quoteId }: { quoteId: string }) {
  const me = useMe();
  const push = usePushFlags(quoteId);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  if (me.data && !me.data.canReview) {
    return <p className="lr-meta">Read-only: exporting the memo, pushing flags and follow-ups are for underwriting roles.</p>;
  }
  return (
    <div className="quote-actions">
      <button
        type="button"
        className="lr-btn"
        disabled={busy}
        onClick={async () => {
          setBusy(true);
          setMessage(null);
          try {
            await downloadMemo(quoteId);
            setMessage("Pricing memo downloaded (Word). The export is in the audit trail.");
          } catch (e) {
            setMessage(`The memo could not be exported: ${(e as Error).message}`);
          } finally {
            setBusy(false);
          }
        }}
      >
        <FileDown size={16} aria-hidden="true" />Export pricing memo
      </button>
      <button
        type="button"
        className="lr-btn"
        disabled={push.isPending}
        onClick={async () => {
          setMessage(null);
          try {
            const r = await push.mutateAsync({ rationale: "pushed from the workbench" });
            setMessage(`Flags pushed for ${r.claims} claims (${Object.entries(r.flags).map(([k, v]) => `${v} ${k}`).join(", ")}). ${r.delivery}.`);
          } catch (e) {
            setMessage(`Flags could not be pushed: ${(e as Error).message}`);
          }
        }}
      >
        <Send size={16} aria-hidden="true" />Push flags to quote
      </button>
      <FollowUpDialog quoteId={quoteId} onDone={setMessage} />
      {message ? <p className="lr-meta quote-actions__msg" role="status">{message}</p> : null}
    </div>
  );
}

const followSchema = z.object({
  assignee: z.string().trim().min(1, "Who should do it?"),
  action: z.string().trim().min(4, "Describe the follow-up."),
  due: z.string().regex(/^(\d{4}-\d{2}-\d{2})?$/, "Use yyyy-mm-dd.").optional(),
  note: z.string().optional(),
});

function FollowUpDialog({ quoteId, onDone }: { quoteId: string; onDone: (m: string) => void }) {
  const [open, setOpen] = useState(false);
  const assign = useAssignFollowUp(quoteId);
  const existing = useFollowUps(quoteId);
  const { register, handleSubmit, formState, reset } = useForm<z.infer<typeof followSchema>>({ resolver: zodResolver(followSchema), defaultValues: { assignee: "", action: "", due: "", note: "" } });
  const err = formState.errors;
  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Trigger className="lr-btn"><UserPlus size={16} aria-hidden="true" />Assign follow-up</Dialog.Trigger>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog__overlay" />
        <Dialog.Content className="dialog lr-panel" aria-describedby="fu-desc">
          <div className="dialog__head">
            <Dialog.Title>Assign a follow-up</Dialog.Title>
            <Dialog.Close className="lr-btn lr-btn--sm lr-btn--quiet" aria-label="Close"><X size={16} aria-hidden="true" /></Dialog.Close>
          </div>
          <Dialog.Description id="fu-desc" className="lr-meta">Recorded on quote {quoteId} and in its audit trail.</Dialog.Description>
          <form
            className="dialog__form"
            noValidate
            onSubmit={handleSubmit(async (v) => {
              const f = await assign.mutateAsync({ assignee: v.assignee, action: v.action, due: v.due || undefined, note: v.note || undefined });
              reset();
              setOpen(false);
              onDone(`Follow-up assigned to ${f.assignee}: ${f.action}.`);
            })}
          >
            <div className="lr-field">
              <label className="lr-label" htmlFor="fu-assignee">Assign to</label>
              <input id="fu-assignee" className="lr-input" aria-invalid={!!err.assignee} aria-describedby={err.assignee ? "fu-assignee-err" : undefined} {...register("assignee")} />
              {err.assignee ? <p id="fu-assignee-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.assignee.message}</p> : null}
            </div>
            <div className="lr-field">
              <label className="lr-label" htmlFor="fu-action">Follow-up</label>
              <input id="fu-action" className="lr-input" placeholder="Request the 2024 policy-year loss run" aria-invalid={!!err.action} aria-describedby={err.action ? "fu-action-err" : undefined} {...register("action")} />
              {err.action ? <p id="fu-action-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.action.message}</p> : null}
            </div>
            <div className="lr-field">
              <label className="lr-label" htmlFor="fu-due">Due <span className="lr-meta">(optional, yyyy-mm-dd)</span></label>
              <input id="fu-due" className="lr-input" aria-invalid={!!err.due} aria-describedby={err.due ? "fu-due-err" : undefined} {...register("due")} />
              {err.due ? <p id="fu-due-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{err.due.message}</p> : null}
            </div>
            <div className="lr-field">
              <label className="lr-label" htmlFor="fu-note">Note <span className="lr-meta">(optional)</span></label>
              <textarea id="fu-note" className="lr-textarea" {...register("note")} />
            </div>
            <button type="submit" className="lr-btn lr-btn--primary" disabled={assign.isPending}>Assign</button>
          </form>
          {existing.data && existing.data.length > 0 ? (
            <>
              <h3>Already assigned</h3>
              <ul className="dialog__list">
                {existing.data.map((f) => <li key={f.id}><strong>{f.assignee}</strong> · {f.action}{f.due ? ` · due ${f.due}` : ""}</li>)}
              </ul>
            </>
          ) : null}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}

/** Brief 29: the quote's target effective date and requested years (they drive R-15 and R-25), in the header. */
function QuoteTerms({ quoteId }: { quoteId: string }) {
  const ctx = useQuoteContext(quoteId);
  const target = ctx.data?.targetPolicyEffectiveDate;
  const years = ctx.data?.requestedPolicyYears ?? [];
  return (
    <p className="lr-meta analytics__terms">
      {target || years.length
        ? <>Quoting{target ? <> a policy effective <strong>{target}</strong></> : null}{years.length ? <>{target ? "; " : " "}requested policy years <strong>{years.join(", ")}</strong></> : null} (valuation and year checks R-15, R-25).</>
        : "No target effective date or requested years: the valuation and year checks (R-15, R-25) do not run. Set them under Submission context."}
    </p>
  );
}

const contextSchema = z.object({
  payroll: z.array(z.object({ effectiveDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, "Use yyyy-mm-dd."), amount: z.coerce.number({ message: "Enter an amount." }).min(0, "Cannot be negative.") })),
  fte: z.array(z.object({ effectiveDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, "Use yyyy-mm-dd."), amount: z.coerce.number({ message: "Enter a count." }).min(0, "Cannot be negative.") })),
  lastDayWorked: z.string().regex(/^(\d{4}-\d{2}-\d{2})?$/, "Use yyyy-mm-dd.").optional(),
  targetPolicyEffectiveDate: z.string().regex(/^(\d{4}-\d{2}-\d{2})?$/, "Use yyyy-mm-dd.").optional(),
  requestedPolicyYears: z.string().regex(/^(\s*\d{4}\s*(,\s*\d{4}\s*)*)?$/, "Four-digit years separated by commas, e.g. 2022, 2023, 2024.").optional(),
});
type ContextValues = z.input<typeof contextSchema>;

/** Submission context (report 10 answer 1): payroll / FTE per policy period and the last day worked. */
function ContextEditor({ quoteId }: { quoteId: string }) {
  const ctx = useQuoteContext(quoteId);
  const put = usePutContext(quoteId);
  const [open, setOpen] = useState(false);
  const { register, control, handleSubmit, formState, reset } = useForm<ContextValues>({ resolver: zodResolver(contextSchema), defaultValues: { payroll: [], fte: [], lastDayWorked: "", targetPolicyEffectiveDate: "", requestedPolicyYears: "" } });
  const payroll = useFieldArray({ control, name: "payroll" });
  const fte = useFieldArray({ control, name: "fte" });
  useEffect(() => {
    if (ctx.data) reset({ payroll: ctx.data.payrollByPeriod?.map((p) => ({ effectiveDate: p.effectiveDate, amount: p.amount })) ?? [], fte: ctx.data.fteByPeriod?.map((p) => ({ effectiveDate: p.effectiveDate, amount: p.amount })) ?? [], lastDayWorked: ctx.data.lastDayWorked ?? "", targetPolicyEffectiveDate: ctx.data.targetPolicyEffectiveDate ?? "", requestedPolicyYears: (ctx.data.requestedPolicyYears ?? []).join(", ") });
  }, [ctx.data, open]);
  const err = formState.errors;
  const n = (ctx.data?.payrollByPeriod?.length ?? 0) + (ctx.data?.fteByPeriod?.length ?? 0);
  return (
    <section className="context-box" aria-labelledby="ctx-title">
      <h2 id="ctx-title">Submission context</h2>
      <p className="lr-meta">{n > 0 ? `${ctx.data?.payrollByPeriod?.length ?? 0} payroll and ${ctx.data?.fteByPeriod?.length ?? 0} FTE periods${ctx.data?.lastDayWorked ? ` · last day worked ${ctx.data.lastDayWorked}` : ""}.` : "No payroll or FTE yet: frequency is not measurable."}</p>
      <Dialog.Root open={open} onOpenChange={setOpen}>
        <Dialog.Trigger className="lr-btn lr-btn--sm">Edit exposures</Dialog.Trigger>
        <Dialog.Portal>
          <Dialog.Overlay className="dialog__overlay" />
          <Dialog.Content className="dialog dialog--wide lr-panel" aria-describedby="ctx-desc">
            <div className="dialog__head">
              <Dialog.Title>Submission context · {quoteId}</Dialog.Title>
              <Dialog.Close className="lr-btn lr-btn--sm lr-btn--quiet" aria-label="Close"><X size={16} aria-hidden="true" /></Dialog.Close>
            </div>
            <Dialog.Description id="ctx-desc" className="lr-meta">Exposures sum by policy year. Saving re-keys the cached metrics and is recorded in the audit trail.</Dialog.Description>
            <form
              className="dialog__form"
              noValidate
              onSubmit={handleSubmit(async (v) => {
                const parsed = contextSchema.parse(v);
                const years = (parsed.requestedPolicyYears ?? "").split(",").map((y) => y.trim()).filter(Boolean).map(Number);
                await put.mutateAsync({ quoteId, payrollByPeriod: parsed.payroll, fteByPeriod: parsed.fte, lastDayWorked: parsed.lastDayWorked || null,
                  targetPolicyEffectiveDate: parsed.targetPolicyEffectiveDate || null, requestedPolicyYears: years.length ? years : null });
                setOpen(false);
              })}
            >
              {([["payroll", payroll, "Payroll ($)"], ["fte", fte, "FTE"]] as const).map(([name, arr, unit]) => (
                <fieldset key={name} className="exposures">
                  <legend className="lr-label">{name === "payroll" ? "Payroll by policy period" : "Full-time equivalents by policy period"}</legend>
                  {arr.fields.map((f, i) => (
                    <div key={f.id} className="exposures__row">
                      <div className="lr-field">
                        <label className="lr-label" htmlFor={`${name}-d-${i}`}>Effective date</label>
                        <input id={`${name}-d-${i}`} className="lr-input" aria-invalid={!!err[name]?.[i]?.effectiveDate} aria-describedby={err[name]?.[i]?.effectiveDate ? `${name}-d-${i}-err` : undefined} {...register(`${name}.${i}.effectiveDate` as const)} />
                        {err[name]?.[i]?.effectiveDate ? <p id={`${name}-d-${i}-err`} className="lr-error">{err[name]![i]!.effectiveDate!.message}</p> : null}
                      </div>
                      <div className="lr-field">
                        <label className="lr-label" htmlFor={`${name}-a-${i}`}>{unit}</label>
                        <input id={`${name}-a-${i}`} className="lr-input lr-input--num" inputMode="decimal" aria-invalid={!!err[name]?.[i]?.amount} aria-describedby={err[name]?.[i]?.amount ? `${name}-a-${i}-err` : undefined} {...register(`${name}.${i}.amount` as const)} />
                        {err[name]?.[i]?.amount ? <p id={`${name}-a-${i}-err`} className="lr-error">{err[name]![i]!.amount!.message}</p> : null}
                      </div>
                      <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => arr.remove(i)} aria-label={`Remove ${name} row ${i + 1}`}><Trash2 size={14} aria-hidden="true" /></button>
                    </div>
                  ))}
                  <button type="button" className="lr-btn lr-btn--sm" onClick={() => arr.append({ effectiveDate: "", amount: "" as unknown as number })}><Plus size={14} aria-hidden="true" />Add a period</button>
                </fieldset>
              ))}
              <div className="lr-field">
                <label className="lr-label" htmlFor="ctx-ldw">Last day worked <span className="lr-meta">(account level, optional)</span></label>
                <input id="ctx-ldw" className="lr-input" placeholder="yyyy-mm-dd" aria-invalid={!!err.lastDayWorked} aria-describedby={err.lastDayWorked ? "ctx-ldw-err" : "ctx-ldw-hint"} {...register("lastDayWorked")} />
                {err.lastDayWorked ? <p id="ctx-ldw-err" className="lr-error">{err.lastDayWorked.message}</p> : <p id="ctx-ldw-hint" className="lr-hint">A claim's own last day worked, when the carrier prints it, wins.</p>}
              </div>
              <div className="lr-field">
                <label className="lr-label" htmlFor="ctx-tpe">Target policy effective date <span className="lr-meta">(optional; R-15)</span></label>
                <input id="ctx-tpe" className="lr-input" placeholder="yyyy-mm-dd" aria-invalid={!!err.targetPolicyEffectiveDate} aria-describedby={err.targetPolicyEffectiveDate ? "ctx-tpe-err" : "ctx-tpe-hint"} {...register("targetPolicyEffectiveDate")} />
                {err.targetPolicyEffectiveDate ? <p id="ctx-tpe-err" className="lr-error">{err.targetPolicyEffectiveDate.message}</p> : <p id="ctx-tpe-hint" className="lr-hint">The loss runs should be valued within 100 days of it.</p>}
              </div>
              <div className="lr-field">
                <label className="lr-label" htmlFor="ctx-rpy">Requested policy years <span className="lr-meta">(optional; R-25)</span></label>
                <input id="ctx-rpy" className="lr-input" placeholder="2022, 2023, 2024" aria-invalid={!!err.requestedPolicyYears} aria-describedby={err.requestedPolicyYears ? "ctx-rpy-err" : "ctx-rpy-hint"} {...register("requestedPolicyYears")} />
                {err.requestedPolicyYears ? <p id="ctx-rpy-err" className="lr-error">{err.requestedPolicyYears.message}</p> : <p id="ctx-rpy-hint" className="lr-hint">Each year should have a policy period on the runs.</p>}
              </div>
              {put.isError ? <p className="lr-error" role="alert">{(put.error as Error).message}</p> : null}
              <button type="submit" className="lr-btn lr-btn--primary" disabled={put.isPending}>{put.isPending ? "Saving…" : "Save context"}</button>
            </form>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </section>
  );
}

/** Brief 24 step 2: whether the quote's values are all approved, and what still blocks it (pending reviews, unresolved values). */
export function ReadinessBanner({ readiness }: { readiness: QuoteReadiness }) {
  const excluded = readiness.rejectedDocuments.length;
  const excludedNote = excluded > 0 ? ` ${count(excluded)} rejected document${excluded === 1 ? " is" : "s are"} excluded.` : "";
  if (readiness.ready) {
    return (
      <section className="readiness readiness--ready" aria-label="Quote readiness">
        <p><CircleCheck size={16} aria-hidden="true" /> <strong>Ready for quoting.</strong> Every value is approved.{excludedNote}</p>
      </section>
    );
  }
  const shown = readiness.blockingFields.slice(0, 5);
  return (
    <section className="readiness readiness--blocked" aria-label="Quote readiness">
      <p>
        <CircleAlert size={16} aria-hidden="true" /> <strong>Not ready for quoting.</strong>{" "}
        {readiness.pendingDocuments.length > 0 ? `${count(readiness.pendingDocuments.length)} document${readiness.pendingDocuments.length === 1 ? "" : "s"} waiting for review. ` : ""}
        {readiness.blockingFieldCount > 0 ? `${count(readiness.blockingFieldCount)} value${readiness.blockingFieldCount === 1 ? "" : "s"} not yet verified.` : ""}
        {excludedNote}
      </p>
      {readiness.pendingDocuments.length > 0 || shown.length > 0 ? (
        <ul className="readiness__list">
          {readiness.pendingDocuments.map((d) => (
            <li key={d.documentId}>
              <Link to="/reviews/$documentId" params={{ documentId: d.documentId }}>Review {d.documentId}</Link>
            </li>
          ))}
          {shown.map((f) => (
            <li key={`${f.documentId}:${f.fieldPath}`}>
              <Link to="/documents/$documentId" params={{ documentId: f.documentId }}>{fieldLabel(f.fieldPath)}</Link>{" "}
              <span className="lr-meta">({reasonLabel(f.reason)})</span>
            </li>
          ))}
          {readiness.blockingFieldCount > shown.length ? <li className="lr-meta">and {count(readiness.blockingFieldCount - shown.length)} more</li> : null}
        </ul>
      ) : null}
    </section>
  );
}
