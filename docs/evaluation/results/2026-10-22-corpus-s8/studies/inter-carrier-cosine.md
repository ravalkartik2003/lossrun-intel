### Inter-carrier cosine (`docs/architecture/carrier-format-memory.md`)

24 documents (6 per carrier across renderings), `fp-text-1` text of the first page, embedded with the run's embedding model; carrier centroids are the mean of each carrier's documents. Mean cosine, document vs carrier centroid:

| from \ to | c1 | c2 | c4 | c5 |
|---|---:|---:|---:|---:|
| c1 | 0.985 | 0.779 | 0.798 | 0.754 |
| c2 | 0.775 | 0.979 | 0.760 | 0.747 |
| c4 | 0.797 | 0.763 | 0.983 | 0.790 |
| c5 | 0.764 | 0.761 | 0.802 | 0.998 |

- Highest inter-carrier cosine: **0.815** (c4-002-xlsx → c1), i.e. a minimum inter-carrier distance of 0.185.
- Lowest own-carrier cosine: **0.962** (c1-007-scan-150dpi-fax, scan-150dpi-fax); own-carrier identity containment min 0.63.
- Cosine-only false hits (≥ 0.95 to another carrier): **0**; after the identity guard (containment ≥ 0.8): **0**.

**Decision:** keep the 0.95 embedding threshold with the identity guard: 0 false hits after the guard (0 before); minimum inter-carrier distance 0.185. Spreadsheets are matched on header/identity keys, not embeddings; they are in the matrix for completeness.
