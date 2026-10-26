# Builds a Functions project and zip-deploys it to a Flex Consumption Function App (brief 07 §4; 12b: also the mock quote system).
# Keyless: uses the signed-in Azure CLI identity (az login). App settings come from Terraform (infra/orchestration.tf, infra/api.tf).
# Usage (repo root): pwsh infra/scripts/deploy-functions.ps1 [-ResourceGroup <rg>] [-App <function app>] [-Project src/LossRun.Functions/LossRun.Functions.csproj]
#   -ResourceGroup and -App default to the Terraform outputs resource_group and function_app_name (infra/scripts/tf-output.ps1);
#   the mock quote system: -App (terraform output -raw quote_mock_app_name) -Project src/LossRun.QuoteSystemMock/LossRun.QuoteSystemMock.csproj
# One deploy per app at a time (lock file); each run builds in its own temp folder; any failure stops the script (12 Answers).
# Brief 23 Answers 4: refuses to deploy while database changesets are pending (lossrun-db deploy --check), and after the deploy
# a smoke upload of a synthetic fixture must reach a published tier, or the deploy is reported failed (deploy-checks.ps1).
param(
    [string]$ResourceGroup,
    [string]$App,
    [string]$Project = "src/LossRun.Functions/LossRun.Functions.csproj",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "tf-output.ps1")
. (Join-Path $PSScriptRoot "deploy-checks.ps1")
if (-not $ResourceGroup) { $ResourceGroup = Get-TfOutput resource_group }
if (-not $App) { $App = Get-TfOutput function_app_name }
$apiApp = Get-TfOutput function_app_name
Assert-DatabaseCurrent -FunctionApp $apiApp
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$lockPath = Join-Path ([System.IO.Path]::GetTempPath()) "lossrun-deploy-$App.lock"
try {
    $lock = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
catch {
    throw "Another deploy of $App is running (lock $lockPath). Wait for it to finish; nothing was deployed."
}

$run = Join-Path ([System.IO.Path]::GetTempPath()) ("lossrun-deploy-" + [guid]::NewGuid().ToString("N").Substring(0, 12))
$out = Join-Path $run "publish"
$zip = Join-Path $run "functions.zip"
New-Item -ItemType Directory -Force $run | Out-Null
try {

# No MSBuild node reuse: an idle node from another build can hold the output DLLs locked.
$env:MSBUILDDISABLENODEREUSE = "1"
dotnet publish (Join-Path $root $Project) -c $Configuration -o $out --nologo -v quiet -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE); nothing was deployed" }

# Flex Consumption runs on Linux: drop the Windows/macOS natives to keep the package small.
Get-ChildItem (Join-Path $out "runtimes") -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike "linux-x64*" -and $_.Name -ne "linux" -and $_.Name -ne "unix" } |
    Remove-Item -Recurse -Force

# Not Compress-Archive (skips the hidden .azurefunctions folder the Flex Consumption deploy requires at the root) and not
# ZipFile.CreateFromDirectory on Windows PowerShell (writes '\' separators, which Linux does not treat as folders).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $prefix = (Resolve-Path $out).Path.TrimEnd('\', '/').Length + 1
    Get-ChildItem -Path $out -Recurse -File -Force | ForEach-Object {
        $entry = $_.FullName.Substring($prefix).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}
"package: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB)

az functionapp deployment source config-zip --resource-group $ResourceGroup --name $App --src $zip --only-show-errors | Out-Null
if ($LASTEXITCODE -ne 0) { throw "zip deploy failed" }

az functionapp function list --resource-group $ResourceGroup --name $App --query "[].name" -o tsv
if ($LASTEXITCODE -ne 0) { throw "function list failed" }
Invoke-SmokeUpload -ResourceGroup $ResourceGroup -FunctionApp $apiApp
}
finally {
    $lock.Dispose()
    Remove-Item -Force $lockPath -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $run -ErrorAction SilentlyContinue
}
