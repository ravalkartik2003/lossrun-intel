# Reads Terraform outputs of infra/ (brief 15): the deploy scripts take their resource names from here, so no environment's
# names are written into a script. Dot-source it, then: $name = Get-TfOutput function_app_name
# Needs `terraform init` in infra/ (with the environment's backend) and an applied state; read-only (terraform output).
$script:TfOutputs = $null

function Get-TfOutput([Parameter(Mandatory)][string]$Name) {
    if ($null -eq $script:TfOutputs) {
        $infra = Resolve-Path (Join-Path $PSScriptRoot "..")
        $json = terraform "-chdir=$infra" output -json
        if ($LASTEXITCODE -ne 0) { throw "terraform output failed in $infra (run terraform init with the environment's backend first, or pass the names as parameters)" }
        $script:TfOutputs = ($json | Out-String) | ConvertFrom-Json
    }
    $output = $script:TfOutputs.PSObject.Properties[$Name]
    if ($null -eq $output -or [string]::IsNullOrEmpty($output.Value.value)) {
        throw "Terraform output '$Name' is not in the state (terraform apply first, or pass the value as a parameter)"
    }
    $output.Value.value
}
