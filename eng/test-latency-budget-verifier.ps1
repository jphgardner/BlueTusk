[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifierPath = Join-Path $PSScriptRoot 'verify-latency-budgets.ps1'
$budgetPath = Join-Path $PSScriptRoot '..\benchmarks\latency-budgets.json'
$baselinePath = Join-Path $PSScriptRoot '..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results'
$temporaryPath = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-latency-budget-$([Guid]::NewGuid().ToString('N')).json"
$syntheticRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-latency-budget-evidence-$([Guid]::NewGuid().ToString('N'))"
$syntheticResultsPath = Join-Path $syntheticRoot 'results'
$syntheticBudgetPath = Join-Path $syntheticRoot 'latency-budgets.json'
$syntheticReportPath = Join-Path $syntheticResultsPath 'Synthetic-report-brief.json'

function Assert-Rejected {
    param(
        [Parameter(Mandatory)]
        [scriptblock] $Mutate,

        [Parameter(Mandatory)]
        [string] $ExpectedMessage
    )

    $configuration = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json
    & $Mutate $configuration
    $configuration | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $temporaryPath -Encoding utf8NoBOM

    try {
        & $verifierPath -BudgetFile $temporaryPath -BaselinePath $baselinePath *> $null
        throw "Verifier accepted invalid calibration expected to match '$ExpectedMessage'."
    }
    catch {
        if ($_.Exception.Message -like 'Verifier accepted invalid calibration*') {
            throw
        }
        if ($_.Exception.Message -notmatch [regex]::Escape($ExpectedMessage)) {
            throw (
                "Verifier rejected invalid calibration for an unexpected reason. " +
                "Expected '$ExpectedMessage'; received '$($_.Exception.Message)'.")
        }
    }
}

function Write-SyntheticBudget {
    param(
        [string] $Parameters = 'MutationCount=100',
        [string] $Divisor = 'MutationCount'
    )

    [ordered]@{
        schemaVersion = 2
        environment = 'synthetic verifier self-test'
        policy = [ordered]@{
            maximumMeanRegressionPercent = 10
            maximumP95RegressionPercent = 10
            minimumSamples = 3
            minimumCalibrationObservations = 2
            calibratedBudgetRoundingNanoseconds = 10
        }
        budgets = @(
            [ordered]@{
                benchmark = 'BlueTusk.Benchmarks.SyntheticBenchmarks.Publish'
                parameters = $Parameters
                normalizationDivisorParameter = $Divisor
                maximumMeanNanoseconds = 100
                maximumP95Nanoseconds = 120
                reason = 'Synthetic exact-parameter and normalization contract.'
            }
        )
    } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $syntheticBudgetPath -Encoding utf8NoBOM
}

function Write-SyntheticReport {
    param(
        [string] $TargetParameters = 'MutationCount=100',
        [switch] $OmitTarget,
        [switch] $DuplicateTarget
    )

    $benchmarks = @(
        [ordered]@{
            Namespace = 'BlueTusk.Benchmarks'
            Type = 'SyntheticBenchmarks'
            Method = 'Publish'
            Parameters = 'MutationCount=1'
            Statistics = [ordered]@{
                N = 3
                Mean = 20000
                Percentiles = [ordered]@{ P95 = 24000 }
            }
        }
    )
    if (-not $OmitTarget) {
        $target = [ordered]@{
            Namespace = 'BlueTusk.Benchmarks'
            Type = 'SyntheticBenchmarks'
            Method = 'Publish'
            Parameters = $TargetParameters
            Statistics = [ordered]@{
                N = 3
                Mean = 10000
                Percentiles = [ordered]@{ P95 = 12000 }
            }
        }
        $benchmarks += $target
        if ($DuplicateTarget) {
            $benchmarks += [ordered]@{} + $target
        }
    }
    [ordered]@{ Benchmarks = $benchmarks } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $syntheticReportPath -Encoding utf8NoBOM
}

function Assert-SyntheticRejected {
    param(
        [Parameter(Mandatory)]
        [string] $ExpectedMessage
    )

    try {
        & $verifierPath `
            -BudgetFile $syntheticBudgetPath `
            -BaselinePath $syntheticResultsPath *> $null
        throw "Verifier accepted invalid synthetic evidence expected to match '$ExpectedMessage'."
    }
    catch {
        if ($_.Exception.Message -like 'Verifier accepted invalid synthetic evidence*') {
            throw
        }
        if ($_.Exception.Message -notmatch [regex]::Escape($ExpectedMessage)) {
            throw (
                "Verifier rejected synthetic evidence for an unexpected reason. " +
                "Expected '$ExpectedMessage'; received '$($_.Exception.Message)'.")
        }
    }
}

try {
    & $verifierPath -BudgetFile $budgetPath -BaselinePath $baselinePath *> $null

    Assert-Rejected -ExpectedMessage 'Unsupported latency budget schema version' -Mutate {
        param($configuration)
        $configuration.schemaVersion = 1
    }
    Assert-Rejected -ExpectedMessage 'invalid or duplicate workflow run' -Mutate {
        param($configuration)
        $budget = $configuration.budgets | Where-Object {
            $null -ne $_.PSObject.Properties['calibration']
        } | Select-Object -First 1
        $budget.calibration.observations[1].workflowRunId =
            $budget.calibration.observations[0].workflowRunId
    }
    Assert-Rejected -ExpectedMessage 'does not declare the maximum observed values' -Mutate {
        param($configuration)
        $budget = $configuration.budgets | Where-Object {
            $null -ne $_.PSObject.Properties['calibration']
        } | Select-Object -First 1
        $budget.calibration.maximumObservedP95Nanoseconds = 31
    }
    Assert-Rejected -ExpectedMessage 'is not within its evidence-derived ceilings' -Mutate {
        param($configuration)
        $budget = $configuration.budgets | Where-Object {
            $null -ne $_.PSObject.Properties['calibration']
        } | Select-Object -First 1
        $budget.maximumMeanNanoseconds = 40
    }

    New-Item -ItemType Directory -Path $syntheticResultsPath -Force | Out-Null
    Write-SyntheticBudget
    Write-SyntheticReport
    & $verifierPath `
        -BudgetFile $syntheticBudgetPath `
        -BaselinePath $syntheticResultsPath *> $null

    Write-SyntheticReport -OmitTarget
    Assert-SyntheticRejected -ExpectedMessage 'Missing latency result'

    Write-SyntheticReport
    Write-SyntheticBudget -Divisor 'UnknownParameter'
    Assert-SyntheticRejected -ExpectedMessage 'invalid normalization parameter'

    Write-SyntheticBudget -Parameters 'MutationCount=not-a-number'
    Write-SyntheticReport -TargetParameters 'MutationCount=not-a-number'
    Assert-SyntheticRejected -ExpectedMessage 'invalid normalization parameter'

    Write-SyntheticBudget
    Write-SyntheticReport -DuplicateTarget
    Assert-SyntheticRejected -ExpectedMessage 'is ambiguous'
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $syntheticRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output (
    'Latency budget verifier self-test passed: checked-in evidence is accepted, ' +
    'the exact parameterized result is selected and normalized, and schema drift, ' +
    'duplicate workflow evidence, false maxima, excessive calibrated ceilings, ' +
    'missing results, ambiguous results, and invalid divisors are rejected.')
