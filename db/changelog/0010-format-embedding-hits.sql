--liquibase formatted sql

--changeset lossrun:0010-format-embedding-hits splitStatements:false
--comment: Running cosine statistics of accepted documents per format (Welford count/mean/M2), for the adaptive embedding threshold max(0.90, mean - 2 sigma) after 5 hits (08 Answers 2, brief 08b). Kept beside the format, never in the immutable spec.
IF COL_LENGTH(N'dbo.Format', N'EmbeddingHits') IS NULL
    ALTER TABLE dbo.Format ADD EmbeddingHits int NULL, EmbeddingMean float NULL, EmbeddingM2 float NULL;
--rollback IF COL_LENGTH(N'dbo.Format', N'EmbeddingHits') IS NOT NULL ALTER TABLE dbo.Format DROP COLUMN EmbeddingHits, EmbeddingMean, EmbeddingM2;
