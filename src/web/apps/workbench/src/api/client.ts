import { API_BASE, AUTH_MODE } from "../env.ts";
import { authorizationHeader, signInAgain, useAuth } from "../auth/auth.ts";
import type { ApiError } from "./types.ts";

export class HttpError extends Error {
  constructor(public status: number, public body: ApiError | null) {
    super(body?.message ?? `HTTP ${status}`);
  }
}

async function request(path: string, init: RequestInit = {}): Promise<Response> {
  const send = async (forceRefresh: boolean) =>
    fetch(`${API_BASE}${path}`, { ...init, headers: { ...(await authorizationHeader(forceRefresh)), ...(init.headers as Record<string, string> | undefined) } });
  let res = await send(false);
  if (res.status === 401 && AUTH_MODE === "entra") {
    // Brief 24 step 1: one retry with a token refreshed past the cache, then sign in again (the 401 was a thrown error before).
    res = await send(true);
    if (res.status === 401) await signInAgain();
  } else if (res.status === 401) useAuth.getState().signOut();
  if (!res.ok) {
    let body: ApiError | null = null;
    try {
      body = (await res.json()) as ApiError;
    } catch {
      /* not JSON */
    }
    throw new HttpError(res.status, body);
  }
  return res;
}

export async function getJson<T>(path: string, signal?: AbortSignal): Promise<T> {
  return (await (await request(path, { signal })).json()) as T;
}

/** The raw text, for canonical snapshots whose numbers must not be re-formatted. */
export async function getText(path: string, signal?: AbortSignal): Promise<string> {
  return (await request(path, { signal })).text();
}

export async function getBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  return (await request(path, { signal })).blob();
}

export async function sendJson<T>(method: "POST" | "PUT" | "PATCH", path: string, body: unknown): Promise<T> {
  const res = await request(path, { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  const text = await res.text();
  return (text ? JSON.parse(text) : null) as T;
}

export async function postForm<T>(path: string, form: FormData, idempotencyKey?: string): Promise<T> {
  const res = await request(path, { method: "POST", body: form, headers: idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {} });
  return (await res.json()) as T;
}

export { request as rawRequest };
