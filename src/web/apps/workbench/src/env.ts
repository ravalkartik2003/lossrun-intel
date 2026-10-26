// Build-time configuration (Vite env). VITE_MOCK=1 serves docs/ui/mock through MSW and runs fully offline.

export const MOCK = import.meta.env.VITE_MOCK === "1";

/** API base: the Function App in live mode (…/api/v1), or the mock origin's /api/v1. */
export const API_BASE: string = MOCK ? "/api/v1" : (import.meta.env.VITE_API_BASE ?? "/api/v1").replace(/\/$/, "");

/** "basic" on the demo host (LOSSRUN_DEV_AUTH=basic), "entra" for MSAL bearer tokens. */
export const AUTH_MODE: "basic" | "entra" | "mock" = MOCK ? "mock" : import.meta.env.VITE_AUTH === "entra" ? "entra" : "basic";

export const ENTRA = {
  clientId: import.meta.env.VITE_ENTRA_CLIENT_ID ?? "",
  authority: import.meta.env.VITE_ENTRA_AUTHORITY ?? "",
  scope: import.meta.env.VITE_ENTRA_SCOPE ?? "",
};

/** The top-bar attribution chip (brief 13 addendum): set per build with VITE_ENV_LABEL; no client branding anywhere. */
export const ENVIRONMENT_LABEL: string = import.meta.env.VITE_ENV_LABEL?.trim() || "Independent POC · synthetic data";
