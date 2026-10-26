--liquibase formatted sql

--changeset lossrun:0019-upload-item-kept-rows splitStatements:false
--comment: Brief 23 §2 (ADR 0020 amendment): a manifest item's row-level list of rows of another line of business, kept, not extracted (claim number, policy, reason, page, total incurred) as JSON. UploadItem is an ordinary table, so the column is added in place.
IF COL_LENGTH(N'dbo.UploadItem', N'KeptRowsJson') IS NULL
    ALTER TABLE dbo.UploadItem ADD KeptRowsJson nvarchar(max) NULL;
--rollback IF COL_LENGTH(N'dbo.UploadItem', N'KeptRowsJson') IS NOT NULL ALTER TABLE dbo.UploadItem DROP COLUMN KeptRowsJson;
