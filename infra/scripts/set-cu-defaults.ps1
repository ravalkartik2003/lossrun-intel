# Sets Content Understanding resource defaults (model name/alias -> deployment).
# Called by terraform_data.cu_defaults. Keyless: uses the signed-in az CLI identity,
# which needs Cognitive Services User on the Foundry resource (assigned in rbac.tf).
# Inputs (env): CU_ENDPOINT, CU_API_VERSION, CU_MAPPING (JSON body).
$ErrorActionPreference = 'Stop'

$uri  = "$($env:CU_ENDPOINT)/contentunderstanding/defaults?api-version=$($env:CU_API_VERSION)"
$body = Join-Path ([System.IO.Path]::GetTempPath()) ("cu-defaults-" + [guid]::NewGuid() + ".json")
[System.IO.File]::WriteAllText($body, $env:CU_MAPPING)

try {
  # Role assignments can take several minutes to propagate; retry for ~10 minutes.
  for ($i = 1; $i -le 20; $i++) {
    az rest --method patch --uri $uri --resource "https://cognitiveservices.azure.com" `
      --headers "Content-Type=application/json" --body "@$body" --output none
    if ($LASTEXITCODE -eq 0) {
      Write-Host "CU defaults set on $($env:CU_ENDPOINT)"
      az rest --method get --uri $uri --resource "https://cognitiveservices.azure.com"
      exit 0
    }
    Write-Host "Attempt $i failed (exit $LASTEXITCODE); retrying in 30 s"
    Start-Sleep -Seconds 30
  }
  throw "Failed to set CU defaults after 20 attempts"
}
finally {
  Remove-Item $body -ErrorAction SilentlyContinue
}
