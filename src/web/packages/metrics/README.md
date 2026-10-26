# @lossrun/metrics — client what-if engine (ADR 0011)

This package is pure TypeScript with no dependencies and no UI. It computes every metric in `docs/analytics/catalog.yaml` exactly as the server engine (`src/LossRun.Analytics`) does: for the same input, the snapshots are byte-identical (ADR 0011).

## Usage

```ts
import { readInput, readScenario, whatIf } from "@lossrun/metrics";

const input = readInput(await (await fetch(`/api/v1/quotes/${quoteId}/claims`)).json().then((r) => r.input)); // load once
const catalog = await (await fetch("/api/v1/catalog")).json();                                                   // load once
const { deltas } = whatIf(input, catalog, readScenario({ reserveFactorBps: 1000, capPerClaim: 250000 }));         // every slider move
```

Run `whatIf` in a web worker. Treat `input` as immutable: the input hash is memoised per payload object.

## Tests and benchmark

The tests need Node ≥ 22.18, which runs TypeScript directly; no install or build step is needed.

```bash
node --test test/*.test.ts
```

```bash
node test/bench.ts
```

The tests check four things:
- the hand-computed account (`test/hand-account.json`), which the C# tests assert too;
- the 60 golden cases from `lossrun eval golden`, byte for byte;
- the 1,000-claim performance account, whose snapshot hash must match the C# engine's;
- SHA-256 against `node:crypto`.

## Regenerating after a catalog change

1. Bump `catalogVersion` in the YAML.
2. Regenerate `catalog.json`:
   ```bash
   LOSSRUN_UPDATE_GOLDEN=1 dotnet test src/LossRun.Analytics.Tests
   ```
3. Regenerate `test/golden.json`:
   ```bash
   lossrun eval golden --corpus <corpus-v2> --out src/web/packages/metrics/test/golden.json
   ```
4. Run both test suites.
