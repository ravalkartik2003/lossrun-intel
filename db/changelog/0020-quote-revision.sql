--liquibase formatted sql

--changeset lossrun:0020-quote-revision splitStatements:false
--comment: Brief 24 step 3 (ADR 0024): the quote-level revision. One row per quote, bumped in the same transaction as an upload's completion (CompleteUpload) or a decision on one of its documents (dbo.ApplyDecision): review decision, reopened decision or direct override. The quote payload carries it as quoteRevision and ETag "q-<n>"; every quote.payload command and quote.revised event carries the revision of the transaction that wrote it, so the quote system can refuse an older one (409 stale-revision). System-versioned: the history is every revision with its cause.
IF OBJECT_ID(N'dbo.QuoteRevision', N'U') IS NULL
CREATE TABLE dbo.QuoteRevision
(
    QuoteId          nvarchar(128) NOT NULL CONSTRAINT PK_QuoteRevision PRIMARY KEY,
    Revision         int           NOT NULL,
    ChangedUtc       datetime2(3)  NOT NULL,
    Cause            nvarchar(32)  NOT NULL,
    DocumentId       nvarchar(128) NULL,
    DocumentRevision int           NULL,
    ValidFromUtc     datetime2(3)  GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc       datetime2(3)  GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.QuoteRevisionHistory));
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.QuoteRevision'), N'TableTemporalType') = 2 ALTER TABLE dbo.QuoteRevision SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.QuoteRevision;
--rollback DROP TABLE IF EXISTS dbo.QuoteRevisionHistory;

--changeset lossrun:0020-bump-quote-revision-proc runOnChange:true splitStatements:false
--comment: Brief 24 step 3: bumps a quote's revision and returns the new value in @revision. Runs inside the caller's transaction (CompleteUpload's audit + outbox unit; dbo.ApplyDecision). UPDLOCK + HOLDLOCK serialize concurrent bumps of one quote, including its first. CREATE OR ALTER + runOnChange: rerunnable.
CREATE OR ALTER PROCEDURE dbo.BumpQuoteRevision @quoteId nvarchar(128), @at datetime2(3), @cause nvarchar(32), @documentId nvarchar(128) = NULL,
    @documentRevision int = NULL, @revision int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET @revision = NULL;
    UPDATE dbo.QuoteRevision WITH (UPDLOCK, HOLDLOCK)
    SET @revision = Revision = Revision + 1, ChangedUtc = @at, Cause = @cause, DocumentId = @documentId, DocumentRevision = @documentRevision
    WHERE QuoteId = @quoteId;
    IF @revision IS NULL
    BEGIN
        SET @revision = 1;
        INSERT INTO dbo.QuoteRevision (QuoteId, Revision, ChangedUtc, Cause, DocumentId, DocumentRevision) VALUES (@quoteId, 1, @at, @cause, @documentId, @documentRevision);
    END;
END;
--rollback DROP PROCEDURE IF EXISTS dbo.BumpQuoteRevision;

--changeset lossrun:0020-bump-quote-revision-grant splitStatements:false
--comment: The Function App's managed identity executes dbo.BumpQuoteRevision. Azure SQL only; a no-op when appPrincipalName is empty.
DECLARE @principal sysname = N'${appPrincipalName}';
IF @principal <> N'' AND CAST(SERVERPROPERTY('EngineEdition') AS int) = 5 AND DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'GRANT EXECUTE ON dbo.BumpQuoteRevision TO ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END
--rollback DECLARE @principal sysname = N'${appPrincipalName}'; DECLARE @sql nvarchar(max) = N'REVOKE EXECUTE ON dbo.BumpQuoteRevision FROM ' + QUOTENAME(@principal) + N';'; IF @principal <> N'' AND DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL EXEC sys.sp_executesql @sql;
