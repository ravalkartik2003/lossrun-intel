--liquibase formatted sql

--changeset lossrun:0018-glossary-alias splitStatements:false
--comment: ADR 0019 learned glossary aliases: one row per learned header and canonical field, with its lifecycle (candidate, active, retired), the learn that introduced it, and who activated or retired it and why. Id is the alias key at creation (normalized header|field) and stays when a reviewer edits the field. System-versioned: every status change is history. glossary.json stays the hand-curated base.
IF OBJECT_ID(N'dbo.GlossaryAlias', N'U') IS NULL
CREATE TABLE dbo.GlossaryAlias
(
    Id                  nvarchar(400)  NOT NULL CONSTRAINT PK_GlossaryAlias PRIMARY KEY,
    Header              nvarchar(256)  NOT NULL,
    CanonicalField      nvarchar(128)  NOT NULL,
    Status              nvarchar(16)   NOT NULL,
    CarrierName         nvarchar(256)  NOT NULL,
    FormatId            nvarchar(128)  NOT NULL,
    FormatVersion       nvarchar(16)   NOT NULL,
    FirstSeenDocumentId nvarchar(128)  NOT NULL,
    FirstSeenUtc        datetime2(3)   NOT NULL,
    LearnedModel        nvarchar(128)  NOT NULL,
    LearnedModelVersion nvarchar(64)   NULL,
    PromptVersion       nvarchar(32)   NOT NULL,
    Confidence          float          NOT NULL,
    TotalsTie           bit            NULL,
    VerificationGate    nvarchar(32)   NULL,
    ReviewedBy          nvarchar(256)  NULL,
    ActivatedBy         nvarchar(256)  NULL,
    ActivatedUtc        datetime2(3)   NULL,
    ActivationReason    nvarchar(256)  NULL,
    RetiredBy           nvarchar(256)  NULL,
    RetiredUtc          datetime2(3)   NULL,
    RetiredReason       nvarchar(1000) NULL,
    RetiredAuditEventId nvarchar(64)   NULL,
    EditedFrom          nvarchar(128)  NULL,
    ValidFromUtc        datetime2(3)   GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc          datetime2(3)   GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc),
    CONSTRAINT CK_GlossaryAlias_Status CHECK (Status IN (N'candidate', N'active', N'retired'))
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.GlossaryAliasHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_GlossaryAlias_FormatId' AND object_id = OBJECT_ID(N'dbo.GlossaryAlias'))
    CREATE INDEX IX_GlossaryAlias_FormatId ON dbo.GlossaryAlias (FormatId);
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.GlossaryAlias'), N'TableTemporalType') = 2 ALTER TABLE dbo.GlossaryAlias SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.GlossaryAlias;
--rollback DROP TABLE IF EXISTS dbo.GlossaryAliasHistory;

--changeset lossrun:0018-glossary-alias-evidence splitStatements:false
--comment: ADR 0019 verification evidence: one row per document that verified with a spec mapping the alias (the introducing learn, then accepted replays). Activation without a reviewer needs 3 documents across 2 formats (configuration). Append-only ledger: UPDATE and DELETE fail in the engine; the natural key needs no server-generated value.
IF OBJECT_ID(N'dbo.GlossaryAliasEvidence', N'U') IS NULL
CREATE TABLE dbo.GlossaryAliasEvidence
(
    AliasId    nvarchar(400) NOT NULL,
    DocumentId nvarchar(128) NOT NULL,
    FormatRef  nvarchar(160) NOT NULL,
    Route      nvarchar(16)  NOT NULL,
    TotalsTie  bit           NULL,
    AtUtc      datetime2(3)  NOT NULL,
    CONSTRAINT PK_GlossaryAliasEvidence PRIMARY KEY NONCLUSTERED (AliasId, DocumentId)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
--rollback DROP TABLE IF EXISTS dbo.GlossaryAliasEvidence;
