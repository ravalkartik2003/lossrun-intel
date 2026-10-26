import type { ReactNode } from "react";
import * as Tabs from "@radix-ui/react-tabs";
import { FileText, Moon, Rows2, Rows3, Sun } from "lucide-react";

/** First focusable element on every page (WCAG 2.4.1). */
export function SkipLink({ target = "main" }: { target?: string }) {
  return (
    <a className="lr-skip" href={`#${target}`}>
      Skip to content
    </a>
  );
}

export function ThemeToggle({ theme, onChange }: { theme: "light" | "dark"; onChange: (t: "light" | "dark") => void }) {
  const next = theme === "light" ? "dark" : "light";
  return (
    <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet lr-topbar__icon" onClick={() => onChange(next)} aria-label={`Switch to ${next} theme`} title={`Switch to ${next} theme`}>
      {theme === "light" ? <Moon size={16} aria-hidden="true" /> : <Sun size={16} aria-hidden="true" />}
    </button>
  );
}

export function DensityToggle({ density, onChange }: { density: "dense" | "comfortable"; onChange: (d: "dense" | "comfortable") => void }) {
  return (
    <div className="lr-segmented" role="group" aria-label="Table density">
      <button type="button" className="lr-segmented__item" aria-pressed={density === "dense"} onClick={() => onChange("dense")} title="Dense rows (32px)">
        <Rows3 size={16} aria-hidden="true" /><span className="lr-sr-only">Dense</span>
      </button>
      <button type="button" className="lr-segmented__item" aria-pressed={density === "comfortable"} onClick={() => onChange("comfortable")} title="Comfortable rows (40px)">
        <Rows2 size={16} aria-hidden="true" /><span className="lr-sr-only">Comfortable</span>
      </button>
    </div>
  );
}

/** A citation chip: "loss-run-2024.pdf · p.4"; its accessible name says it opens the source. */
export function Citation({ label, onOpen }: { label: string; onOpen: () => void }) {
  return (
    <button type="button" className="lr-citation" onClick={onOpen} aria-label={`${label} — open source`}>
      <FileText size={14} aria-hidden="true" />
      {label}
    </button>
  );
}

/** Radix tabs with the workbench styling (arrow keys move between tabs). */
export function TabSet({ label, value, onValueChange, tabs, className }: { label: string; value: string; onValueChange: (v: string) => void; tabs: { value: string; label: ReactNode; content: ReactNode }[]; className?: string }) {
  return (
    <Tabs.Root value={value} onValueChange={onValueChange} className={`lr-tabs ${className ?? ""}`}>
      <Tabs.List aria-label={label} className="lr-tabs__list">
        {tabs.map((t) => (
          <Tabs.Trigger key={t.value} value={t.value} className="lr-tabs__tab">
            {t.label}
          </Tabs.Trigger>
        ))}
      </Tabs.List>
      {tabs.map((t) => (
        <Tabs.Content key={t.value} value={t.value} className="lr-tabs__panel">
          {t.content}
        </Tabs.Content>
      ))}
    </Tabs.Root>
  );
}

/** Loading / empty / error states, consistent everywhere. */
export function StateMessage({ kind, children }: { kind: "loading" | "empty" | "error"; children: ReactNode }) {
  return (
    <div className={`lr-state lr-state--${kind}`} role={kind === "error" ? "alert" : "status"}>
      {kind === "loading" ? <span className="lr-skeleton lr-state__bar" aria-hidden="true" /> : null}
      <p>{children}</p>
    </div>
  );
}
