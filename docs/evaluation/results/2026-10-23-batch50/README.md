# 50 MB mixed batch (`docs/poc-plan/closeout/README.md`)

**Criterion:** the requirement asks for end-to-end latency "≤ XXX seconds for 50 MB mixed batch". It had not been measured before.

**Method**
- **The batch.** One multipart upload of 72 corpus-v3 files: 50.8 MB of files, 144 pages.
- **The mix:** 6 XLSX, 6 CSV, 16 native PDF, 10 fax (150 dpi), 14 rotated scans (200 dpi), 20 clean scans (300 dpi).
- **The target.** Posted to the deployed API on 2026-10-23, with a warm Function App and `demo_mode` off.
- **Timing.** From the POST until every manifest item is terminal, polling `GET /uploads/{id}` every 3 s.
- **Script:** `batch50.py`. Run it with `LOSSRUN_API_USER` / `LOSSRUN_API_PASSWORD` set and a quote id as the argument.

**Result** (`result.json`)

| Measure | Value |
|---|---:|
| 202 Accepted (request received, files stored, orchestration started) | 10.8 s |
| First document processed | 53.1 s |
| Median document terminal | 76.1 s |
| **Whole batch terminal** | **89.0 s** |
| Delivered (replay) / review | 59 / 13 (10 faxes by design, 3 rotated scans) |
| Metered cost (manifest) | $0.26 |

**Found and fixed on the way.** The first attempt failed with an unlogged HTTP 500.
- **Cause.** The isolated worker's Kestrel caps request bodies at 30,000,000 bytes, which is below the Functions host's 100 MB limit.
- **Fix.** The upload route now raises its own cap to 100 MB, and answers 413 with a message above it (`HttpApi.MaxUploadBytes`). Every other route keeps the default.

**Re-measure** on the blind set's largest real bundles when they exist. Real scans are longer and denser than these synthetic 1–3 page documents.
