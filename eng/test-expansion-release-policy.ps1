[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifier = Join-Path $PSScriptRoot 'verify-expansion-release-policy.ps1'
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'
$policyPath = Join-Path $PSScriptRoot 'expansion-release-policy.json'
$governancePath = Join-Path $PSScriptRoot 'v1-github-governance.json'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'bluetusk-expansion-release-tests-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

function Assert-Rejected
{
    param(
        [Parameter(Mandatory)]
        [string] $ExpectedMessage,

        [Parameter(Mandatory)]
        [hashtable] $Arguments
    )

    $failure = $null
    try { & $verifier @Arguments | Out-Null }
    catch { $failure = $_.Exception.Message }
    if ($null -eq $failure -or -not $failure.Contains($ExpectedMessage))
    {
        throw "Expected rejection containing '$ExpectedMessage'; received '$failure'."
    }
}

try
{
    & $verifier | Out-Null
    Assert-Rejected -ExpectedMessage 'has not been armed' -Arguments @{
        Family = 'Jobs'
        RequireArmed = $true
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw |
        ConvertFrom-Json -AsHashtable
    $manifest.families.Jobs.publication.enabled = $true
    $manifest.families.Jobs.publication.channel = 'stable'
    $testManifest = Join-Path $temporaryRoot 'families.json'
    $manifest | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $testManifest -Encoding utf8
    $arguments = @{
        Family = 'Jobs'
        ManifestPath = $testManifest
        PolicyPath = $policyPath
        GovernancePath = $governancePath
    }
    $testPolicy = Join-Path $temporaryRoot 'policy.json'
    $policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json -AsHashtable
    $policy.families.Jobs.capacityWorkflow = 'ecosystem-performance.yml'
    $policy | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $testPolicy -Encoding utf8
    $arguments.PolicyPath = $testPolicy
    Assert-Rejected -ExpectedMessage 'product-specific capacity' -Arguments $arguments
    $arguments.PolicyPath = $policyPath
    Assert-Rejected -ExpectedMessage 'stable 1.0.0-or-newer version' -Arguments $arguments

    $versionDirectory = Join-Path $temporaryRoot 'eng/versions'
    [IO.Directory]::CreateDirectory($versionDirectory) | Out-Null
    Set-Content -LiteralPath (Join-Path $versionDirectory 'Jobs.props') `
        -Value '<Project><PropertyGroup><VersionPrefix>1.0.0</VersionPrefix><VersionSuffix /></PropertyGroup></Project>' `
        -Encoding utf8
    $arguments.RepositoryRoot = $temporaryRoot
    $workflowDirectory = Join-Path $temporaryRoot '.github/workflows'
    [IO.Directory]::CreateDirectory($workflowDirectory) | Out-Null
    foreach ($workflow in @('build.yml', 'security.yml', 'ecosystem-build.yml'))
    {
        Set-Content -LiteralPath (Join-Path $workflowDirectory $workflow) `
            -Value "name: fixture`non:`n  push:`n  workflow_dispatch:`njobs:`n  fixture:`n    runs-on: ubuntu-latest`n    steps:`n      - run: true`n" `
            -Encoding utf8
    }
    Assert-Rejected -ExpectedMessage "must require exact 'fuzzing.yml'" -Arguments $arguments

    $workflows = @('fuzzing.yml', 'jobs-release-capacity.yml',
        'jobs-release-failover.yml', 'jobs-release-upgrade.yml',
        'expansion-candidate-readiness.yml')
    foreach ($workflow in $workflows)
    {
        $manifest.families.Jobs.publication.requiredWorkflowEvidence += @{
            workflowFile = $workflow
            allowedEvents = @('workflow_dispatch')
        }
    }
    $manifest | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath $testManifest -Encoding utf8
    foreach ($workflow in @($workflows | Where-Object { $_ -cne 'jobs-release-capacity.yml' }))
    {
        Set-Content -LiteralPath (Join-Path $workflowDirectory $workflow) `
            -Value "name: fixture`non:`n  workflow_dispatch:`njobs:`n  fixture:`n    runs-on: ubuntu-latest`n    steps:`n      - run: true`n" `
            -Encoding utf8
    }
    Assert-Rejected -ExpectedMessage "missing required workflow 'jobs-release-capacity.yml'" -Arguments $arguments
    Set-Content -LiteralPath (Join-Path $workflowDirectory 'jobs-release-capacity.yml') `
        -Value "name: fixture`non:`n  workflow_dispatch:`njobs:`n  fixture:`n    runs-on: ubuntu-latest`n    steps:`n      - run: true`n" `
        -Encoding utf8
    $failoverPath = Join-Path $workflowDirectory 'jobs-release-failover.yml'
    Set-Content -LiteralPath $failoverPath `
        -Value "name: fixture`non:`n  push:`n  workflow_dispatch:`njobs:`n  fixture:`n    runs-on: ubuntu-latest`n    steps:`n      - run: true`n" `
        -Encoding utf8
    Assert-Rejected -ExpectedMessage 'must be manual-only' -Arguments $arguments
    Set-Content -LiteralPath $failoverPath `
        -Value "name: fixture`non:`n  workflow_dispatch:`njobs:`n  fixture:`n    runs-on: ubuntu-latest`n    steps:`n      - run: true`n" `
        -Encoding utf8

    # The committed governance is the owner-authorized sole-maintainer mode, so the
    # readiness environment must follow it exactly: self-review is permitted there.
    # Each case below declares its own readiness environment on a copy of that governance.
    $governance = Get-Content -LiteralPath $governancePath -Raw |
        ConvertFrom-Json -AsHashtable
    if ([string]$governance.maintenancePolicy.mode -cne 'sole-maintainer')
    {
        throw 'This self-test expects the committed sole-maintainer governance mode.'
    }
    $governance.environments = @($governance.environments | Where-Object {
        [string]$_.name -cne 'expansion-candidate-readiness'
    })
    $testGovernance = Join-Path $temporaryRoot 'governance.json'
    function Write-Governance
    {
        $governance | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath $testGovernance -Encoding utf8
    }
    Write-Governance
    $arguments.GovernancePath = $testGovernance
    Assert-Rejected -ExpectedMessage 'protected independent candidate readiness' -Arguments $arguments

    $readinessEnvironment = @{
        name = 'expansion-candidate-readiness'
        workflow = '.github/workflows/expansion-candidate-readiness.yml'
        minimumConfiguredReviewers = 1
        preventSelfReview = $true
        canAdminsBypass = $false
    }
    $governance.environments += $readinessEnvironment
    Write-Governance
    # Sole-maintainer mismatch: a governed-mode reviewer flag cannot be declared here.
    Assert-Rejected -ExpectedMessage 'protected independent candidate readiness' -Arguments $arguments
    $readinessEnvironment.preventSelfReview = $false
    $readinessEnvironment.canAdminsBypass = $true
    Write-Governance
    Assert-Rejected -ExpectedMessage 'protected independent candidate readiness' -Arguments $arguments
    $readinessEnvironment.canAdminsBypass = $false
    $readinessEnvironment.minimumConfiguredReviewers = 0
    Write-Governance
    Assert-Rejected -ExpectedMessage 'protected independent candidate readiness' -Arguments $arguments
    $readinessEnvironment.minimumConfiguredReviewers = 1
    $readinessEnvironment.preventSelfReview = 'false'
    Write-Governance
    Assert-Rejected -ExpectedMessage 'protected independent candidate readiness' -Arguments $arguments
    $readinessEnvironment.preventSelfReview = $false
    Write-Governance
    Assert-Rejected -ExpectedMessage "exact 'jobs-v1.0.0' protected production tag pattern" -Arguments $arguments

    $production = @($governance.environments | Where-Object name -eq 'package-production')[0]
    $production.deploymentBranchPolicy.requiredPatterns += 'jobs-v1.0.0'
    Write-Governance
    & $verifier @arguments | Out-Null

    $maintenancePolicy = $governance.maintenancePolicy
    $maintenancePolicy.authorizedOn = ''
    Write-Governance
    Assert-Rejected -ExpectedMessage 'Sole-maintainer governance requires' -Arguments $arguments
    $maintenancePolicy.authorizedOn = '2026-09-30'
    $maintenancePolicy.mode = 'unreviewed'
    Write-Governance
    Assert-Rejected -ExpectedMessage 'Unknown governance maintenance policy' -Arguments $arguments

    # Governed mode, as verify-github-governance.ps1 restores it: no maintenance policy,
    # an active ruleset, and independent reviewers who cannot approve their own runs.
    $governance.Remove('maintenancePolicy')
    Write-Governance
    Assert-Rejected -ExpectedMessage 'Governed mode requires an active ruleset' -Arguments $arguments
    $governance.ruleset.enforcement = 'active'
    foreach ($environment in $governance.environments) { $environment.preventSelfReview = $true }
    $readinessEnvironment.preventSelfReview = $false
    Write-Governance
    # Governed mismatch: sole-maintainer self-review cannot survive the restored policy.
    Assert-Rejected -ExpectedMessage 'protected independent candidate readiness' -Arguments $arguments
    $readinessEnvironment.preventSelfReview = $true
    Write-Governance
    & $verifier @arguments | Out-Null

    Write-Output 'Expansion release policy self-test passed: disabled preview, stable-version, exact workflow, mode-aligned independent-readiness and protected-tag gates.'
}
finally
{
    $resolvedRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $tempPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
        [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot).StartsWith(
            'bluetusk-expansion-release-tests-', [StringComparison]::Ordinal)))
    {
        throw 'Refusing to remove an expansion-release test directory outside the explicit temporary root.'
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}
