[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $EvidencePath,

    [Parameter(Mandatory)]
    [string] $EvidenceRoot,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $ExpectedCommit,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedPackageManifestSha256,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedPackageProvenanceSha256,

    [Parameter(Mandatory)]
    [string] $StreamsReportPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedStreamsReportSha256,

    [Parameter(Mandatory)]
    [string] $SyncReportPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-fA-F]{64}$')]
    [string] $ExpectedSyncReportSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RequiredProperty
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $property = $Value.PSObject.Properties[$Name]
    if ($null -eq $property)
    {
        throw "$Context is missing required property '$Name'."
    }

    return $property.Value
}

function Get-RequiredText
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $text = [string](Get-RequiredProperty -Value $Value -Name $Name -Context $Context)
    if ([string]::IsNullOrWhiteSpace($text))
    {
        throw "$Context property '$Name' must be non-empty."
    }

    return $text
}

function Get-UtcTimestamp
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $text = Get-RequiredText -Value $Value -Name $Name -Context $Context
    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            $text,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero)
    {
        throw "$Context property '$Name' must be an explicit UTC timestamp."
    }

    return $parsed
}

function Assert-Boolean
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [bool] $Expected,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $actual = Get-RequiredProperty -Value $Value -Name $Name -Context $Context
    if ($actual -isnot [bool] -or [bool]$actual -ne $Expected)
    {
        throw "$Context property '$Name' must be '$($Expected.ToString().ToLowerInvariant())'."
    }
}

function Get-PositiveNumber
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Name,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $raw = Get-RequiredProperty -Value $Value -Name $Name -Context $Context
    $number = 0.0
    if (-not [double]::TryParse(
            [string]$raw,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$number) -or
        [double]::IsNaN($number) -or
        [double]::IsInfinity($number) -or
        $number -le 0)
    {
        throw "$Context property '$Name' must be a finite positive number."
    }

    return $number
}

function Resolve-EvidenceArtifact
{
    param(
        [Parameter(Mandatory)]
        [string] $Root,

        [Parameter(Mandatory)]
        [string] $Path
    )

    if ([IO.Path]::IsPathRooted($Path))
    {
        throw "Disturbance artifact path '$Path' must be relative to the evidence root."
    }

    $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path
    $candidate = Join-Path $resolvedRoot $Path
    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    $prefix = $resolvedRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolved -PathType Leaf))
    {
        throw "Disturbance artifact '$resolved' escapes the evidence root or is not a file."
    }

    return $resolved
}

function Assert-DistinctText
{
    param(
        [Parameter(Mandatory)]
        [object] $Facts,

        [Parameter(Mandatory)]
        [string] $First,

        [Parameter(Mandatory)]
        [string] $Second,

        [Parameter(Mandatory)]
        [string] $Context
    )

    $firstValue = Get-RequiredText -Value $Facts -Name $First -Context $Context
    $secondValue = Get-RequiredText -Value $Facts -Name $Second -Context $Context
    if ([string]::Equals($firstValue, $secondValue, [StringComparison]::Ordinal))
    {
        throw "$Context facts '$First' and '$Second' must identify different values."
    }
}

$resolvedEvidence = (Resolve-Path -LiteralPath $EvidencePath).Path
$resolvedRoot = (Resolve-Path -LiteralPath $EvidenceRoot).Path
$rootPrefix = $resolvedRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedEvidence.StartsWith(
        $rootPrefix,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Operational-disturbance evidence '$resolvedEvidence' escapes '$resolvedRoot'."
}

$contract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-endurance-disturbance-contract.json') -Raw | ConvertFrom-Json
if ([int]$contract.schemaVersion -ne 1)
{
    throw "Expected endurance-disturbance contract schema 1; found '$($contract.schemaVersion)'."
}

$evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
if ([int]$evidence.schemaVersion -ne 1)
{
    throw "Expected operational-disturbance evidence schema 1; found '$($evidence.schemaVersion)'."
}

$ExpectedCommit = $ExpectedCommit.ToLowerInvariant()
if (-not [string]::Equals(
        [string]$evidence.candidateCommit,
        $ExpectedCommit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Operational-disturbance evidence is not for candidate '$ExpectedCommit'."
}

foreach ($binding in @(
        @{
            Name = 'packageManifestSha256'
            Expected = $ExpectedPackageManifestSha256
        },
        @{
            Name = 'packageProvenanceSha256'
            Expected = $ExpectedPackageProvenanceSha256
        }))
{
    $actual = Get-RequiredText `
        -Value $evidence `
        -Name $binding.Name `
        -Context 'Operational-disturbance evidence'
    if ($actual -notmatch '^[0-9a-f]{64}$' -or
        -not [string]::Equals(
            $actual,
            [string]$binding.Expected,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Operational-disturbance '$($binding.Name)' does not match exact package evidence."
    }
}

if ([string]::IsNullOrWhiteSpace([string]$evidence.reviewedBy) -or
    [string]::IsNullOrWhiteSpace([string]$evidence.summary) -or
    [long]$evidence.blockingFindings -ne 0)
{
    throw 'Operational-disturbance evidence requires a named reviewer, summary, and zero blockers.'
}
$reviewedUtc = Get-UtcTimestamp `
    -Value $evidence `
    -Name 'reviewedUtc' `
    -Context 'Operational-disturbance evidence'
if ($reviewedUtc -gt [DateTimeOffset]::UtcNow)
{
    throw 'Operational-disturbance evidence is future-dated.'
}

$reportDefinitions = @{
    streams = @{
        Path = $StreamsReportPath
        Hash = $ExpectedStreamsReportSha256.ToLowerInvariant()
    }
    sync = @{
        Path = $SyncReportPath
        Hash = $ExpectedSyncReportSha256.ToLowerInvariant()
    }
}
$reportWindows = @{}
foreach ($runId in @($contract.requiredRuns | ForEach-Object { [string]$_ }))
{
    $definition = $reportDefinitions[$runId]
    $reportPath = (Resolve-Path -LiteralPath $definition.Path).Path
    $actualHash = (
        Get-FileHash -LiteralPath $reportPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actualHash -ne $definition.Hash)
    {
        throw "The '$runId' endurance report changed before disturbance verification."
    }

    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ($report.completed -ne $true)
    {
        throw "The '$runId' endurance report is not complete."
    }
    $reportStarted = Get-UtcTimestamp `
        -Value $report `
        -Name 'startedAt' `
        -Context "'$runId' endurance report"
    $reportCompleted = Get-UtcTimestamp `
        -Value $report `
        -Name 'completedAt' `
        -Context "'$runId' endurance report"
    if ($reportCompleted -le $reportStarted)
    {
        throw "The '$runId' endurance report has an invalid observation window."
    }

    $reportWindows[$runId] = @{
        Started = $reportStarted
        Completed = $reportCompleted
        Hash = $actualHash
    }
}

$runs = @($evidence.runs)
if ($runs.Count -ne @($contract.requiredRuns).Count)
{
    throw (
        "Operational-disturbance evidence contains $($runs.Count) run records; " +
        "$(@($contract.requiredRuns).Count) are required.")
}

$artifactPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
$latestScenarioCompletion = [DateTimeOffset]::MinValue
$scenarioCount = 0
foreach ($requiredRun in @($contract.requiredRuns | ForEach-Object { [string]$_ }))
{
    $runMatches = @($runs | Where-Object { [string]$_.id -eq $requiredRun })
    if ($runMatches.Count -ne 1)
    {
        throw "Operational-disturbance evidence requires exactly one '$requiredRun' run record."
    }
    $run = $runMatches[0]
    $declaredReportHash = Get-RequiredText `
        -Value $run `
        -Name 'enduranceReportSha256' `
        -Context "'$requiredRun' disturbance run"
    if ($declaredReportHash -notmatch '^[0-9a-f]{64}$' -or
        -not [string]::Equals(
            $declaredReportHash,
            [string]$reportWindows[$requiredRun].Hash,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "'$requiredRun' disturbances are not bound to the exact endurance report."
    }

    $scenarios = @($run.scenarios)
    if ($scenarios.Count -ne @($contract.requiredScenarios).Count)
    {
        throw (
            "'$requiredRun' contains $($scenarios.Count) disturbance scenarios; " +
            "$(@($contract.requiredScenarios).Count) are required.")
    }

    foreach ($requiredScenario in @($contract.requiredScenarios))
    {
        $scenarioId = [string]$requiredScenario.id
        $matches = @($scenarios | Where-Object { [string]$_.id -eq $scenarioId })
        if ($matches.Count -ne 1)
        {
            throw "'$requiredRun' requires exactly one '$scenarioId' disturbance."
        }
        $scenario = $matches[0]
        $context = "'$requiredRun/$scenarioId' disturbance"
        $scenarioCount++

        foreach ($property in @(
                'target',
                'injectionMethod',
                'detectionSignal',
                'recoveryAction',
                'recoveryProbe',
                'observations'))
        {
            $null = Get-RequiredText -Value $scenario -Name $property -Context $context
        }
        if ([string]$scenario.outcome -ne 'passed' -or
            [long]$scenario.blockingFindings -ne 0)
        {
            throw "$context must have outcome 'passed' and zero blocking findings."
        }
        Assert-Boolean -Value $scenario -Name 'faultInjected' -Expected $true -Context $context
        Assert-Boolean -Value $scenario -Name 'detectionObserved' -Expected $true -Context $context
        Assert-Boolean -Value $scenario -Name 'recoveryObserved' -Expected $true -Context $context
        Assert-Boolean -Value $scenario -Name 'continuityVerified' -Expected $true -Context $context
        Assert-Boolean -Value $scenario -Name 'dataLossObserved' -Expected $false -Context $context

        $startedAt = Get-UtcTimestamp `
            -Value $scenario `
            -Name 'startedAt' `
            -Context $context
        $completedAt = Get-UtcTimestamp `
            -Value $scenario `
            -Name 'completedAt' `
            -Context $context
        if ($completedAt -le $startedAt -or
            $startedAt -lt $reportWindows[$requiredRun].Started -or
            $completedAt -gt $reportWindows[$requiredRun].Completed)
        {
            throw "$context did not occur wholly inside the exact endurance observation window."
        }
        if ($completedAt -gt $latestScenarioCompletion)
        {
            $latestScenarioCompletion = $completedAt
        }

        $facts = Get-RequiredProperty -Value $scenario -Name 'facts' -Context $context
        foreach ($requiredFact in @($requiredScenario.requiredFacts | ForEach-Object { [string]$_ }))
        {
            $null = Get-RequiredProperty -Value $facts -Name $requiredFact -Context "$context facts"
        }

        switch ($scenarioId)
        {
            'process-death'
            {
                Assert-DistinctText `
                    -Facts $facts `
                    -First 'preFaultProcessIdentity' `
                    -Second 'postRecoveryProcessIdentity' `
                    -Context "$context facts"
            }
            'network-interruption'
            {
                $null = Get-PositiveNumber `
                    -Value $facts `
                    -Name 'interruptionSeconds' `
                    -Context "$context facts"
                $null = Get-PositiveNumber `
                    -Value $facts `
                    -Name 'recoveredConnections' `
                    -Context "$context facts"
            }
            'storage-exhaustion'
            {
                $capacity = Get-PositiveNumber `
                    -Value $facts `
                    -Name 'capacityBytes' `
                    -Context "$context facts"
                $peak = Get-PositiveNumber `
                    -Value $facts `
                    -Name 'peakUsedBytes' `
                    -Context "$context facts"
                $null = Get-PositiveNumber `
                    -Value $facts `
                    -Name 'recoveredFreeBytes' `
                    -Context "$context facts"
                Assert-Boolean `
                    -Value $facts `
                    -Name 'exhaustionSignalObserved' `
                    -Expected $true `
                    -Context "$context facts"
                if (($peak / $capacity) -lt [double]$contract.storageMinimumUtilisation)
                {
                    throw (
                        "$context reached less than " +
                        "$([double]$contract.storageMinimumUtilisation * 100)% of its hard limit.")
                }
            }
            'credential-rotation'
            {
                Assert-DistinctText `
                    -Facts $facts `
                    -First 'previousCredentialVersion' `
                    -Second 'currentCredentialVersion' `
                    -Context "$context facts"
                Assert-Boolean `
                    -Value $facts `
                    -Name 'oldCredentialRejected' `
                    -Expected $true `
                    -Context "$context facts"
                Assert-Boolean `
                    -Value $facts `
                    -Name 'newCredentialAccepted' `
                    -Expected $true `
                    -Context "$context facts"
            }
            'primary-failover'
            {
                Assert-DistinctText `
                    -Facts $facts `
                    -First 'previousPrimaryIdentity' `
                    -Second 'currentPrimaryIdentity' `
                    -Context "$context facts"
                Assert-Boolean `
                    -Value $facts `
                    -Name 'sourceIdentityContinuityVerified' `
                    -Expected $true `
                    -Context "$context facts"
            }
            'clock-movement'
            {
                $timeSource = Get-RequiredText `
                    -Value $facts `
                    -Name 'timeSource' `
                    -Context "$context facts"
                if (@($contract.allowedClockTimeSources | Where-Object {
                            [string]$_ -eq $timeSource
                        }).Count -ne 1)
                {
                    throw "$context used unsupported clock boundary '$timeSource'."
                }
                $backward = [double](Get-RequiredProperty `
                        -Value $facts `
                        -Name 'backwardOffsetSeconds' `
                        -Context "$context facts")
                $forward = [double](Get-RequiredProperty `
                        -Value $facts `
                        -Name 'forwardOffsetSeconds' `
                        -Context "$context facts")
                if ([double]::IsNaN($backward) -or
                    [double]::IsInfinity($backward) -or
                    [double]::IsNaN($forward) -or
                    [double]::IsInfinity($forward) -or
                    $backward -ge 0 -or
                    $forward -le 0)
                {
                    throw "$context must exercise both backward and forward clock movement."
                }
            }
            'postgresql-minor-upgrade'
            {
                $fromText = Get-RequiredText `
                    -Value $facts `
                    -Name 'fromVersion' `
                    -Context "$context facts"
                $toText = Get-RequiredText `
                    -Value $facts `
                    -Name 'toVersion' `
                    -Context "$context facts"
                $fromVersion = [Version]::new()
                $toVersion = [Version]::new()
                if (-not [Version]::TryParse($fromText, [ref]$fromVersion) -or
                    -not [Version]::TryParse($toText, [ref]$toVersion) -or
                    $fromVersion.Major -lt 15 -or
                    $fromVersion.Major -gt 19 -or
                    $fromVersion.Major -ne $toVersion.Major -or
                    $toVersion -le $fromVersion)
                {
                    throw "$context must move to a higher minor release of the same PostgreSQL major."
                }
                $fromImage = Get-RequiredText `
                    -Value $facts `
                    -Name 'fromImage' `
                    -Context "$context facts"
                $toImage = Get-RequiredText `
                    -Value $facts `
                    -Name 'toImage' `
                    -Context "$context facts"
                if ($fromImage -notmatch '^.+@sha256:[0-9a-f]{64}$' -or
                    $toImage -notmatch '^.+@sha256:[0-9a-f]{64}$' -or
                    [string]::Equals($fromImage, $toImage, [StringComparison]::Ordinal))
                {
                    throw "$context requires two distinct digest-pinned PostgreSQL images."
                }
                Assert-Boolean `
                    -Value $facts `
                    -Name 'stateContinuityVerified' `
                    -Expected $true `
                    -Context "$context facts"
                Assert-Boolean `
                    -Value $facts `
                    -Name 'checkpointContinuityVerified' `
                    -Expected $true `
                    -Context "$context facts"
            }
        }

        $references = @($scenario.references | ForEach-Object { [string]$_ })
        if ($references.Count -lt 1)
        {
            throw "$context must cite at least one external observation or change record."
        }
        foreach ($reference in $references)
        {
            $uri = [Uri]::new('https://invalid.example')
            if (-not [Uri]::TryCreate($reference, [UriKind]::Absolute, [ref]$uri) -or
                $uri.Scheme -ne [Uri]::UriSchemeHttps)
            {
                throw "$context reference '$reference' must be an absolute HTTPS URI."
            }
        }

        $artifacts = @($scenario.artifacts)
        if ($artifacts.Count -ne @($contract.requiredArtifactRoles).Count)
        {
            throw (
                "$context contains $($artifacts.Count) artifact records; " +
                "$(@($contract.requiredArtifactRoles).Count) are required.")
        }
        foreach ($role in @($contract.requiredArtifactRoles | ForEach-Object { [string]$_ }))
        {
            $roleMatches = @($artifacts | Where-Object { [string]$_.role -eq $role })
            if ($roleMatches.Count -ne 1)
            {
                throw "$context requires exactly one '$role' artifact."
            }
            $artifact = $roleMatches[0]
            $relativePath = Get-RequiredText `
                -Value $artifact `
                -Name 'path' `
                -Context "$context '$role' artifact"
            $resolvedArtifact = Resolve-EvidenceArtifact `
                -Root $resolvedRoot `
                -Path $relativePath
            if (-not $artifactPaths.Add($resolvedArtifact))
            {
                throw "Disturbance artifact '$relativePath' is reused by multiple scenarios."
            }
            $expectedHash = Get-RequiredText `
                -Value $artifact `
                -Name 'sha256' `
                -Context "$context '$role' artifact"
            $actualHash = (
                Get-FileHash -LiteralPath $resolvedArtifact -Algorithm SHA256
            ).Hash.ToLowerInvariant()
            if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or
                $actualHash -ne $expectedHash)
            {
                throw "$context '$role' artifact does not match its SHA-256."
            }
            if ((Get-Item -LiteralPath $resolvedArtifact).Length -le 0)
            {
                throw "$context '$role' artifact is empty."
            }
            $null = Get-RequiredText `
                -Value $artifact `
                -Name 'mediaType' `
                -Context "$context '$role' artifact"
        }
    }
}

if ($reviewedUtc -lt $latestScenarioCompletion)
{
    throw 'Operational-disturbance review predates one or more recorded recoveries.'
}

Write-Output (
    "Operational-disturbance evidence passed: $scenarioCount exact-candidate recoveries " +
    "across $($runs.Count) endurance runs and $($artifactPaths.Count) content-addressed records.")
