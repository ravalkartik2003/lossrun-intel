# Renders the load-test files for one environment (brief 15): copies infra/loadtest/* to infra/loadtest/out/ and fills
# {{keyvault_uri}}, {{function_host}} and {{function_app_name}} from Terraform outputs. The committed files keep the tokens.
# Usage (repo root): powershell -File infra/loadtest/render.ps1   then   az load test update ... --load-test-config-file infra/loadtest/out/config.yaml
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "../scripts/tf-output.ps1")
$values = @{
    "{{keyvault_uri}}"      = Get-TfOutput key_vault_uri
    "{{function_host}}"     = Get-TfOutput function_app_hostname
    "{{function_app_name}}" = Get-TfOutput function_app_name
}
$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $PSScriptRoot -File | Where-Object { $_.Extension -in ".yaml", ".properties", ".jmx", ".kql", ".csv" } | ForEach-Object {
    $text = [System.IO.File]::ReadAllText($_.FullName)
    foreach ($token in $values.Keys) { $text = $text.Replace($token, $values[$token]) }
    [System.IO.File]::WriteAllText((Join-Path $out $_.Name), $text, (New-Object System.Text.UTF8Encoding $false))
}
"rendered to $out"
