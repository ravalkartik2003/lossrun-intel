--liquibase formatted sql

--changeset lossrun:0004-extracted-value-seq splitStatements:false
--comment: Event-id sequences increment by 100: EF Core allocates ids client-side in hi/lo blocks of 100, because append-only ledger tables reject the MERGE EF uses to read back server-generated keys.
IF OBJECT_ID(N'dbo.ExtractedValueSeq', N'SO') IS NULL
    CREATE SEQUENCE dbo.ExtractedValueSeq AS bigint START WITH 1 INCREMENT BY 100;
--rollback DROP SEQUENCE IF EXISTS dbo.ExtractedValueSeq;

--changeset lossrun:0004-extracted-value splitStatements:false
--comment: The immutable as-reported values: one row per extracted field per document revision, with lineage (page + polygon, or sheet + A1), extractor, format and model versions and confidence. Append-only ledger: UPDATE and DELETE fail in the engine. Dropping a ledger table only renames it (sys.ledger_table_history), so the rollback keeps the evidence.
IF OBJECT_ID(N'dbo.ExtractedValue', N'U') IS NULL
CREATE TABLE dbo.ExtractedValue
(
    Id               bigint           NOT NULL CONSTRAINT DF_ExtractedValue_Id DEFAULT (NEXT VALUE FOR dbo.ExtractedValueSeq),
    DocumentId       nvarchar(128)    NOT NULL,
    DocumentRevision int              NOT NULL,
    ClaimId          uniqueidentifier NULL,
    FieldPath        nvarchar(256)    NOT NULL,
    ClaimField       nvarchar(128)    NULL,
    Value            nvarchar(4000)   NULL,
    RawText          nvarchar(4000)   NULL,
    Confidence       float            NULL,
    Extractor        nvarchar(32)     NULL,
    FormatRef        nvarchar(160)    NULL,
    Page             int              NULL,
    PolygonJson      nvarchar(400)    NULL,
    Sheet            nvarchar(128)    NULL,
    CellA1           nvarchar(16)     NULL,
    Model            nvarchar(128)    NULL,
    PromptVersion    nvarchar(32)     NULL,
    RecordedUtc      datetime2(3)     NOT NULL,
    CONSTRAINT PK_ExtractedValue PRIMARY KEY NONCLUSTERED (Id)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExtractedValue_DocumentId' AND object_id = OBJECT_ID(N'dbo.ExtractedValue'))
    CREATE CLUSTERED INDEX IX_ExtractedValue_DocumentId ON dbo.ExtractedValue (DocumentId, DocumentRevision);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExtractedValue_FieldPath' AND object_id = OBJECT_ID(N'dbo.ExtractedValue'))
    CREATE INDEX IX_ExtractedValue_FieldPath ON dbo.ExtractedValue (FieldPath);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ExtractedValue_ClaimId' AND object_id = OBJECT_ID(N'dbo.ExtractedValue'))
    CREATE INDEX IX_ExtractedValue_ClaimId ON dbo.ExtractedValue (ClaimId);
--rollback DROP TABLE IF EXISTS dbo.ExtractedValue;
