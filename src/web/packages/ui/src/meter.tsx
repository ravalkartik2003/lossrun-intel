/** Confidence thresholds (design-brief §4): ≥ 0.9 verified, ≥ 0.7 review, below fail. */
export function confidenceTone(value: number): "verified" | "review" | "fail" {
  return value >= 0.9 ? "verified" : value >= 0.7 ? "review" : "fail";
}

/** 24×6 bar + the number, always shown (color is never the only signal). role=meter with the value announced. */
export function ConfidenceMeter({ value, label = "Confidence" }: { value: number | null | undefined; label?: string }) {
  if (value === null || value === undefined) {
    return <span className="lr-meter lr-meta">—</span>;
  }
  const v = Math.max(0, Math.min(1, value));
  const tone = confidenceTone(v);
  return (
    <span className="lr-meter">
      <span
        className={`lr-meter__bar lr-meter__bar--${tone}`}
        role="meter"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={1}
        aria-valuenow={Number(v.toFixed(2))}
        aria-valuetext={`${v.toFixed(2)} (${tone === "verified" ? "high" : tone === "review" ? "check" : "low"})`}
      >
        <span className="lr-meter__fill" style={{ width: `${Math.round(v * 100)}%` }} />
      </span>
      <span className="lr-num" aria-hidden="true">{v.toFixed(2)}</span>
    </span>
  );
}
