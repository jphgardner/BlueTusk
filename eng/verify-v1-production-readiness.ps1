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

function ConvertTo-VerifiedUtcDateTime
{
    param(
        [Parameter(Mandatory)]
        [object] $Value,

        [Parameter(Mandatory)]
        [string] $Context
    )

    if ($Value -is [DateTimeOffset])
    {
        $parsed = [DateTimeOffset]$Value
        if ($parsed.Offset -ne [TimeSpan]::Zero)
        {
            throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
        }

        return $parsed
    }
    if ($Value -is [DateTime])
    {
        $dateTime = [DateTime]$Value
        if ($dateTime.Kind -ne [DateTimeKind]::Utc)
        {
            throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
        }

        return [DateTimeOffset]::new($dateTime)
    }
    if ($Value -isnot [string])
    {
        throw "$Context must be an ISO 8601 UTC timestamp."
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$Value,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal,
            [ref]$parsed) -or
        $parsed.Offset -ne [TimeSpan]::Zero)
    {
        throw "$Context must be an ISO 8601 UTC timestamp with a Z offset."
    }

    return $parsed
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
foreach ($requiredCandidateSource in @(
        '$continuousGraphReport = OneFile (Join-Path $root continuous-graph) report.json',
        '$continuousGraphProvenance = OneFile (Join-Path $root continuous-graph) build-provenance.json',
        'reportSha256 = Hash $continuousGraphReport',
        'candidateProvenanceSha256 = Hash $continuousGraphProvenance'))
{
    if (-not $candidateWorkflowSource.Contains(
            $requiredCandidateSource,
            [StringComparison]::Ordinal))
    {
        throw (
            'The exact-candidate aggregation workflow does not bind the ' +
            "ContinuousGraph endurance artifact through '$requiredCandidateSource'.")
    }
}
foreach ($requiredCandidateSource in @(
        'runAttempt = [int]$run.run_attempt',
        'completedUtc = [string]$run.updated_at',
        'schemaVersion = 3'))
{
    if (-not $candidateWorkflowSource.Contains(
            $requiredCandidateSource,
            [StringComparison]::Ordinal))
    {
        throw (
            'The exact-candidate aggregation workflow does not preserve temporal ' +
            "workflow evidence through '$requiredCandidateSource'.")
    }
}

$exampleEvidence = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-candidate-evidence.example.json') -Raw | ConvertFrom-Json
if ([int]$exampleEvidence.schemaVersion -ne 3)
{
    throw 'The candidate-evidence example must use schema 3.'
}
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
    if ([int]$exampleMatches[0].runAttempt -lt 1)
    {
        throw "The candidate-evidence example '$workflow' run attempt is invalid."
    }
    $null = ConvertTo-VerifiedUtcDateTime `
        $exampleMatches[0].completedUtc `
        "Candidate-evidence example '$workflow' completedUtc"
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

$approvalContract = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-approval-evidence-contract.json') -Raw |
    ConvertFrom-Json
$contractGateIds = @($approvalContract.gates | ForEach-Object { [string]$_.id })
$requiredApprovalIds = @(
    $configuration.requiredApprovalEvidence |
        ForEach-Object { [string]$_.id }
)
if ($contractGateIds.Count -ne $requiredApprovalIds.Count -or
    @($contractGateIds | Select-Object -Unique).Count -ne $contractGateIds.Count -or
    @($requiredApprovalIds | Where-Object { $_ -notin $contractGateIds }).Count -ne 0)
{
    throw (
        'The gate-specific approval contract must define exactly the configured V1 ' +
        'approval gates.')
}

$approvalExamples = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot 'v1-approval-evidence.examples.json') -Raw |
    ConvertFrom-Json
$exampleApprovals = @($approvalExamples.examples)
if ($exampleApprovals.Count -ne $requiredApprovalIds.Count)
{
    throw (
        'The detailed approval examples must contain exactly one record for every ' +
        "required approval; found $($exampleApprovals.Count).")
}
$zeroCommit = '0' * 40
$approvalVerifier = Join-Path $PSScriptRoot 'verify-v1-approval-evidence.ps1'
foreach ($gateId in $requiredApprovalIds)
{
    $matches = @($exampleApprovals | Where-Object {
        [string]$_.gateId -eq $gateId
    })
    if ($matches.Count -ne 1)
    {
        throw "The detailed approval examples must contain exactly one '$gateId' record."
    }

    $temporaryExample = Join-Path (
        [IO.Path]::GetTempPath()
    ) "bluetusk-v1-approval-$([Guid]::NewGuid().ToString('N')).json"
    try
    {
        $matches[0] | ConvertTo-Json -Depth 20 |
            Set-Content -LiteralPath $temporaryExample -Encoding utf8NoBOM
        & $approvalVerifier `
            -EvidencePath $temporaryExample `
            -ExpectedGateId $gateId `
            -ExpectedCommit $zeroCommit | Out-Null
    }
    finally
    {
        if (Test-Path -LiteralPath $temporaryExample)
        {
            Remove-Item -LiteralPath $temporaryExample -Force
        }
    }
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

$expectedFamilyOrder = @(
    'Provider',
    'Streams',
    'Sync',
    'Live',
    'ControlPlane',
    'ContinuousGraph'
)
$registeredFamilies = @($families.families.PSObject.Properties.Name)
if ($registeredFamilies.Count -ne $expectedFamilyOrder.Count -or
    (Compare-Object $expectedFamilyOrder $registeredFamilies))
{
    throw (
        'The immutable candidate must register exactly the six V1 product ' +
        'families in dependency order.')
}

$seenFamilies = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($familyName in $expectedFamilyOrder)
{
    $family = $families.families.$familyName
    if ($family.publication.enabled -ne $true)
    {
        throw "Candidate family '$familyName' is not armed for protected publication."
    }
    if (-not [string]::Equals(
            [string]$family.publication.channel,
            'stable',
            [StringComparison]::Ordinal))
    {
        throw "Candidate family '$familyName' is not on the stable channel."
    }

    [xml]$versionDocument = Get-Content -LiteralPath (
        Join-Path $repositoryRoot ([string]$family.versionFile)) -Raw
    $versionPrefix = [string]$versionDocument.Project.PropertyGroup.VersionPrefix
    $versionSuffix = [string]$versionDocument.Project.PropertyGroup.VersionSuffix
    if ($versionPrefix -ne '1.0.0' -or
        -not [string]::IsNullOrWhiteSpace($versionSuffix))
    {
        throw "Candidate family '$familyName' must have exact stable version 1.0.0."
    }

    foreach ($dependency in @($family.releaseDependencies))
    {
        if (-not $seenFamilies.Contains([string]$dependency))
        {
            throw (
                "Candidate family '$familyName' has out-of-order or unknown " +
                "dependency '$dependency'.")
        }
    }
    [void]$seenFamilies.Add($familyName)
}

$originMain = (& git -C $repositoryRoot rev-parse refs/remotes/origin/main).Trim()
if ($LASTEXITCODE -ne 0 -or
    -not [string]::Equals(
        $originMain,
        $Commit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw (
        "Candidate '$Commit' must be the reviewed immutable origin/main commit; " +
        "origin/main is '$originMain'.")
}
$releaseTags = @(
    & git -C $repositoryRoot tag --points-at $Commit |
        Where-Object {
            $_ -match (
                '^(?:provider|streams|sync|live|control-plane|' +
                'continuous-graph)-v1\.0\.0$')
        }
)
if ($LASTEXITCODE -ne 0 -or $releaseTags.Count -ne 0)
{
    throw (
        'Candidate verification is a pre-publication gate; release tags already ' +
        "exist at the candidate: $($releaseTags -join ', ').")
}

$candidateCommitUtcText = (
    & git -C $repositoryRoot show -s --format=%cI $Commit
).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw "Could not read commit timestamp for candidate '$Commit'."
}
$candidateCommitUtc = ConvertTo-VerifiedUtcDateTime `
    $candidateCommitUtcText `
    "Candidate '$Commit' commit timestamp"

& (Join-Path $PSScriptRoot 'verify-postgresql19-programme.ps1') `
    -RepositoryRoot $repositoryRoot `
    -RequireGeneralAvailability
& (Join-Path $PSScriptRoot 'verify-github-governance.ps1') -Mode Remote

$resolvedEvidence = (Resolve-Path -LiteralPath $EvidencePath).Path
$evidenceRoot = Split-Path -Parent $resolvedEvidence
$evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
if ([int]$evidence.schemaVersion -ne 3)
{
    throw "Expected candidate-evidence schema 3; found '$($evidence.schemaVersion)'."
}
if (-not [string]::Equals(
        [string]$evidence.candidateCommit,
        $Commit,
        [StringComparison]::OrdinalIgnoreCase))
{
    throw "Evidence candidate '$($evidence.candidateCommit)' does not match '$Commit'."
}

$workflowRuns = @($evidence.workflowRuns)
$workflowEvidence = & (
    Join-Path $PSScriptRoot 'verify-v1-workflow-evidence.ps1'
) `
    -EvidencePath $resolvedEvidence `
    -ExpectedCommit $Commit `
    -CandidateCommitUtc $candidateCommitUtc
$latestWorkflowCompletedUtc = [DateTimeOffset]$workflowEvidence.LatestCompletedUtc

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
$postgresqlImage = [string]$programme.generalAvailability.image

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
if ($destinationImages.Count -ne 5 -or
    @($destinationImages | Where-Object {
        $_ -notmatch '@sha256:[0-9a-f]{64}$'
    }).Count -ne 0)
{
    throw 'Sync evidence must declare exactly five digest-pinned destination images.'
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

$continuousGraphReport = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.continuousGraph.reportPath)
$continuousGraphProvenance = Resolve-EvidenceFile `
    -BasePath $evidenceRoot `
    -Path ([string]$evidence.continuousGraph.candidateProvenancePath)
foreach ($integrityCheck in @(
        @{
            Path = $continuousGraphReport
            Expected = [string]$evidence.continuousGraph.reportSha256
            Description = 'ContinuousGraph endurance report'
        },
        @{
            Path = $continuousGraphProvenance
            Expected = [string]$evidence.continuousGraph.candidateProvenanceSha256
            Description = 'ContinuousGraph candidate provenance'
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
& (Join-Path $PSScriptRoot 'verify-continuous-graph-endurance-report.ps1') `
    -ReportPath $continuousGraphReport `
    -RequiredDuration ([TimeSpan]::FromHours(
        [double]$configuration.minimums.continuousGraphEnduranceHours)) `
    -MinimumEvaluations (
        [long]$configuration.minimums.continuousGraphMinimumEvaluations) `
    -ExpectedCommit $Commit `
    -CandidateProvenancePath $continuousGraphProvenance `
    -ExpectedPostgreSqlImage $postgresqlImage

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
    if ($relativePath -notmatch (
            '^(?:benchmark\.log|multiplexing-paired-evidence\.json|' +
            'BenchmarkRun-[0-9]{8}-[0-9]{6}\.log|results/[A-Za-z0-9_.-]+)$') -or
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
$pairedMultiplexingReport = Resolve-EvidenceFile `
    -BasePath (Split-Path -Parent $multiplexingEvidence) `
    -Path ([string]$multiplexingManifest.pairedReport.path)
$canonicalPairedReport = [IO.Path]::GetFullPath(
    (Join-Path (Split-Path -Parent $multiplexingEvidence) 'multiplexing-paired-evidence.json'))
if ($pairedMultiplexingReport -ne $canonicalPairedReport -or
    -not $performanceArtifactPaths.Contains('multiplexing-paired-evidence.json'))
{
    throw 'The paired multiplexing report is not at its canonical integrity-bound path.'
}
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
    -PairedReportPath $pairedMultiplexingReport `
    -EvidencePath $multiplexingEvidence

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
    $canonicalApprovalPath = (
        Resolve-Path -LiteralPath (
            Join-Path $evidenceRoot "approvals/$gateId.json")
    ).Path
    if ($approvalPath -ne $canonicalApprovalPath)
    {
        throw "Approval '$gateId' is not at its canonical evidence path."
    }
    $expectedHash = ([string]$entry[0].sha256).ToLowerInvariant()
    $actualHash = (
        Get-FileHash -LiteralPath $approvalPath -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($expectedHash -notmatch '^[0-9a-f]{64}$' -or
        $actualHash -ne $expectedHash)
    {
        throw "Approval '$gateId' does not match its declared SHA-256."
    }

}

& (Join-Path $PSScriptRoot 'verify-v1-approval-evidence-set.ps1') `
    -EvidenceDirectory (Join-Path $evidenceRoot 'approvals') `
    -ExpectedCommit $Commit `
    -ExpectedWebsiteProductionMetricsSha256 (
        [string]$evidence.website.productionMetricsSha256
    ) `
    -NotBeforeUtc $latestWorkflowCompletedUtc

$disturbanceRecoveryCount =
    [int]$configuration.minimums.enduranceDisturbanceRuns *
    [int]$configuration.minimums.enduranceDisturbancesPerRun
Write-Output (
    "V1 candidate readiness passed for immutable commit ${Commit}: " +
    "$($workflowRuns.Count) exact workflow runs, six-family package/SBOM/provenance evidence, " +
    "72-hour Streams, 24-hour Sync, and 24-hour ContinuousGraph endurance with " +
    "$disturbanceRecoveryCount content-addressed disturbance " +
    "recoveries, fresh reference-machine performance evidence, " +
    "PostgreSQL 19 GA, and " +
    "$($requiredApprovalIds.Count) integrity-checked approvals. Stable publication may now be " +
    "enabled through the protected release environments.")
