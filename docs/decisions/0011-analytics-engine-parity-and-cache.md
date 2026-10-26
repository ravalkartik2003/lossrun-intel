# ADR 0011 — Analytics engine: integer canonical JSON for C# ↔ TypeScript parity, as-reported from overrides, two-level snapshot cache

**Status:** accepted · **Date:** 2026-10-22

## Context
Use Case 2 needs every metric computed identically on the server (official snapshots, pricing memo) and in the browser
(what-if sliders). The research (underwriter-analytics.md KQ2) recommends:
- plain TypeScript metric functions over the per-account payload;
- golden tests for consistency;
- HybridCache for the baseline;
- as-reported snapshots computed from `ExtractedValue` (or `vClaimAsReported`).

## Decision
1. **Parity by construction, not by tolerance.**
   - Both engines compute in integers: money in cents, ratios and rates in millionths, days in tenths, rounding half away from zero.
   - BigInt is used in TypeScript wherever a product can exceed 2^53.
   - Each engine has its own small canonical JSON writer, so neither depends on its platform's float formatting.
   - The input hash uses SHA-256 on both sides; TypeScript has a pure implementation so the browser needs no async crypto.
   - **Result:** 60 golden cases from corpus-v2 truth, plus a 1,000-claim account, are byte-identical across both engines. The C# side is the source of truth: `lossrun eval golden` regenerates the cases.
2. **One catalog drives both engines.**
   - `docs/analytics/catalog.yaml` is embedded in C#.
   - `src/web/packages/metrics/catalog.json` is generated from it; a test fails if it is stale.
   - Both engines iterate the catalog and dispatch by metric name, and a metric without an implementation throws.
3. **As-reported = current document with each overridden field reverted to the value before its first override** (`AccountBuilder.Revert`).
   - It is equivalent to `vClaimAsReported`, but works in both record-store modes.
   - It covers every canonical claim field; `vClaimAsReported` pivots only 13.
   - The overrides ride along in the engine input, so each metric names the overrides that explain reported → adjusted.
4. **Cache:** an in-process L1 plus `dbo.FeatureSnapshot` (or Blob) as L2, instead of HybridCache.
   - **Key:** (quote, revision key, catalog version, view, scenario hash). The revision key hashes the quote's document ids and revisions, from one small query.
   - An override bumps a revision, so L1 can never serve a stale entry.
   - L2 is also invalidated explicitly, in the command handler's transaction (SQL), or by a generation counter (Blob).
   - HybridCache would add a package and an `IDistributedCache` registration for a 25-user POC, without improving on a key that already changes on every claim change.

## Consequences
- The catalog, the C# engine and the TypeScript engine must change together. A metric change means: bump `catalogVersion`, regenerate `catalog.json` and `golden.json`, and both test suites must pass.
- Production can swap L1 for HybridCache behind the same key without changing behaviour.
- The TypeScript twin memoises the input hash per payload object. Inputs must be treated as immutable in the client store, which Zustand state is anyway.
