[CmdletBinding()]
param(
    [ValidateSet('Events', 'Jobs', 'Documents', 'Schema', 'Projections',
        'Search', 'Sql', 'Studio', 'Edge', 'Workflows')]
    [string] $Family,

    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string] $ManifestPath = (Join-Path $PSScriptRoot 'product-families.json'),

    [string] $PolicyPath = (Join-Path $PSScriptRoot 'expansion-release-policy.json'),

    [string] $GovernancePath = (Join-Path $PSScriptRoot 'v1-github-governance.json'),

    [switch] $RequireArmed
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$policy = Get-Content -LiteralPath $PolicyPath -Raw | ConvertFrom-Json
$governance = Get-Content -LiteralPath $GovernancePath -Raw | ConvertFrom-Json

$expansionFamilies = @('Events', 'Jobs', 'Documents', 'Schema', 'Projections',
    'Search', 'Sql', 'Studio', 'Edge', 'Workflows')
$sharedCapacityFamilies = @('Jobs', 'Documents', 'Projections', 'Workflows')
$requiredBaseWorkflows = @('build.yml', 'security.yml', 'fuzzing.yml',
    'ecosystem-build.yml')

if ([int]$manifest.schemaVersion -ne 2 -or
    [int]$policy.schemaVersion -ne 1 -or
    [int]$governance.schemaVersion -ne 1)
{
    throw 'Expansion release policy requires the supported manifest, policy and governance schemas.'
}

$policyNames = @($policy.families.PSObject.Properties.Name)
if ($policyNames.Count -ne $expansionFamilies.Count -or
    @(Compare-Object $expansionFamilies $policyNames).Count -ne 0 -or
    [string]$policy.readinessWorkflow -cne 'expansion-candidate-readiness.yml')
{
    throw 'Expansion release policy must name every expansion family and its dedicated candidate-readiness workflow.'
}

$selectedFamilies = if ([string]::IsNullOrWhiteSpace($Family))
{
    $expansionFamilies
}
else
{
    @($Family)
}

foreach ($selectedFamily in $selectedFamilies)
{
    $definition = $manifest.families.PSObject.Properties[$selectedFamily].Value
    $qualification = $policy.families.PSObject.Properties[$selectedFamily].Value
    if ($null -eq $definition -or $null -eq $qualification)
    {
        throw "Expansion family '$selectedFamily' is missing from the release manifest or policy."
    }

    $publication = $definition.publication
    $armed = $publication.enabled -eq $true
    if ($RequireArmed -and -not $armed)
    {
        throw "Expansion family '$selectedFamily' has not been armed for publication."
    }

    $tagPrefix = [string]$publication.tagPrefix
    if ($tagPrefix -notmatch '^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$')
    {
        throw "Expansion family '$selectedFamily' has an invalid tag prefix."
    }

    $capacityWorkflow = [string]$qualification.capacityWorkflow
    $failoverWorkflow = [string]$qualification.failoverWorkflow
    $upgradeWorkflow = [string]$qualification.upgradeWorkflow
    $expectedCapacityWorkflow = if ($selectedFamily -in $sharedCapacityFamilies)
    {
        'ecosystem-performance.yml'
    }
    else
    {
        "$tagPrefix-release-capacity.yml"
    }
    if ($capacityWorkflow -cne $expectedCapacityWorkflow -or
        $failoverWorkflow -cne "$tagPrefix-release-failover.yml" -or
        $upgradeWorkflow -cne "$tagPrefix-release-upgrade.yml")
    {
        throw "Expansion family '$selectedFamily' must use its declared product-specific capacity, failover and upgrade workflows."
    }

    $versionFile = Join-Path $root ([string]$definition.versionFile)
    [xml]$versionDocument = Get-Content -LiteralPath $versionFile -Raw
    $prefix = [string]$versionDocument.Project.PropertyGroup.VersionPrefix
    $suffix = [string]$versionDocument.Project.PropertyGroup.VersionSuffix
    $version = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }
    $parsedVersion = $null
    if (-not [Version]::TryParse($prefix, [ref]$parsedVersion))
    {
        throw "Expansion family '$selectedFamily' has an invalid version prefix."
    }

    if (-not $armed)
    {
        if ([string]$publication.channel -cne 'preview' -or
            [string]::IsNullOrWhiteSpace($suffix))
        {
            throw "Unreleased expansion family '$selectedFamily' must retain a preview channel and version."
        }
        continue
    }

    if ([string]$publication.channel -cne 'stable' -or
        $parsedVersion.Major -lt 1 -or
        -not [string]::IsNullOrWhiteSpace($suffix))
    {
        throw "Expansion family '$selectedFamily' cannot be armed until it has a stable 1.0.0-or-newer version."
    }

    $requiredWorkflows = @($requiredBaseWorkflows + @(
        $capacityWorkflow, $failoverWorkflow, $upgradeWorkflow,
        [string]$policy.readinessWorkflow))
    $qualificationWorkflows = @($capacityWorkflow, $failoverWorkflow,
        $upgradeWorkflow, [string]$policy.readinessWorkflow)
    $declaredEvidence = @($publication.requiredWorkflowEvidence)
    $declaredFiles = @($declaredEvidence | ForEach-Object { [string]$_.workflowFile })
    foreach ($workflowFile in $requiredWorkflows)
    {
        if (@($declaredFiles | Where-Object { $_ -ceq $workflowFile }).Count -ne 1)
        {
            throw "Expansion family '$selectedFamily' must require exact '$workflowFile' workflow evidence."
        }
        $entry = @($declaredEvidence | Where-Object {
            [string]$_.workflowFile -ceq $workflowFile
        })[0]
        $allowedEvents = @($entry.allowedEvents | ForEach-Object { [string]$_ })
        if ($allowedEvents.Count -ne 1 -or $allowedEvents[0] -cne 'workflow_dispatch')
        {
            throw "Expansion workflow '$workflowFile' must require only manually dispatched evidence."
        }
        $workflowPath = Join-Path $root ".github/workflows/$workflowFile"
        if (-not (Test-Path -LiteralPath $workflowPath -PathType Leaf))
        {
            throw "Expansion family '$selectedFamily' is missing required workflow '$workflowFile'."
        }
        $workflowText = Get-Content -LiteralPath $workflowPath -Raw
        if ($workflowText -notmatch '(?m)^  workflow_dispatch:\s*$')
        {
            throw "Expansion workflow '$workflowFile' must support manual dispatch."
        }
        if ($workflowFile -in $qualificationWorkflows -and
            $workflowText -match '(?m)^  (?:push|pull_request|schedule):\s*$')
        {
            throw "Expansion qualification workflow '$workflowFile' must be manual-only."
        }
    }

    $readinessEnvironments = @($governance.environments | Where-Object {
        [string]$_.name -ceq 'expansion-candidate-readiness'
    })
    if ($readinessEnvironments.Count -ne 1 -or
        [string]$readinessEnvironments[0].workflow -cne
            '.github/workflows/expansion-candidate-readiness.yml' -or
        [int]$readinessEnvironments[0].minimumConfiguredReviewers -lt 1 -or
        $readinessEnvironments[0].preventSelfReview -ne $true -or
        $readinessEnvironments[0].canAdminsBypass -ne $false)
    {
        throw "Expansion family '$selectedFamily' requires protected independent candidate readiness."
    }

    $production = @($governance.environments | Where-Object {
        [string]$_.name -ceq 'package-production'
    })
    $expectedTag = "$tagPrefix-v$version"
    if ($production.Count -ne 1 -or
        @($production[0].deploymentBranchPolicy.requiredPatterns | Where-Object {
            [string]$_ -ceq $expectedTag
        }).Count -ne 1)
    {
        throw "Expansion family '$selectedFamily' requires the exact '$expectedTag' protected production tag pattern."
    }
}

Write-Output (
    "Verified expansion release source policy for $($selectedFamilies -join ', '); " +
    'disabled previews remain gated and arming requires dedicated evidence workflows and protected governance. Runtime evidence is checked separately.')
