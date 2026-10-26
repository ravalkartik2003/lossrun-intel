# Blanks the AzureWebJobsStorage app setting that azurerm writes as a key-based
# connection string, so the Functions host uses AzureWebJobsStorage__accountName
# (managed identity). Called by terraform_data.blank_webjobs_storage.
# Inputs (env): FUNC_RG, FUNC_NAME.
$ErrorActionPreference = 'Stop'

az functionapp config appsettings set -g $env:FUNC_RG -n $env:FUNC_NAME --settings "AzureWebJobsStorage=" --output none
if ($LASTEXITCODE -ne 0) { throw "Failed to blank AzureWebJobsStorage on $($env:FUNC_NAME)" }

$len = az functionapp config appsettings list -g $env:FUNC_RG -n $env:FUNC_NAME --query "[?name=='AzureWebJobsStorage'].value | [0] | length(@)" -o tsv
Write-Host "AzureWebJobsStorage on $($env:FUNC_NAME) now has length $len"
