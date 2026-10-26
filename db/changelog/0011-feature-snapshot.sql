--liquibase formatted sql

--changeset lossrun:0011-feature-snapshot splitStatements:false
--comment: Cached feature snapshots (brief 10 §2): the analytics engine's output for one quote under one view (reported | adjusted), catalog version and scenario. RevisionKey hashes the quote's document ids and revisions, so a new upload or override never matches an old row; ClaimCommandService also stamps InvalidatedUtc in the same transaction as any claim change. A plain table (a cache, not a record): the audit trail stays in the ledger tables.
IF OBJECT_ID(N'dbo.FeatureSnapshot', N'U') IS NULL
CREATE TABLE dbo.FeatureSnapshot
(
    Id             bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_FeatureSnapshot PRIMARY KEY,
    QuoteId        nvarchar(128)  NOT NULL,
    RevisionKey    nvarchar(64)   NOT NULL,
    CatalogVersion nvarchar(32)   NOT NULL,
    [View]         nvarchar(16)   NOT NULL,
    ScenarioHash   nvarchar(64)   NOT NULL,
    InputsHash     nvarchar(64)   NOT NULL,
    ClaimCount     int            NOT NULL,
    SnapshotJson   nvarchar(max)  NOT NULL,
    CreatedUtc     datetime2(3)   NOT NULL,
    InvalidatedUtc datetime2(3)   NULL,
    InvalidatedBy  nvarchar(256)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FeatureSnapshot_Key' AND object_id = OBJECT_ID(N'dbo.FeatureSnapshot'))
    CREATE INDEX IX_FeatureSnapshot_Key ON dbo.FeatureSnapshot (QuoteId, RevisionKey, CatalogVersion, [View], ScenarioHash) INCLUDE (InvalidatedUtc);
--rollback DROP TABLE IF EXISTS dbo.FeatureSnapshot;
