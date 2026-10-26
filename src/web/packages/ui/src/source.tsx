import { forwardRef, useEffect, useRef, type KeyboardEvent } from "react";
import { ChevronLeft, ChevronRight } from "lucide-react";

export interface SourcePage {
  number: number;
  /** Image URL (object URL or http). */
  src?: string | null;
  /** Page size in the layout's unit (inches for PDFs). */
  width: number;
  height: number;
}

export interface SourceRegion {
  id: string;
  page: number;
  /** x1,y1,…,x4,y4 in the page's unit. */
  polygon: number[];
  /** Accessible name: field, claim and printed text. */
  label: string;
  /** Short visible label for hover/focus. */
  short?: string;
  tone?: "default" | "fail";
}

export type Zoom = "fit" | "100" | "125";

export interface SourceViewerProps {
  title: string;
  pages: SourcePage[];
  page: number;
  onPageChange: (page: number) => void;
  regions: SourceRegion[];
  activeRegionIds?: ReadonlySet<string>;
  onRegionActivate?: (id: string) => void;
  zoom: Zoom;
  onZoomChange: (zoom: Zoom) => void;
  loading?: boolean;
  error?: string | null;
  /** Called when the page area scrolls (the lineage thread redraws). */
  onScroll?: () => void;
}

const CSS_PX_PER_INCH = 96;

/**
 * The source page with highlight regions (design-brief §4 SourceViewer). Regions are buttons positioned over the page
 * image, focusable with a visible outline; Arrow keys move between them in reading order, Enter selects. Fit-width,
 * 100 % and 125 %; previous / next page.
 */
export const SourceViewer = forwardRef<HTMLDivElement, SourceViewerProps>(function SourceViewer(props, scrollRef) {
  const { pages, page, regions, zoom } = props;
  const current = pages.find((p) => p.number === page) ?? pages[0];
  const onPage = regions.filter((r) => r.page === (current?.number ?? 1)).sort(readingOrder);
  const regionRefs = useRef(new Map<string, HTMLButtonElement>());

  // Bring the active region into view when it changes from outside (a ledger cell was selected).
  const firstActive = onPage.find((r) => props.activeRegionIds?.has(r.id));
  useEffect(() => {
    if (firstActive) regionRefs.current.get(firstActive.id)?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [firstActive?.id]);

  function onRegionKey(e: KeyboardEvent<HTMLButtonElement>, index: number) {
    const step = e.key === "ArrowRight" || e.key === "ArrowDown" ? 1 : e.key === "ArrowLeft" || e.key === "ArrowUp" ? -1 : 0;
    if (step === 0) return;
    e.preventDefault();
    const next = onPage[(index + step + onPage.length) % onPage.length];
    regionRefs.current.get(next.id)?.focus();
  }

  const width = zoom === "fit" ? "100%" : `${(current?.width ?? 8.5) * CSS_PX_PER_INCH * (zoom === "125" ? 1.25 : 1)}px`;

  return (
    <section className="lr-source" aria-label={props.title}>
      <div className="lr-source__bar">
        <div className="lr-source__pager">
          <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => props.onPageChange(page - 1)} disabled={page <= 1} aria-label="Previous page">
            <ChevronLeft size={16} aria-hidden="true" />
          </button>
          <span className="lr-meta" aria-live="polite">Page {current?.number ?? "—"} of {pages.length || "—"}</span>
          <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => props.onPageChange(page + 1)} disabled={page >= pages.length} aria-label="Next page">
            <ChevronRight size={16} aria-hidden="true" />
          </button>
        </div>
        <div className="lr-segmented" role="group" aria-label="Zoom">
          {(["fit", "100", "125"] as Zoom[]).map((z) => (
            <button key={z} type="button" className="lr-segmented__item" aria-pressed={zoom === z} onClick={() => props.onZoomChange(z)}>
              {z === "fit" ? "Fit width" : `${z} %`}
            </button>
          ))}
        </div>
      </div>
      <div className="lr-source__scroll" ref={scrollRef} onScroll={props.onScroll} tabIndex={0} aria-label={`${props.title}, page ${current?.number ?? ""}. ${onPage.length} highlighted values; Tab to reach them, arrow keys to move between them.`}>
        {props.error ? <p className="lr-source__state lr-error">{props.error}</p> : null}
        {props.loading && !props.error ? <div className="lr-source__state lr-skeleton" aria-label="Loading page" /> : null}
        {current && !props.error ? (
          <div className="lr-source__page" style={{ width, aspectRatio: `${current.width} / ${current.height}` }}>
            {current.src ? <img src={current.src} alt={`Source page ${current.number}`} draggable={false} /> : <div className="lr-source__blank" />}
            {onPage.map((r, i) => {
              const box = bounds(r.polygon);
              const active = props.activeRegionIds?.has(r.id) ?? false;
              return (
                <button
                  key={r.id}
                  ref={(el) => {
                    if (el) regionRefs.current.set(r.id, el);
                    else regionRefs.current.delete(r.id);
                  }}
                  type="button"
                  data-region={r.id}
                  className={`lr-region${active ? " lr-region--active" : ""}${r.tone === "fail" ? " lr-region--fail" : ""}`}
                  style={{
                    left: `${(box.x / current.width) * 100}%`,
                    top: `${(box.y / current.height) * 100}%`,
                    width: `${(box.w / current.width) * 100}%`,
                    height: `${(box.h / current.height) * 100}%`,
                  }}
                  aria-label={r.label}
                  aria-pressed={active}
                  onClick={() => props.onRegionActivate?.(r.id)}
                  onKeyDown={(e) => onRegionKey(e, i)}
                >
                  {r.short ? <span className="lr-region__tag" aria-hidden="true">{r.short}</span> : null}
                </button>
              );
            })}
          </div>
        ) : null}
      </div>
    </section>
  );
});

export function bounds(polygon: number[]): { x: number; y: number; w: number; h: number } {
  const xs = polygon.filter((_, i) => i % 2 === 0);
  const ys = polygon.filter((_, i) => i % 2 === 1);
  const x = Math.min(...xs);
  const y = Math.min(...ys);
  return { x, y, w: Math.max(...xs) - x, h: Math.max(...ys) - y };
}

function readingOrder(a: SourceRegion, b: SourceRegion): number {
  const ba = bounds(a.polygon);
  const bb = bounds(b.polygon);
  return Math.abs(ba.y - bb.y) > 0.08 ? ba.y - bb.y : ba.x - bb.x;
}
