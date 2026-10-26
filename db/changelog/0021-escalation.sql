--liquibase formatted sql

--changeset lossrun:0021-escalation splitStatements:false
--comment: Brief 26 (ADR 0025): the engineering escalation queue. One row per (ReasonCode, FormatFingerprint): learn exhausted, replay failure on an active format, unhandled exception, too many segments, cost over the limit, and the hourly patterns (repeated review of one fingerprint, one rule on most of a carrier's documents, review-rate spike, repeated reopen), plus reviewers' manual flags. TraceSummaryJson holds identifiers and counts only (rule ids, scrubbed verifier reasons, counts), never document values. Version is the optimistic-concurrency token. System-versioned: the history is every count and status change; each change also writes escalation.raised / escalation.updated / escalation.status to the AuditEvent ledger (FieldPath = escalations/{id}) and a CloudEvent through the outbox.
IF OBJECT_ID(N'dbo.Escalation', N'U') IS NULL
CREATE TABLE dbo.Escalation
(
    Id                nvarchar(64)   NOT NULL CONSTRAINT PK_Escalation PRIMARY KEY,
    ReasonCode        nvarchar(48)   NOT NULL,
    FormatFingerprint nvarchar(128)  NOT NULL,
    FormatRef         nvarchar(160)  NULL,
    Carrier           nvarchar(256)  NULL,
    DocumentIdsJson   nvarchar(max)  NOT NULL,
    UploadIdsJson     nvarchar(max)  NOT NULL,
    FirstSeenUtc      datetime2(3)   NOT NULL,
    LastSeenUtc       datetime2(3)   NOT NULL,
    Count             int            NOT NULL,
    TraceSummaryJson  nvarchar(max)  NOT NULL,
    Status            nvarchar(16)   NOT NULL,
    ResolutionNote    nvarchar(2000) NULL,
    TuningItemRef     nvarchar(64)   NULL,
    Version           int            NOT NULL,
    UpdatedUtc        datetime2(3)   NOT NULL,
    UpdatedBy         nvarchar(256)  NOT NULL,
    ValidFromUtc      datetime2(3)   GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc        datetime2(3)   GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc),
    CONSTRAINT UQ_Escalation_Key UNIQUE (ReasonCode, FormatFingerprint),
    CONSTRAINT CK_Escalation_Status CHECK (Status IN (N'new', N'investigating', N'resolved', N'wont-fix'))
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.EscalationHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Escalation_Status' AND object_id = OBJECT_ID(N'dbo.Escalation'))
    CREATE INDEX IX_Escalation_Status ON dbo.Escalation (Status, LastSeenUtc);
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.Escalation'), N'TableTemporalType') = 2 ALTER TABLE dbo.Escalation SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.Escalation;
--rollback DROP TABLE IF EXISTS dbo.EscalationHistory;
