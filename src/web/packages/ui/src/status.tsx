import { Archive, CircleAlert, CircleCheck, FileQuestionMark, OctagonX, Sparkles, type LucideIcon } from "lucide-react";

/** The status vocabulary (design-brief §3): every status is an icon, a word and a color — never color alone. */
export type Status = "verified" | "review" | "fail" | "learned" | "kept" | "unsupported" | "pending";

const STATUS: Record<Status, { word: string; icon: LucideIcon; tone: string }> = {
  verified: { word: "Verified", icon: CircleCheck, tone: "verified" },
  review: { word: "Needs review", icon: CircleAlert, tone: "review" },
  fail: { word: "Failed rule", icon: OctagonX, tone: "fail" },
  learned: { word: "Learned", icon: Sparkles, tone: "learned" },
  kept: { word: "Kept, not processed", icon: Archive, tone: "neutral" },
  unsupported: { word: "Unsupported", icon: FileQuestionMark, tone: "neutral" },
  pending: { word: "Processing", icon: CircleAlert, tone: "neutral" },
};

export function statusWord(status: Status): string {
  return STATUS[status].word;
}

export function StatusIcon({ status, size = 16 }: { status: Status; size?: number }) {
  const Icon = STATUS[status].icon;
  return <Icon size={size} strokeWidth={1.75} aria-hidden="true" className={`lr-tone-${STATUS[status].tone}`} />;
}

/** A pill: icon (aria-hidden) + the status word, which is the accessible name. */
export function StatusBadge({ status, label, title }: { status: Status; label?: string; title?: string }) {
  const s = STATUS[status];
  const Icon = s.icon;
  return (
    <span className={`lr-badge lr-badge--${s.tone}`} title={title}>
      <Icon size={14} strokeWidth={1.75} aria-hidden="true" />
      <span>{label ?? s.word}</span>
    </span>
  );
}

/** Manifest item status → vocabulary. */
export function statusOfItem(item: { status: string; routingTier?: string | null; formatStatus?: string | null }): Status {
  switch (item.status) {
    case "kept-not-processed":
      return "kept";
    case "unsupported":
      return "unsupported";
    case "awaiting-review":
      return "review";
    case "failed":
      return "fail";
    case "processed":
      return tierStatus(item.routingTier, item.formatStatus);
    default:
      return "pending";
  }
}

/** Routing tier (ReplayAccepted | Learned | Delta | Review, any case) → vocabulary color. */
export function tierStatus(tier: string | null | undefined, formatStatus?: string | null): Status {
  const t = (tier ?? "").toLowerCase();
  if (t === "review") return "review";
  if (t === "learned" || t === "delta" || formatStatus === "shadow") return "learned";
  if (t === "replayaccepted") return "verified";
  return "pending";
}

export interface RoutingInfo {
  tier?: string | null;
  badge?: string | null;
  formatRef?: string | null;
  formatStatus?: string | null;
  modelCalls?: number | null;
}

/**
 * The product's signature claim: "Format memory hit · Synthetic Mutual v5 · 0 model calls". Color by tier; a shadow
 * format appends "· confirming". The server's badge text is used verbatim when present (it carries the shadow count).
 */
export function RoutingBadge({ routing }: { routing: RoutingInfo }) {
  const status = tierStatus(routing.tier, routing.formatStatus);
  const text = routing.badge ? readableBadge(routing.badge) : fallbackBadge(routing);
  const Icon = STATUS[status === "pending" ? "verified" : status].icon;
  return (
    <span className={`lr-routing lr-routing--${STATUS[status].tone}`}>
      <Icon size={16} strokeWidth={1.75} aria-hidden="true" />
      <span>{text}</span>
    </span>
  );
}

/** Server badges read "format memory hit: Carrier format-id v5 · 0 LLM calls"; the UI says "model calls" and capitalizes. */
export function readableBadge(badge: string): string {
  const text = badge.replace(/LLM call/g, "model call").replace(/: /, " · ").replace(/→/g, "→");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function fallbackBadge(r: RoutingInfo): string {
  const calls = r.modelCalls ?? 0;
  const head = tierStatus(r.tier, r.formatStatus) === "verified" ? "Format memory hit" : tierStatus(r.tier) === "review" ? "Needs review" : "Format learned";
  return [head, r.formatRef, `${calls} model call${calls === 1 ? "" : "s"}`, r.formatStatus === "shadow" ? "confirming" : null].filter(Boolean).join(" · ");
}
