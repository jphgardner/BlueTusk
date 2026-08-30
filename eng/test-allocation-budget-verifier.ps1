[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$verifierPath = Join-Path $PSScriptRoot 'verify-allocation-budgets.ps1'
$budgetPath = Join-Path $PSScriptRoot '..\benchmarks\allocation-budgets.json'
$baselinePath = Join-Path $PSScriptRoot '..\benchmarks\baselines\windows-ryzen7-5800x-dotnet10\results'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) "bluetusk-allocation-budget-$([Guid]::NewGuid().ToString('N'))"
$syntheticResultsPath = Join-Path $temporaryRoot 'results'
$syntheticBudgetPath = Join-Path $temporaryRoot 'allocation-budgets.json'
$syntheticReportPath = Join-Path $syntheticResultsPath 'Synthetic-report-brief.json'

function Write-SyntheticBudget {
    param(
        [string] $Parameters = 'MutationCount=100',
        [string] $Divisor = 'MutationCount',
        [switch] $Duplicate
    )

    $budget = [ordered]@{
        benchmark = 'BlueTusk.Benchmarks.SyntheticBenchmarks.Encode'
        parameters = $Parameters
        normalizationDivisorParameter = $Divisor
        maximumBytesPerOperation = 100
        maximumGen2Collections = 0
    }
    $budgets = @($budget)
    if ($Duplicate) {
        $budgets += [ordered]@{} + $budget
    }
    [ordered]@{
        schemaVersion = 2
        environment = 'synthetic verifier self-test'
        budgets = $budgets
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
            Method = 'Encode'
            Parameters = 'MutationCount=1'
            Memory = [ordered]@{
                BytesAllocatedPerOperation = 20000
                Gen2Collections = 0
            }
        }
    )
    if (-not $OmitTarget) {
        $target = [ordered]@{
            Namespace = 'BlueTusk.Benchmarks'
            Type = 'SyntheticBenchmarks'
            Method = 'Encode'
            Parameters = $TargetParameters
            Memory = [ordered]@{
                BytesAllocatedPerOperation = 10000
                Gen2Collections = 0
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
    New-Item -ItemType Directory -Path $syntheticResultsPath -Force | Out-Null

    & $verifierPath -BudgetFile $budgetPath -BaselinePath $baselinePath *> $null

    Write-SyntheticBudget
    Write-SyntheticReport
    & $verifierPath `
        -BudgetFile $syntheticBudgetPath `
        -BaselinePath $syntheticResultsPath *> $null

    Write-SyntheticReport -OmitTarget
    Assert-SyntheticRejected -ExpectedMessage 'Missing allocation result'

    Write-SyntheticReport
    Write-SyntheticBudget -Divisor 'UnknownParameter'
    Assert-SyntheticRejected -ExpectedMessage 'invalid normalization parameter'

    Write-SyntheticBudget -Parameters 'MutationCount=not-a-number'
    Write-SyntheticReport -TargetParameters 'MutationCount=not-a-number'
    Assert-SyntheticRejected -ExpectedMessage 'invalid normalization parameter'

    Write-SyntheticBudget -Duplicate
    Assert-SyntheticRejected -ExpectedMessage 'Duplicate allocation budget'

    Write-SyntheticBudget
    Write-SyntheticReport -DuplicateTarget
    Assert-SyntheticRejected -ExpectedMessage 'is ambiguous'

    $configuration = Get-Content -LiteralPath $syntheticBudgetPath -Raw | ConvertFrom-Json
    $configuration.schemaVersion = 1
    $configuration | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $syntheticBudgetPath -Encoding utf8NoBOM
    Assert-SyntheticRejected -ExpectedMessage 'Unsupported allocation budget schema version'
}
finally {
    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output (
    'Allocation budget verifier self-test passed: checked-in evidence and the exact ' +
    'parameterized result are accepted; missing, ambiguous, invalidly normalized, ' +
    'duplicate, and schema-drifted evidence is rejected.')
