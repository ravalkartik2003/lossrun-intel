import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { createColumnHelper, type ColumnDef } from "@tanstack/react-table";
import { CircleCheck, OctagonX, Rows3 } from "lucide-react";
import { DataTable, LineagePanel, LineageThread, SheetViewer, SourceViewer, TabSet, money, type CellRef, type ThreadEnds, type Zoom } from "@lossrun/ui";
import { useLayout, usePageImage } from "../api/queries.ts";
import type { AuditTrailEntry, DocumentResult } from "../api/types.ts";
import { useWidth } from "../shell/hooks.ts";
import { LEDGER_COLUMNS, cellText, claimPath, moneyOf, factsFor, failingPaths, lineageOf, regionsFor, rowPaths, rowsOf, sheetRegionsFor, totalsCheck, traceable, type LedgerRow } from "./model.ts";

type Selection =
  | { mode: "cell"; path: string; cell: CellRef }
  | { mode: "row"; row: LedgerRow; paths: string[]; index: number };

export interface WorkbenchProps {
  doc: DocumentResult;
  overrides: Map<string, AuditTrailEntry[]>;
  /** Review (brief 24 step 4): values with typed changes not yet added to the decision; their cells show a draft chip. */
  drafts?: ReadonlySet<string>;
  /** Review: outline failing cells in fail color. */
  outlineFailures?: boolean;
  /** Extra controls in the lineage panel for the selected value (the override form in Review). */
  panelExtras?: (path: string) => ReactNode;
  /** Imperative selection from outside (rule outcome "Open in source"). */
  focusPath?: { path: string; nonce: number } | null;
}

const helper = createColumnHelper<LedgerRow>();

/**
 * The document workbench (design-brief §5.2): the source page and the claims ledger side by side, a lineage thread from
 * the selected cell to its page region, and the lineage panel. Row trace (activate a claim number) shows every value of
 * the row at once (brief 11 §4 geometry). 768–1279px: source and ledger as tabs, lineage as a bottom sheet.
 */
export function Workbench({ doc, overrides, drafts, outlineFailures, panelExtras, focusPath }: WorkbenchProps) {
  const width = useWidth();
  const [periodIndex, setPeriodIndex] = useState(0);
  const [page, setPage] = useState(1);
  const [zoom, setZoom] = useState<Zoom>("fit");
  const [split, setSplit] = useState(45);
  const [selection, setSelection] = useState<Selection | null>(null);
  const [tab, setTab] = useState<"ledger" | "source">("ledger");
  const [scrollTick, setScrollTick] = useState(0);
  const ledgerRef = useRef<HTMLDivElement>(null);
  const sourceScrollRef = useRef<HTMLDivElement>(null);
  const returnFocus = useRef<HTMLElement | null>(null);

  // A workbook's lineage is sheet + cell: its source is the sheet grid from the layout, never a page image (brief 13).
  const isSheet = useMemo(() => Object.values(doc.document.lineage ?? {}).some((l) => !!l.sheet), [doc]);
  const pages = useMemo(() => {
    const max = Math.max(1, ...Object.values(doc.document.lineage ?? {}).map((l) => l.page ?? 1));
    return Array.from({ length: max }, (_, i) => i + 1);
  }, [doc]);
  const layout = useLayout(doc.documentId, page);
  const image = usePageImage(doc.documentId, page, !isSheet);
  const sheets = useMemo(
    () => (layout.data?.sheets ?? [{ number: page, sheetName: layout.data?.pageInfo.sheetName }]).map((s) => ({ number: s.number, name: s.sheetName ?? `Sheet ${s.number}` })),
    [layout.data, page],
  );
  const sheetName = layout.data?.pageInfo.sheetName ?? null;
  const failing = useMemo(() => (outlineFailures ? failingPaths(doc) : new Set<string>()), [doc, outlineFailures]);
  const regions = useMemo(() => regionsFor(doc, page, failing), [doc, page, failing]);
  const sheetRegions = useMemo(() => (isSheet && sheetName ? sheetRegionsFor(doc, sheetName, failing) : []), [doc, isSheet, sheetName, failing]);
  const rows = useMemo(() => rowsOf(doc, periodIndex), [doc, periodIndex]);

  const selectedPath = selection?.mode === "cell" ? selection.path : selection?.mode === "row" ? selection.paths[selection.index] : null;
  const activeRegions = useMemo(() => new Set(selection?.mode === "row" ? selection.paths : selectedPath ? [selectedPath] : []), [selection, selectedPath]);

  // Keep the source on the page (or sheet) of the selected value.
  useEffect(() => {
    if (!selectedPath) return;
    const l = lineageOf(doc, selectedPath);
    const p = l?.sheet ? sheets.find((s) => s.name === l.sheet)?.number : l?.page;
    if (p && p !== page) setPage(p);
  }, [selectedPath]);

  const close = useCallback(() => {
    setSelection(null);
    setSplit((s) => (s === 50 ? 45 : s));
    const el = returnFocus.current;
    returnFocus.current = null;
    requestAnimationFrame(() => el?.focus());
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape" && selection) close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [selection, close]);

  function selectCell(cell: CellRef, element: HTMLElement) {
    const row = rows.find((r) => r.id === cell.rowId);
    if (!row) return;
    returnFocus.current = element;
    if (cell.columnId === "claimNumber") {
      const paths = rowPaths(doc, row);
      if (paths.length === 0) return;
      setSelection({ mode: "row", row, paths, index: 0 });
      setSplit(50);
      setZoom("fit");
      return;
    }
    setSelection({ mode: "cell", path: claimPath(row, cell.columnId), cell });
    requestAnimationFrame(() => element.scrollIntoView({ block: "nearest", inline: "nearest" }));
    if (width === "medium") setTab("ledger");
  }

  function selectRegion(path: string) {
    const m = /^policyPeriods\[(\d+)\]\.claims\[(\d+)\]\.(.+)$/.exec(path);
    if (m) {
      const p = Number(m[1]);
      if (p !== periodIndex) setPeriodIndex(p);
      const cell = { rowId: `${m[1]}/${m[2]}`, columnId: m[3] };
      returnFocus.current = document.activeElement as HTMLElement;
      setSelection({ mode: "cell", path, cell });
    } else {
      returnFocus.current = document.activeElement as HTMLElement;
      setSelection({ mode: "cell", path, cell: { rowId: "", columnId: "" } });
    }
  }

  useEffect(() => {
    if (focusPath) selectRegion(focusPath.path);
  }, [focusPath?.nonce]);

  // Row trace geometry: scroll the source so the row's first region sits at the row's height.
  useEffect(() => {
    if (selection?.mode !== "row") return;
    const id = requestAnimationFrame(() => {
      const rowEl = ledgerRef.current?.querySelector<HTMLElement>(`[data-cell="${selection.row.id}|claimNumber"]`);
      const regionEl = sourceScrollRef.current?.querySelector<HTMLElement>(`[data-region="${CSS.escape(selection.paths[0])}"]`);
      if (rowEl && regionEl && sourceScrollRef.current) {
        sourceScrollRef.current.scrollTop += regionEl.getBoundingClientRect().top - rowEl.getBoundingClientRect().top;
        setScrollTick((t) => t + 1);
      }
    });
    return () => cancelAnimationFrame(id);
  }, [selection?.mode === "row" ? selection.row.id : null, image.data, layout.data, split]);

  const ends = useMemo<ThreadEnds | null>(() => {
    if (!selection || width !== "wide") return null;
    if (selection.mode === "cell") {
      if (!traceable(lineageOf(doc, selection.path))) return null;
      return {
        mode: "cell",
        occluder: () => ledgerRef.current?.querySelector<HTMLElement>(".lr-datatable") ?? null,
        origin: () =>
          ledgerRef.current?.querySelector<HTMLElement>(
            selection.cell.rowId ? `[data-cell="${CSS.escape(selection.cell.rowId)}|${CSS.escape(selection.cell.columnId)}"]` : `[data-path="${CSS.escape(selection.path)}"]`,
          ) ?? null,
        targets: () => [sourceScrollRef.current?.querySelector<HTMLElement>(`[data-region="${CSS.escape(selection.path)}"]`)].filter((x): x is HTMLElement => !!x),
      };
    }
    return {
      mode: "row",
      origin: () => ledgerRef.current?.querySelector<HTMLElement>(`[data-cell="${CSS.escape(selection.row.id)}|claimNumber"]`) ?? null,
      targets: () => selection.paths.map((p) => sourceScrollRef.current?.querySelector<HTMLElement>(`[data-region="${CSS.escape(p)}"]`)).filter((x): x is HTMLElement => !!x),
    };
  }, [selection, width, doc]);

  const columns = useMemo<ColumnDef<LedgerRow, any>[]>(() => {
    const leaf = (c: (typeof LEDGER_COLUMNS)[number]) =>
      helper.accessor((r) => (c.kind === "money" ? moneyOf(r.claim, c.field) ?? 0 : cellText(r.claim, c)), {
        id: c.id,
        header: c.label,
        meta: { numeric: c.numeric, rowHeader: c.id === "claimNumber" },
        cell: ({ row }) => {
          const path = claimPath(row.original, c.field);
          return (
            <>
              {cellText(row.original.claim, c)}
              {overrides.has(path) ? <span className="lr-sr-only"> (overridden)</span> : null}
              {drafts?.has(path) ? <span className="lr-badge lr-badge--review draft-chip">Draft<span className="lr-sr-only">: unsaved change</span></span> : null}
              {c.id === "claimNumber" ? <span className="lr-sr-only"> — press Enter to trace the whole row</span> : null}
            </>
          );
        },
      });
    const plain = LEDGER_COLUMNS.filter((c) => !c.group).map(leaf);
    const groups = (["Indemnity", "Medical", "Expense", "Total"] as const).map((g) => helper.group({ id: g, header: g, columns: LEDGER_COLUMNS.filter((c) => c.group === g).map(leaf) }));
    return [...plain, ...groups];
  }, [overrides, drafts]);

  const rowSel = selection?.mode === "row" ? selection : null;
  const facts = selectedPath ? factsFor(doc, selectedPath, overrides) : null;
  const position = rowSel
    ? {
        index: rowSel.index,
        total: rowSel.paths.length,
        onPrev: () => setSelection({ ...rowSel, index: (rowSel.index - 1 + rowSel.paths.length) % rowSel.paths.length }),
        onNext: () => setSelection({ ...rowSel, index: (rowSel.index + 1) % rowSel.paths.length }),
      }
    : null;

  // ADR 0017: a summary-only run prints period rows, not claims; its ledger is the period table.
  const summaryOnly = doc.document.policyPeriods.length > 0 && doc.document.policyPeriods.every((p) => p.detailLevel === "summaryOnly");
  const onSelectPath = (path: string, el: HTMLElement) => {
    returnFocus.current = el;
    setSelection({ mode: "cell", path, cell: { rowId: "", columnId: "" } });
  };
  const ledger = summaryOnly ? (
    <div className="ledger" ref={ledgerRef}>
      <PeriodSummaryTable doc={doc} selectedPath={selectedPath} failing={failing} onSelect={onSelectPath} />
    </div>
  ) : (
    <div className="ledger" ref={ledgerRef}>
      {doc.document.policyPeriods.length > 1 ? (
        <TabSet
          label="Policy periods"
          value={String(periodIndex)}
          onValueChange={(v) => {
            setPeriodIndex(Number(v));
            setSelection(null);
          }}
          tabs={doc.document.policyPeriods.map((p, i) => ({ value: String(i), label: `${p.policyNumber ?? "Policy"} · ${p.effectiveDate?.slice(0, 4) ?? "?"}`, content: null }))}
        />
      ) : null}
      <DataTable
        key={periodIndex}
        data={rows}
        columns={columns}
        caption={`Claims, policy ${doc.document.policyPeriods[periodIndex]?.policyNumber ?? ""} (${doc.document.policyPeriods[periodIndex]?.effectiveDate ?? ""} to ${doc.document.policyPeriods[periodIndex]?.expirationDate ?? ""})`}
        getRowId={(r) => r.id}
        activeCell={selection?.mode === "cell" && selection.cell.rowId ? selection.cell : null}
        onCellActivate={selectCell}
        isCellActivatable={() => true}
        cellClassName={(cell) => {
          const row = rows.find((r) => r.id === cell.rowId);
          if (!row) return undefined;
          const path = claimPath(row, cell.columnId);
          return [
            failing.has(path) ? "lr-cell--fail" : "",
            overrides.has(path) ? "lr-cell--overridden" : "",
            rowSel?.row.id === cell.rowId ? "lr-cell--row" : "",
            traceable(lineageOf(doc, path)) || cell.columnId === "claimNumber" ? "lr-cell--traceable" : "",
          ].join(" ");
        }}
        emptyText={doc.document.policyPeriods[periodIndex]?.noLossesStatement ? "The run states no losses for this policy period." : "This policy period printed no claims."}
      />
      <TotalsStrip
        doc={doc}
        periodIndex={periodIndex}
        selectedPath={selectedPath}
        failing={failing}
        onSelect={(path, el) => {
          returnFocus.current = el;
          setSelection({ mode: "cell", path, cell: { rowId: "", columnId: "" } });
        }}
      />
    </div>
  );

  const source = isSheet ? (
    <SheetViewer
      ref={sourceScrollRef}
      title={`Source: ${doc.document.carrierName ?? "loss run"}`}
      sheets={sheets}
      sheet={page}
      onSheetChange={setPage}
      cells={(layout.data?.cells ?? []).map((c) => ({ row: c.row, column: c.column, rowSpan: c.rowSpan, columnSpan: c.columnSpan, text: c.rawValue ?? c.content ?? "" }))}
      regions={sheetRegions}
      activeRegionIds={activeRegions}
      onRegionActivate={selectRegion}
      loading={layout.isLoading}
      error={layout.isError ? "The sheet could not be loaded." : null}
      onScroll={() => setScrollTick((t) => t + 1)}
    />
  ) : (
    <SourceViewer
      ref={sourceScrollRef}
      title={`Source: ${doc.document.carrierName ?? "loss run"}`}
      pages={pages.map((n) => ({ number: n, src: n === page ? image.data : null, width: layout.data?.pageInfo.width ?? 8.5, height: layout.data?.pageInfo.height ?? 11 }))}
      page={page}
      onPageChange={(p) => setPage(Math.max(1, Math.min(pages.length, p)))}
      regions={regions}
      activeRegionIds={activeRegions}
      onRegionActivate={selectRegion}
      zoom={zoom}
      onZoomChange={setZoom}
      loading={image.isLoading || layout.isLoading}
      error={image.isError ? "The page image could not be loaded." : null}
      onScroll={() => setScrollTick((t) => t + 1)}
    />
  );

  const panel = selection ? (
    <LineagePanel
      facts={facts}
      onClose={close}
      rowSummary={rowSel ? `Row ${rowSel.row.claim.claimNumber ?? ""} · ${rowSel.paths.length} values traced · ←/→ to step` : null}
      position={position}
      actions={
        <>
          {selection.mode === "cell" && selection.cell.rowId ? (
            <button
              type="button"
              className="lr-btn lr-btn--sm"
              onClick={() => {
                const row = rows.find((r) => r.id === selection.cell.rowId);
                if (row) setSelection({ mode: "row", row, paths: rowPaths(doc, row), index: Math.max(0, rowPaths(doc, row).indexOf(selection.path)) });
                setSplit(50);
                setZoom("fit");
              }}
            >
              <Rows3 size={16} aria-hidden="true" />Trace whole row
            </button>
          ) : null}
          {selectedPath && panelExtras ? panelExtras(selectedPath) : null}
        </>
      }
    />
  ) : null;

  if (width === "medium") {
    return (
      <div className="workbench workbench--tabs">
        <TabSet label="Document view" value={tab} onValueChange={(v) => setTab(v as "ledger" | "source")} tabs={[{ value: "ledger", label: "Ledger", content: ledger }, { value: "source", label: "Source page", content: <div className="workbench__source-tab">{source}</div> }]} />
        {panel ? <div className="sheet" role="region" aria-label="Lineage">{panel}</div> : null}
      </div>
    );
  }

  return (
    <div className={`workbench${rowSel ? " workbench--row" : ""}${selection?.mode === "cell" ? " workbench--panel" : ""}`} style={{ "--split": `${split}%` } as React.CSSProperties}>
      <div className="workbench__source">{source}</div>
      <Splitter value={split} onChange={setSplit} />
      <div className="workbench__ledger">{ledger}</div>
      {selection?.mode === "cell" ? <div className="workbench__panel">{panel}</div> : null}
      {rowSel ? <div className="workbench__strip">{panel}</div> : null}
      <LineageThread ends={ends} version={`${scrollTick}|${split}|${zoom}|${image.data ?? ""}|${selectedPath ?? ""}`} />
    </div>
  );
}

function Splitter({ value, onChange }: { value: number; onChange: (v: number) => void }) {
  const dragging = useRef(false);
  return (
    <div
      className="splitter"
      role="separator"
      aria-orientation="vertical"
      aria-label="Resize source and ledger"
      aria-valuemin={25}
      aria-valuemax={75}
      aria-valuenow={Math.round(value)}
      tabIndex={0}
      onKeyDown={(e) => {
        if (e.key === "ArrowLeft") onChange(Math.max(25, value - 5));
        else if (e.key === "ArrowRight") onChange(Math.min(75, value + 5));
        else if (e.key === "Home") onChange(25);
        else if (e.key === "End") onChange(75);
        else return;
        e.preventDefault();
      }}
      onPointerDown={(e) => {
        dragging.current = true;
        (e.target as HTMLElement).setPointerCapture(e.pointerId);
      }}
      onPointerMove={(e) => {
        if (!dragging.current) return;
        const box = (e.currentTarget.parentElement as HTMLElement).getBoundingClientRect();
        onChange(Math.max(25, Math.min(75, ((e.clientX - box.left) / box.width) * 100)));
      }}
      onPointerUp={() => (dragging.current = false)}
    />
  );
}

type SummaryColumn = { id: string; label: string; count?: boolean };

const SUMMARY_COLUMNS: SummaryColumn[] = [
  { id: "claimCount", label: "Claims", count: true },
  { id: "openCount", label: "Open", count: true },
  { id: "closedCount", label: "Closed", count: true },
  { id: "total.paid", label: "Paid" },
  { id: "total.reserve", label: "Reserve" },
  { id: "total.incurred", label: "Incurred" },
  { id: "recoverySubrogation", label: "Subrogation" },
  { id: "recoveries.deductible", label: "Deductible rec." },
  { id: "recoveries.reinsurance", label: "Reinsurance rec." },
  { id: "recoveries.sif", label: "SIF rec." },
  { id: "recoveries.other", label: "Other rec." },
];

function valueAt(node: unknown, path: string): number | null {
  const v = path.split(".").reduce<unknown>((o, k) => (o && typeof o === "object" ? (o as Record<string, unknown>)[k] : undefined), node);
  return typeof v === "number" ? v : null;
}

/**
 * ADR 0017: a summary-only loss run as one row per period (fiscal, report or policy year) with its printed counts, paid /
 * reserve / incurred and recoveries. Every printed value is a button that shows where it was printed (lineage panel and
 * thread), like the totals strip. Columns no period prints are left out.
 */
function PeriodSummaryTable({ doc, onSelect, selectedPath, failing }: { doc: DocumentResult; onSelect: (path: string, el: HTMLElement) => void; selectedPath: string | null; failing: Set<string> }) {
  const periods = doc.document.policyPeriods;
  const columns = SUMMARY_COLUMNS.filter((c) => periods.some((p) => valueAt(p.subtotals, c.id) !== null));
  const label = (p: (typeof periods)[number]) => p.periodLabel ?? p.policyNumber ?? ([p.effectiveDate, p.expirationDate].filter(Boolean).join(" to ") || "Period");
  return (
    <div className="totals">
      <table className="lr-table totals__table">
        <caption>Period summary (summary-only loss run: no claim rows printed)</caption>
        <thead>
          <tr>
            <th scope="col">Period</th>
            {columns.map((c) => (
              <th key={c.id} scope="col" className="num">{c.label}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {periods.map((p, i) => (
            <tr key={i}>
              <th scope="row">{label(p)}</th>
              {columns.map((c) => {
                const value = valueAt(p.subtotals, c.id);
                const path = `policyPeriods[${i}].subtotals.${c.id}`;
                const text = value === null ? "—" : c.count ? String(value) : money(value);
                return (
                  <td key={c.id} className="num">
                    {value === null ? (
                      <span aria-label="not printed">—</span>
                    ) : (
                      <button
                        type="button"
                        data-path={path}
                        className={`totals__value${selectedPath === path ? " lr-cell--active" : ""}${failing.has(path) ? " lr-cell--fail" : ""}`}
                        aria-pressed={selectedPath === path}
                        aria-label={`${label(p)} ${c.label}: ${text}. Show where it was printed`}
                        onClick={(e) => onSelect(path, e.currentTarget)}
                      >
                        {text}
                      </button>
                    )}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Printed subtotals vs the sum of extracted rows (design-brief §5.2): ✓ or the delta in fail color. */
function TotalsStrip({ doc, periodIndex, onSelect, selectedPath, failing }: { doc: DocumentResult; periodIndex: number; onSelect: (path: string, el: HTMLElement) => void; selectedPath: string | null; failing: Set<string> }) {
  const checks = totalsCheck(doc, periodIndex).filter((c) => c.printed !== null);
  if (checks.length === 0) return <p className="lr-meta totals__none">This period prints no subtotals to check against.</p>;
  return (
    <div className="totals">
      <table className="lr-table totals__table">
        <caption>Printed subtotal vs extracted rows</caption>
        <thead>
          <tr>
            <th scope="col">Column</th>
            <th scope="col" className="num">Printed</th>
            <th scope="col" className="num">Extracted</th>
            <th scope="col">Check</th>
          </tr>
        </thead>
        <tbody>
          {checks.map((c) => {
            const delta = Math.round(((c.printed ?? 0) - c.extracted) * 100) / 100;
            return (
              <tr key={c.id}>
                <th scope="row">{c.label}</th>
                <td className="num">
                  {(() => {
                    const path = `policyPeriods[${periodIndex}].subtotals.${c.id}`;
                    return (
                      <button
                        type="button"
                        data-path={path}
                        className={`totals__value${selectedPath === path ? " lr-cell--active" : ""}${failing.has(path) ? " lr-cell--fail" : ""}`}
                        aria-pressed={selectedPath === path}
                        aria-label={`Printed subtotal ${c.label}: ${money(c.printed)}. Show where it was printed`}
                        onClick={(e) => onSelect(path, e.currentTarget)}
                      >
                        {money(c.printed)}
                      </button>
                    );
                  })()}
                </td>
                <td className="num">{money(c.extracted)}</td>
                <td>
                  {delta === 0 ? (
                    <span className="totals__ok"><CircleCheck size={14} aria-hidden="true" /> matches</span>
                  ) : (
                    <span className="totals__bad"><OctagonX size={14} aria-hidden="true" /> off by {money(delta)}</span>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
