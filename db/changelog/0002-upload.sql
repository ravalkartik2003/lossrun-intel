--liquibase formatted sql

--changeset lossrun:0002-upload splitStatements:false
--comment: One row per upload: the manifest header (ADR 0005).
IF OBJECT_ID(N'dbo.Upload', N'U') IS NULL
CREATE TABLE dbo.Upload
(
    Id             nvarchar(64)   NOT NULL CONSTRAINT PK_Upload PRIMARY KEY,
    Status         nvarchar(32)   NOT NULL,
    CreatedUtc     datetime2(3)   NOT NULL,
    CompletedUtc   datetime2(3)   NULL,
    QuoteId        nvarchar(128)  NULL,
    CarrierHint    nvarchar(256)  NULL,
    UploaderId     nvarchar(256)  NOT NULL,
    UploaderRole   nvarchar(32)   NOT NULL,
    ValuationDate  date           NULL,
    SubmissionJson nvarchar(max)  NOT NULL,
    FilesJson      nvarchar(max)  NOT NULL,
    CostUsd        decimal(18, 6) NULL,
    CostJson       nvarchar(max)  NULL,
    FirstClaimMs   bigint         NULL,
    CompletedMs    bigint         NULL,
    RowVersion     rowversion     NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Upload_QuoteId' AND object_id = OBJECT_ID(N'dbo.Upload'))
    CREATE INDEX IX_Upload_QuoteId ON dbo.Upload (QuoteId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Upload_UploaderId' AND object_id = OBJECT_ID(N'dbo.Upload'))
    CREATE INDEX IX_Upload_UploaderId ON dbo.Upload (UploaderId, CreatedUtc);
--rollback DROP TABLE IF EXISTS dbo.Upload;

--changeset lossrun:0002-upload-item splitStatements:false
--comment: Manifest rows: every file and segment of an upload with classification, status, reason, page range, blob path and cost.
IF OBJECT_ID(N'dbo.UploadItem', N'U') IS NULL
CREATE TABLE dbo.UploadItem
(
    DocumentId       nvarchar(128)  NOT NULL CONSTRAINT PK_UploadItem PRIMARY KEY,
    UploadId         nvarchar(64)   NOT NULL CONSTRAINT FK_UploadItem_Upload REFERENCES dbo.Upload (Id),
    Ordinal          int            NOT NULL,
    FileName         nvarchar(512)  NOT NULL,
    ParentDocumentId nvarchar(128)  NULL,
    PageRange        nvarchar(32)   NULL,
    Classification   nvarchar(64)   NOT NULL,
    Status           nvarchar(32)   NOT NULL,
    Reason           nvarchar(2000) NULL,
    BlobPath         nvarchar(1024) NULL,
    RoutingTier      nvarchar(32)   NULL,
    Badge            nvarchar(256)  NULL,
    FormatRef        nvarchar(160)  NULL,
    FormatStatus     nvarchar(16)   NULL,
    ClaimCount       int            NULL,
    CostUsd          decimal(18, 6) NULL,
    ReviewKind       nvarchar(32)   NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_UploadItem_UploadId' AND object_id = OBJECT_ID(N'dbo.UploadItem'))
    CREATE INDEX IX_UploadItem_UploadId ON dbo.UploadItem (UploadId, Ordinal);
--rollback DROP TABLE IF EXISTS dbo.UploadItem;
