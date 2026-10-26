--liquibase formatted sql

--changeset lossrun:0013-app-ledger-view runOnChange:true splitStatements:false
--comment: Brief 11: the API reads dbo.vAuditTrail (the audit trail with the ledger transaction that committed each row), which joins sys.database_ledger_transactions; that catalog view needs VIEW LEDGER CONTENT. Granted to the Function App's managed identity only. Azure SQL only; a no-op when appPrincipalName is empty.
DECLARE @principal sysname = N'${appPrincipalName}';
IF @principal <> N'' AND CAST(SERVERPROPERTY('EngineEdition') AS int) = 5 AND DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL
BEGIN
    DECLARE @sql nvarchar(max) = N'GRANT VIEW LEDGER CONTENT TO ' + QUOTENAME(@principal) + N';';
    EXEC sys.sp_executesql @sql;
END
--rollback DECLARE @principal sysname = N'${appPrincipalName}'; DECLARE @sql nvarchar(max) = N'REVOKE VIEW LEDGER CONTENT FROM ' + QUOTENAME(@principal) + N';'; IF @principal <> N'' AND DATABASE_PRINCIPAL_ID(@principal) IS NOT NULL EXEC sys.sp_executesql @sql;
