[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-github-governance.ps1'
$baseline = Get-Content (Join-Path $PSScriptRoot 'v1-github-governance.json') -Raw
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('bluetusk-governance-' + [Guid]::NewGuid().ToString('N') + '.json')
try
{
    & (Join-Path $PSScriptRoot 'verify-github-governance.ps1') | Out-Null
    $rejected = 0
    foreach ($case in @('wrong-owner', 'missing-authorization', 'unexpected-ruleset', 'self-review-blocked', 'admin-bypass', 'security-disabled', 'unknown-mode'))
    {
        $configuration = $baseline | ConvertFrom-Json
        switch ($case)
        {
            'wrong-owner' { $configuration.maintenancePolicy.owner = 'another-owner' }
            'missing-authorization' { $configuration.maintenancePolicy.authorizedOn = '' }
            'unexpected-ruleset' { $configuration.ruleset.enforcement = 'active' }
            'self-review-blocked' { $configuration.environments[0].preventSelfReview = $true }
            'admin-bypass' { $configuration.environments[0].canAdminsBypass = $true }
            'security-disabled' { $configuration.repositorySecurity.vulnerabilityAlerts = $false }
            'unknown-mode' { $configuration.maintenancePolicy.mode = 'unreviewed' }
        }
        $configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
        $failed = $false
        try { & (Join-Path $PSScriptRoot 'verify-github-governance.ps1') -ConfigurationPath $temporary | Out-Null }
        catch { $failed = $true }
        if (-not $failed) { throw "Unsafe governance case '$case' was accepted." }
        $rejected++
    }
    $restored = $baseline | ConvertFrom-Json
    $restored.PSObject.Properties.Remove('maintenancePolicy')
    $restored.ruleset.enforcement = 'active'
    $restored.ruleset.pullRequest.minimumApprovals = 1
    $restored.ruleset.pullRequest.requireLastPushApproval = $true
    foreach ($environment in $restored.environments) { $environment.preventSelfReview = $true }
    $restored | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'verify-github-governance.ps1') -ConfigurationPath $temporary | Out-Null
    $scoped = $baseline | ConvertFrom-Json
    $prerelease = $scoped.environments | Where-Object name -ceq 'package-prerelease'
    $prerelease.requiredSecrets = @('UNAVAILABLE_HISTORICAL_SECRET')
    $expansion = $scoped.environments | Where-Object name -ceq 'expansion-candidate-readiness'
    if ($null -ne $expansion) { $expansion.requiredSecrets = @('UNAVAILABLE_EXPANSION_SECRET') }
    $scoped | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    & $verifier -Scope CorePackages -ConfigurationPath $temporary | Out-Null
    $failure = $null
    try { & $verifier -ConfigurationPath $temporary | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -notmatch 'UNAVAILABLE_HISTORICAL_SECRET')
    { throw 'Full governance must continue to validate the historical environment.' }

    $production = $scoped.environments | Where-Object name -ceq 'package-production'
    $production.deploymentBranchPolicy.requiredPatterns = @(
        $production.deploymentBranchPolicy.requiredPatterns | Where-Object { $_ -cne 'provider-v1.2.0' })
    $scoped | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $temporary -Encoding utf8NoBOM
    $failure = $null
    try { & $verifier -Scope CorePackages -ConfigurationPath $temporary | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($failure -notmatch 'five exact 1.2 tags')
    { throw 'Core package governance must reject a missing production tag.' }

    Write-Output "Verified governance scopes, historical environment protection, every Core production tag, and $rejected rejected unsafe cases."
}
finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
