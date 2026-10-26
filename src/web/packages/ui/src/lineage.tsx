import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import { ChevronLeft, ChevronRight, X } from "lucide-react";
import { ConfidenceMeter } from "./meter.tsx";
import { StatusBadge, type Status } from "./status.tsx";

export interface LineageFacts {
  /** "Medical paid" */
  field: string;
  /** Path in the canonical document, e.g. policyPeriods[0].claims[1].medical.paid */
  path: string;
  /** Claim or period the value belongs to. */
  context?: string;
  value: string;
  sourceText?: string | null;
  confidence?: number | null;
  /** "Page 1 · x 7.00–7.81 in, y 1.58–1.80 in" or "Sheet1!F12". */
  source?: string | null;
  format?: string | null;
  /** replay | learn | delta | manual | context | derived */
  extractor?: string | null;
  /** For derived values: the inputs, as readable labels. */
  derivedFrom?: string[];
  /** ADR 0018: position of the value in a stacked cell (1-based for display); null for the whole cell. */
  part?: number | null;
  /** Brief 31 Answers 1: what replay changed in the printed text, as a sentence ("OCR read the capital I as l"). */
  repair?: string | null;
  rules: { ruleId: string; status: Status; message: string }[];
  overrides?: { at: string; user: string; oldValue?: string | null; newValue?: string | null; rationale?: string | null }[];
}

const EXTRACTOR: Record<string, string> = {
  replay: "Replay (format memory, no model call)",
  learn: "Learn (model-proposed format, verified)",
  delta: "Delta (format repaired by a small model call)",
  manual: "Manual override",
  context: "Submission context",
  derived: "Derived from other printed values",
};

/**
 * The lineage panel (design-brief §4): everything the thread shows, as text. It takes focus when opened; Esc or the
 * close button closes it and the caller returns focus to the cell. In row-trace mode it collapses to a 48px strip.
 */
export function LineagePanel(props: {
  facts: LineageFacts | null;
  onClose: () => void;
  /** Row trace: "WC-22-0001 · 9 values". */
  rowSummary?: string | null;
  position?: { index: number; total: number; onPrev: () => void; onNext: () => void } | null;
  actions?: ReactNode;
  autoFocus?: boolean;
}) {
  const ref = useRef<HTMLElement>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    if (props.autoFocus !== false) headingRef.current?.focus();
  }, [props.facts?.path, props.rowSummary]);

  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === "Escape") {
      e.stopPropagation();
      props.onClose();
    } else if (props.position && (e.key === "ArrowLeft" || e.key === "ArrowRight") && (e.target as HTMLElement).tagName !== "INPUT") {
      e.preventDefault();
      (e.key === "ArrowLeft" ? props.position.onPrev : props.position.onNext)();
    }
  };

  if (props.rowSummary) {
    const f = props.facts;
    return (
      <aside ref={ref} className="lr-lineage lr-lineage--strip" aria-label="Row lineage" onKeyDown={onKey}>
        <h2 ref={headingRef} tabIndex={-1} className="lr-lineage__strip-title">{props.rowSummary}</h2>
        {f ? (
          <p className="lr-lineage__strip-value">
            <span className="lr-meta">{f.field}</span> <strong className="lr-num">{f.value}</strong>
            {f.sourceText ? <span className="lr-meta"> · printed “{f.sourceText}”</span> : null}
            {f.repair ? <span className="lr-meta"> · repaired: {f.repair}</span> : null}
            {f.confidence !== null && f.confidence !== undefined ? <span className="lr-meta"> · confidence {f.confidence.toFixed(2)}</span> : null}
          </p>
        ) : null}
        {props.position ? (
          <div className="lr-lineage__stepper">
            <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={props.position.onPrev} aria-label="Previous value in row"><ChevronLeft size={16} aria-hidden="true" /></button>
            <span className="lr-meta" aria-live="polite">{props.position.index + 1} of {props.position.total}</span>
            <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={props.position.onNext} aria-label="Next value in row"><ChevronRight size={16} aria-hidden="true" /></button>
          </div>
        ) : null}
        <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={props.onClose} aria-label="Close row trace (Esc)"><X size={16} aria-hidden="true" /></button>
      </aside>
    );
  }

  const f = props.facts;
  if (!f) return null;
  return (
    <aside ref={ref} className="lr-lineage" aria-label="Value lineage" onKeyDown={onKey}>
      <header className="lr-lineage__head">
        <div>
          <p className="lr-meta">{f.context}</p>
          <h2 ref={headingRef} tabIndex={-1}>{f.field}</h2>
        </div>
        <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={props.onClose} aria-label="Close lineage (Esc)"><X size={16} aria-hidden="true" /></button>
      </header>
      <p className="lr-lineage__value lr-num">{f.value}</p>
      <dl className="lr-lineage__facts">
        <dt>Printed text</dt>
        <dd>{f.sourceText ? <q>{f.sourceText}</q> : <span className="lr-muted">not printed</span>}</dd>
        {f.repair ? (
          <>
            <dt>Repaired</dt>
            <dd>{f.repair}; the value above is the repaired text</dd>
          </>
        ) : null}
        {f.part !== null && f.part !== undefined ? (
          <>
            <dt>Stacked cell</dt>
            <dd>line {f.part} of the printed cell</dd>
          </>
        ) : null}
        <dt>Confidence</dt>
        <dd><ConfidenceMeter value={f.confidence} /></dd>
        <dt>Source</dt>
        <dd>{f.source ?? <span className="lr-muted">—</span>}</dd>
        <dt>Format</dt>
        <dd>{f.format ?? <span className="lr-muted">—</span>}</dd>
        <dt>Extractor</dt>
        <dd>{f.extractor ? EXTRACTOR[f.extractor] ?? f.extractor : <span className="lr-muted">—</span>}</dd>
        {f.derivedFrom && f.derivedFrom.length > 0 ? (
          <>
            <dt>Computed as</dt>
            <dd>{f.derivedFrom.join(" + ")}</dd>
          </>
        ) : null}
        <dt>Field path</dt>
        <dd><code className="lr-code">{f.path}</code></dd>
      </dl>
      <h3 className="lr-lineage__sub">Rules that touched it</h3>
      {f.rules.length === 0 ? (
        <p className="lr-meta">No rule reads this value.</p>
      ) : (
        <ul className="lr-lineage__rules">
          {f.rules.map((r, i) => (
            <li key={i}>
              <StatusBadge status={r.status} label={`${r.ruleId} · ${r.status === "verified" ? "passed" : r.status === "fail" ? "failed" : r.status === "review" ? "flagged" : "not informative"}`} />
              <span>{r.message}</span>
            </li>
          ))}
        </ul>
      )}
      {f.overrides && f.overrides.length > 0 ? (
        <>
          <h3 className="lr-lineage__sub">Overrides</h3>
          <ul className="lr-lineage__rules">
            {f.overrides.map((o, i) => (
              <li key={i}>
                <span className="lr-num">{o.oldValue ?? "—"} → {o.newValue ?? "—"}</span>
                <span className="lr-meta">{o.user} · {o.at}{o.rationale ? ` · ${o.rationale}` : ""}</span>
              </li>
            ))}
          </ul>
        </>
      ) : null}
      {props.position ? (
        <div className="lr-lineage__stepper">
          <button type="button" className="lr-btn lr-btn--sm" onClick={props.position.onPrev}><ChevronLeft size={16} aria-hidden="true" />Previous value</button>
          <span className="lr-meta">{props.position.index + 1} of {props.position.total} in row</span>
          <button type="button" className="lr-btn lr-btn--sm" onClick={props.position.onNext}>Next value<ChevronRight size={16} aria-hidden="true" /></button>
        </div>
      ) : null}
      {props.actions ? <div className="lr-lineage__actions">{props.actions}</div> : null}
    </aside>
  );
}

export interface ThreadEnds {
  /** The ledger end: the cell (single trace) or the row (row trace). */
  origin: () => HTMLElement | null;
  /** The source ends: one region (single) or one per value (row trace). */
  targets: () => HTMLElement[];
  mode: "cell" | "row";
  /**
   * The ledger's scroll area: the thread is masked out over it (except inside the origin cell), so it leaves the ledger
   * at its edge on the row's line instead of striking through the neighbouring numbers.
   */
  occluder?: () => HTMLElement | null;
  /** Labels shown at each end on hover/focus (aria-hidden; the panel carries the text). */
  originLabel?: string;
  targetLabel?: string;
}

interface Box {
  x: number;
  y: number;
  w: number;
  h: number;
}

interface Geometry {
  paths: string[];
  hide: Box | null;
  keep: Box | null;
  dot: { x: number; y: number } | null;
  originTag: { x: number; y: number } | null;
  targetTag: { x: number; y: number } | null;
}

/**
 * The bold moment (design-brief §2): a drawn connector from the ledger to the page region. Cubic Bézier with horizontal
 * tangents, from the cell's left edge through the gutter to the region's right edge; drawn in ≤ 250 ms (none under
 * reduced motion); redrawn on scroll and resize. Row trace: a 6px origin dot at the row's left edge fanning to one thread
 * per value. Purely decorative — aria-hidden.
 */
export function LineageThread({ ends, version }: { ends: ThreadEnds | null; version: unknown }) {
  const [geo, setGeo] = useState<Geometry | null>(null);
  const [drawKey, setDrawKey] = useState(0);
  const frame = useRef(0);

  useLayoutEffect(() => {
    if (!ends) {
      setGeo(null);
      return;
    }
    const measure = () => {
      cancelAnimationFrame(frame.current);
      frame.current = requestAnimationFrame(() => setGeo(compute(ends)));
    };
    setGeo(compute(ends));
    setDrawKey((k) => k + 1);
    window.addEventListener("scroll", measure, true);
    window.addEventListener("resize", measure);
    const ro = new ResizeObserver(measure);
    ro.observe(document.body);
    return () => {
      window.removeEventListener("scroll", measure, true);
      window.removeEventListener("resize", measure);
      ro.disconnect();
      cancelAnimationFrame(frame.current);
    };
  }, [ends, version]);

  if (!geo || geo.paths.length === 0) return null;
  return (
    <svg className="lr-thread" aria-hidden="true" focusable="false" width="100%" height="100%">
      <defs>
        <mask id="lr-thread-mask" maskUnits="userSpaceOnUse" x="0" y="0" width="100%" height="100%">
          <rect x="0" y="0" width="100%" height="100%" fill="white" />
          {geo.hide ? <rect x={geo.hide.x} y={geo.hide.y} width={geo.hide.w} height={geo.hide.h} fill="black" /> : null}
          {geo.keep ? <rect x={geo.keep.x} y={geo.keep.y} width={geo.keep.w} height={geo.keep.h} fill="white" /> : null}
        </mask>
      </defs>
      <g key={drawKey} className="lr-thread__draw" mask="url(#lr-thread-mask)">
        {geo.paths.map((d, i) => (
          <path key={i} d={d} pathLength={1} className="lr-thread__path" />
        ))}
      </g>
      {geo.dot ? <circle cx={geo.dot.x} cy={geo.dot.y} r={3} className="lr-thread__dot" /> : null}
      {ends?.originLabel && geo.originTag ? (
        <text x={geo.originTag.x} y={geo.originTag.y} className="lr-thread__tag" textAnchor="end">{ends.originLabel}</text>
      ) : null}
      {ends?.targetLabel && geo.targetTag ? (
        <text x={geo.targetTag.x} y={geo.targetTag.y} className="lr-thread__tag">{ends.targetLabel}</text>
      ) : null}
    </svg>
  );
}

function compute(ends: ThreadEnds): Geometry {
  const origin = ends.origin();
  const targets = ends.targets();
  if (!origin || targets.length === 0) return { paths: [], hide: null, keep: null, dot: null, originTag: null, targetTag: null };
  const o = origin.getBoundingClientRect();
  const occ = ends.occluder?.()?.getBoundingClientRect();
  const box = (r: DOMRect): Box => ({ x: r.left, y: r.top, w: r.width, h: r.height });
  const x1 = o.left;
  const y1 = o.top + o.height / 2;
  const paths: string[] = [];
  let targetTag: Geometry["targetTag"] = null;
  for (const t of targets) {
    const r = t.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) continue;
    const x2 = r.right;
    const y2 = r.top + r.height / 2;
    const dx = Math.max(24, Math.abs(x1 - x2) / 2);
    paths.push(`M ${x1} ${y1} C ${x1 - dx} ${y1}, ${x2 + dx} ${y2}, ${x2} ${y2}`);
    targetTag ??= { x: r.right + 6, y: r.top - 4 };
  }
  return {
    paths,
    hide: occ ? { x: occ.left + 2, y: occ.top, w: occ.width - 2, h: occ.height } : null,
    keep: occ ? box(o) : null,
    dot: ends.mode === "row" ? { x: x1, y: y1 } : occ ? { x: occ.left + 2, y: y1 } : null,
    originTag: { x: x1 - 6, y: o.top - 4 },
    targetTag,
  };
}
