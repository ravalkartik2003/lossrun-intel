--liquibase formatted sql

--changeset lossrun:0023-upload-item-unnormalized splitStatements:false
--comment: Brief 30 item 4: a manifest item's count of printed values the replay could not normalize (each sends the document to Review, with the cell in the reasons). UploadItem is an ordinary table, so the column is added in place.
IF COL_LENGTH(N'dbo.UploadItem', N'UnnormalizedValues') IS NULL
    ALTER TABLE dbo.UploadItem ADD UnnormalizedValues int NULL;
--rollback IF COL_LENGTH(N'dbo.UploadItem', N'UnnormalizedValues') IS NOT NULL ALTER TABLE dbo.UploadItem DROP COLUMN UnnormalizedValues;
