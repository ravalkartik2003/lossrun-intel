import { useState } from "react";
import * as Collapsible from "@radix-ui/react-collapsible";
import { ChevronRight } from "lucide-react";
import { StatusBadge, type Status } from "./status.tsx";

export interface RuleRow {
  ruleId: string;
  severity: string;
  status: string;
  message: string;
  claimId?: string | null;
  affectedFields: string[];
}

/** Rule outcome → vocabulary: hard fail = Failed rule; soft fail / flag = Needs review; pass = Verified. */
export function ruleStatus(r: { severity: string; status: string }): Status {
  if (r.status === "fail") return r.severity === "hard" ? "fail" : "review";
  if (r.status === "pass") return "verified";
  return "kept";
}

const GROUPS: { key: string; title: string; match: (r: RuleRow) => boolean }[] = [
  { key: "hard", title: "Failed rules", match: (r) => r.status === "fail" && r.severity === "hard" },
  { key: "soft", title: "Needs review", match: (r) => r.status === "fail" && r.severity !== "hard" },
  { key: "pass", title: "Passed", match: (r) => r.status === "pass" },
  { key: "other", title: "Not applicable or not informative", match: (r) => r.status !== "fail" && r.status !== "pass" },
];

/**
 * Rule outcomes grouped by severity (design-brief §4). Each row: rule id, message, the claim, "Open in source".
 * notInformative rows are muted and say "derived, not printed". Passed and not-applicable groups start collapsed.
 */
export function RuleOutcomeList({ outcomes, onOpenInSource, onSendToReview }: {
  outcomes: RuleRow[];
  onOpenInSource?: (r: RuleRow) => void;
  onSendToReview?: (r: RuleRow) => void;
}) {
  return (
    <div className="lr-rules">
      {GROUPS.map((g) => {
        const rows = dedupe(outcomes.filter(g.match));
        if (rows.length === 0) return null;
        return <RuleGroup key={g.key} title={g.title} rows={rows} defaultOpen={g.key === "hard" || g.key === "soft"} onOpen={onOpenInSource} onSend={g.key === "hard" || g.key === "soft" ? onSendToReview : undefined} />;
      })}
    </div>
  );
}

function RuleGroup({ title, rows, defaultOpen, onOpen, onSend }: { title: string; rows: RuleRow[]; defaultOpen: boolean; onOpen?: (r: RuleRow) => void; onSend?: (r: RuleRow) => void }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <Collapsible.Root open={open} onOpenChange={setOpen} className="lr-rules__group">
      <h3 className="lr-rules__title">
        <Collapsible.Trigger className="lr-disclosure">
          <ChevronRight size={16} aria-hidden="true" className="lr-disclosure__icon" />
          {title} <span className="lr-meta">({rows.length})</span>
        </Collapsible.Trigger>
      </h3>
      <Collapsible.Content>
        <ul className="lr-rules__list">
          {rows.map((r, i) => {
            const status = ruleStatus(r);
            const muted = r.status === "notInformative";
            return (
              <li key={i} className={`lr-rules__row${muted ? " lr-rules__row--muted" : ""}`}>
                <StatusBadge status={status} label={r.ruleId} />
                <span className="lr-rules__msg">
                  {r.message}
                  {muted ? <span className="lr-meta"> — derived, not printed</span> : null}
                </span>
                {r.claimId ? <span className="lr-meta">{r.claimId}</span> : <span />}
                <span className="lr-rules__actions">
                  {onOpen && r.affectedFields.some((f) => !f.includes("*")) ? (
                    <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet" onClick={() => onOpen(r)}>
                      Open in source<span className="lr-sr-only"> for {r.ruleId}{r.claimId ? ` on ${r.claimId}` : ""}</span>
                    </button>
                  ) : null}
                  {onSend ? (
                    <button type="button" className="lr-btn lr-btn--sm" onClick={() => onSend(r)}>
                      Send to review<span className="lr-sr-only"> ({r.ruleId})</span>
                    </button>
                  ) : null}
                </span>
              </li>
            );
          })}
        </ul>
      </Collapsible.Content>
    </Collapsible.Root>
  );
}

/** Pass outcomes repeat per claim and component; keep one row per rule id + message. */
function dedupe(rows: RuleRow[]): RuleRow[] {
  const seen = new Set<string>();
  return rows.filter((r) => {
    const k = r.status === "fail" ? `${r.ruleId}|${r.message}|${r.affectedFields.join()}` : r.ruleId;
    if (seen.has(k)) return false;
    seen.add(k);
    return true;
  });
}
