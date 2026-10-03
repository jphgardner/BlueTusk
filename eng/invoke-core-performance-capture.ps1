[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string] $Os,
    [Parameter(Mandatory)][ValidateSet('provider', 'family')][string] $Kind,
    [string] $Variant = '',
    [string] $Family = '',
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    # Host-persistent store shared by the capture and summarize jobs of one OS (ephemeral runners lose workspaces).
    [Parameter(Mandatory)][string] $EvidenceStore,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{2,24}$')][string] $RunId,
    [ValidateRange(1, 50)][int] $Trials = 10,
    [switch] $Diagnostic,
    [string[]] $Features = @(),
    [int[]] $Concurrency = @(1, 64, 256),
    [double] $WarmupSeconds = 5,
    [double] $MeasurementSeconds = 10,
    [string] $Owner = 'core-performance-evidence'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtimeOs = if ($IsWindows) { 'windows' } elseif ($IsLinux) { 'linux' } else { 'other' }
if ($runtimeOs -cne $Os) { throw "The $Os leg is running on a $runtimeOs host." }
$inputId = if ($Kind -eq 'provider') { "provider-$Variant" } else { $Family.ToLowerInvariant() }
$store = [IO.Path]::GetFullPath($EvidenceStore)
$output = Join-Path $store "$Os/raw/$inputId"
$plan = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-evidence-plan.json') -Raw | ConvertFrom-Json

# Optional host-wide measurement lock shared with other agents and workflows on this machine.
$lock = $env:BLUETUSK_HOST_MEASUREMENT_LOCK
$ownsLock = $false
function Enter-HostLock
{
    if (-not $lock) { return }
    $deadline = [DateTimeOffset]::UtcNow.AddHours(12)
    while ($true)
    {
        try
        {
            $stream = [IO.File]::Open($lock, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try
            {
                $bytes = [Text.UTF8Encoding]::new($false).GetBytes(([ordered]@{
                    owner = "$Owner/$RunId"; purpose = "Core performance capture $Os $inputId at $ExpectedCommit"
                    startedUtc = [DateTimeOffset]::UtcNow.ToString('o'); expectedEndUtc = [DateTimeOffset]::UtcNow.AddHours(8).ToString('o')
                } | ConvertTo-Json))
                $stream.Write($bytes, 0, $bytes.Length)
            }
            finally { $stream.Dispose() }
            $script:ownsLock = $true
            return
        }
        catch [IO.IOException]
        {
            if ([DateTimeOffset]::UtcNow -gt $deadline) { throw 'The host measurement lock stayed held for 12 hours.' }
            Write-Output 'Waiting for the host measurement lock to clear.'
            Start-Sleep -Seconds 60
        }
    }
}

Enter-HostLock
$fixtureRun = "$RunId-$($Os.Substring(0, 3))-$($inputId.Replace('provider-', 'p-').Replace('constrained-network', 'cn'))"
try
{
    if ($Kind -eq 'family')
    {
        & (Join-Path $PSScriptRoot 'capture-family-comparison.ps1') -Family $Family -ExpectedCommit $ExpectedCommit `
            -OutputPath $output -Trials $Trials -Diagnostic:$Diagnostic
        return
    }
    $map = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'performance-variant-map.json') -Raw | ConvertFrom-Json
    $profileName = [string]$map.variants.$Os.$Variant
    if ($profileName -ceq 'crossOsProfile') { $profileName = [string]$map.crossOsProfile }
    if ($profileName -ceq 'unresolved' -or -not $profileName)
    { throw "Provider variant '$Variant' on $Os is unresolved (see $($map.proposal)); this leg fails closed and captures nothing." }
    $fixturePath = "artifacts/performance-fixtures/$fixtureRun"
    & (Join-Path $PSScriptRoot 'start-performance-fixture.ps1') -RunId $fixtureRun -Owner $Owner -CaptureProfile $profileName `
        -OutputPath $fixturePath -SourceCommit $ExpectedCommit
    $arguments = @{
        ExpectedCommit = $ExpectedCommit
        PostgreSqlImage = [string]$plan.fixture.postgreSqlImage
        OutputPath = $output
        Variant = $Variant
        FixturePath = (Join-Path $root $fixturePath)
        Trials = $Trials
        Concurrency = $Concurrency
        WarmupSeconds = $WarmupSeconds
        MeasurementSeconds = $MeasurementSeconds
        MaximumTotalSamples = [int]$plan.timing.maximumTotalSamples
        Diagnostic = $Diagnostic
    }
    if ($Features.Count -ne 0) { $arguments.Features = $Features }
    & (Join-Path $PSScriptRoot 'capture-provider-request-matrix.ps1') @arguments
}
finally
{
    if ($Kind -eq 'provider') { & (Join-Path $PSScriptRoot 'stop-performance-fixture.ps1') -RunId $fixtureRun -Owner $Owner }
    if ($ownsLock) { [IO.File]::Delete($lock) }
}
