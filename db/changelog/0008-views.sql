--liquibase formatted sql

--changeset lossrun:0008-v-claim-as-reported runOnChange:true splitStatements:false
--comment: As-reported claim values: the first extraction revision of each claim from the ExtractedValue ledger, pivoted to the Claim columns (underwriter-analytics.md KQ4).
CREATE OR ALTER VIEW dbo.vClaimAsReported
AS
SELECT ev.ClaimId,
       ev.DocumentId,
       MAX(CASE WHEN ev.ClaimField = N'claimNumber' THEN ev.Value END)                                  AS ClaimNumber,
       MAX(CASE WHEN ev.ClaimField = N'claimantName' THEN ev.Value END)                                 AS ClaimantName,
       TRY_CONVERT(date, MAX(CASE WHEN ev.ClaimField = N'dateOfInjury' THEN ev.Value END))              AS DateOfInjury,
       MAX(CASE WHEN ev.ClaimField = N'status' THEN ev.Value END)                                       AS Status,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'indemnity.paid' THEN ev.Value END))     AS IndemnityPaid,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'indemnity.reserve' THEN ev.Value END))  AS IndemnityReserve,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'medical.paid' THEN ev.Value END))       AS MedicalPaid,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'medical.reserve' THEN ev.Value END))    AS MedicalReserve,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'expense.paid' THEN ev.Value END))       AS ExpensePaid,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'expense.reserve' THEN ev.Value END))    AS ExpenseReserve,
       TRY_CONVERT(decimal(18, 2), MAX(CASE WHEN ev.ClaimField = N'total.incurred' THEN ev.Value END))     AS TotalIncurred
FROM dbo.ExtractedValue AS ev
WHERE ev.ClaimId IS NOT NULL
  AND ev.DocumentRevision = (SELECT MIN(f.DocumentRevision) FROM dbo.ExtractedValue AS f WHERE f.DocumentId = ev.DocumentId)
GROUP BY ev.ClaimId, ev.DocumentId;
--rollback DROP VIEW IF EXISTS dbo.vClaimAsReported;

--changeset lossrun:0008-v-claim-as-adjusted runOnChange:true splitStatements:false
--comment: As-adjusted claim values (current Claim row) with the number of overrides applied; use FOR SYSTEM_TIME AS OF on dbo.Claim for "as adjusted at quote time".
CREATE OR ALTER VIEW dbo.vClaimAsAdjusted
AS
SELECT c.Id AS ClaimId, c.DocumentId, d.UploadId, d.QuoteId, d.CarrierName, d.ValuationDate, p.PolicyNumber, p.EffectiveDate, p.ExpirationDate,
       c.ClaimNumber, c.ClaimantName, c.DateOfInjury, c.DateReported, c.DateClosed, c.Status, c.ClaimType,
       c.IndemnityPaid, c.IndemnityReserve, c.IndemnityIncurred, c.MedicalPaid, c.MedicalReserve, c.MedicalIncurred,
       c.ExpensePaid, c.ExpenseReserve, c.ExpenseIncurred, c.TotalPaid, c.TotalReserve, c.TotalIncurred,
       (SELECT COUNT(*) FROM dbo.Override AS o WHERE o.ClaimId = c.Id) AS OverrideCount,
       c.ValidFromUtc AS AdjustedUtc
FROM dbo.Claim AS c
JOIN dbo.Document AS d ON d.Id = c.DocumentId
JOIN dbo.PolicyPeriod AS p ON p.Id = c.PolicyPeriodId;
--rollback DROP VIEW IF EXISTS dbo.vClaimAsAdjusted;

--changeset lossrun:0008-v-audit-trail runOnChange:true splitStatements:false
--comment: Audit events with the engine's own record of who committed them and when (ledger view joined to sys.database_ledger_transactions).
CREATE OR ALTER VIEW dbo.vAuditTrail
AS
SELECT a.Id, a.AtUtc, a.UserId, a.Role, a.Action, a.UploadId, a.DocumentId, a.DocumentRevision, a.ClaimId, a.FieldPath, a.OldValue, a.NewValue, a.Rationale,
       a.OverrideId, a.CatalogVersion, a.FormatRef, a.Source, a.CorrelationId,
       t.commit_time AS CommittedUtc, t.principal_name AS CommittedBy, l.ledger_transaction_id AS LedgerTransactionId
FROM dbo.AuditEvent_Ledger AS l
JOIN dbo.AuditEvent AS a ON a.Id = l.Id
JOIN sys.database_ledger_transactions AS t ON t.transaction_id = l.ledger_transaction_id;
--rollback DROP VIEW IF EXISTS dbo.vAuditTrail;

--changeset lossrun:0008-v-cost-by-path runOnChange:true splitStatements:false
--comment: Cost and model calls per path and carrier (cost-tracking.md 3).
CREATE OR ALTER VIEW dbo.vCostByPath
AS
SELECT dc.Path, d.CarrierName, COUNT(*) AS Documents, SUM(dc.CostUsd) AS CostUsd, AVG(dc.CostUsd) AS MeanCostUsd,
       SUM(dc.LlmCalls) AS LlmCalls, SUM(dc.Pages) AS Pages, SUM(dc.TokensIn) AS TokensIn, SUM(dc.TokensOut) AS TokensOut
FROM dbo.DocumentCost AS dc
LEFT JOIN dbo.Document AS d ON d.Id = dc.DocumentId
GROUP BY dc.Path, d.CarrierName;
--rollback DROP VIEW IF EXISTS dbo.vCostByPath;
