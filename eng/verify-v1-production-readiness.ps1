[CmdletBinding()]
param(
    [ValidateSet('Engineering', 'Candidate')]
    [string] $Mode = 'Engineering',

    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string] $Commit,

    [string] $EvidencePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$configurationPath = Join-Path $PSScriptRoot 'v1-production-readiness.json'
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
if ([int]$configuration.schemaVersion -ne 1)
{
    throw "Expected V1 production-readiness schema 1; found '$($configuration.schemaVersion)'."
}

function Resolve-EvidenceFile
{
    param(
        [Parameter(Mandatory)]
        [string] $BasePath,

        [Parameter(Mandatory)]
        [string] $Path,

        [switch] $Directory
    )

    $resolvedBase = (Resolve-Path -LiteralPath $BasePath).Path
    $candidate = if ([IO.Path]::IsPathRooted($Path))
    {
        $Path
    }
    else
    {
        Join-Path $resolvedBase $Path
    }

    $resolved = (Resolve-Path -LiteralPath $candidate).Path
    $basePrefix = $resolvedBase.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith(
            $basePrefix,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Evidence path '$resolved' escapes evidence root '$resolvedBase'."
    }
    if ($Directory -and -not (Test-Path -LiteralPath $resolved -PathType Container))
    {
        throw "Evidence directory '$resolved' does not exist."
    }
    if (-not $Directory -and -not (Test-Path -LiteralPath $resolved -PathType Leaf))
    {
        throw "Evidence file '$resolved' does not exist."
    }

    return $resolved
}

foreach ($relativePath in @($configuration.requiredFiles))
{
    $path = Join-Path $repositoryRoot ([string]$relativePath)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required V1 production asset '$relativePath' is missing."
    }
}

foreach ($workflow in @($configuration.requiredWorkflows))
{
    $path = Join-Path $repositoryRoot ".github/workflows/$workflow"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw "Required V1 workflow '$workflow' is missing."
    }

    $source = Get-Content -LiteralPath $path -Raw
    if ($source -notmatch '(?m)^\s*workflow_dispatch\s*:')
    {
        throw "Required V1 workflow '$workflow' has no manual exact-candidate entry point."
    }
}

$fuzzWorkflowSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot '.github/workflows/fuzzing.yml') -Raw
$requiredFuzzDuration = [int]$configuration.minimums.fuzzTargetSeconds
if ($requiredFuzzDuration -lt 1 -or
    -not $fuzzWorkflowSource.Contains(
        "`$duration -lt $requiredFuzzDuration",
        [StringComparison]::Ordinal))
{
    throw (
        'The manual fuzzing workflow does not enforce the configured exact-candidate minimum of ' +
        "$requiredFuzzDuration seconds per target.")
}

$candidateWorkflowPath = Join-Path $repositoryRoot '.github/workflows/v1-candidate-readiness.yml'
$candidateWorkflowSource = Get-Content -LiteralPath $candidateWorkflowPath -Raw
$buildWorkflowSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot '.github/workflows/build.yml') -Raw
$securityWorkflowSource = Get-Content -LiteralPath (
    Join-Path $repositoryRoot '.github/workflows/security.yml') -Raw
if (-not $securityWorkflowSource.Contains(
        './eng/verify-test-credential-inventory.ps1',
        [StringComparison]::Ordinal))
{
    throw 'The exact security workflow does not verify the intentional test-credential boundary.'
}
foreach ($requiredBuildSource in @(
        './eng/build-v1-candidate-packages.ps1',
        'name: v1-candidate-packages-${{ github.sha }}',
        'retention-days: 90'))
{
    if (-not $buildWorkflowSource.Contains(
            $requiredBuildSource,
            [StringComparison]::Ordinal))
    {
        throw (
            'The manual build does not create retained canonical package evidence through ' +
            "'$requiredBuildSource'.")
    }
}
foreach ($requiredCandidateSource in @(
        "Prefix = 'bluetusk-website'",
        'productionMetricsPath = Relative $websiteMetrics',
        'productionMetricsSha256 = Hash $websiteMetrics'))
{
    if (-not $candidateWorkflowSource.Contains(
            $requiredCandidateSource,
            [StringComparison]::Ordinal))
    {
        throw (
            'The exact-candidate aggregation workflow does not bind the production website ' +
            "artifact through '$requiredCandidateSource'.")
    }
}
foreach ($requiredCandidateSource in @(
        'Prefix = "v1-candidate-packages-$($env:CANDIDATE_SHA)"',
        'manifestPath = Relative $packageManifest',
        'manifestSha256 = Hash $packageManifest',
        'provenanceSha256 = Hash $packageProvenance'))
{
    if (-not $candidateWorkflowSource.Contains(
            $requiredCandidateSource,
            [StringComparison]::Ordinal))
    {
        throw (
            'The exact-candidate aggregation workflow does not bind the canonical package ' +
            "artifact through '$requiredCandidateSource'.")
    }
}
if (-not $candidateWorkflowSource.Contains(
        'multiplexingEvidenceSha256 = Hash $performanceManifest',
        [StringComparison]::Ordinal))
{
    throw 'The exact-candidate aggregation workflow does not hash the performance manifest.'
}
foreach ($requiredCandidateSource in @(
        '$disturbanceReport = OneFile (Join-Path $root disturbances) operational-disturbance-evidence.json',
        'reportPath = Relative $disturbanceReport',
        'reportSha256 = Hash $disturbanceReport'))
{
    if (-not $candidateWorkflowSource.Contains(
            $requiredCandidateSource,
            [StringComparison]::Ordinal))
    {
        throw (
            'The exact-candidate aggregation workflow does not bind operational disturbances ' +
            "through '$requiredCandidateSource'.")
    }
}

$exampleEvidence = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-candidate-evidence.example.json') -Raw | ConvertFrom-Json
$exampleWorkflowRuns = @($exampleEvidence.workflowRuns)
if ($exampleWorkflowRuns.Count -ne @($configuration.requiredWorkflows).Count)
{
    throw (
        'The candidate-evidence example must contain exactly one record for every required ' +
        "workflow; found $($exampleWorkflowRuns.Count).")
}
foreach ($workflow in @($configuration.requiredWorkflows))
{
    if (-not $candidateWorkflowSource.Contains(
            "Workflow = '$workflow'",
            [StringComparison]::Ordinal))
    {
        throw "The exact-candidate aggregation workflow does not require '$workflow'."
    }

    $exampleMatches = @($exampleWorkflowRuns | Where-Object {
        [string]::Equals(
            [string]$_.workflowFile,
            [string]$workflow,
            [StringComparison]::Ordinal)
    })
    if ($exampleMatches.Count -ne 1)
    {
        throw "The candidate-evidence example must contain exactly one '$workflow' record."
    }
}

$exampleApprovalEntries = @($exampleEvidence.approvals)
if ($exampleApprovalEntries.Count -ne @($configuration.requiredApprovalEvidence).Count)
{
    throw (
        'The candidate-evidence example must contain exactly one record for every required ' +
        "approval; found $($exampleApprovalEntries.Count).")
}
foreach ($requiredApproval in @($configuration.requiredApprovalEvidence))
{
    $approvalId = [string]$requiredApproval.id
    if (-not $candidateWorkflowSource.Contains(
            "'$approvalId'",
            [StringComparison]::Ordinal))
    {
        throw "The exact-candidate aggregation workflow does not require approval '$approvalId'."
    }

    $exampleApprovalMatches = @($exampleApprovalEntries | Where-Object {
        [string]::Equals(
            [string]$_.id,
            $approvalId,
            [StringComparison]::Ordinal)
    })
    if ($exampleApprovalMatches.Count -ne 1)
    {
        throw "The candidate-evidence example must contain exactly one '$approvalId' approval."
    }
}

$approvalExample = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-approval-evidence.example.json') -Raw | ConvertFrom-Json
$approvalExampleReferences = @($approvalExample.references | ForEach-Object { [string]$_ })
if ($approvalExampleReferences.Count -lt 1 -or
    @($approvalExampleReferences | Where-Object {
        $uri = [Uri]::new('https://invalid.example')
        -not [Uri]::TryCreate($_, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne [Uri]::UriSchemeHttps
    }).Count -ne 0)
{
    throw 'The approval-evidence example must contain at least one absolute HTTPS reference.'
}

foreach ($gate in @($configuration.engineeringGates))
{
    $scriptPath = Join-Path $PSScriptRoot ([string]$gate)
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf))
    {
        throw "Engineering gate '$gate' is missing."
    }

    Write-Host "V1 engineering gate: $gate"
    & $scriptPath
}

$families = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'product-families.json') -Raw | ConvertFrom-Json
$enabledFamilies = @(
    $families.families.PSObject.Properties |
        Where-Object { $_.Value.publication.enabled -eq $true } |
        ForEach-Object { $_.Name }
)
if ($Mode -eq 'Engineering' -and $enabledFamilies.Count -ne 0)
{
    throw (
        'Engineering readiness requires publication to remain fail closed; enabled families: ' +
        ($enabledFamilies -join ', '))
}

if ($Mode -eq 'Engineering')
{
    Write-Output (
        "V1 engineering readiness passed with publication disabled: " +
        "$(@($configuration.engineeringGates).Count) engineering gates, " +
        "$($configuration.minimums.runtimeInstruments) runtime instruments, " +
        "$($configuration.minimums.productionSlos) production SLOs, and " +
        "$($configuration.minimums.benchmarkResults) benchmark results. " +
        "This does not authorise stable publication; exact-candidate evidence remains required.")
    return
}

if ([string]::IsNullOrWhiteSpace($Commit) -or
    [string]::IsNullOrWhiteSpace($EvidencePath))
{
    throw 'Candidate mode requires both -Commit and -EvidencePath.'
}

$Commit = $Commit.ToLowerInvariant()
$headCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim().ToLowerInvariant()
if ($LASTEXITCODE -ne 0 -or $headCommit -ne $Commit)
{
    throw "Checked-out commit '$headCommit' does not match candidate commit '$Commit'."
}

$trackedStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $trackedStatus.Count -ne 0)
{
    throw 'Candidate verification requires a clean tracked worktree at the exact candidate commit.'
}

& (Join-Path $PSScriptRoot 'verify-github-governance.ps1') -Mode Remote

$resolvedEvidence = (Resolve-Path -LiteralPath $EvidencePath).Path
$evidenceRoot = Split-Path -Parent $resolvedEvidence
$evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
if ([int]$evidence.schemaVersion -ne 1)
{
    throw "Expected candidate-evidence schema 1; found '$($evidence.schemaVersion)'."
}
if (-not [string]::Equals(
        [string]$evidence.candidateCommit,
        $Commit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Evidence candidate '$($evidence.candidateCommit)' does not match '$Commit'."
}

$workflowRuns = @($evidence.workflowRuns)
if ($workflowRuns.Count -ne @($configuration.requiredWorkflows).Count)
{
    throw (
        'Candidate evidence must contain exactly one run record for each required workflow; ' +
        "expected $(@($configuration.requiredWorkflows).Count), found $($workflowRuns.Count).")
}
foreach ($requiredWorkflow in @($configuration.requiredWorkflows))
{
    $successful = @($workflowRuns | Where-Object {
        [string]::Equals(
            [string]$_.workflowFile,
            [string]$requiredWorkflow,
            [StringComparison]::Ordinal) -and
        [string]::Equals(
            [string]$_.headSha,
            $Commit,
            [StringComparison]::OrdinalIgnoreCase) -and
        [string]$_.event -eq 'workflow_dispatch' -and
        [string]$_.conclusion -eq 'success' -and
        [long]$_.runId -gt 0 -and
        [Uri]::IsWellFormedUriString([string]$_.url, [UriKind]::Absolute)
    })
    if ($successful.Count -ne 1)
    {
        throw (
            "Candidate evidence must contain exactly one successful manual '$requiredWorkflow' " +
            "run for '$Commit'; found $($successful.Count).")
    }
}

$websiteDistribution = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.website.distributionPath) `
    -Directory
$websiteMetricsPath = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.website.productionMetricsPath)
$websiteMetricsHash = (
    Get-FileHash -LiteralPath $websiteMetricsPath -Algorithm SHA256
).Hash.ToLowerInvariant()
if ([string]$evidence.website.productionMetricsSha256 -notmatch '^[0-9a-f]{64}$' -or
    $websiteMetricsHash -ne [string]$evidence.website.productionMetricsSha256)
{
    throw 'The website production metrics do not match the candidate-manifest SHA-256.'
}

& (Join-Path $PSScriptRoot 'verify-website-evidence.ps1') `
    -DistributionPath $websiteDistribution `
    -MetricsPath $websiteMetricsPath `
    -ExpectedCommit $Commit

$packageEvidenceRoot = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.packages.evidencePath) `
    -Directory
$packageManifestPath = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.packages.manifestPath)
$packageProvenancePath = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.packages.provenancePath)
$expectedPackageManifestPath = (
    Resolve-Path -LiteralPath (
        Join-Path $packageEvidenceRoot 'package-manifest.json')
).Path
$expectedPackageProvenancePath = (
    Resolve-Path -LiteralPath (
        Join-Path $packageEvidenceRoot 'sbom/build-provenance.json')
).Path
if ($packageManifestPath -ne $expectedPackageManifestPath -or
    $packageProvenancePath -ne $expectedPackageProvenancePath)
{
    throw 'Candidate package manifest or provenance path is not canonical.'
}
foreach ($integrityCheck in @(
        @{
            Path = $packageManifestPath
            Expected = [string]$evidence.packages.manifestSha256
            Description = 'V1 package manifest'
        },
        @{
            Path = $packageProvenancePath
            Expected = [string]$evidence.packages.provenanceSha256
            Description = 'V1 package provenance'
        }))
{
    $actualHash = (
        Get-FileHash -LiteralPath $integrityCheck.Path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($integrityCheck.Expected -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne $integrityCheck.Expected)
    {
        throw "$($integrityCheck.Description) does not match its candidate-manifest SHA-256."
    }
}
& (Join-Path $PSScriptRoot 'verify-v1-package-evidence.ps1') `
    -EvidenceRoot $packageEvidenceRoot `
    -ExpectedCommit $Commit

$programme = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'postgresql19-programme.json') -Raw | ConvertFrom-Json
$milestone = @($programme.milestones | Where-Object {
    [string]$_.version -eq [string]$programme.currentOfficialMilestone
})
if ($milestone.Count -ne 1)
{
    throw 'The current PostgreSQL 19 programme milestone is ambiguous.'
}
$postgresqlImage = [string]$milestone[0].image

$streamsReport = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.streams.reportPath)
$streamsProvenance = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.streams.candidateProvenancePath)
foreach ($integrityCheck in @(
        @{
            Path = $streamsReport
            Expected = [string]$evidence.streams.reportSha256
            Description = 'Streams endurance report'
        },
        @{
            Path = $streamsProvenance
            Expected = [string]$evidence.streams.candidateProvenanceSha256
            Description = 'Streams candidate provenance'
        }))
{
    $actualHash = (
        Get-FileHash -LiteralPath $integrityCheck.Path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($integrityCheck.Expected -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne $integrityCheck.Expected)
    {
        throw "$($integrityCheck.Description) does not match its candidate-manifest SHA-256."
    }
}
& (Join-Path $PSScriptRoot 'verify-streams-endurance-report.ps1') `
    -ReportPath $streamsReport `
    -RequiredDuration ([TimeSpan]::FromHours(
        [double]$configuration.minimums.streamsEnduranceHours)) `
    -MinimumTransactions ([long]$configuration.minimums.streamsMinimumTransactions) `
    -ExpectedCommit $Commit `
    -CandidateProvenancePath $streamsProvenance `
    -ExpectedPostgreSqlImage $postgresqlImage

$syncReport = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.sync.reportPath)
$syncProvenance = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.sync.candidateProvenancePath)
foreach ($integrityCheck in @(
        @{
            Path = $syncReport
            Expected = [string]$evidence.sync.reportSha256
            Description = 'Sync endurance report'
        },
        @{
            Path = $syncProvenance
            Expected = [string]$evidence.sync.candidateProvenanceSha256
            Description = 'Sync candidate provenance'
        }))
{
    $actualHash = (
        Get-FileHash -LiteralPath $integrityCheck.Path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($integrityCheck.Expected -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne $integrityCheck.Expected)
    {
        throw "$($integrityCheck.Description) does not match its candidate-manifest SHA-256."
    }
}
$destinationImages = @($evidence.sync.expectedDestinationImages | ForEach-Object {
    [string]$_
})
if ($destinationImages.Count -ne 3 -or
    @($destinationImages | Where-Object {
        $_ -notmatch '@sha256:[0-9a-f]{64}$'
    }).Count -ne 0)
{
    throw 'Sync evidence must declare exactly three digest-pinned destination images.'
}
& (Join-Path $PSScriptRoot 'verify-sync-endurance-report.ps1') `
    -ReportPath $syncReport `
    -RequiredDuration ([TimeSpan]::FromHours(
        [double]$configuration.minimums.syncEnduranceHours)) `
    -MinimumCycles ([long]$configuration.minimums.syncMinimumCycles) `
    -ExpectedCommit $Commit `
    -CandidateProvenancePath $syncProvenance `
    -ExpectedPostgreSqlImage $postgresqlImage `
    -ExpectedDestinationImages $destinationImages

$disturbanceReport = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.disturbances.reportPath)
$expectedDisturbanceReport = (
    Resolve-Path -LiteralPath (
        Join-Path $evidenceRoot 'disturbances/operational-disturbance-evidence.json')
).Path
if ($disturbanceReport -ne $expectedDisturbanceReport)
{
    throw 'Operational-disturbance evidence path is not canonical.'
}
$disturbanceReportHash = (
    Get-FileHash -LiteralPath $disturbanceReport -Algorithm SHA256
).Hash.ToLowerInvariant()
if ([string]$evidence.disturbances.reportSha256 -notmatch '^[0-9a-f]{64}$' -or
    $disturbanceReportHash -ne [string]$evidence.disturbances.reportSha256)
{
    throw 'Operational-disturbance evidence does not match the candidate-manifest SHA-256.'
}
& (Join-Path $PSScriptRoot 'verify-endurance-disturbance-evidence.ps1') `
    -EvidencePath $disturbanceReport `
    -EvidenceRoot $evidenceRoot `
    -ExpectedCommit $Commit `
    -ExpectedPackageManifestSha256 ([string]$evidence.packages.manifestSha256) `
    -ExpectedPackageProvenanceSha256 ([string]$evidence.packages.provenanceSha256) `
    -StreamsReportPath $streamsReport `
    -ExpectedStreamsReportSha256 ([string]$evidence.streams.reportSha256) `
    -SyncReportPath $syncReport `
    -ExpectedSyncReportSha256 ([string]$evidence.sync.reportSha256)

$performanceResults = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.performance.resultsPath) `
    -Directory
$multiplexingEvidence = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.performance.multiplexingEvidencePath)
$multiplexingEvidenceHash = (
    Get-FileHash -LiteralPath $multiplexingEvidence -Algorithm SHA256
).Hash.ToLowerInvariant()
if ([string]$evidence.performance.multiplexingEvidenceSha256 -notmatch
        '^[0-9a-f]{64}$' -or
    $multiplexingEvidenceHash -ne
        [string]$evidence.performance.multiplexingEvidenceSha256)
{
    throw 'Multiplexing performance evidence does not match the candidate-manifest SHA-256.'
}
$multiplexingManifest = Get-Content -LiteralPath $multiplexingEvidence -Raw |
    ConvertFrom-Json
if (-not [string]::Equals(
        [string]$multiplexingManifest.sourceCommit,
        $Commit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw 'Multiplexing performance evidence does not match the candidate commit.'
}
$performanceArtifacts = @($multiplexingManifest.artifacts)
if ($performanceArtifacts.Count -lt [int]$configuration.minimums.performanceArtifacts)
{
    throw (
        "Performance evidence contains $($performanceArtifacts.Count) integrity records; " +
        "$($configuration.minimums.performanceArtifacts) are required.")
}
$performanceArtifactPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($artifact in $performanceArtifacts)
{
    $relativePath = ([string]$artifact.path).Replace('\', '/')
    if ($relativePath -notmatch '^(?:benchmark\.log|results/[A-Za-z0-9_.-]+)$' -or
        -not $performanceArtifactPaths.Add($relativePath))
    {
        throw "Performance artifact path '$relativePath' is unsafe or duplicated."
    }

    $artifactPath = Resolve-EvidenceFile `
        -BasePath (Split-Path -Parent $multiplexingEvidence) `
        -Path $relativePath
    $actualHash = (
        Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ([string]$artifact.sha256 -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne [string]$artifact.sha256 -or
        (Get-Item -LiteralPath $artifactPath).Length -ne [long]$artifact.bytes)
    {
        throw "Performance artifact '$relativePath' does not match its hash or byte count."
    }
}
foreach ($resultFile in Get-ChildItem -LiteralPath $performanceResults -File)
{
    $relativePath = "results/$($resultFile.Name)"
    if (-not $performanceArtifactPaths.Contains($relativePath))
    {
        throw "Performance result '$relativePath' is not integrity-bound by the manifest."
    }
}
$multiplexingReport = Resolve-EvidenceFile `
    -BasePath (Split-Path -Parent $multiplexingEvidence) `
    -Path ([string]$multiplexingManifest.report.path)
& (Join-Path $PSScriptRoot 'verify-benchmark-coverage.ps1') `
    -BaselinePath $performanceResults `
    -MinimumFixtureCount ([int]$configuration.minimums.benchmarkFixtures) `
    -MinimumBenchmarkCount ([int]$configuration.minimums.benchmarkResults)
& (Join-Path $PSScriptRoot 'verify-allocation-budgets.ps1') `
    -BaselinePath $performanceResults
& (Join-Path $PSScriptRoot 'verify-latency-budgets.ps1') `
    -BaselinePath $performanceResults
& (Join-Path $PSScriptRoot 'verify-multiplexing-performance.ps1') `
    -ReportPath $multiplexingReport `
    -EvidencePath $multiplexingEvidence

$requiredApprovalIds = @(
    $configuration.requiredApprovalEvidence |
        ForEach-Object { [string]$_.id }
)
$approvalEntries = @($evidence.approvals)
if ($approvalEntries.Count -ne $requiredApprovalIds.Count)
{
    throw (
        "Candidate evidence contains $($approvalEntries.Count) approval records; " +
        "$($requiredApprovalIds.Count) are required.")
}

foreach ($gateId in $requiredApprovalIds)
{
    $entry = @($approvalEntries | Where-Object {
        [string]$_.id -eq $gateId
    })
    if ($entry.Count -ne 1)
    {
        throw "Candidate evidence must contain exactly one '$gateId' approval."
    }

    $approvalPath = Resolve-EvidenceFile `
        -BasePath $evidenceRoot `
        -Path ([string]$entry[0].path)
    $expectedHash = ([string]$entry[0].sha256).ToLowerInvariant()
    $actualHash = (
        Get-FileHash -LiteralPath $approvalPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne $expectedHash)
    {
        throw "Approval '$gateId' does not match its declared SHA-256."
    }

    $approval = Get-Content -LiteralPath $approvalPath -Raw | ConvertFrom-Json
    $approvalReferences = @($approval.references | ForEach-Object { [string]$_ })
    if ($approvalReferences.Count -lt 1)
    {
        throw "Approval '$gateId' must cite at least one retained evidence record."
    }
    foreach ($approvalReference in $approvalReferences)
    {
        $referenceUri = [Uri]::new('https://invalid.example')
        if (-not [Uri]::TryCreate(
                $approvalReference,
                [UriKind]::Absolute,
                [ref]$referenceUri) -or
            $referenceUri.Scheme -ne [Uri]::UriSchemeHttps)
        {
            throw "Approval '$gateId' reference '$approvalReference' must be an absolute HTTPS URI."
        }
    }
    $approvedUtc = [DateTimeOffset]::MinValue
    if ([int]$approval.schemaVersion -ne 1 -or
        [string]$approval.gateId -ne $gateId -or
        -not [string]::Equals(
            [string]$approval.candidateCommit,
            $Commit,
            [StringComparison]::OrdinalIgnoreCase) -or
        [string]$approval.outcome -ne 'approved' -or
        [string]::IsNullOrWhiteSpace([string]$approval.approvedBy) -or
        [string]::IsNullOrWhiteSpace([string]$approval.summary) -or
        [long]$approval.blockingFindings -ne 0 -or
        -not [DateTimeOffset]::TryParse(
            [string]$approval.approvedUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$approvedUtc) -or
        $approvedUtc.Offset -ne [TimeSpan]::Zero -or
        $approvedUtc -gt [DateTimeOffset]::UtcNow)
    {
        throw "Approval '$gateId' is incomplete, non-UTC, future-dated, blocked, or for another candidate."
    }
}

& (Join-Path $PSScriptRoot 'verify-postgresql19-programme.ps1') `
    -RepositoryRoot $repositoryRoot `
    -RequireGeneralAvailability

$disturbanceRecoveryCount =
    [int]$configuration.minimums.enduranceDisturbanceRuns *
    [int]$configuration.minimums.enduranceDisturbancesPerRun
Write-Output (
    "V1 candidate readiness passed for immutable commit ${Commit}: " +
    "$($workflowRuns.Count) exact workflow runs, six-family package/SBOM/provenance evidence, " +
    "72-hour Streams and 24-hour Sync endurance with " +
    "$disturbanceRecoveryCount content-addressed disturbance " +
    "recoveries, fresh reference-machine performance evidence, " +
    "PostgreSQL 19 GA, and " +
    "$($requiredApprovalIds.Count) integrity-checked approvals. Stable publication may now be " +
    "enabled through the protected release environments.")
