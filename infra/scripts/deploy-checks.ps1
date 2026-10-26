# Deploy gates shared by deploy-functions.ps1 and deploy-web.ps1 (brief 23 Answers 4). Dot-source after tf-output.ps1.
#   Assert-DatabaseCurrent  before a deploy: lossrun-db deploy --check; pending or drifted changesets stop the deploy.
#                           (Brief 21's changeset never reached the live database and every upload failed at parse.)
#   Invoke-SmokeUpload      after a deploy: one synthetic fixture through the live API must reach a published tier
#                           (ReplayAccepted, Learned, Delta), or the deploy is reported failed.
# Keyless for Azure (az login, Entra token to SQL). The API smoke user's password is read from Key Vault into memory, passed
# to curl.exe in a config file deleted at once, and never printed.

function Assert-DatabaseCurrent([Parameter(Mandatory)][string]$FunctionApp) {
    $root = Resolve-Path (Join-Path $PSScriptRoot "../..")
    $server = Get-TfOutput sql_server_fqdn
    $database = Get-TfOutput sql_database_name
    $env:LOSSRUN_SQL_CONNECTION = "Server=tcp:$server,1433;Database=$database;Authentication=Active Directory Default;Encrypt=True;"
    $env:MSBUILDDISABLENODEREUSE = "1"
    dotnet run --project (Join-Path $root "src/LossRun.DbDeploy/LossRun.DbDeploy.csproj") -c Release -- deploy --check --param "appPrincipalName=$FunctionApp"
    $code = $LASTEXITCODE
    Remove-Item Env:\LOSSRUN_SQL_CONNECTION -ErrorAction SilentlyContinue
    if ($code -eq 3) { throw "database has pending changesets: run 'lossrun-db deploy' first (runbook section Deploy); nothing was deployed" }
    if ($code -ne 0) { throw "database check failed (exit $($code); drifted changesets or no connection); nothing was deployed" }
}

function Invoke-SmokeUpload([Parameter(Mandatory)][string]$ResourceGroup, [Parameter(Mandatory)][string]$FunctionApp,
    [string]$Fixture = "src/TestShared/fixtures/lossrun-export.csv", [string]$QuoteId = "Q-SMOKE", [int]$TimeoutSeconds = 240) {
    $root = Resolve-Path (Join-Path $PSScriptRoot "../..")
    $file = Join-Path $root $Fixture
    # The Terraform output, not az functionapp show: a Flex Consumption app reports no defaultHostName there.
    $api = "https://" + (Get-TfOutput function_app_hostname) + "/api/v1"
    $vault = az keyvault list -g $ResourceGroup --query "[0].name" -o tsv
    $users = az keyvault secret show --vault-name $vault -n devauth-users --query value -o tsv
    $entry = ($users -split ";") | Where-Object { $_ -like "underwriter:*" } | Select-Object -First 1
    $users = $null
    if (-not $entry) { throw "smoke upload: no underwriter in devauth-users" }
    $password = ($entry -split ":")[1]
    $entry = $null
    $config = [System.IO.Path]::GetTempFileName()
    try {
        # curl reads the credentials from a config file, not the command line (process listings show arguments).
        [System.IO.File]::WriteAllText($config, "user = `"underwriter:$password`"`n", (New-Object System.Text.UTF8Encoding $false))
        $password = $null
        $key = "smoke-" + [DateTime]::UtcNow.ToString("yyyyMMddHHmmss") + "-" + [guid]::NewGuid().ToString("N").Substring(0, 6)
        $accepted = curl.exe -s -K $config -H "Idempotency-Key: $key" -F "file=@$file" -F "quoteId=$QuoteId" -F "carrierHint=Synthetic Mutual Insurance Co." "$api/uploads" | ConvertFrom-Json
        if (-not $accepted.uploadId) { throw "smoke upload: the API did not accept the upload" }
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        do {
            Start-Sleep -Seconds 5
            $status = curl.exe -s -K $config "$api/uploads/$($accepted.uploadId)/status" | ConvertFrom-Json
        } while ($status.runtimeStatus -notin @("Completed", "Failed", "Terminated") -and [DateTime]::UtcNow -lt $deadline)
        $manifest = curl.exe -s -K $config "$api/uploads/$($accepted.uploadId)" | ConvertFrom-Json
    }
    finally {
        Remove-Item -Force $config -ErrorAction SilentlyContinue
    }

    $item = $manifest.items | Where-Object { $_.classification -eq "LossRun" } | Select-Object -First 1
    $published = $status.runtimeStatus -eq "Completed" -and $item -and $item.status -eq "processed" -and $item.routingTier -in @("ReplayAccepted", "Learned", "Delta")
    "smoke upload $($accepted.uploadId): $($status.runtimeStatus), $($item.status) $($item.routingTier), $($item.claimCount) claims"
    if (-not $published) { throw "DEPLOY FAILED: the smoke upload $($accepted.uploadId) did not reach a published tier (runbook section Deploy)" }
}
