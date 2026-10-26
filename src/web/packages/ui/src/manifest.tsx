import type { ReactNode } from "react";
import { StatusBadge, statusOfItem } from "./status.tsx";

export interface ManifestRow {
  documentId: string;
  fileName: string;
  pageRange?: string | null;
  classification: string;
  status: string;
  reason?: string | null;
  routingTier?: string | null;
  formatStatus?: string | null;
  claimCount?: number | null;
}

const CLASS_WORDS: Record<string, string> = {
  LossRun: "Loss run",
  Payroll: "Payroll report",
  Correspondence: "Letter",
  LossAnalysis: "Loss analysis report",
  Other: "Other document",
};

export function classificationWord(c: string): string {
  return CLASS_WORDS[c] ?? c;
}

/** Plain-language next step for applicants (no routing tiers): what happened and what to do. */
export function applicantNextStep(item: ManifestRow): string {
  switch (item.status) {
    case "processed":
      return `Read ${item.claimCount ?? 0} claim${item.claimCount === 1 ? "" : "s"}. Nothing else needed.`;
    case "awaiting-review":
      return "An underwriter is checking a total on this report. You don't need to do anything.";
    case "kept-not-processed":
      return `Kept with your submission (${classificationWord(item.classification).toLowerCase()}); the underwriter can read it.`;
    case "unsupported":
      return "We couldn't read this file. Upload a clearer copy or a PDF export.";
    case "failed":
      return "Processing failed. Try uploading it again.";
    default:
      return "Processing…";
  }
}

/**
 * Package contents (design-brief §4 ManifestList): one row per file or segment with status, classification, page range
 * and reason. Non-loss-run items read "Kept, not processed — payroll report; available to chat". The applicant variant
 * hides routing and shows plain-language next steps.
 */
export function ManifestList({ items, variant = "underwriter", renderDetail, caption = "Package contents" }: {
  items: ManifestRow[];
  variant?: "underwriter" | "applicant";
  renderDetail?: (item: ManifestRow) => ReactNode;
  caption?: string;
}) {
  return (
    <ul className="lr-manifest" aria-label={caption}>
      {items.map((item) => {
        const status = statusOfItem(item);
        return (
          <li key={item.documentId} className="lr-manifest__item">
            <div className="lr-manifest__head">
              <StatusBadge status={status} />
              <span className="lr-manifest__name">{item.fileName}</span>
              {item.pageRange ? <span className="lr-meta">pages {item.pageRange}</span> : null}
              <span className="lr-meta">{classificationWord(item.classification)}</span>
            </div>
            {variant === "applicant" ? (
              <p className="lr-manifest__reason">{applicantNextStep(item)}</p>
            ) : item.reason ? (
              <p className="lr-manifest__reason">{status === "kept" ? `Kept, not processed — ${humanReason(item.reason)}; available to chat` : humanReason(item.reason)}</p>
            ) : null}
            {renderDetail ? renderDetail(item) : null}
          </li>
        );
      })}
    </ul>
  );
}

/** "classified as Payroll (confidence n/a); kept, not extracted" → "payroll report". */
function humanReason(reason: string): string {
  const m = /^classified as (\w+)/.exec(reason);
  return m ? classificationWord(m[1]).toLowerCase() : reason;
}
