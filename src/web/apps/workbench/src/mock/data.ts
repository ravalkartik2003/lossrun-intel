// Mock data: the live-API capture in docs/ui/mock/capture (synthetic fixtures only; scripts/capture-mock.mjs) plus the
// design-brief mocks in docs/ui/mock. Canonical JSON (features, claims, catalog) stays as text so numbers are exact.

const raw = import.meta.glob<string>("@mock/capture/**/*.json", { query: "?raw", import: "default", eager: true });
const pngs = import.meta.glob<string>("@mock/capture/documents/*/pages/*.png", { query: "?url", import: "default", eager: true });
const brief = import.meta.glob<string>("@mock/*.json", { query: "?raw", import: "default", eager: true });

function find(map: Record<string, string>, suffix: string): string | undefined {
  const key = Object.keys(map).find((k) => k.endsWith(suffix));
  return key ? map[key] : undefined;
}

export function captureText(path: string): string | undefined {
  return find(raw, `/capture/${path}`);
}

export function captureJson<T>(path: string): T | undefined {
  const t = captureText(path);
  return t === undefined ? undefined : (JSON.parse(t) as T);
}

export function briefJson<T>(name: string): T | undefined {
  const t = find(brief, `/mock/${name}`);
  return t === undefined ? undefined : (JSON.parse(t) as T);
}

export function pageUrl(documentId: string, page: number): string | undefined {
  return find(pngs, `/capture/documents/${documentId}/pages/${page}.png`);
}

export function capturedDocumentIds(): string[] {
  return [...new Set(Object.keys(raw).map((k) => /\/documents\/([^/]+)\/result\.json$/.exec(k)?.[1]).filter((x): x is string => !!x))];
}

export function capturedQuoteIds(): string[] {
  return [...new Set(Object.keys(raw).map((k) => /\/quotes\/([^/]+)\/features-adjusted\.json$/.exec(k)?.[1]).filter((x): x is string => !!x))];
}
