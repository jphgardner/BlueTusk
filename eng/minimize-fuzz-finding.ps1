[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Target,

    [Parameter(Mandatory)]
    [string] $InputPath,

    [Parameter(Mandatory)]
    [string] $OutputPath,

    [Parameter(Mandatory)]
    [string] $InstrumentedDirectory,

    [string] $MinimizerCommand = 'afl-tmin',

    [ValidateRange(100, 60000)]
    [int] $ExecutionTimeoutMilliseconds = 2000,

    [ValidateRange(128, 8192)]
    [int] $MemoryLimitMegabytes = 1024
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Get-Command $MinimizerCommand -ErrorAction SilentlyContinue)) {
    throw "Fuzz minimizer '$MinimizerCommand' is not installed or not on PATH."
}

$input = (Resolve-Path -LiteralPath $InputPath).Path
$instrumented = (Resolve-Path -LiteralPath $InstrumentedDirectory).Path
$harness = Join-Path $instrumented 'BlueTusk.Fuzzing.dll'
if (-not (Test-Path -LiteralPath $harness -PathType Leaf)) {
    throw "Instrumented fuzz harness '$harness' does not exist."
}

$env:BLUETUSK_FUZZ_TARGET = $Target
$env:AFL_SKIP_BIN_CHECK = '1'
try {
    & $MinimizerCommand `
        -i $input `
        -o $OutputPath `
        -t "$ExecutionTimeoutMilliseconds+" `
        -m $MemoryLimitMegabytes `
        -- dotnet $harness
    if ($LASTEXITCODE -ne 0) {
        throw "Fuzz minimization failed with exit code $LASTEXITCODE."
    }
}
finally {
    Remove-Item Env:\BLUETUSK_FUZZ_TARGET -ErrorAction SilentlyContinue
    Remove-Item Env:\AFL_SKIP_BIN_CHECK -ErrorAction SilentlyContinue
}

Write-Output "Minimized '$input' to '$OutputPath'."
