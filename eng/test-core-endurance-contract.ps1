[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$verifier = Join-Path $PSScriptRoot 'verify-core-endurance-contract.ps1'
$configuration = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'v1.2-candidate-readiness.json') -Raw
$scratch = Join-Path ([IO.Path]::GetTempPath()) "bluetusk-core-contract-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
try
{
    & $verifier | Out-Null
    $mutations = @(
        {param($c) $c.minimums.streamsEnduranceHours = 71},
        {param($c) $c.minimums.syncEnduranceHours = 23},
        {param($c) $c.minimums.liveAndControlPlaneEnduranceHours = 23},
        {param($c) $c.minimums.streamsMinimumTransactions = 99999},
        {param($c) $c.minimums.syncMinimumCycles = 99},
        {param($c) $c.minimums.liveAndControlPlaneMinimumCycles = 100},
        {param($c) $c.coreFamilies += 'ContinuousGraph'},
        {param($c) $c.endurancePostgreSqlImage = "postgres:19beta3-alpine@sha256:$('a' * 64)"},
        {param($c) $c.endurancePostgreSqlImage = 'postgres:18-alpine'},
        {param($c) $c.publicationEnabled = $true},
        {param($c) $c.status = 'approved'}
    )
    foreach ($mutation in $mutations)
    {
        $copy = $configuration | ConvertFrom-Json
        & $mutation $copy
        $path = Join-Path $scratch 'contract.json'
        $copy | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $rejected = $false
        try { & $verifier -ConfigurationPath $path | Out-Null } catch { $rejected = $true }
        if (-not $rejected) { throw 'A weakened core evidence producer contract was accepted.' }
    }
    Write-Output 'Core evidence producer contract self-test passed: independent wiring and 11 rejected scope, image, duration, minimum or publication changes.'
}
finally
{
    if ((Split-Path $scratch -Parent) -ne [IO.Path]::GetTempPath().TrimEnd([IO.Path]::DirectorySeparatorChar))
    { throw 'Refusing unexpected self-test cleanup target.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
