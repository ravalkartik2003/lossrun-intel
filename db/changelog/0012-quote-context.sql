--liquibase formatted sql

--changeset lossrun:0012-quote-context splitStatements:false
--comment: Submission context per quote (report 10 answer 1, brief 11): payroll and FTE per policy period and the account-level last day worked, as JSON. System-versioned so "the context at quote time" is answerable; every change also writes a quote.context row to the AuditEvent ledger (FieldPath = quotes/{quoteId}). Feature snapshots key on a hash of it.
IF OBJECT_ID(N'dbo.QuoteContext', N'U') IS NULL
CREATE TABLE dbo.QuoteContext
(
    QuoteId     nvarchar(128) NOT NULL CONSTRAINT PK_QuoteContext PRIMARY KEY,
    ContextJson nvarchar(max) NOT NULL,
    UpdatedUtc  datetime2(3)  NOT NULL,
    UpdatedBy   nvarchar(256) NOT NULL,
    ValidFromUtc datetime2(3)  GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc   datetime2(3)  GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.QuoteContextHistory));
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.QuoteContext'), N'TableTemporalType') = 2 ALTER TABLE dbo.QuoteContext SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.QuoteContext;
--rollback DROP TABLE IF EXISTS dbo.QuoteContextHistory;
