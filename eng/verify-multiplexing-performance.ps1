param(
    [string]$ReportPath = (
        Join-Path $PSScriptRoot (
            "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results\" +
            "BlueTusk.Benchmarks.MultiplexingComparisonBenchmarks-report-full.json")),
    [string]$BudgetPath = (
        Join-Path $PSScriptRoot "multiplexing-performance-budgets.json"),
    [string]$PairedReportPath,
    [string]$EvidencePath = (
        Join-Path $PSScriptRoot (
            "..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\" +
            "multiplexing-evidence.json"))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-Percentile {
    param(
        [double[]]$Values,
        [double]$Percentile
    )

    if ($Values.Length -eq 0) {
        throw "Cannot calculate a percentile without measurements."
    }

    $sorted = @($Values | Sort-Object)
    $rank = ($sorted.Length - 1) * ($Percentile / 100)
    $lowerIndex = [Math]::Floor($rank)
    $upperIndex = [Math]::Ceiling($rank)
    if ($lowerIndex -eq $upperIndex) {
        return [double]$sorted[$lowerIndex]
    }

    $fraction = $rank - $lowerIndex
    return [double]$sorted[$lowerIndex] +
        (([double]$sorted[$upperIndex] - [double]$sorted[$lowerIndex]) * $fraction)
}

function Get-Metrics {
    param(
        [object]$Benchmark,
        [int]$MinimumSamples
    )

    $resultMeasurements = @(
        $Benchmark.Measurements |
            Where-Object {
                $_.IterationMode -eq 'Workload' -and
                $_.IterationStage -eq 'Result'
            })
    if ($resultMeasurements.Count -lt $MinimumSamples) {
        throw (
            "$($Benchmark.Method) has $($resultMeasurements.Count) measured result samples; " +
            "$MinimumSamples are required.")
    }

    $normalizedNanoseconds = [double[]]@(
        $resultMeasurements |
            ForEach-Object {
                [double]$_.Nanoseconds / [double]$_.Operations
            })
    return [pscustomobject]@{
        Method = [string]$Benchmark.Method
        Samples = $resultMeasurements.Count
        MeanNanoseconds = [double]$Benchmark.Statistics.Mean
        P95Nanoseconds = Get-Percentile $normalizedNanoseconds 95
        P99Nanoseconds = Get-Percentile $normalizedNanoseconds 99
        OperationsPerSecond = 1e9 / [double]$Benchmark.Statistics.Mean
        AllocatedBytes = [double]$Benchmark.Memory.BytesAllocatedPerOperation
    }
}

function Assert-MaximumRatio {
    param(
        [string]$Metric,
        [double]$Candidate,
        [double]$Reference,
        [double]$Maximum,
        [string]$CandidateName,
        [string]$ReferenceName
    )

    if ($Reference -le 0) {
        throw "$ReferenceName has a non-positive $Metric reference value."
    }

    $ratio = $Candidate / $Reference
    if ($ratio -gt $Maximum) {
        throw (
            "$CandidateName $Metric ratio against $ReferenceName is " +
            "$($ratio.ToString('F4')); maximum is $($Maximum.ToString('F4')).")
    }
}

function Assert-MaximumValue {
    param(
        [string]$Metric,
        [double]$Actual,
        [double]$Maximum,
        [string]$CandidateName,
        [string]$ReferenceName
    )

    if ($Actual -gt $Maximum) {
        throw (
            "$CandidateName paired median-of-trials $Metric ratio against " +
            "$ReferenceName is $($Actual.ToString('F4')); maximum is " +
            "$($Maximum.ToString('F4')).")
    }
}

function Get-PositiveSamples {
    param(
        [object[]]$Values,
        [int]$ExpectedCount,
        [string]$Label
    )

    if ($Values.Count -ne $ExpectedCount) {
        throw "$Label contains $($Values.Count) samples; expected $ExpectedCount."
    }

    $samples = [double[]]@($Values | ForEach-Object { [double]$_ })
    foreach ($sample in $samples) {
        if ($sample -le 0 -or
            [double]::IsNaN($sample) -or
            [double]::IsInfinity($sample)) {
            throw "$Label contains a non-positive or non-finite sample."
        }
    }

    return $samples
}

function Assert-ExactProperties {
    param(
        [object]$Value,
        [string[]]$Expected,
        [string]$Label
    )

    $actual = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($property in $Value.PSObject.Properties) {
        $null = $actual.Add([string]$property.Name)
    }

    if ($actual.Count -ne $Expected.Count -or
        @($Expected | Where-Object { -not $actual.Contains($_) }).Count -ne 0) {
        throw "$Label does not match its closed property schema."
    }
}

function Get-PairedMetrics {
    param(
        [object]$PairedReport
    )

    Assert-ExactProperties `
        -Value $PairedReport `
        -Expected @(
            'schemaVersion',
            'method',
            'capturedUtc',
            'stopwatchFrequency',
            'operationsPerBurst',
            'warmupBurstsPerProvider',
            'trialCount',
            'blocksPerTrial',
            'burstsPerBlock',
            'workloads') `
        -Label 'The paired multiplexing report'

    if ([int]$PairedReport.schemaVersion -ne 1) {
        throw 'The paired multiplexing report schemaVersion must be 1.'
    }
    if ([string]$PairedReport.method -ne 'alternating-provider-blocks') {
        throw 'The paired multiplexing report has an unsupported measurement method.'
    }
    if ([long]$PairedReport.stopwatchFrequency -le 0) {
        throw 'The paired multiplexing report has an invalid Stopwatch frequency.'
    }
    if ([int]$PairedReport.operationsPerBurst -ne 64 -or
        [int]$PairedReport.warmupBurstsPerProvider -ne 64 -or
        [int]$PairedReport.trialCount -ne 5 -or
        [int]$PairedReport.blocksPerTrial -ne 501 -or
        [int]$PairedReport.burstsPerBlock -ne 4) {
        throw (
            'The paired multiplexing report must contain 64 operations per burst, ' +
            '64 warmups per provider, five trials, 501 blocks per trial and 4 bursts per block.')
    }

    $capturedUtc = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$PairedReport.capturedUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$capturedUtc) -or
        $capturedUtc -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'The paired multiplexing report capturedUtc is invalid or in the future.'
    }

    $workloads = @($PairedReport.workloads)
    if ($workloads.Count -ne 4) {
        throw "The paired multiplexing report contains $($workloads.Count) workloads; expected 4."
    }

    $result = @{}
    foreach ($workload in $workloads) {
        Assert-ExactProperties `
            -Value $workload `
            -Expected @('candidate', 'reference', 'trials') `
            -Label 'A paired multiplexing workload'

        $candidateName = [string]$workload.candidate
        $referenceName = [string]$workload.reference
        $key = "$candidateName|$referenceName"
        if ($result.ContainsKey($key)) {
            throw "The paired multiplexing report repeats '$key'."
        }

        $trials = @($workload.trials)
        if ($trials.Count -ne [int]$PairedReport.trialCount) {
            throw "$key contains $($trials.Count) trials; expected $($PairedReport.trialCount)."
        }

        $meanRatios = [double[]]::new($trials.Count)
        $p95Ratios = [double[]]::new($trials.Count)
        $p99Ratios = [double[]]::new($trials.Count)
        for ($trialIndex = 0; $trialIndex -lt $trials.Count; $trialIndex++) {
            $trial = $trials[$trialIndex]
            Assert-ExactProperties `
                -Value $trial `
                -Expected @(
                    'candidateFirst',
                    'candidateNanosecondsPerOperation',
                    'referenceNanosecondsPerOperation') `
                -Label "$key trial $trialIndex"

            $expectedCandidateFirst = ($trialIndex % 2) -eq 0
            if ([bool]$trial.candidateFirst -ne $expectedCandidateFirst) {
                throw "$key trial $trialIndex has an invalid provider start order."
            }

            $candidateSamples = Get-PositiveSamples `
                -Values @($trial.candidateNanosecondsPerOperation) `
                -ExpectedCount ([int]$PairedReport.blocksPerTrial) `
                -Label "$key trial $trialIndex candidate"
            $referenceSamples = Get-PositiveSamples `
                -Values @($trial.referenceNanosecondsPerOperation) `
                -ExpectedCount ([int]$PairedReport.blocksPerTrial) `
                -Label "$key trial $trialIndex reference"
            $candidateMean = ($candidateSamples | Measure-Object -Average).Average
            $referenceMean = ($referenceSamples | Measure-Object -Average).Average
            $meanRatios[$trialIndex] = $candidateMean / $referenceMean
            $p95Ratios[$trialIndex] =
                (Get-Percentile $candidateSamples 95) /
                (Get-Percentile $referenceSamples 95)
            $p99Ratios[$trialIndex] =
                (Get-Percentile $candidateSamples 99) /
                (Get-Percentile $referenceSamples 99)
        }

        $result[$key] = [pscustomobject]@{
            Candidate = $candidateName
            Reference = $referenceName
            Trials = $trials.Count
            MeanRatio = Get-Percentile $meanRatios 50
            P95Ratio = Get-Percentile $p95Ratios 50
            P99Ratio = Get-Percentile $p99Ratios 50
        }
    }

    return $result
}

$resolvedReport = (Resolve-Path -LiteralPath $ReportPath).Path
$resolvedBudgets = (Resolve-Path -LiteralPath $BudgetPath).Path
$report = Get-Content -LiteralPath $resolvedReport -Raw | ConvertFrom-Json
$budgets = Get-Content -LiteralPath $resolvedBudgets -Raw | ConvertFrom-Json
$pairedMetrics = $null
if ($PSBoundParameters.ContainsKey('PairedReportPath')) {
    $resolvedPairedReport = (Resolve-Path -LiteralPath $PairedReportPath).Path
    $pairedReport = Get-Content -LiteralPath $resolvedPairedReport -Raw | ConvertFrom-Json
    $pairedMetrics = Get-PairedMetrics $pairedReport
}

# The default CI invocation validates the immutable checked-in evidence as well
# as its budgets. A caller supplying a fresh -ReportPath can evaluate a new run
# without pretending that it is the frozen reference. Supplying -EvidencePath
# explicitly validates both a custom report and its proposed manifest.
$validateEvidence = (
    -not $PSBoundParameters.ContainsKey('ReportPath') -or
    $PSBoundParameters.ContainsKey('EvidencePath'))
if ($validateEvidence) {
    $resolvedEvidence = (Resolve-Path -LiteralPath $EvidencePath).Path
    $evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
    $manifestReport = (
        Resolve-Path -LiteralPath (
            Join-Path (Split-Path -Parent $resolvedEvidence) ([string]$evidence.report.path))
    ).Path
    if ($manifestReport -ne $resolvedReport) {
        throw "The evidence manifest references '$manifestReport', not '$resolvedReport'."
    }

    $actualReportHash = (
        Get-FileHash -LiteralPath $resolvedReport -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    $expectedReportHash = ([string]$evidence.report.sha256).ToLowerInvariant()
    if ($actualReportHash -ne $expectedReportHash) {
        throw (
            "The multiplexing report SHA-256 is $actualReportHash; " +
            "the evidence manifest requires $expectedReportHash.")
    }

    if ([string]$evidence.sourceCommit -notmatch '^[0-9a-f]{40}$') {
        throw "The evidence source commit must be a full lowercase Git SHA."
    }

    if ([string]$evidence.environment.postgresqlImageDigest -notmatch '^postgres@sha256:[0-9a-f]{64}$') {
        throw "The evidence manifest must pin the PostgreSQL image digest."
    }

    if ([string]$report.HostEnvironmentInfo.DotNetCliVersion -ne [string]$evidence.environment.dotnetSdk) {
        throw "The report SDK does not match the evidence manifest."
    }

    if ([string]$report.HostEnvironmentInfo.RuntimeVersion -notlike (
        ".NET $($evidence.environment.dotnetRuntime)*")) {
        throw "The report runtime does not match the evidence manifest."
    }

    if ($PSBoundParameters.ContainsKey('PairedReportPath')) {
        if ($null -eq $evidence.pairedReport) {
            throw 'The evidence manifest does not reference the paired multiplexing report.'
        }

        $manifestPairedPath = Join-Path `
            (Split-Path -Parent $resolvedEvidence) `
            ([string]$evidence.pairedReport.path)
        $manifestPairedReport = (
            Resolve-Path -LiteralPath $manifestPairedPath
        ).Path
        if ($manifestPairedReport -ne $resolvedPairedReport) {
            throw (
                "The evidence manifest references '$manifestPairedReport', not " +
                "'$resolvedPairedReport'.")
        }

        $actualPairedHash = (
            Get-FileHash -LiteralPath $resolvedPairedReport -Algorithm SHA256
        ).Hash.ToLowerInvariant()
        $expectedPairedHash = ([string]$evidence.pairedReport.sha256).ToLowerInvariant()
        if ($actualPairedHash -ne $expectedPairedHash) {
            throw (
                "The paired multiplexing report SHA-256 is $actualPairedHash; " +
                "the evidence manifest requires $expectedPairedHash.")
        }
    }
}

$minimumSamples = [int]$budgets.minimumResultSamples
$metrics = @{}

foreach ($benchmark in $report.Benchmarks) {
    if ([string]$benchmark.Type -ne 'MultiplexingComparisonBenchmarks') {
        continue
    }

    $metric = Get-Metrics $benchmark $minimumSamples
    $metrics[$metric.Method] = $metric
}

foreach ($comparison in $budgets.comparisons) {
    $candidateName = [string]$comparison.candidate
    $referenceName = [string]$comparison.reference
    if (-not $metrics.ContainsKey($candidateName)) {
        throw "The report does not contain $candidateName."
    }

    if (-not $metrics.ContainsKey($referenceName)) {
        throw "The report does not contain $referenceName."
    }

    $candidate = $metrics[$candidateName]
    $reference = $metrics[$referenceName]
    $pairedKey = "$candidateName|$referenceName"
    # Alternating-provider evidence is authoritative when the capture includes
    # this exact pair. BlueTusk-only comparisons against its ordinary pool still
    # use the absolute MediumRun measurements.
    $requiresPairedLatency = $null -ne $pairedMetrics -and
        $pairedMetrics.ContainsKey($pairedKey)
    if ($requiresPairedLatency) {
        $paired = $pairedMetrics[$pairedKey]
        Assert-MaximumValue `
            -Metric 'mean latency' `
            -Actual $paired.MeanRatio `
            -Maximum ([double]$comparison.maxMeanRatio) `
            -CandidateName $candidateName `
            -ReferenceName $referenceName
        Assert-MaximumValue `
            -Metric 'P95 latency' `
            -Actual $paired.P95Ratio `
            -Maximum ([double]$comparison.maxP95Ratio) `
            -CandidateName $candidateName `
            -ReferenceName $referenceName
        Assert-MaximumValue `
            -Metric 'P99 latency' `
            -Actual $paired.P99Ratio `
            -Maximum ([double]$comparison.maxP99Ratio) `
            -CandidateName $candidateName `
            -ReferenceName $referenceName
    }
    else {
        Assert-MaximumRatio `
            -Metric 'mean latency' `
            -Candidate $candidate.MeanNanoseconds `
            -Reference $reference.MeanNanoseconds `
            -Maximum ([double]$comparison.maxMeanRatio) `
            -CandidateName $candidateName `
            -ReferenceName $referenceName
        Assert-MaximumRatio `
            -Metric 'P95 latency' `
            -Candidate $candidate.P95Nanoseconds `
            -Reference $reference.P95Nanoseconds `
            -Maximum ([double]$comparison.maxP95Ratio) `
            -CandidateName $candidateName `
            -ReferenceName $referenceName
        Assert-MaximumRatio `
            -Metric 'P99 latency' `
            -Candidate $candidate.P99Nanoseconds `
            -Reference $reference.P99Nanoseconds `
            -Maximum ([double]$comparison.maxP99Ratio) `
            -CandidateName $candidateName `
            -ReferenceName $referenceName
    }
    Assert-MaximumRatio `
        -Metric 'managed allocation' `
        -Candidate $candidate.AllocatedBytes `
        -Reference $reference.AllocatedBytes `
        -Maximum ([double]$comparison.maxAllocationRatio) `
        -CandidateName $candidateName `
        -ReferenceName $referenceName
}

$metrics.Values |
    Sort-Object Method |
    Format-Table `
        Method,
        Samples,
        @{ Label = 'Mean us'; Expression = { $_.MeanNanoseconds / 1e3 }; FormatString = 'F2' },
        @{ Label = 'P95 us'; Expression = { $_.P95Nanoseconds / 1e3 }; FormatString = 'F2' },
        @{ Label = 'P99 us'; Expression = { $_.P99Nanoseconds / 1e3 }; FormatString = 'F2' },
        @{ Label = 'Ops/s'; Expression = { $_.OperationsPerSecond }; FormatString = 'F0' },
        @{ Label = 'Allocated B'; Expression = { $_.AllocatedBytes }; FormatString = 'F0' }

if ($null -ne $pairedMetrics) {
    $pairedMetrics.Values |
        Sort-Object Candidate |
        Format-Table `
            Candidate,
            Reference,
            Trials,
            @{ Label = 'Median mean ratio'; Expression = { $_.MeanRatio }; FormatString = 'F4' },
            @{ Label = 'Median P95 ratio'; Expression = { $_.P95Ratio }; FormatString = 'F4' },
            @{ Label = 'Median P99 ratio'; Expression = { $_.P99Ratio }; FormatString = 'F4' }
}

Write-Host (
    "Multiplexing performance evidence satisfies all configured mean, P95, P99, " +
    "throughput-derived, and allocation budgets.")
if ($validateEvidence) {
    Write-Host (
        "Evidence integrity matches source commit $($evidence.sourceCommit), " +
        "the report SHA-256, runtime, SDK, and pinned PostgreSQL image digest.")
}
