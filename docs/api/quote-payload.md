# Quote payload

## v2 — `GET /api/v1/quotes/{quoteId}/quote-payload` (ADR 0011; current)

One payload per quote, across all of its loss-run documents. It supersedes v1 below, which stays for compatibility.

| Field | Contents |
|---|---|
| `payloadVersion` | `"2"` |
| `quoteId`, `catalogVersion`, `valuationDate`, `generatedUtc` | Account header. `valuationDate` is the latest of the documents'. |
| `documents[]` | Per document that counts (not rejected): id, upload, revision, carrier, insured, valuation date, format ref, routing tier, review state, claim count, `granularity` (`claim`, `period` when every policy period is summary-only, ADR 0017, or `mixed`) and `fieldStatusCounts` (fields per status, brief 24). |
| `features.adjusted` / `features.reported` | Feature snapshots (`FeatureSnapshot` in openapi.yaml): every catalog metric with breakdowns, contributors and override lineage. Reported = values before each field's first override. |
| `periods[]` | Per policy period: claim and open counts, and paid / reserve / incurred by component, from `features.adjusted.perPeriod`. |
| `claims[]` | Canonical claims with `id` (`{documentId}/{period}/{claim}`), `policyNumber`, `flags` (ct, large, litigated, nonCA, open, …) and `fieldStatus`: the claim's fields that are not approved, by path relative to the claim, with status and reason. Brief 24: such a value is `null` here, never the unverified extraction. |
| `readiness` | Brief 24 step 2: `{ ready, blockingFields[], blockingFieldCount, rejectedDocuments[], pendingDocuments[] }`. Ready when at least one document counts, none waits for review, and no field of a counted document is unresolved (a hard rule still failing on it, or R-33 required for quoting and null). `blockingFields` lists the first 200; `blockingFieldCount` counts them all. Also on `GET /quotes/{id}/claims`. |
| `policyPeriods[]` | Brief 25: one row per policy period of every document: policy number, effective and expiration date, period label, carrier, the document's valuation date and its source, detail level, the no-losses statement, `excludedReason` (`rejected` / `pending-review` / null) and the identity fields' `fieldStatus`. A value that is not approved is null. The quote system's loss-information grid reads it. |
| `featuresView` | `verified-only` (brief 24 Answers 2): the payload never carries an unverified value. `features` (its `adjusted` key is the verified-only snapshot), `periods`, `periodSummaries` and `claims` leave out documents pending review and null every unresolved field. The pricing memo is built from it and prints "Verified values only". The Analytics screen keeps the whole picture (`view=adjusted`), labelled by the readiness banner; `GET /quotes/{id}/features|claims?view=verified-only` gives the payload's view. |
| `excluded[]` | Rejected documents (`reason: rejected`) and, in the verified-only payload, documents pending review (`reason: pending-review`), with revision and claim count. Their claims are not in `claims`, `features` or `periods`, and they are out of the chat scope; the listing keeps the audit trail visible. |
| `periodSummaries` | ADR 0017: `{ granularity: "period", rows, metrics }`. One row per summary-only period (period kind and label, dates, counts, paid / reserve / incurred, recoveries by kind, `lineagePath`). `metrics` holds the catalog metrics marked `fromSummary: yes`, computed from the rows; the others are not measurable. These periods are not in `claims` or `features`. Example: `docs/ui/mock/quote-payload-period.json`. |
| `sourceAvailability` | Brief 19 Answers 1: per component a document prints no column for (e.g. medical), the documents and the catalog metrics computed without it ("not available from source"). Each document entry also lists its `absentComponents`. |
| `lineage` | Documents, pending review, review rate, valuation dates, format versions, value-source counts by extractor, and every override with rationale, user and time. |

Metric definitions are in `docs/analytics/catalog.yaml`, also served at `GET /api/v1/catalog`.

## v1 — `GET /api/v1/documents/{id}/quote-payload` (ADR 0008; superseded)

**Status:** first cut (ADR 0008). This payload is the normalized input for the pricing engine and the quote system. The analytics catalog (`docs/analytics/catalog.yaml`, ADR 0011) defines the rating features (frequency, severity, loss ratio, development, large-loss flags, and so on). This payload carries only the raw material for them.

```json
{
  "documentId": "doc-…",
  "carrierName": "Synthetic Mutual Insurance Co.",
  "insuredName": "Acme Widgets (synthetic)",
  "valuationDate": "2024-09-30",
  "periods": [
    {
      "policyNumber": "SYN-WC-1001", "effectiveDate": "2022-07-01", "expirationDate": "2023-07-01",
      "claimCount": 2, "openCount": 1,
      "indemnity": { "paid": 12000, "reserve": 0, "incurred": 12000 },
      "medical":   { "paid": 9200,  "reserve": 800, "incurred": 10000 },
      "expense":   { "paid": 1500,  "reserve": 0, "incurred": 1500 },
      "total":     { "paid": 22700, "reserve": 800, "incurred": 23500 }
    }
  ],
  "claims": [ { "claimNumber": "WC-22-0001", "dateOfInjury": "2022-09-15", "status": "closed", "indemnity": { … }, … } ]
}
```

## How each field is computed

- **Period sums** are summed over the claim rows. The printed subtotals are not used: they have already been reconciled against the rows by rule R-10.
- **A missing paid or reserve value counts as 0.** A missing incurred value is computed as paid + reserve.
- **`openCount`** counts claims whose status is `open` or `reopened`.
- **`claims`** are canonical `Claim` objects, as defined in `docs/evaluation/canonical-schema.json`. The quote system reads lineage from `GET /documents/{id}` when it needs to cite a value.
- **The values reflect the current revision.** After a reviewer override, the payload shows the overridden values.
