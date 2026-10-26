// Brief 24 step 2: labels for the Analytics readiness banner (openapi Readiness).

/** "Policy 1, claim 3 · total.incurred" from a lineage path; document-level paths stay as they are. */
export function fieldLabel(path: string): string {
  const m = /^policyPeriods\[(\d+)\](?:\.claims\[(\d+)\])?\.(.+)$/.exec(path);
  if (!m) return path;
  return m[2] !== undefined ? `Policy ${Number(m[1]) + 1}, claim ${Number(m[2]) + 1} · ${m[3]}` : `Policy ${Number(m[1]) + 1} · ${m[3]}`;
}

const REASONS: Record<string, string> = { "R-33": "required and missing", "review-pending": "waiting for review", "document-rejected": "document rejected" };

/** Why a value blocks the quote: a named reason, or the hard rule that still fails on it. */
export function reasonLabel(reason?: string | null): string {
  if (!reason) return "unresolved";
  return REASONS[reason] ?? `rule ${reason} still fails`;
}
