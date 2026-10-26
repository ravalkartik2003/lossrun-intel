import { forwardRef, useEffect, useMemo, useRef, type KeyboardEvent } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";

export interface SheetCell {
  /** 0-based row and column in the sheet. */
  row: number;
  column: number;
  rowSpan?: number;
  columnSpan?: number;
  /** The value as the workbook holds it. */
  text: string;
}

export interface SheetRegion {
  id: string;
  /** A1 address of the cell the value was read from. */
  cell: string;
  /** Accessible name: field, claim and printed text. */
  label: string;
  tone?: "default" | "fail";
}

export interface SheetViewerProps {
  title: string;
  /** The workbook's sheets in order; `number` is the layout page of the sheet. */
  sheets: { number: number; name: string }[];
  sheet: number;
  onSheetChange: (sheet: number) => void;
  cells: SheetCell[];
  regions: SheetRegion[];
  activeRegionIds?: ReadonlySet<string>;
  onRegionActivate?: (id: string) => void;
  loading?: boolean;
  error?: string | null;
  /** Called when the grid scrolls (the lineage thread redraws). */
  onScroll?: () => void;
}

/** Column letters for a 0-based index (0 → A, 26 → AA). */
export function columnName(index: number): string {
  let name = "";
  for (let n = index + 1; n > 0; n = Math.floor((n - 1) / 26)) name = String.fromCharCode(65 + ((n - 1) % 26)) + name;
  return name;
}

/** 0-based (row, column) of an A1 address, or null. */
export function parseA1(a1: string): { row: number; column: number } | null {
  const m = /^\$?([A-Z]+)\$?(\d+)$/i.exec(a1.trim());
  if (!m) return null;
  const column = [...m[1].toUpperCase()].reduce((a, c) => a * 26 + c.charCodeAt(0) - 64, 0) - 1;
  return { row: Number(m[2]) - 1, column };
}

/**
 * A spreadsheet source rendered as its grid (brief 13): workbooks have no page images, their lineage is sheet + cell.
 * Cells a value was read from are buttons (`data-region`, the same hook the lineage thread and row trace use), focusable
 * with a visible outline; arrow keys move between them in reading order, Enter selects. Sheets page like PDF pages.
 */
export const SheetViewer = forwardRef<HTMLDivElement, SheetViewerProps>(function SheetViewer(props, scrollRef) {
  const { sheets, sheet, cells, regions } = props;
  const current = sheets.find((s) => s.number === sheet) ?? sheets[0];
  const index = sheets.findIndex((s) => s.number === current?.number);
  const regionRefs = useRef(new Map<string, HTMLButtonElement>());

  const grid = useMemo(() => {
    const rows = Math.max(0, ...cells.map((c) => c.row + (c.rowSpan ?? 1)));
    const columns = Math.max(0, ...cells.map((c) => c.column + (c.columnSpan ?? 1)));
    const at = new Map<string, SheetCell>();
    const covered = new Set<string>();
    for (const c of cells) {
      at.set(`${c.row}:${c.column}`, c);
      for (let r = 0; r < (c.rowSpan ?? 1); r++)
        for (let k = 0; k < (c.columnSpan ?? 1); k++) if (r || k) covered.add(`${c.row + r}:${c.column + k}`);
    }
    return { rows, columns, at, covered };
  }, [cells]);

  const byCell = useMemo(() => {
    const map = new Map<string, SheetRegion[]>();
    for (const r of regions) {
      const p = parseA1(r.cell);
      if (!p) continue;
      const key = `${p.row}:${p.column}`;
      map.set(key, [...(map.get(key) ?? []), r]);
    }
    return map;
  }, [regions]);

  // Reading order: row, then column (the keyboard walk between highlighted cells).
  const ordered = useMemo(
    () =>
      [...byCell.entries()]
        .map(([key, rs]) => ({ key, id: rs[0].id, pos: key.split(":").map(Number) }))
        .sort((a, b) => a.pos[0] - b.pos[0] || a.pos[1] - b.pos[1]),
    [byCell],
  );

  const firstActive = regions.find((r) => props.activeRegionIds?.has(r.id));
  useEffect(() => {
    if (firstActive) regionRefs.current.get(firstActive.id)?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [firstActive?.id]);

  function onCellKey(e: KeyboardEvent<HTMLButtonElement>, id: string) {
    const step = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (step === 0 || ordered.length === 0) return;
    e.preventDefault();
    const i = ordered.findIndex((o) => o.id === id);
    regionRefs.current.get(ordered[(i + step + ordered.length) % ordered.length].id)?.focus();
  }

  return (
    <section className="lr-source" aria-label={props.title}>
      <div className="lr-source__bar">
        <div className="lr-source__pager">
          <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => props.onSheetChange(sheets[index - 1].number)} disabled={index <= 0} aria-label="Previous sheet">
            <ChevronLeft size={16} aria-hidden="true" />
          </button>
          <span className="lr-meta" aria-live="polite">
            Sheet {index + 1} of {sheets.length || "—"}: {current?.name ?? "—"}
          </span>
          <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => props.onSheetChange(sheets[index + 1].number)} disabled={index < 0 || index >= sheets.length - 1} aria-label="Next sheet">
            <ChevronRight size={16} aria-hidden="true" />
          </button>
        </div>
      </div>
      <div className="lr-source__scroll lr-sheet__scroll" ref={scrollRef} onScroll={props.onScroll} tabIndex={0} aria-label={`${props.title}, sheet ${current?.name ?? ""}. ${ordered.length} highlighted cells; Tab to reach them, arrow keys to move between them.`}>
        {props.error ? <p className="lr-source__state lr-error">{props.error}</p> : null}
        {props.loading && !props.error ? <div className="lr-source__state lr-skeleton" aria-label="Loading sheet" /> : null}
        {!props.loading && !props.error && grid.rows > 0 ? (
          <table className="lr-sheet">
            <caption className="lr-sr-only">Sheet {current?.name}, as printed in the workbook</caption>
            <thead>
              <tr>
                <td className="lr-sheet__corner" />
                {Array.from({ length: grid.columns }, (_, c) => (
                  <th key={c} scope="col" className="lr-sheet__head">
                    {columnName(c)}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {Array.from({ length: grid.rows }, (_, r) => (
                <tr key={r}>
                  <th scope="row" className="lr-sheet__head">
                    {r + 1}
                  </th>
                  {Array.from({ length: grid.columns }, (_, c) => {
                    const key = `${r}:${c}`;
                    if (grid.covered.has(key)) return null;
                    const cell = grid.at.get(key);
                    const hits = byCell.get(key);
                    const span = { rowSpan: cell?.rowSpan ?? 1, colSpan: cell?.columnSpan ?? 1 };
                    if (!hits) {
                      return (
                        <td key={c} {...span} className="lr-sheet__cell">
                          {cell?.text ?? ""}
                        </td>
                      );
                    }
                    const region = hits[0];
                    const active = hits.some((h) => props.activeRegionIds?.has(h.id));
                    return (
                      <td key={c} {...span} className="lr-sheet__cell lr-sheet__cell--traced">
                        <button
                          ref={(el) => {
                            for (const h of hits) {
                              if (el) regionRefs.current.set(h.id, el);
                              else regionRefs.current.delete(h.id);
                            }
                          }}
                          type="button"
                          data-region={region.id}
                          className={`lr-sheet__value${active ? " lr-sheet__value--active" : ""}${hits.some((h) => h.tone === "fail") ? " lr-sheet__value--fail" : ""}`}
                          aria-label={`${columnName(c)}${r + 1}: ${region.label}`}
                          aria-pressed={active}
                          onClick={() => props.onRegionActivate?.(region.id)}
                          onKeyDown={(e) => onCellKey(e, region.id)}
                        >
                          {cell?.text ?? ""}
                        </button>
                      </td>
                    );
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        ) : null}
      </div>
    </section>
  );
});
