import { create } from "zustand";

// UI state that outlives a screen: theme, density, the selected quote, and the top bar's contextual cost line.
// Per-viewer conveniences only (localStorage, wrapped: it may be unavailable).

type Theme = "light" | "dark";
type Density = "dense" | "comfortable";

export interface ContextCost {
  label: string;
  usd: number | null;
  pages?: number | null;
  modelCalls?: number | null;
}

interface UiState {
  theme: Theme;
  density: Density;
  quoteId: string | null;
  contextCost: ContextCost | null;
  setTheme: (t: Theme) => void;
  setDensity: (d: Density) => void;
  setQuote: (q: string | null) => void;
  setContextCost: (c: ContextCost | null) => void;
}

function load<T extends string>(key: string, fallback: T): T {
  try {
    return (localStorage.getItem(key) as T | null) ?? fallback;
  } catch {
    return fallback;
  }
}

function save(key: string, value: string | null) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch {
    /* storage unavailable */
  }
}

const prefersDark = typeof matchMedia !== "undefined" && matchMedia("(prefers-color-scheme: dark)").matches;

export const useUi = create<UiState>((set) => ({
  theme: load<Theme>("lossrun.theme", prefersDark ? "dark" : "light"),
  density: load<Density>("lossrun.density", "dense"),
  quoteId: load<string>("lossrun.quote", "") || null,
  contextCost: null,
  setTheme: (theme) => {
    save("lossrun.theme", theme);
    set({ theme });
  },
  setDensity: (density) => {
    save("lossrun.density", density);
    set({ density });
  },
  setQuote: (quoteId) => {
    save("lossrun.quote", quoteId);
    set({ quoteId });
  },
  setContextCost: (contextCost) => set({ contextCost }),
}));
