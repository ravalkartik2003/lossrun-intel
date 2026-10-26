import { useEffect, useRef, useState } from "react";
import { Check, Circle, LoaderCircle } from "lucide-react";

export interface Step {
  key: string;
  label: string;
  state: "done" | "active" | "pending" | "skipped";
  /** Milliseconds the step took (shown when known). */
  ms?: number | null;
}

/**
 * Classified → Layout → Format match → Extracted → Validated → Ready (design-brief §4). One polite live region per row
 * announces each step change once ("example-carrier-loss-run.pdf: Validated"), never the whole list.
 */
export function ProcessingStepper({ steps, name }: { steps: Step[]; name: string }) {
  const active = steps.find((s) => s.state === "active") ?? [...steps].reverse().find((s) => s.state === "done");
  const [announced, setAnnounced] = useState("");
  const last = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (active && active.key !== last.current) {
      last.current = active.key;
      setAnnounced(`${name}: ${active.label}${active.state === "done" && active.key === steps[steps.length - 1].key ? ", ready" : ""}`);
    }
  }, [active?.key, active?.state]);

  return (
    <div className="lr-stepper">
      <ol className="lr-stepper__list" aria-label={`Processing steps for ${name}`}>
        {steps.map((s) => (
          <li key={s.key} className={`lr-step lr-step--${s.state}`} aria-current={s.state === "active" ? "step" : undefined}>
            <span className="lr-step__icon" aria-hidden="true">
              {s.state === "done" ? <Check size={12} strokeWidth={2.5} /> : s.state === "active" ? <LoaderCircle size={12} className="lr-spin" /> : <Circle size={8} />}
            </span>
            <span className="lr-step__label">{s.label}</span>
            {s.ms !== null && s.ms !== undefined && s.state === "done" ? <span className="lr-step__ms">{formatMs(s.ms)}</span> : null}
            <span className="lr-sr-only">{s.state === "done" ? "(done)" : s.state === "active" ? "(in progress)" : s.state === "skipped" ? "(not needed)" : "(waiting)"}</span>
          </li>
        ))}
      </ol>
      <span className="lr-sr-only" aria-live="polite">{announced}</span>
    </div>
  );
}

function formatMs(ms: number): string {
  return ms < 1000 ? `${Math.round(ms)} ms` : `${(ms / 1000).toFixed(1)} s`;
}
