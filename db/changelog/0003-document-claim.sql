--liquibase formatted sql

--changeset lossrun:0003-document splitStatements:false
--comment: One row per extracted loss-run document. ResultJson is the read model the API serves (document, lineage, rule outcomes); ClaimCommandService rewrites it in the same transaction as the Claim rows it projects.
IF OBJECT_ID(N'dbo.Document', N'U') IS NULL
CREATE TABLE dbo.Document
(
    Id                nvarchar(128)  NOT NULL CONSTRAINT PK_Document PRIMARY KEY,
    UploadId          nvarchar(64)   NOT NULL CONSTRAINT FK_Document_Upload REFERENCES dbo.Upload (Id),
    QuoteId           nvarchar(128)  NULL,
    CarrierName       nvarchar(256)  NULL,
    InsuredName       nvarchar(256)  NULL,
    LineOfBusiness    nvarchar(64)   NULL,
    ValuationDate     date           NULL,
    ReportRunDate     date           NULL,
    ExpenseTreatment  nvarchar(32)   NOT NULL CONSTRAINT DF_Document_ExpenseTreatment DEFAULT N'Unknown',
    FormatRef         nvarchar(160)  NULL,
    RoutingTier       nvarchar(32)   NULL,
    Badge             nvarchar(256)  NULL,
    ReviewState       nvarchar(32)   NULL,
    Revision          int            NOT NULL CONSTRAINT DF_Document_Revision DEFAULT 1,
    CatalogVersion    nvarchar(64)   NULL,
    CostUsd           decimal(18, 6) NULL,
    ResultJson        nvarchar(max)  NOT NULL,
    CreatedUtc        datetime2(3)   NOT NULL,
    UpdatedUtc        datetime2(3)   NOT NULL,
    RowVersion        rowversion     NOT NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Document_UploadId' AND object_id = OBJECT_ID(N'dbo.Document'))
    CREATE INDEX IX_Document_UploadId ON dbo.Document (UploadId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Document_QuoteId' AND object_id = OBJECT_ID(N'dbo.Document'))
    CREATE INDEX IX_Document_QuoteId ON dbo.Document (QuoteId);
--rollback DROP TABLE IF EXISTS dbo.Document;

--changeset lossrun:0003-policy-period splitStatements:false
--comment: Policy periods of a document. Id is deterministic from (DocumentId, PeriodIndex), so persisting and backfill are idempotent.
IF OBJECT_ID(N'dbo.PolicyPeriod', N'U') IS NULL
CREATE TABLE dbo.PolicyPeriod
(
    Id             uniqueidentifier NOT NULL CONSTRAINT PK_PolicyPeriod PRIMARY KEY,
    DocumentId     nvarchar(128)    NOT NULL CONSTRAINT FK_PolicyPeriod_Document REFERENCES dbo.Document (Id),
    PeriodIndex    int              NOT NULL,
    PolicyNumber   nvarchar(128)    NULL,
    CarrierName    nvarchar(256)    NULL,
    EffectiveDate  date             NULL,
    ExpirationDate date             NULL,
    DetailLevel    nvarchar(16)     NOT NULL,
    SubtotalsJson  nvarchar(max)    NULL,
    CONSTRAINT UQ_PolicyPeriod_Document UNIQUE (DocumentId, PeriodIndex)
);
--rollback DROP TABLE IF EXISTS dbo.PolicyPeriod;

--changeset lossrun:0003-claim splitStatements:false
--comment: Resolved (as-adjusted) claim values, one row per claim, system-versioned to dbo.ClaimHistory: every override is a new history version (brief 09 6a). Only ClaimCommandService writes it.
IF OBJECT_ID(N'dbo.Claim', N'U') IS NULL
CREATE TABLE dbo.Claim
(
    Id                   uniqueidentifier NOT NULL CONSTRAINT PK_Claim PRIMARY KEY,
    DocumentId           nvarchar(128)    NOT NULL CONSTRAINT FK_Claim_Document REFERENCES dbo.Document (Id),
    PolicyPeriodId       uniqueidentifier NOT NULL CONSTRAINT FK_Claim_PolicyPeriod REFERENCES dbo.PolicyPeriod (Id),
    PeriodIndex          int              NOT NULL,
    ClaimIndex           int              NOT NULL,
    ClaimNumber          nvarchar(128)    NULL,
    ClaimantName         nvarchar(256)    NULL,
    DateOfInjury         date             NULL,
    DateReported         date             NULL,
    DateClosed           date             NULL,
    Status               nvarchar(16)     NULL,
    RawStatus            nvarchar(64)     NULL,
    ClaimType            nvarchar(16)     NULL,
    InjuryTypeCode       nvarchar(16)     NULL,
    NatureOfInjuryCode   nvarchar(16)     NULL,
    CauseOfInjuryCode    nvarchar(16)     NULL,
    PartOfBodyCode       nvarchar(16)     NULL,
    AccidentDescription  nvarchar(2000)   NULL,
    LitigationFlag       bit              NULL,
    CumulativeTrauma     bit              NULL,
    JurisdictionState    nvarchar(8)      NULL,
    ClassCode            nvarchar(16)     NULL,
    IndemnityPaid        decimal(18, 2)   NULL,
    IndemnityReserve     decimal(18, 2)   NULL,
    IndemnityIncurred    decimal(18, 2)   NULL,
    MedicalPaid          decimal(18, 2)   NULL,
    MedicalReserve       decimal(18, 2)   NULL,
    MedicalIncurred      decimal(18, 2)   NULL,
    ExpensePaid          decimal(18, 2)   NULL,
    ExpenseReserve       decimal(18, 2)   NULL,
    ExpenseIncurred      decimal(18, 2)   NULL,
    TotalPaid            decimal(18, 2)   NULL,
    TotalReserve         decimal(18, 2)   NULL,
    TotalIncurred        decimal(18, 2)   NULL,
    RecoverySubrogation  decimal(18, 2)   NULL,
    NetIncurred          decimal(18, 2)   NULL,
    ExpenseTreatment     nvarchar(32)     NOT NULL,
    DetailLevel          nvarchar(16)     NOT NULL,
    LastOverrideId       bigint           NULL,
    ChangedBy            nvarchar(256)    NOT NULL,
    ValidFromUtc         datetime2(3)     GENERATED ALWAYS AS ROW START NOT NULL,
    ValidToUtc           datetime2(3)     GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFromUtc, ValidToUtc),
    CONSTRAINT UQ_Claim_Position UNIQUE (DocumentId, PeriodIndex, ClaimIndex)
)
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.ClaimHistory));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Claim_ClaimNumber' AND object_id = OBJECT_ID(N'dbo.Claim'))
    CREATE INDEX IX_Claim_ClaimNumber ON dbo.Claim (ClaimNumber);
--rollback IF OBJECTPROPERTY(OBJECT_ID(N'dbo.Claim'), N'TableTemporalType') = 2 ALTER TABLE dbo.Claim SET (SYSTEM_VERSIONING = OFF);
--rollback DROP TABLE IF EXISTS dbo.Claim;
--rollback DROP TABLE IF EXISTS dbo.ClaimHistory;
