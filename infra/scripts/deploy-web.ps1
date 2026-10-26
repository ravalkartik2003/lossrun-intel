# Builds the workbench (src/web/apps/workbench) and deploys it to the Static Web App (brief 11 §7, ADR 0012):
#   production       = live mode against the Function App API (Basic on the demo host, or -Auth entra)
#   preview "mock"   = the offline demo build (VITE_MOCK=1, docs/ui/mock served by MSW in the browser)
#   demo app         = the same offline build on its own Static Web App (custom domain; CSP connect-src 'self'), brief 13 addendum
# Keyless for Azure access (az login). The SWA deployment token is read into memory only and never printed.
# Brief 23 Answers 4: refuses to deploy while database changesets are pending, and after the deploy the production host must
# answer and a smoke upload through the live API must reach a published tier (deploy-checks.ps1).
# Usage (repo root): powershell -File infra/scripts/deploy-web.ps1 [-Auth basic|entra] [-SkipMock]
#   The resource names default to the Terraform outputs resource_group, static_web_app_name, function_app_name and
#   demo_static_web_app_name (infra/scripts/tf-output.ps1); pass a parameter to override one.
param(
    [string]$ResourceGroup,
    [string]$App,
    [string]$FunctionApp,
    [ValidateSet("basic", "entra")][string]$Auth = "basic",
    [switch]$SkipMock,
    # The offline demo's own Static Web App (Terraform azurerm_static_web_app.demo); -SkipDemo skips it.
    [string]$DemoApp,
    [switch]$SkipDemo,
    # Clean install first (CI-style). Off by default: a running dev server holds native modules open on Windows.
    [switch]$CleanInstall
)

$ErrorActionPreference = "Stop"
# Windows PowerShell 5.1 turns any stderr line of a native command into a terminating error under "Stop" when stderr is captured
# (Vite's reporter and the SWA CLI write warnings there). npm and npx run under "Continue"; their exit code decides.
function Invoke-Native([string]$Command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    # The .cmd shim: npm.ps1 mangles arguments forwarded through @args ("Unknown command: pm").
    try { & "$Command.cmd" @args 2>&1 | ForEach-Object { "$_" } } finally { $ErrorActionPreference = $previous }
}
. (Join-Path $PSScriptRoot "tf-output.ps1")
. (Join-Path $PSScriptRoot "deploy-checks.ps1")
if (-not $ResourceGroup) { $ResourceGroup = Get-TfOutput resource_group }
if (-not $App) { $App = Get-TfOutput static_web_app_name }
if (-not $FunctionApp) { $FunctionApp = Get-TfOutput function_app_name }
Assert-DatabaseCurrent -FunctionApp $FunctionApp
if (-not $SkipDemo -and -not $DemoApp) { $DemoApp = Get-TfOutput demo_static_web_app_name }
$root = Resolve-Path (Join-Path $PSScriptRoot "../..")
$web = Join-Path $root "src/web"
$workbench = Join-Path $web "apps/workbench"

Push-Location $web
try {
    if ($CleanInstall -or -not (Test-Path (Join-Path $web "node_modules"))) {
        Invoke-Native npm ci --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }

    # Flex Consumption apps report the host under properties (the top-level defaultHostName is empty).
    $funcHost = az functionapp show -g $ResourceGroup -n $FunctionApp --query "properties.defaultHostName" -o tsv
    if (-not $funcHost) { $funcHost = "$FunctionApp.azurewebsites.net" }
    if ($funcHost -notmatch '^[a-z0-9-]+(\.[a-z0-9-]+)+$') { throw "unexpected Function App host '$funcHost'" }
    $env:VITE_API_BASE = "https://$funcHost/api/v1"
    "API base: $($env:VITE_API_BASE)"
    $env:VITE_AUTH = $Auth
    Invoke-Native npm run build -w '@lossrun/workbench'
    if ($LASTEXITCODE -ne 0) { throw "live build failed" }
    Remove-Item Env:VITE_API_BASE, Env:VITE_AUTH

    if (-not $SkipMock) {
        Invoke-Native npm run build:mock -w '@lossrun/workbench'
        if ($LASTEXITCODE -ne 0) { throw "mock build failed" }
    }

    $token = az staticwebapp secrets list -g $ResourceGroup -n $App --query properties.apiKey -o tsv
    Invoke-Native npx --yes '@azure/static-web-apps-cli@2.1.0' deploy (Join-Path $workbench "dist") --deployment-token $token --env production
    if ($LASTEXITCODE -ne 0) { throw "deploy (production) failed" }
    if (-not $SkipMock) {
        Invoke-Native npx --yes '@azure/static-web-apps-cli@2.1.0' deploy (Join-Path $workbench "dist-mock") --deployment-token $token --env mock
        if ($LASTEXITCODE -ne 0) { throw "deploy (mock) failed" }
    }
    $token = $null
    if (-not $SkipMock -and -not $SkipDemo) {
        # The demo calls no API: tighten connect-src to 'self' in a copy of the mock build.
        $demoDir = Join-Path ([System.IO.Path]::GetTempPath()) "lossrun-offline-demo"
        if (Test-Path $demoDir) { Remove-Item -Recurse -Force $demoDir -Confirm:$false }
        Copy-Item -Recurse (Join-Path $workbench "dist-mock") $demoDir
        $cfg = Join-Path $demoDir "staticwebapp.config.json"
        $tight = (Get-Content $cfg -Raw) -replace "connect-src [^;]*;", "connect-src 'self';"
        [System.IO.File]::WriteAllText($cfg, $tight, (New-Object System.Text.UTF8Encoding $false)) # no BOM
        $demoToken = az staticwebapp secrets list -g $ResourceGroup -n $DemoApp --query properties.apiKey -o tsv
        Invoke-Native npx --yes '@azure/static-web-apps-cli@2.1.0' deploy $demoDir --deployment-token $demoToken --env production
        if ($LASTEXITCODE -ne 0) { throw "deploy (demo) failed" }
        $demoToken = $null
        "demo: https://" + (az staticwebapp show -g $ResourceGroup -n $DemoApp --query defaultHostname -o tsv)
    }
    $production = "https://" + (az staticwebapp show -g $ResourceGroup -n $App --query defaultHostname -o tsv)
    "production: $production"
    az staticwebapp environment list -g $ResourceGroup -n $App --query "[].{env:name, host:hostname}" -o table
    $code = curl.exe -s -o NUL -w "%{http_code}" $production
    if ($code -notin @("200", "401")) { throw "DEPLOY FAILED: $production answered $code" } # 401: the demo host's Basic challenge
    Invoke-SmokeUpload -ResourceGroup $ResourceGroup -FunctionApp $FunctionApp
}
finally {
    Pop-Location
}
