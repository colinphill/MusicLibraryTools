[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SubscriptionId,
    [Parameter(Mandatory)]
    [string]$OutputFile
)

$ErrorActionPreference = "Stop"

function Invoke-AzureJson([string[]]$Arguments) {
    $json = & az @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Azure CLI failed: az $($Arguments -join ' ')"
    }
    if ([string]::IsNullOrWhiteSpace(($json -join "`n"))) {
        return $null
    }
    return ($json -join "`n") | ConvertFrom-Json
}

& az account set --subscription $SubscriptionId
if ($LASTEXITCODE -ne 0) {
    throw "Could not select Azure subscription '$SubscriptionId'."
}

$accounts = @(Invoke-AzureJson @(
    "resource", "list",
    "--subscription", $SubscriptionId,
    "--resource-type", "Microsoft.CodeSigning/codeSigningAccounts",
    "--output", "json"
))
if ($accounts.Count -ne 1) {
    $names = @($accounts | ForEach-Object { $_.name }) -join ", "
    throw "Expected exactly one accessible Artifact Signing account in subscription '$SubscriptionId'; found $($accounts.Count): $names"
}

$accountResource = $accounts[0]
$account = Invoke-AzureJson @(
    "rest", "--method", "get",
    "--url", "https://management.azure.com$($accountResource.id)?api-version=2025-10-13",
    "--output", "json"
)
$endpoint = [string]$account.properties.accountUri
if (-not [Uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref]([Uri]$null)) -or
    -not $endpoint.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifact Signing account '$($accountResource.name)' did not return a valid HTTPS accountUri."
}

$profileResponse = Invoke-AzureJson @(
    "rest", "--method", "get",
    "--url", "https://management.azure.com$($accountResource.id)/certificateProfiles?api-version=2025-10-13",
    "--output", "json"
)
$profiles = @($profileResponse.value)
$publicProfiles = @($profiles | Where-Object {
    [string]::Equals([string]$_.properties.profileType, "PublicTrust", [StringComparison]::OrdinalIgnoreCase)
})
if ($publicProfiles.Count -ne 1) {
    $descriptions = @($profiles | ForEach-Object {
        "$($_.name) ($($_.properties.profileType))"
    }) -join ", "
    throw "Expected exactly one PublicTrust certificate profile in Artifact Signing account '$($accountResource.name)'; found $($publicProfiles.Count). Available profiles: $descriptions"
}

$profileName = ([string]$publicProfiles[0].name -split '/')[-1]
if ([string]::IsNullOrWhiteSpace($profileName)) {
    throw "The Artifact Signing certificate profile did not have a usable name."
}

Add-Content -LiteralPath $OutputFile -Encoding utf8 -Value "endpoint=$endpoint"
Add-Content -LiteralPath $OutputFile -Encoding utf8 -Value "account_name=$($accountResource.name)"
Add-Content -LiteralPath $OutputFile -Encoding utf8 -Value "profile_name=$profileName"

Write-Host "Using Artifact Signing account '$($accountResource.name)' and PublicTrust profile '$profileName'."
