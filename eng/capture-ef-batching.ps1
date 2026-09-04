[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [Parameter(Mandatory)][ValidatePattern('^postgres:[^@\s]+@sha256:[0-9a-f]{64}$')][string] $PostgreSqlImage,
    [Parameter(Mandatory)][string] $BenchmarkAssemblyPath,
    [Parameter(Mandatory)][string] $OutputPath,
    [ValidateSet(1, 100, 1000)][int[]] $Rows = @(100, 1000),
    [ValidateRange(1, 50)][int] $Trials = 5,
    [ValidateRange(0.1, 300)][double] $WarmupSeconds = 1,
    [ValidateRange(0.1, 300)][double] $MeasurementSeconds = 2
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ((& git -C $root rev-parse HEAD).Trim() -cne $ExpectedCommit -or
    @(& git -C $root status --porcelain --untracked-files=normal).Count -ne 0)
{
    throw 'Commit the measured sources before capturing this diagnostic.'
}
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_BENCHMARK_CONNECTION_STRING))
{
    throw 'Configure the dedicated benchmark database in BLUETUSK_BENCHMARK_CONNECTION_STRING.'
}
if (@($Rows | Select-Object -Unique).Count -ne $Rows.Count) { throw 'Row counts must be unique.' }
$assemblyPath = (Resolve-Path -LiteralPath $BenchmarkAssemblyPath).Path
$destination = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $destination.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $destination)) { throw 'Use a new output directory beneath artifacts.' }
$null = New-Item -ItemType Directory -Path $destination
$arms = @(
    @{ Name = 'bluetusk-1'; Provider = 'bluetusk'; Batch = '1' },
    @{ Name = 'bluetusk-42'; Provider = 'bluetusk'; Batch = '42' },
    @{ Name = 'bluetusk-1000'; Provider = 'bluetusk'; Batch = '1000' },
    @{ Name = 'npgsql-default'; Provider = 'npgsql'; Batch = 'default' }
)
$records = [Collections.Generic.List[object]]::new()
foreach ($count in $Rows)
{
    for ($trial = 0; $trial -lt $Trials; $trial++)
    {
        # Rotate process order; no two provider processes run concurrently.
        for ($offset = 0; $offset -lt $arms.Count; $offset++)
        {
            $arm = $arms[($offset + $trial) % $arms.Count]
            $stem = "rows$count-trial$trial-$($arm.Name)"
            $capturePath = Join-Path $destination "$stem.json"
            $optionsPath = Join-Path $destination "$stem.options.json"
            [ordered]@{
                provider = $arm.Provider; feature = 'ef-update'; concurrency = 1
                warmupSeconds = $WarmupSeconds; measurementSeconds = $MeasurementSeconds
                maximumSamplesPerWorker = 100000; requireTls = $false
                sourceCommit = $ExpectedCommit; postgreSqlImage = $PostgreSqlImage
                outputPath = $capturePath; diagnostic = $true
            } | ConvertTo-Json | Set-Content -LiteralPath $optionsPath -Encoding utf8NoBOM
            Write-Output "EF diagnostic: $count rows, trial $($trial + 1)/$Trials, $($arm.Name)."
            & dotnet $assemblyPath --ef-batch-capture $optionsPath $count $arm.Batch
            if ($LASTEXITCODE -ne 0) { throw "Capture failed: $stem. Partial files are retained without a completed index." }
            $report = Get-Content -LiteralPath $capturePath -Raw | ConvertFrom-Json
            $expectedBatch = if ($arm.Batch -eq 'default') { $null } else { [int]$arm.Batch }
            if ($report.sourceCommit -cne $ExpectedCommit -or $report.provider -cne $arm.Provider -or
                $report.rowsPerOperation -ne $count -or $report.maxBatchSize -ne $expectedBatch -or
                -not $report.diagnostic -or $report.releaseGatePassed -or $report.environment.tlsActive -or
                -not $report.environment.harnessAssembly.EndsWith("+$ExpectedCommit", [StringComparison]::Ordinal) -or
                -not $report.environment.candidateAssembly.EndsWith("+$ExpectedCommit", [StringComparison]::Ordinal) -or
                $report.environment.referenceAssembly -notmatch '^10\.0\.3(?:\+|$)' -or
                $report.measurement.completedOperations -le 0 -or
                @($report.measurement.workers).Count -ne 1 -or
                @($report.measurement.workers[0].requestTicks).Count -ne $report.measurement.completedOperations)
            {
                throw "Capture identity, metadata, or sample count mismatch: $stem."
            }
            $records.Add([ordered]@{
                rows = $count; trial = $trial; arm = $arm.Name; path = "$stem.json"
                sha256 = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash.ToLowerInvariant()
            })
        }
    }
}
[ordered]@{
    schemaVersion = 1; evidenceKind = 'ef-batch-diagnostic'; diagnostic = $true
    releaseGatePassed = $false; sourceCommit = $ExpectedCommit
    harnessSha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
    trials = $Trials; rows = $Rows; records = $records.ToArray()
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'capture-index.json') -Encoding utf8NoBOM
Write-Output "Retained $($records.Count) diagnostic captures. This is not a release gate."
