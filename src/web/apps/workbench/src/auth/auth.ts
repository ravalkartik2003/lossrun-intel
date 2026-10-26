import { create } from "zustand";
import { AUTH_MODE } from "../env.ts";

// Who is signed in. Basic (demo host): the credentials live in sessionStorage for the tab's life only and are sent as
// an Authorization header; nothing is written to localStorage or URLs. Entra: MSAL acquires a token per request.
// Mock: a role picker (no secrets).

const KEY = "lossrun.basic";
const MOCK_ROLE_KEY = "lossrun.mockRole";

interface AuthState {
  basic: string | null;
  mockRole: string;
  signIn: (user: string, password: string) => void;
  setMockRole: (role: string) => void;
  signOut: () => void;
}

function read(key: string): string | null {
  try {
    return sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

export const useAuth = create<AuthState>((set) => ({
  basic: read(KEY),
  mockRole: read(MOCK_ROLE_KEY) ?? "Underwriter",
  signIn: (user, password) => {
    const token = btoa(`${user}:${password}`);
    try {
      sessionStorage.setItem(KEY, token);
    } catch {
      /* private mode: keep it in memory */
    }
    set({ basic: token });
  },
  setMockRole: (role) => {
    try {
      sessionStorage.setItem(MOCK_ROLE_KEY, role);
    } catch {
      /* ignore */
    }
    set({ mockRole: role });
  },
  signOut: () => {
    try {
      sessionStorage.removeItem(KEY);
    } catch {
      /* ignore */
    }
    set({ basic: null });
  },
}));

let entraToken: ((forceRefresh: boolean) => Promise<string>) | null = null;
let entraInteractive: (() => Promise<void>) | null = null;

/**
 * Registered by msal.ts when VITE_AUTH=entra: the silent token source (forceRefresh bypasses the cache) and the
 * interactive fallback (a redirect to sign in), used after a 401 the refreshed token did not cure (brief 24 step 1).
 */
export function setEntraTokenSource(source: (forceRefresh: boolean) => Promise<string>, interactive?: () => Promise<void>) {
  entraToken = source;
  entraInteractive = interactive ?? null;
}

/** Entra mode: the API answered 401 twice (cached then refreshed token): sign in again. */
export async function signInAgain(): Promise<void> {
  await entraInteractive?.();
}

export async function authorizationHeader(forceRefresh = false): Promise<Record<string, string>> {
  if (AUTH_MODE === "entra" && entraToken) return { Authorization: `Bearer ${await entraToken(forceRefresh)}` };
  if (AUTH_MODE === "mock") return { "X-Mock-Role": useAuth.getState().mockRole };
  const basic = useAuth.getState().basic;
  return basic ? { Authorization: `Basic ${basic}` } : {};
}

export function isSignedIn(): boolean {
  return AUTH_MODE !== "basic" || useAuth.getState().basic !== null;
}
