--liquibase formatted sql

--changeset lossrun:0016-apply-decision-proc runOnChange:true splitStatements:false
--comment: Brief 13 (12b Answers 1): a review decision or direct override in one round trip. The app computes every row (overrides, audit events, changed claims, rule outcomes, the review.decided outbox row) and sends them as one JSON document; this procedure locks the document row, checks the revision, writes them in one transaction (one history version per touched claim, as before), and invalidates the quote's feature snapshots after the commit (housekeeping: snapshots key on document revisions). Brief 24 step 3 (ADR 0024): with a QuoteBump it also bumps dbo.QuoteRevision (dbo.BumpQuoteRevision, changeset 0020) and writes the new revision into the outbox rows ({{quoteRevision}}), in the same transaction. Returns 1 when applied, 0 when the document is missing or another decision already took this revision, 2 when this decision (DecisionKey: its keyed review.decided outbox row, brief 30 item 6) was already applied. CREATE OR ALTER + runOnChange: rerunnable.
CREATE OR ALTER PROCEDURE dbo.ApplyDecision @json nvarchar(max)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @documentId nvarchar(128) = JSON_VALUE(@json, '$.DocumentId');
    DECLARE @newRevision int = CAST(JSON_VALUE(@json, '$.NewRevision') AS int);
    DECLARE @at datetime2(3) = CAST(JSON_VALUE(@json, '$.At') AS datetime2(3));
    DECLARE @user nvarchar(256) = JSON_VALUE(@json, '$.User');
    DECLARE @decisionKey nvarchar(64) = JSON_VALUE(@json, '$.DecisionKey');
    DECLARE @revision int, @quoteId nvarchar(128);

    BEGIN TRANSACTION;

    -- Brief 30 item 6: the decision's review.decided outbox row is keyed on its decision id; if it is stored, this decision was
    -- applied (a retried activity) and nothing is written again: one override set, one outbox set, one revision bump.
    IF @decisionKey IS NOT NULL AND EXISTS (SELECT 1 FROM dbo.Outbox WITH (UPDLOCK, HOLDLOCK) WHERE Id = @decisionKey)
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(2 AS int) AS Value;
        RETURN;
    END;

    SELECT @revision = Revision, @quoteId = QuoteId FROM dbo.Document WITH (UPDLOCK, HOLDLOCK) WHERE Id = @documentId;
    -- Retry rule (brief 32): a stored revision is this decision's only through its own keyed review.decided row above; any other decision's is a conflict, never a resume — do not relax this.
    IF @revision IS NULL OR @revision >= @newRevision
    BEGIN
        ROLLBACK TRANSACTION;
        SELECT CAST(0 AS int) AS Value;
        RETURN;
    END;

    -- Brief 24 step 3: the quote's revision moves with the decision, and the outbox rows carry the value it moved to.
    DECLARE @quoteRevision int = NULL, @bumpQuote nvarchar(128) = JSON_VALUE(@json, '$.QuoteBump.QuoteId');
    IF @bumpQuote IS NOT NULL
    BEGIN
        DECLARE @cause nvarchar(32) = JSON_VALUE(@json, '$.QuoteBump.Cause');
        EXEC dbo.BumpQuoteRevision @quoteId = @bumpQuote, @at = @at, @cause = @cause, @documentId = @documentId, @documentRevision = @newRevision,
            @revision = @quoteRevision OUTPUT;
    END;
    DECLARE @revisionText nvarchar(12) = CAST(@quoteRevision AS nvarchar(12));

    -- Overrides: ids from the sequence (one value per row; EF's hi/lo blocks start at other values), each superseding the field's last override.
    DECLARE @o TABLE (Ord int PRIMARY KEY, Id bigint NOT NULL, ClaimId uniqueidentifier NULL, FieldPath nvarchar(256) NOT NULL);
    INSERT INTO @o (Ord, Id, ClaimId, FieldPath)
    SELECT j.Ord, NEXT VALUE FOR dbo.OverrideSeq OVER (ORDER BY j.Ord), j.ClaimId, j.FieldPath
    FROM OPENJSON(@json, '$.Overrides') WITH (Ord int, ClaimId uniqueidentifier, FieldPath nvarchar(256)) AS j;

    INSERT INTO dbo.Override (Id, DocumentId, DocumentRevision, ClaimId, FieldPath, ClaimField, OldValue, NewValue, Rationale, UserId, Role, AtUtc, CatalogVersion,
        FormatRef, Source, CorrelationId, SupersedesOverrideId)
    SELECT o.Id, j.DocumentId, j.DocumentRevision, j.ClaimId, j.FieldPath, j.ClaimField, j.OldValue, j.NewValue, j.Rationale, j.UserId, j.Role, j.AtUtc,
        j.CatalogVersion, j.FormatRef, j.Source, j.CorrelationId,
        (SELECT MAX(p.Id) FROM dbo.Override AS p WHERE p.ClaimId = j.ClaimId AND p.FieldPath = j.FieldPath)
    FROM OPENJSON(@json, '$.Overrides') WITH (Ord int, DocumentId nvarchar(128), DocumentRevision int, ClaimId uniqueidentifier, FieldPath nvarchar(256),
        ClaimField nvarchar(128), OldValue nvarchar(4000), NewValue nvarchar(4000), Rationale nvarchar(2000), UserId nvarchar(256), Role nvarchar(32),
        AtUtc datetime2(3), CatalogVersion nvarchar(64), FormatRef nvarchar(160), Source nvarchar(16), CorrelationId nvarchar(64)) AS j
    JOIN @o AS o ON o.Ord = j.Ord;

    INSERT INTO dbo.AuditEvent (Id, AtUtc, UserId, Role, Action, UploadId, DocumentId, DocumentRevision, ClaimId, FieldPath, OldValue, NewValue, Rationale,
        OverrideId, CatalogVersion, FormatRef, Source, CorrelationId)
    SELECT NEXT VALUE FOR dbo.AuditEventSeq OVER (ORDER BY j.Ord), j.AtUtc, j.UserId, j.Role, j.Action, j.UploadId, j.DocumentId, j.DocumentRevision, j.ClaimId,
        j.FieldPath, j.OldValue, j.NewValue, j.Rationale, o.Id, j.CatalogVersion, j.FormatRef, j.Source, j.CorrelationId
    FROM OPENJSON(@json, '$.Audits') WITH (Ord int, OverrideOrd int, AtUtc datetime2(3), UserId nvarchar(256), Role nvarchar(32), Action nvarchar(64),
        UploadId nvarchar(64), DocumentId nvarchar(128), DocumentRevision int, ClaimId uniqueidentifier, FieldPath nvarchar(256), OldValue nvarchar(4000),
        NewValue nvarchar(4000), Rationale nvarchar(2000), CatalogVersion nvarchar(64), FormatRef nvarchar(160), Source nvarchar(16), CorrelationId nvarchar(64)) AS j
    LEFT JOIN @o AS o ON o.Ord = j.OverrideOrd;

    -- Changed claims in one statement: one temporal history version per touched claim (the invariant AuditProof checks).
    UPDATE c SET
        ClaimNumber = j.ClaimNumber, ClaimantName = j.ClaimantName, DateOfInjury = j.DateOfInjury, DateReported = j.DateReported, DateClosed = j.DateClosed,
        Status = j.Status, RawStatus = j.RawStatus, ClaimType = j.ClaimType, InjuryTypeCode = j.InjuryTypeCode, NatureOfInjuryCode = j.NatureOfInjuryCode,
        CauseOfInjuryCode = j.CauseOfInjuryCode, PartOfBodyCode = j.PartOfBodyCode, AccidentDescription = j.AccidentDescription, LitigationFlag = j.LitigationFlag,
        CumulativeTrauma = j.CumulativeTrauma, JurisdictionState = j.JurisdictionState, ClassCode = j.ClassCode,
        IndemnityPaid = j.IndemnityPaid, IndemnityReserve = j.IndemnityReserve, IndemnityIncurred = j.IndemnityIncurred,
        MedicalPaid = j.MedicalPaid, MedicalReserve = j.MedicalReserve, MedicalIncurred = j.MedicalIncurred,
        ExpensePaid = j.ExpensePaid, ExpenseReserve = j.ExpenseReserve, ExpenseIncurred = j.ExpenseIncurred,
        TotalPaid = j.TotalPaid, TotalReserve = j.TotalReserve, TotalIncurred = j.TotalIncurred, RecoverySubrogation = j.RecoverySubrogation, NetIncurred = j.NetIncurred,
        ExpenseTreatment = j.ExpenseTreatment, DetailLevel = j.DetailLevel,
        LastOverrideId = COALESCE((SELECT o.Id FROM @o AS o WHERE o.Ord = j.LastOverrideOrd), c.LastOverrideId), ChangedBy = j.ChangedBy
    FROM dbo.Claim AS c
    JOIN OPENJSON(@json, '$.Claims') WITH (Id uniqueidentifier, ClaimNumber nvarchar(128), ClaimantName nvarchar(256), DateOfInjury date, DateReported date,
        DateClosed date, Status nvarchar(16), RawStatus nvarchar(64), ClaimType nvarchar(16), InjuryTypeCode nvarchar(16), NatureOfInjuryCode nvarchar(16),
        CauseOfInjuryCode nvarchar(16), PartOfBodyCode nvarchar(16), AccidentDescription nvarchar(2000), LitigationFlag bit, CumulativeTrauma bit,
        JurisdictionState nvarchar(8), ClassCode nvarchar(16), IndemnityPaid decimal(18, 2), IndemnityReserve decimal(18, 2), IndemnityIncurred decimal(18, 2),
        MedicalPaid decimal(18, 2), MedicalReserve decimal(18, 2), MedicalIncurred decimal(18, 2), ExpensePaid decimal(18, 2), ExpenseReserve decimal(18, 2),
        ExpenseIncurred decimal(18, 2), TotalPaid decimal(18, 2), TotalReserve decimal(18, 2), TotalIncurred decimal(18, 2), RecoverySubrogation decimal(18, 2),
        NetIncurred decimal(18, 2), ExpenseTreatment nvarchar(32), DetailLevel nvarchar(16), LastOverrideOrd int, ChangedBy nvarchar(256)) AS j ON j.Id = c.Id
    WHERE c.DocumentId = @documentId;

    INSERT INTO dbo.RuleOutcome (DocumentId, DocumentRevision, Ordinal, RuleId, Severity, Status, Message, AffectedFieldsJson, ClaimRef, PeriodIndex, Detail)
    SELECT DocumentId, DocumentRevision, Ordinal, RuleId, Severity, Status, Message, AffectedFieldsJson, ClaimRef, PeriodIndex, Detail
    FROM OPENJSON(@json, '$.Outcomes') WITH (DocumentId nvarchar(128), DocumentRevision int, Ordinal int, RuleId nvarchar(16), Severity nvarchar(8), Status nvarchar(16),
        Message nvarchar(2000), AffectedFieldsJson nvarchar(max), ClaimRef nvarchar(128), PeriodIndex int, Detail nvarchar(2000));

    UPDATE dbo.Document SET Revision = @newRevision, ReviewState = JSON_VALUE(@json, '$.ReviewState'), ResultJson = JSON_QUERY(@json, '$.Result'), UpdatedUtc = @at
    WHERE Id = @documentId;

    -- Brief 30 item 15: a direct override on a document with a pending confirm-format review leaves that review open.
    IF ISNULL(JSON_VALUE(@json, '$.KeepReviewOpen'), N'false') <> N'true'
        UPDATE dbo.ReviewItem SET State = N'decided', DecidedUtc = @at, DecisionJson = JSON_QUERY(@json, '$.Decision') WHERE DocumentId = @documentId;

    INSERT INTO dbo.Outbox (Id, Channel, Kind, QuoteId, Subject, PayloadJson, CreatedUtc, CreatedBy)
    SELECT Id, Channel, Kind, QuoteId, Subject,
        CASE WHEN @revisionText IS NULL THEN PayloadJson ELSE REPLACE(REPLACE(PayloadJson, N'"{{quoteRevision}}"', @revisionText), N'{{quoteRevision}}', @revisionText) END,
        CreatedUtc, CreatedBy
    FROM OPENJSON(@json, '$.Outbox') WITH (Id nvarchar(64), Channel nvarchar(16), Kind nvarchar(64), QuoteId nvarchar(128), Subject nvarchar(256),
        PayloadJson nvarchar(max), CreatedUtc datetime2(3), CreatedBy nvarchar(256));

    COMMIT TRANSACTION;

    IF @quoteId IS NOT NULL
        UPDATE dbo.FeatureSnapshot SET InvalidatedUtc = @at, InvalidatedBy = @user WHERE QuoteId = @quoteId AND InvalidatedUtc IS NULL;

    SELECT CAST(1 AS int) AS Value;
END;
--rollback DROP PROCEDURE IF EXISTS dbo.ApplyDecision;

--changeset lossrun:0016-apply-decision-grant splitStatements:false
--comment: The Function App's managed identity executes dbo.ApplyDecision (it has read/write, not EXECUTE). Azure SQL only; a no-op when appPrincipalName is empty.
DECLARE @principal sysname = N'${appPrincipalName}';
IF @principal <> N'' AND CAST(SERVERPROPERTY('EngineEdition') AS int) = 5 AND DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'GRANT EXECUTE ON dbo.ApplyDecision TO ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END
--rollback DECLARE @principal sysname = N'${appPrincipalName}'; DECLARE @sql nvarchar(max) = N'REVOKE EXECUTE ON dbo.ApplyDecision FROM ' + QUOTENAME(@principal) + N';'; IF @principal <> N'' AND DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL EXEC sys.sp_executesql @sql;
