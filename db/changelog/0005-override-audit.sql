--liquibase formatted sql

--changeset lossrun:0005-event-seq splitStatements:false
IF OBJECT_ID(N'dbo.OverrideSeq', N'SO') IS NULL
    CREATE SEQUENCE dbo.OverrideSeq AS bigint START WITH 1 INCREMENT BY 100;
IF OBJECT_ID(N'dbo.AuditEventSeq', N'SO') IS NULL
    CREATE SEQUENCE dbo.AuditEventSeq AS bigint START WITH 1 INCREMENT BY 100;
--rollback DROP SEQUENCE IF EXISTS dbo.OverrideSeq;
--rollback DROP SEQUENCE IF EXISTS dbo.AuditEventSeq;

--changeset lossrun:0005-override splitStatements:false
--comment: Underwriter overrides (append-only ledger; underwriter-analytics.md KQ4). Never updated: a reversal is a new row that supersedes the earlier one. Rationale is required.
IF OBJECT_ID(N'dbo.Override', N'U') IS NULL
CREATE TABLE dbo.Override
(
    Id                   bigint           NOT NULL CONSTRAINT DF_Override_Id DEFAULT (NEXT VALUE FOR dbo.OverrideSeq),
    DocumentId           nvarchar(128)    NOT NULL,
    DocumentRevision     int              NOT NULL,
    ClaimId              uniqueidentifier NULL,
    FieldPath            nvarchar(256)    NOT NULL,
    ClaimField           nvarchar(128)    NULL,
    OldValue             nvarchar(4000)   NULL,
    NewValue             nvarchar(4000)   NULL,
    Rationale            nvarchar(2000)   NOT NULL CONSTRAINT CK_Override_Rationale CHECK (len(Rationale) > 0),
    UserId               nvarchar(256)    NOT NULL,
    Role                 nvarchar(32)     NOT NULL,
    AtUtc                datetime2(3)     NOT NULL,
    CatalogVersion       nvarchar(64)     NULL,
    FormatRef            nvarchar(160)    NULL,
    Source               nvarchar(16)     NOT NULL,
    CorrelationId        nvarchar(64)     NULL,
    SupersedesOverrideId bigint           NULL,
    CONSTRAINT PK_Override PRIMARY KEY (Id)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Override_DocumentId' AND object_id = OBJECT_ID(N'dbo.Override'))
    CREATE INDEX IX_Override_DocumentId ON dbo.Override (DocumentId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Override_ClaimId' AND object_id = OBJECT_ID(N'dbo.Override'))
    CREATE INDEX IX_Override_ClaimId ON dbo.Override (ClaimId, FieldPath);
--rollback DROP TABLE IF EXISTS dbo.Override;

--changeset lossrun:0005-audit-event splitStatements:false
--comment: Every mutation (upload, extraction persist, override, re-derive, review decision, format approve/retire, export, quote push) as one append-only ledger row, with the role the user acted under (underwriter-analytics.md KQ5).
IF OBJECT_ID(N'dbo.AuditEvent', N'U') IS NULL
CREATE TABLE dbo.AuditEvent
(
    Id               bigint           NOT NULL CONSTRAINT DF_AuditEvent_Id DEFAULT (NEXT VALUE FOR dbo.AuditEventSeq),
    AtUtc            datetime2(3)     NOT NULL,
    UserId           nvarchar(256)    NOT NULL,
    Role             nvarchar(32)     NOT NULL,
    Action           nvarchar(64)     NOT NULL,
    UploadId         nvarchar(64)     NULL,
    DocumentId       nvarchar(128)    NULL,
    DocumentRevision int              NULL,
    ClaimId          uniqueidentifier NULL,
    FieldPath        nvarchar(256)    NULL,
    OldValue         nvarchar(4000)   NULL,
    NewValue         nvarchar(4000)   NULL,
    Rationale        nvarchar(2000)   NULL,
    OverrideId       bigint           NULL,
    CatalogVersion   nvarchar(64)     NULL,
    FormatRef        nvarchar(160)    NULL,
    Source           nvarchar(16)     NOT NULL,
    CorrelationId    nvarchar(64)     NULL,
    CONSTRAINT PK_AuditEvent PRIMARY KEY (Id)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEvent_DocumentId' AND object_id = OBJECT_ID(N'dbo.AuditEvent'))
    CREATE INDEX IX_AuditEvent_DocumentId ON dbo.AuditEvent (DocumentId, AtUtc);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEvent_UploadId' AND object_id = OBJECT_ID(N'dbo.AuditEvent'))
    CREATE INDEX IX_AuditEvent_UploadId ON dbo.AuditEvent (UploadId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AuditEvent_FieldPath' AND object_id = OBJECT_ID(N'dbo.AuditEvent'))
    CREATE INDEX IX_AuditEvent_FieldPath ON dbo.AuditEvent (FieldPath);
--rollback DROP TABLE IF EXISTS dbo.AuditEvent;

--changeset lossrun:0005-rule-outcome splitStatements:false
--comment: Rule outcomes per document revision (R-01..R-24 from docs/evaluation/rule-catalog.json); a new revision adds rows, earlier revisions stay.
IF OBJECT_ID(N'dbo.RuleOutcome', N'U') IS NULL
CREATE TABLE dbo.RuleOutcome
(
    DocumentId         nvarchar(128)  NOT NULL CONSTRAINT FK_RuleOutcome_Document REFERENCES dbo.Document (Id),
    DocumentRevision   int            NOT NULL,
    Ordinal            int            NOT NULL,
    RuleId             nvarchar(16)   NOT NULL,
    Severity           nvarchar(8)    NOT NULL,
    Status             nvarchar(16)   NOT NULL,
    Message            nvarchar(2000) NOT NULL,
    AffectedFieldsJson nvarchar(max)  NULL,
    ClaimRef           nvarchar(128)  NULL,
    PeriodIndex        int            NULL,
    Detail             nvarchar(2000) NULL,
    CONSTRAINT PK_RuleOutcome PRIMARY KEY (DocumentId, DocumentRevision, Ordinal)
);
--rollback DROP TABLE IF EXISTS dbo.RuleOutcome;

--changeset lossrun:0005-review-item splitStatements:false
--comment: The review queue (brief 07 ReviewItem): pending, decided or expired, with the decision JSON.
IF OBJECT_ID(N'dbo.ReviewItem', N'U') IS NULL
CREATE TABLE dbo.ReviewItem
(
    DocumentId   nvarchar(128)  NOT NULL CONSTRAINT PK_ReviewItem PRIMARY KEY,
    UploadId     nvarchar(64)   NOT NULL,
    Kind         nvarchar(32)   NOT NULL,
    State        nvarchar(16)   NOT NULL,
    ReasonsJson  nvarchar(max)  NOT NULL,
    Badge        nvarchar(256)  NULL,
    FormatRef    nvarchar(160)  NULL,
    OpenedUtc    datetime2(3)   NOT NULL,
    DecidedUtc   datetime2(3)   NULL,
    DecisionJson nvarchar(max)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ReviewItem_State' AND object_id = OBJECT_ID(N'dbo.ReviewItem'))
    CREATE INDEX IX_ReviewItem_State ON dbo.ReviewItem (State, OpenedUtc);
--rollback DROP TABLE IF EXISTS dbo.ReviewItem;
