import { useEffect, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import {
  flexRender,
  getCoreRowModel,
  getSortedRowModel,
  useReactTable,
  type ColumnDef,
  type Header,
  type SortingState,
} from "@tanstack/react-table";
import { useVirtualizer } from "@tanstack/react-virtual";
import { ArrowDown, ArrowUp, ArrowUpDown } from "lucide-react";

export interface CellRef {
  rowId: string;
  columnId: string;
}

export interface DataTableProps<T> {
  data: T[];
  columns: ColumnDef<T, any>[];
  /** Visible caption (or sr-only with captionHidden): what the table is. */
  caption: string;
  captionHidden?: boolean;
  getRowId: (row: T) => string;
  /** Sortable headers announce their state through aria-sort. */
  sortable?: boolean;
  /** Checkbox selection (never row click). */
  selection?: { selected: ReadonlySet<string>; onChange: (next: Set<string>) => void; label: (row: T) => string };
  /** The cell the lineage panel is showing (outlined, and the roving tab stop). */
  activeCell?: CellRef | null;
  /** Enter / click on a cell. The element is passed so callers can draw from it. */
  onCellActivate?: (cell: CellRef, element: HTMLElement) => void;
  /** Focus moved to a cell (arrow keys), for hover/focus labels. */
  onCellFocus?: (cell: CellRef, element: HTMLElement) => void;
  /** Whether a cell can be activated (others still take focus for reading). */
  isCellActivatable?: (cell: CellRef) => boolean;
  /** Extra classes per cell, e.g. a failing-rule outline. */
  cellClassName?: (cell: CellRef) => string | undefined;
  /** Rows at or above which the body is virtualized (design-brief §4: ≥ 200). */
  virtualizeAt?: number;
  /** Height of the scroll area when virtualized or bounded. */
  maxHeight?: number | string;
  emptyText?: string;
  footer?: ReactNode;
  className?: string;
}

const SELECT = "__select";

/**
 * TanStack Table rendered as a real table with grid keyboard semantics: one cell is in the tab order; arrows move
 * between cells, Home/End to the row's ends, Ctrl+Home/End to the corners, Enter activates. Two-row grouped headers
 * (Indemnity / Medical / Expense / Total over Paid / Reserve / Incurred), density from the [data-density] token.
 */
export function DataTable<T>(props: DataTableProps<T>) {
  const { data, columns, caption, getRowId, sortable = true, selection, activeCell, onCellActivate, onCellFocus, isCellActivatable, cellClassName } = props;
  const [sorting, setSorting] = useState<SortingState>([]);

  const allColumns = useMemo<ColumnDef<T, any>[]>(() => {
    if (!selection) return columns;
    return [
      {
        id: SELECT,
        header: () => <span className="lr-sr-only">Select</span>,
        enableSorting: false,
        cell: ({ row }) => (
          <input
            type="checkbox"
            className="lr-check"
            checked={selection.selected.has(row.id)}
            aria-label={selection.label(row.original)}
            tabIndex={-1}
            onChange={(e) => {
              const next = new Set(selection.selected);
              if (e.target.checked) next.add(row.id);
              else next.delete(row.id);
              selection.onChange(next);
            }}
          />
        ),
      },
      ...columns,
    ];
  }, [columns, selection]);

  const table = useReactTable({
    data,
    columns: allColumns,
    state: { sorting },
    onSortingChange: setSorting,
    getCoreRowModel: getCoreRowModel(),
    getSortedRowModel: sortable ? getSortedRowModel() : undefined,
    getRowId: (row) => getRowId(row),
    enableSorting: sortable,
  });

  const rows = table.getRowModel().rows;
  const leafColumns = table.getVisibleLeafColumns();
  const headerGroups = table.getHeaderGroups();
  const scrollRef = useRef<HTMLDivElement>(null);
  const virtual = rows.length >= (props.virtualizeAt ?? 200);
  const rowHeight = useRowHeight(scrollRef);
  const virtualizer = useVirtualizer({
    count: virtual ? rows.length : 0,
    getScrollElement: () => scrollRef.current,
    estimateSize: () => rowHeight,
    overscan: 12,
  });

  // Roving tab stop: the active cell, else the last focused, else the first cell.
  const [focusCell, setFocusCell] = useState<CellRef | null>(null);
  const tabStop = activeCell ?? focusCell ?? (rows[0] && leafColumns[0] ? { rowId: rows[0].id, columnId: leafColumns[0].id } : null);
  const pendingFocus = useRef<CellRef | null>(null);

  useEffect(() => {
    const target = pendingFocus.current;
    if (!target) return;
    const el = scrollRef.current?.querySelector<HTMLElement>(`[data-cell="${cssEscape(target.rowId)}|${cssEscape(target.columnId)}"]`);
    if (el) {
      pendingFocus.current = null;
      el.focus();
    }
  });

  function moveTo(rowIndex: number, colIndex: number) {
    const r = Math.max(0, Math.min(rows.length - 1, rowIndex));
    const c = Math.max(0, Math.min(leafColumns.length - 1, colIndex));
    const next = { rowId: rows[r].id, columnId: leafColumns[c].id };
    setFocusCell(next);
    pendingFocus.current = next;
    if (virtual) virtualizer.scrollToIndex(r, { align: "auto" });
    const el = scrollRef.current?.querySelector<HTMLElement>(`[data-cell="${cssEscape(next.rowId)}|${cssEscape(next.columnId)}"]`);
    if (el) {
      pendingFocus.current = null;
      el.focus();
      onCellFocus?.(next, el);
    }
  }

  function onKeyDown(e: KeyboardEvent<HTMLTableCellElement>, rowIndex: number, colIndex: number, cell: CellRef) {
    switch (e.key) {
      case "ArrowRight": moveTo(rowIndex, colIndex + 1); break;
      case "ArrowLeft": moveTo(rowIndex, colIndex - 1); break;
      case "ArrowDown": moveTo(rowIndex + 1, colIndex); break;
      case "ArrowUp": moveTo(rowIndex - 1, colIndex); break;
      case "PageDown": moveTo(rowIndex + 10, colIndex); break;
      case "PageUp": moveTo(rowIndex - 10, colIndex); break;
      case "Home": moveTo(e.ctrlKey ? 0 : rowIndex, 0); break;
      case "End": moveTo(e.ctrlKey ? rows.length - 1 : rowIndex, leafColumns.length - 1); break;
      case "Enter":
        if (cell.columnId === SELECT) {
          (e.currentTarget.querySelector("input") as HTMLInputElement | null)?.click();
        } else if (onCellActivate && (isCellActivatable?.(cell) ?? true)) {
          onCellActivate(cell, e.currentTarget);
        }
        break;
      case " ":
        if (cell.columnId === SELECT) (e.currentTarget.querySelector("input") as HTMLInputElement | null)?.click();
        else return;
        break;
      default:
        return;
    }
    e.preventDefault();
  }

  const items = virtual ? virtualizer.getVirtualItems() : null;
  const visible = items ? items.map((v) => ({ row: rows[v.index], index: v.index })) : rows.map((row, index) => ({ row, index }));
  const padTop = items && items.length > 0 ? items[0].start : 0;
  const padBottom = items && items.length > 0 ? virtualizer.getTotalSize() - items[items.length - 1].end : 0;
  const headerRows = headerGroups.length;

  return (
    <div
      ref={scrollRef}
      className={`lr-datatable ${props.className ?? ""}`}
      style={{ maxHeight: props.maxHeight ?? (virtual ? 560 : undefined) }}
      tabIndex={-1}
    >
      <table className="lr-table" role="grid" aria-rowcount={rows.length + headerRows} aria-colcount={leafColumns.length}>
        <caption className={props.captionHidden ? "lr-sr-only" : undefined}>{caption}</caption>
        <thead>
          {headerGroups.map((group, depth) => (
            <tr key={group.id} aria-rowindex={depth + 1}>
              {group.headers.map((header) => {
                const isLast = depth === headerGroups.length - 1;
                if (header.isPlaceholder) {
                  // A column without a group: one header cell spanning both rows (rendered in the top row only).
                  if (depth > 0) return null;
                  const leaf = leafHeader(headerGroups, header.column.id);
                  return leaf ? <HeaderCell key={header.id} header={leaf} rowSpan={headerRows} sortable={sortable} /> : null;
                }
                if (isLast && depth > 0 && !header.column.parent) return null;
                if (header.subHeaders.length > 0) {
                  return (
                    <th key={header.id} scope="colgroup" colSpan={header.colSpan} className="lr-th-group">
                      {flexRender(header.column.columnDef.header, header.getContext())}
                    </th>
                  );
                }
                return <HeaderCell key={header.id} header={header} rowSpan={depth === 0 ? headerRows : 1} sortable={sortable} />;
              })}
            </tr>
          ))}
        </thead>
        <tbody>
          {padTop > 0 ? <tr aria-hidden="true" className="lr-pad"><td colSpan={leafColumns.length} style={{ height: padTop }} /></tr> : null}
          {visible.length === 0 ? (
            <tr><td colSpan={leafColumns.length} className="lr-empty">{props.emptyText ?? "No rows."}</td></tr>
          ) : null}
          {visible.map(({ row, index }) => (
            <tr key={row.id} aria-rowindex={index + headerRows + 1} aria-selected={selection ? selection.selected.has(row.id) : undefined}>
              {row.getVisibleCells().map((cell, colIndex) => {
                const ref = { rowId: row.id, columnId: cell.column.id };
                const isTab = tabStop?.rowId === ref.rowId && tabStop.columnId === ref.columnId;
                const isActive = activeCell?.rowId === ref.rowId && activeCell.columnId === ref.columnId;
                const meta = cell.column.columnDef.meta as { numeric?: boolean; rowHeader?: boolean } | undefined;
                const Tag = meta?.rowHeader ? "th" : "td";
                return (
                  <Tag
                    key={cell.id}
                    scope={meta?.rowHeader ? "row" : undefined}
                    role={meta?.rowHeader ? "rowheader" : "gridcell"}
                    data-cell={`${row.id}|${cell.column.id}`}
                    tabIndex={isTab ? 0 : -1}
                    aria-selected={isActive || undefined}
                    className={[meta?.numeric ? "num" : "", isActive ? "lr-cell--active" : "", cellClassName?.(ref) ?? ""].join(" ").trim() || undefined}
                    onKeyDown={(e) => onKeyDown(e, index, colIndex, ref)}
                    onFocus={(e) => {
                      if (e.target === e.currentTarget) {
                        setFocusCell(ref);
                        onCellFocus?.(ref, e.currentTarget);
                      }
                    }}
                    onClick={(e) => {
                      if (cell.column.id === SELECT || !onCellActivate || !(isCellActivatable?.(ref) ?? true)) return;
                      onCellActivate(ref, e.currentTarget);
                    }}
                  >
                    {flexRender(cell.column.columnDef.cell, cell.getContext())}
                  </Tag>
                );
              })}
            </tr>
          ))}
          {padBottom > 0 ? <tr aria-hidden="true" className="lr-pad"><td colSpan={leafColumns.length} style={{ height: padBottom }} /></tr> : null}
        </tbody>
        {props.footer ? <tfoot>{props.footer}</tfoot> : null}
      </table>
    </div>
  );
}

function HeaderCell<T>({ header, rowSpan, sortable }: { header: Header<T, unknown>; rowSpan: number; sortable: boolean }) {
  const meta = header.column.columnDef.meta as { numeric?: boolean } | undefined;
  const canSort = sortable && header.column.getCanSort();
  const sorted = header.column.getIsSorted();
  const content = flexRender(header.column.columnDef.header, header.getContext());
  return (
    <th
      scope="col"
      rowSpan={rowSpan > 1 ? rowSpan : undefined}
      className={meta?.numeric ? "num" : undefined}
      aria-sort={canSort ? (sorted === "asc" ? "ascending" : sorted === "desc" ? "descending" : "none") : undefined}
    >
      {canSort ? (
        <button type="button" className="lr-sort" onClick={header.column.getToggleSortingHandler()}>
          <span>{content}</span>
          {sorted === "asc" ? <ArrowUp size={14} aria-hidden="true" /> : sorted === "desc" ? <ArrowDown size={14} aria-hidden="true" /> : <ArrowUpDown size={14} aria-hidden="true" className="lr-sort__idle" />}
        </button>
      ) : (
        content
      )}
    </th>
  );
}

function leafHeader<T>(groups: ReturnType<ReturnType<typeof useReactTable<T>>["getHeaderGroups"]>, columnId: string) {
  return groups[groups.length - 1].headers.find((h) => h.column.id === columnId);
}

function useRowHeight(ref: React.RefObject<HTMLElement | null>): number {
  const [h, setH] = useState(32);
  useEffect(() => {
    if (!ref.current) return;
    const v = parseFloat(getComputedStyle(ref.current).getPropertyValue("--row-h"));
    if (v > 0) setH(v);
  });
  return h;
}

function cssEscape(s: string): string {
  return typeof CSS !== "undefined" && CSS.escape ? CSS.escape(s) : s.replace(/["\\]/g, "\\$&");
}
