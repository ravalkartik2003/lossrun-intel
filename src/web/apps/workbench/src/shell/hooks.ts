import { useEffect, useState } from "react";

/** Screen widths (brief 11 §5): ≥ 1280 workbench, 768–1279 tablet layout, < 768 small. */
export type Width = "wide" | "medium" | "small";

export function useWidth(): Width {
  const get = (): Width => (typeof window === "undefined" ? "wide" : window.innerWidth >= 1280 ? "wide" : window.innerWidth >= 768 ? "medium" : "small");
  const [w, setW] = useState<Width>(get);
  useEffect(() => {
    const on = () => setW(get());
    window.addEventListener("resize", on);
    return () => window.removeEventListener("resize", on);
  }, []);
  return w;
}

/** Page title per screen (WCAG 2.4.2). */
export function useTitle(title: string) {
  useEffect(() => {
    document.title = `${title} · Loss Run Intelligence`;
  }, [title]);
}

export function useReducedMotion(): boolean {
  const [r, setR] = useState(() => typeof matchMedia !== "undefined" && matchMedia("(prefers-reduced-motion: reduce)").matches);
  useEffect(() => {
    const m = matchMedia("(prefers-reduced-motion: reduce)");
    const on = () => setR(m.matches);
    m.addEventListener("change", on);
    return () => m.removeEventListener("change", on);
  }, []);
  return r;
}
