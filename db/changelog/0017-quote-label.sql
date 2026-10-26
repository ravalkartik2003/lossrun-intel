--liquibase formatted sql

--changeset lossrun:0017-quote-label splitStatements:false
--comment: Brief 13 demo hygiene: a quote's display name and retired flag. Quotes have no row of their own (they are the QuoteId on documents), and the ledger rows that name a quote are immutable, so a "rename" is a display name over the unchanged id and "retire" hides the quote from lists without deleting anything. System-versioned; every change also writes quote.rename / quote.retire / quote.restore to the AuditEvent ledger (FieldPath = quotes/{quoteId}).
IF OBJECT_ID(N'dbo.QuoteLabel', N'U') IS NULL
CREATE TABLE dbo.QuoteLabel
(
    QuoteId      nvarchar(128) NOT NULL CONSTRAINT PK_QuoteLabel PRIMARY KEY,
    DisplayName  nvarchar(256) NULL,
    RetiredUtc   datetime2(3)  NULL,
    RetiredBy    nvarchar(256) NULL,
    UpdatedUtc   datetime2(3)  NOT NULL,
    UpdatedBy    nvarchar(256) NOT NULL,
    ValidFromUtc datetime2(3)  GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc   datetime2(3)  GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.QuoteLabelHistory));
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.QuoteLabel'), N'TableTemporalType') = 2 ALTER TABLE dbo.QuoteLabel SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.QuoteLabel;
--rollback DROP TABLE IF EXISTS dbo.QuoteLabelHistory;
