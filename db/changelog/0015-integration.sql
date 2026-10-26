--liquibase formatted sql

--changeset lossrun:0015-outbox splitStatements:false
--comment: Brief 12b: transactional outbox. A command to the quote system (flags, annotation, follow-up, memo link, quote payload) or a notification (upload.completed, review.decided, format.learned) is written here in the same transaction as its AuditEvent; a relay publishes it (commands to Service Bus with MessageId = Id and SessionId = QuoteId, notifications to Event Grid as CloudEvents with id = Id) and marks it published. Id is also the Idempotency-Key the dispatcher sends, so a re-publish after a crash is dropped by duplicate detection and by the receiver. A plain table: status columns change; what was asked is in the AuditEvent ledger.
IF OBJECT_ID(N'dbo.Outbox', N'U') IS NULL
CREATE TABLE dbo.Outbox
(
    Id               nvarchar(64)   NOT NULL CONSTRAINT PK_Outbox PRIMARY KEY,
    Channel          nvarchar(16)   NOT NULL,
    Kind             nvarchar(64)   NOT NULL,
    QuoteId          nvarchar(128)  NULL,
    Subject          nvarchar(256)  NULL,
    PayloadJson      nvarchar(max)  NOT NULL,
    CreatedUtc       datetime2(3)   NOT NULL,
    CreatedBy        nvarchar(256)  NOT NULL,
    PublishedUtc     datetime2(3)   NULL,
    PublishAttempts  int            NOT NULL CONSTRAINT DF_Outbox_PublishAttempts DEFAULT 0,
    LastError        nvarchar(2000) NULL,
    DeliveredUtc     datetime2(3)   NULL,
    DeliveryStatus   nvarchar(32)   NULL,
    DeliveryJson     nvarchar(max)  NULL,
    CONSTRAINT CK_Outbox_Channel CHECK (Channel IN (N'command', N'event'))
);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Outbox_Pending' AND object_id = OBJECT_ID(N'dbo.Outbox'))
    CREATE INDEX IX_Outbox_Pending ON dbo.Outbox (CreatedUtc) WHERE PublishedUtc IS NULL;
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Outbox_Quote' AND object_id = OBJECT_ID(N'dbo.Outbox'))
    CREATE INDEX IX_Outbox_Quote ON dbo.Outbox (QuoteId, CreatedUtc);
--rollback DROP TABLE IF EXISTS dbo.Outbox;

--changeset lossrun:0015-review-reopened splitStatements:false
--comment: 11 Answers 2: a processed document sent back to review. Its review item is pending again with Reopened = 1; the decision applies outside the upload orchestration (which has finished).
IF COL_LENGTH(N'dbo.ReviewItem', N'Reopened') IS NULL
    ALTER TABLE dbo.ReviewItem ADD Reopened bit NOT NULL CONSTRAINT DF_ReviewItem_Reopened DEFAULT 0;
--rollback ALTER TABLE dbo.ReviewItem DROP CONSTRAINT IF EXISTS DF_ReviewItem_Reopened; ALTER TABLE dbo.ReviewItem DROP COLUMN IF EXISTS Reopened;
