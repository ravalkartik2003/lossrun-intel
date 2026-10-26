--liquibase formatted sql

--changeset lossrun:0006-format splitStatements:false
--comment: Carrier formats (format memory). Specs stay immutable per version; lifecycle (shadow, active, retired) lives on FormatVersion, as it did in the Blob stats sidecar. Blob stays a cache.
IF OBJECT_ID(N'dbo.Format', N'U') IS NULL
CREATE TABLE dbo.Format
(
    Id          nvarchar(128) NOT NULL CONSTRAINT PK_Format PRIMARY KEY,
    CarrierName nvarchar(256) NOT NULL,
    FileKind    nvarchar(16)  NOT NULL,
    CreatedUtc  datetime2(3)  NOT NULL
);
--rollback DROP TABLE IF EXISTS dbo.Format;

--changeset lossrun:0006-format-version splitStatements:false
IF OBJECT_ID(N'dbo.FormatVersion', N'U') IS NULL
CREATE TABLE dbo.FormatVersion
(
    FormatId         nvarchar(128) NOT NULL CONSTRAINT FK_FormatVersion_Format REFERENCES dbo.Format (Id),
    Version          int           NOT NULL,
    SpecJson         nvarchar(max) NOT NULL,
    CreatedBy        nvarchar(128) NOT NULL,
    CreatedUtc       datetime2(3)  NOT NULL,
    Supersedes       nvarchar(160) NULL,
    VerificationGate nvarchar(32)  NULL,
    IdentityKey      nvarchar(128) NULL,
    HeaderKey        nvarchar(128) NULL,
    Status           nvarchar(16)  NOT NULL,
    CleanReplays     int           NOT NULL CONSTRAINT DF_FormatVersion_CleanReplays DEFAULT 0,
    ActivatedBy      nvarchar(256) NULL,
    ActivatedUtc     datetime2(3)  NULL,
    RetiredBy        nvarchar(256) NULL,
    RetiredUtc       datetime2(3)  NULL,
    RetiredReason    nvarchar(1000) NULL,
    CONSTRAINT PK_FormatVersion PRIMARY KEY (FormatId, Version)
);
--rollback DROP TABLE IF EXISTS dbo.FormatVersion;

--changeset lossrun:0006-format-field-stats splitStatements:false
--comment: Per-field success counters of a format (replaces stats.json as the source).
IF OBJECT_ID(N'dbo.FormatFieldStats', N'U') IS NULL
CREATE TABLE dbo.FormatFieldStats
(
    FormatId          nvarchar(128) NOT NULL CONSTRAINT FK_FormatFieldStats_Format REFERENCES dbo.Format (Id),
    FieldPath         nvarchar(256) NOT NULL,
    SuccessCount      int           NOT NULL,
    LastSeenUtc       datetime2(3)  NULL,
    LastFormatVersion nvarchar(160) NULL,
    CONSTRAINT PK_FormatFieldStats PRIMARY KEY (FormatId, FieldPath)
);
--rollback DROP TABLE IF EXISTS dbo.FormatFieldStats;
