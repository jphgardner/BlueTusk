[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'protocol-frames',
        'authentication',
        'pgoutput',
        'binary-copy',
        'array-codec',
        'range-codec',
        'composite-codec',
        'streams-envelope',
        'live-resume-token')]
    [string] $Target,

    [ValidateRange(1, 86400)]
    [int] $DurationSeconds = 60,

    [ValidateRange(100, 60000)]
    [int] $ExecutionTimeoutMilliseconds = 2000,

    [ValidateRange(128, 8192)]
    [int] $MemoryLimitMegabytes = 1024,

    [ValidateRange(1, 65536)]
    [int] $MaximumInputBytes = 65536,

    [string] $FuzzerCommand = 'afl-fuzz',

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fuzzRoot = Join-Path $repositoryRoot 'artifacts/fuzz'
$targetRoot = Join-Path $fuzzRoot $Target
$publishDirectory = Join-Path $targetRoot 'bin'
$corpusDirectory = Join-Path $targetRoot 'corpus'
$findingsDirectory = Join-Path $targetRoot 'findings'
$allowedRoot = [System.IO.Path]::GetFullPath($fuzzRoot) + [System.IO.Path]::DirectorySeparatorChar

foreach ($path in @($targetRoot, $publishDirectory, $corpusDirectory, $findingsDirectory)) {
    $resolvedCandidate = [System.IO.Path]::GetFullPath($path)
    if (-not $resolvedCandidate.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use fuzz path outside '$fuzzRoot': $resolvedCandidate"
    }
}

if (Test-Path -LiteralPath $targetRoot) {
    Remove-Item -LiteralPath $targetRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDirectory, $corpusDirectory, $findingsDirectory -Force | Out-Null

$encodedCorpus = Join-Path $repositoryRoot "tests/fuzz-corpus/$Target"
$corpusCases = @(
    Get-ChildItem -LiteralPath $encodedCorpus -Filter '*.b64' -File
)
if ($corpusCases.Count -eq 0) {
    throw "Fuzz target '$Target' has no checked-in encoded corpus."
}

foreach ($case in $corpusCases) {
    $encoded = (Get-Content -LiteralPath $case.FullName -Raw).Trim()
    $bytes = [Convert]::FromBase64String($encoded)
    if ($bytes.Length -gt $MaximumInputBytes) {
        throw "Corpus case '$($case.FullName)' exceeds the $MaximumInputBytes-byte input limit."
    }

    $rawName = [System.IO.Path]::GetFileNameWithoutExtension($case.Name)
    [System.IO.File]::WriteAllBytes((Join-Path $corpusDirectory $rawName), $bytes)
}

$hadHeapHardLimit = Test-Path Env:\DOTNET_GCHeapHardLimit
$previousHeapHardLimit = $env:DOTNET_GCHeapHardLimit
Push-Location $repositoryRoot
try {
    if (-not $NoRestore) {
        dotnet tool restore
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to restore the pinned SharpFuzz command-line tool.'
        }
    }

    $publishArguments = @(
        'publish',
        'tests/BlueTusk.Fuzzing/BlueTusk.Fuzzing.csproj',
        '--configuration', 'Release',
        '--output', $publishDirectory
    )
    if ($NoRestore) {
        $publishArguments += '--no-restore'
    }

    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish fuzz target '$Target'."
    }

    $instrumentationTargets = @(
        Get-ChildItem -LiteralPath $publishDirectory -Filter 'BlueTusk*.dll' -File |
            Where-Object Name -NE 'BlueTusk.Fuzzing.dll'
    )
    if ($instrumentationTargets.Count -eq 0) {
        throw "No BlueTusk assemblies were found to instrument for '$Target'."
    }

    foreach ($assembly in $instrumentationTargets) {
        dotnet tool run sharpfuzz -- $assembly.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "SharpFuzz instrumentation failed for '$($assembly.FullName)'."
        }
    }

    if (-not (Get-Command $FuzzerCommand -ErrorAction SilentlyContinue)) {
        throw "Coverage-guided fuzzer '$FuzzerCommand' is not installed or not on PATH."
    }

    $env:BLUETUSK_FUZZ_TARGET = $Target
    $env:AFL_SKIP_BIN_CHECK = '1'
    $env:AFL_NO_UI = '1'
    $heapHardLimitBytes = [long]$MemoryLimitMegabytes * 1MB
    $env:DOTNET_GCHeapHardLimit = '0x' + $heapHardLimitBytes.ToString(
        'X',
        [Globalization.CultureInfo]::InvariantCulture)
    $harness = Join-Path $publishDirectory 'BlueTusk.Fuzzing.dll'
    $arguments = @(
        '-i', $corpusDirectory,
        '-o', $findingsDirectory,
        '-t', "$ExecutionTimeoutMilliseconds+",
        '-m', 'none',
        '-g', 1,
        '-G', $MaximumInputBytes,
        '-V', $DurationSeconds,
        '--',
        'dotnet', $harness
    )
    & $FuzzerCommand @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage-guided fuzzing failed for '$Target' with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
    Remove-Item Env:\BLUETUSK_FUZZ_TARGET -ErrorAction SilentlyContinue
    Remove-Item Env:\AFL_SKIP_BIN_CHECK -ErrorAction SilentlyContinue
    Remove-Item Env:\AFL_NO_UI -ErrorAction SilentlyContinue
    if ($hadHeapHardLimit) {
        $env:DOTNET_GCHeapHardLimit = $previousHeapHardLimit
    }
    else {
        Remove-Item Env:\DOTNET_GCHeapHardLimit -ErrorAction SilentlyContinue
    }
}

$findingFiles = @(
    Get-ChildItem -LiteralPath $findingsDirectory -Recurse -File |
        Where-Object {
            $_.Name -ne 'README.txt' -and
            ($_.Directory.Name -eq 'crashes' -or $_.Directory.Name -eq 'hangs')
        }
)
if ($findingFiles.Count -gt 0) {
    & (Join-Path $PSScriptRoot 'archive-fuzz-findings.ps1') `
        -Target $Target `
        -FindingDirectory $findingsDirectory
    throw "Fuzz target '$Target' produced $($findingFiles.Count) crash or hang finding(s)."
}

Write-Output (
    (
        "Fuzz target '{0}' completed for {1}s with a {2}ms execution timeout, " +
        "{3} MiB managed-heap limit, and {4}-byte input limit."
    ) -f
    $Target,
    $DurationSeconds,
    $ExecutionTimeoutMilliseconds,
    $MemoryLimitMegabytes,
    $MaximumInputBytes)
