--liquibase formatted sql

--changeset lossrun:0007-usage-event-seq splitStatements:false
IF OBJECT_ID(N'dbo.UsageEventSeq', N'SO') IS NULL
    CREATE SEQUENCE dbo.UsageEventSeq AS bigint START WITH 1 INCREMENT BY 100;
--rollback DROP SEQUENCE IF EXISTS dbo.UsageEventSeq;

--changeset lossrun:0007-usage-event splitStatements:false
--comment: Every billable call, metered (cost-tracking.md 1 and 6: append-only ledger). Prices are applied at read time from config/azure-prices.json, never stored here.
IF OBJECT_ID(N'dbo.UsageEvent', N'U') IS NULL
CREATE TABLE dbo.UsageEvent
(
    Id              bigint         NOT NULL CONSTRAINT DF_UsageEvent_Id DEFAULT (NEXT VALUE FOR dbo.UsageEventSeq),
    DocumentId      nvarchar(128)  NOT NULL,
    RunId           nvarchar(128)  NOT NULL,
    Stage           nvarchar(64)   NOT NULL,
    Service         nvarchar(64)   NOT NULL,
    Meter           nvarchar(128)  NOT NULL,
    Quantity        decimal(19, 6) NOT NULL,
    Unit            nvarchar(32)   NOT NULL,
    ModelOrAnalyzer nvarchar(128)  NULL,
    ModelVersion    nvarchar(64)   NULL,
    DeploymentType  nvarchar(64)   NULL,
    Region          nvarchar(64)   NOT NULL,
    TimestampUtc    datetime2(3)   NOT NULL,
    CorrelationId   nvarchar(64)   NULL,
    CONSTRAINT PK_UsageEvent PRIMARY KEY NONCLUSTERED (Id)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UsageEvent_DocumentId' AND object_id = OBJECT_ID(N'dbo.UsageEvent'))
    CREATE CLUSTERED INDEX IX_UsageEvent_DocumentId ON dbo.UsageEvent (DocumentId, TimestampUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UsageEvent_RunId' AND object_id = OBJECT_ID(N'dbo.UsageEvent'))
    CREATE INDEX IX_UsageEvent_RunId ON dbo.UsageEvent (RunId);
--rollback DROP TABLE IF EXISTS dbo.UsageEvent;

--changeset lossrun:0007-document-cost splitStatements:false
--comment: Cost per document and path, computed at write time from UsageEvent and the price table version it names (cost-tracking.md 3).
IF OBJECT_ID(N'dbo.DocumentCost', N'U') IS NULL
CREATE TABLE dbo.DocumentCost
(
    DocumentId        nvarchar(128)  NOT NULL CONSTRAINT PK_DocumentCost PRIMARY KEY,
    RunId             nvarchar(128)  NOT NULL,
    Path              nvarchar(32)   NOT NULL,
    LlmCalls          int            NOT NULL,
    EmbeddingCalls    int            NOT NULL,
    Pages             int            NOT NULL,
    TokensIn          bigint         NOT NULL,
    TokensOut         bigint         NOT NULL,
    CostUsd           decimal(18, 6) NOT NULL,
    BreakdownJson     nvarchar(max)  NOT NULL,
    PriceTableVersion nvarchar(64)   NULL,
    ComputedUtc       datetime2(3)   NOT NULL
);
--rollback DROP TABLE IF EXISTS dbo.DocumentCost;
