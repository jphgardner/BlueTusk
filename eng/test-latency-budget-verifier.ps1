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
}
finally {
    Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
}

Write-Output (
    'Latency budget verifier self-test passed: checked-in evidence is accepted, ' +
    'and schema drift, duplicate workflow evidence, false maxima, and excessive ' +
    'calibrated ceilings are rejected.')
