--liquibase formatted sql

--changeset lossrun:0014-workspace splitStatements:false
--comment: Brief 12: ad-hoc chat workspaces. Uploads carry the workspace id in Upload.SubmissionJson ($.workspaceId) and belong to no quote; the chat index drops a workspace's chunks when it expires or is deleted. A plain table: its changes are audited (workspace.create / workspace.delete) in the AuditEvent ledger.
IF OBJECT_ID(N'dbo.Workspace', N'U') IS NULL
CREATE TABLE dbo.Workspace
(
    Id           nvarchar(64)  NOT NULL CONSTRAINT PK_Workspace PRIMARY KEY,
    OwnerUserId  nvarchar(256) NOT NULL,
    Name         nvarchar(200) NULL,
    CreatedUtc   datetime2(3)  NOT NULL,
    ExpiresUtc   datetime2(3)  NOT NULL,
    DeletedUtc   datetime2(3)  NULL
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Workspace_Expires' AND object_id = OBJECT_ID(N'dbo.Workspace'))
    CREATE INDEX IX_Workspace_Expires ON dbo.Workspace (ExpiresUtc) WHERE DeletedUtc IS NULL;
--rollback DROP TABLE IF EXISTS dbo.Workspace;

--changeset lossrun:0014-chat-turn splitStatements:false
--comment: Brief 12 section 4: every chat turn — who asked what in which scope, the answer, its citations, the tool trace (as executed), tokens and cost. Append-only ledger table: a turn is never edited, so the record of what the assistant told an underwriter is tamper-evident like the AuditEvent ledger. JSON columns hold the citation and trace lists (shapes: docs/api/openapi.yaml ChatTurnResult).
IF OBJECT_ID(N'dbo.ChatTurn', N'U') IS NULL
CREATE TABLE dbo.ChatTurn
(
    Id                nvarchar(64)   NOT NULL,
    ConversationId    nvarchar(64)   NOT NULL,
    AtUtc             datetime2(3)   NOT NULL,
    UserId            nvarchar(256)  NOT NULL,
    Role              nvarchar(32)   NOT NULL,
    Scope             nvarchar(16)   NOT NULL,
    QuoteId           nvarchar(128)  NULL,
    WorkspaceId       nvarchar(64)   NULL,
    Question          nvarchar(4000) NOT NULL,
    Answer            nvarchar(max)  NOT NULL,
    NotFound          bit            NOT NULL,
    CitationsJson     nvarchar(max)  NOT NULL,
    ToolTraceJson     nvarchar(max)  NOT NULL,
    InputTokens       bigint         NOT NULL,
    CachedInputTokens bigint         NOT NULL,
    OutputTokens      bigint         NOT NULL,
    ModelCalls        int            NOT NULL,
    Embeddings        int            NOT NULL,
    SearchQueries     int            NOT NULL,
    CostUsd           decimal(18, 6) NOT NULL,
    FirstTokenMs      bigint         NOT NULL,
    TotalMs           bigint         NOT NULL,
    UsageJson         nvarchar(max)  NOT NULL,
    CONSTRAINT PK_ChatTurn PRIMARY KEY (Id)
)
WITH (LEDGER = ON (APPEND_ONLY = ON));
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ChatTurn_Conversation' AND object_id = OBJECT_ID(N'dbo.ChatTurn'))
    CREATE INDEX IX_ChatTurn_Conversation ON dbo.ChatTurn (ConversationId, AtUtc);
--rollback -- dbo.ChatTurn is an append-only ledger table: it is not dropped by a rollback (ADR 0010).
