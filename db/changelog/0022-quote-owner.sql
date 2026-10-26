--liquibase formatted sql

--changeset lossrun:0022-quote-owner splitStatements:false
--comment: Brief 30 item 1: who may attach uploads and context to a quote. One row per quote, written by the first upload that names it: ApplicantUserId is the Applicant who created it, NULL when an underwriter role created it (then only underwriter roles attach to it). A quote with documents or context but no row (created before this changeset) counts as NULL: underwriter roles only. Insert-if-absent, never updated (a change of owner is an engineer action through the ledger, not a column write). System-versioned so the history stays answerable.
IF OBJECT_ID(N'dbo.QuoteOwner', N'U') IS NULL
CREATE TABLE dbo.QuoteOwner
(
    QuoteId         nvarchar(128) NOT NULL CONSTRAINT PK_QuoteOwner PRIMARY KEY,
    ApplicantUserId nvarchar(256) NULL,
    CreatedUtc      datetime2(3)  NOT NULL,
    CreatedBy       nvarchar(256) NOT NULL,
    ValidFromUtc    datetime2(3)  GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc      datetime2(3)  GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.QuoteOwnerHistory));
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.QuoteOwner'), N'TableTemporalType') = 2 ALTER TABLE dbo.QuoteOwner SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.QuoteOwner;
--rollback DROP TABLE IF EXISTS dbo.QuoteOwnerHistory;
