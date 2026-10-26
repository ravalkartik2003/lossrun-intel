--liquibase formatted sql

--changeset lossrun:0001-schema-change-log splitStatements:false
--comment: The POC runner's record of applied changesets (same columns as Liquibase's DATABASECHANGELOG; ADR 0006). The runner also creates it before anything else.
IF OBJECT_ID(N'dbo.SchemaChangeLog', N'U') IS NULL
CREATE TABLE dbo.SchemaChangeLog
(
    Id            nvarchar(255) NOT NULL,
    Author        nvarchar(255) NOT NULL,
    FileName      nvarchar(255) NOT NULL,
    DateExecuted  datetime2(3)  NOT NULL CONSTRAINT DF_SchemaChangeLog_DateExecuted DEFAULT sysutcdatetime(),
    OrderExecuted int           NOT NULL,
    ExecType      nvarchar(10)  NOT NULL,
    MD5Sum        nvarchar(64)  NOT NULL,
    Description   nvarchar(255) NULL,
    Comments      nvarchar(255) NULL,
    LiquibaseTag  nvarchar(255) NULL,
    CONSTRAINT PK_SchemaChangeLog PRIMARY KEY (Id, Author, FileName)
);
--rollback DROP TABLE IF EXISTS dbo.SchemaChangeLog;
