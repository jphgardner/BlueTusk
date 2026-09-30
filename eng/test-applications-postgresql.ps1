[CmdletBinding()]
param(
    [ValidateSet('Core', 'ContinuousGraphPreview')]
    [string] $ReleaseTrack = 'Core',
    [string] $OutputRoot,
    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$plan = & (Join-Path $PSScriptRoot 'get-application-postgresql-test-plan.ps1') -ReleaseTrack $ReleaseTrack
if ($ValidateOnly) { $plan; return }

$captureId = [Guid]::NewGuid().ToString('N')
if ([string]::IsNullOrWhiteSpace($OutputRoot))
{
    $OutputRoot = "artifacts/application-postgresql-$($ReleaseTrack.ToLowerInvariant())-$captureId"
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $outputPath.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $outputPath))
{
    throw 'Application test output must be a new directory beneath repository artifacts. Existing evidence is never overwritten.'
}
$ancestor = $outputPath
while (-not [string]::IsNullOrWhiteSpace($ancestor))
{
    if (Test-Path -LiteralPath $ancestor)
    {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        { throw 'Application evidence output must not traverse a symbolic link or junction.' }
    }
    $ancestor = Split-Path $ancestor -Parent
}
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -cnotmatch '^[0-9a-f]{40}$')
{ throw 'Could not resolve the application test source commit.' }
$sourceStatus = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) { throw 'Could not determine source cleanliness.' }
$null = New-Item -ItemType Directory -Path $outputPath
$containerName = "bluetusk-applications-test-$captureId"
$password = "bluetusk-test-$captureId"
$containerCreated = $false
$previousConnection = $env:BLUETUSK_APPLICATION_TEST_CONNECTION
$previousReset = $env:BLUETUSK_APPLICATION_TEST_ALLOW_RESET
$startedAt = [DateTimeOffset]::UtcNow.ToString('O')
try
{
    $containerId = (& docker run --detach --name $containerName `
        --env "POSTGRES_PASSWORD=$password" `
        --env POSTGRES_DB=bluetusk_app_test `
        --publish 127.0.0.1::5432 `
        $plan.image).Trim()
    if ($LASTEXITCODE -ne 0 -or $containerId -notmatch '^[0-9a-f]{64}$')
    {
        throw 'Could not start the pinned PostgreSQL application test container.'
    }
    $containerCreated = $true
    $ready = $false
    foreach ($attempt in 1..30)
    {
        & docker exec $containerName pg_isready -U postgres -d bluetusk_app_test *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Pinned PostgreSQL application test container did not become ready.' }
    $portText = (& docker port $containerName 5432/tcp).Trim()
    if ($LASTEXITCODE -ne 0 -or $portText -notmatch '^127\.0\.0\.1:(\d+)$')
    { throw "Could not resolve the loopback test container port '$portText'." }
    $port = $Matches[1]
    $env:BLUETUSK_APPLICATION_TEST_CONNECTION = (
        "Host=127.0.0.1;Port=$port;Database=bluetusk_app_test;" +
        "Username=postgres;Password=$password;Pooling=false;SSL Mode=Disable")
    $env:BLUETUSK_APPLICATION_TEST_ALLOW_RESET = '1'
    & dotnet test (
        Join-Path $repositoryRoot 'applications/tests/BlueTusk.Applications.ArchitectureTests/BlueTusk.Applications.ArchitectureTests.csproj') `
        --configuration Release `
        --no-restore --filter $plan.filter `
        --logger 'trx;LogFileName=tests.trx' --results-directory $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Application PostgreSQL integration tests failed.' }
    $trxPath = Join-Path $outputPath 'tests.trx'
    & (Join-Path $PSScriptRoot 'verify-application-postgresql-results.ps1') `
        -TrxPath $trxPath -ReleaseTrack $ReleaseTrack
    [ordered]@{
        schemaVersion = 1
        qualification = 'local-integration-only'
        sourceCommit = $sourceCommit
        sourceClean = $sourceStatus.Count -eq 0
        releaseTrack = $ReleaseTrack
        image = [string]$plan.image
        milestone = [string]$plan.milestone
        startedAt = $startedAt
        completedAt = [DateTimeOffset]::UtcNow.ToString('O')
        expectedTests = @($plan.expectedTests)
        trxPath = 'tests.trx'
        trxSha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash.ToLowerInvariant()
        stableGraphQualification = $false
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputPath 'report.json') -Encoding utf8NoBOM
    Write-Output "Retained $ReleaseTrack integration evidence in '$outputPath'; package provenance and release qualification are separate gates."
}
finally
{
    $env:BLUETUSK_APPLICATION_TEST_CONNECTION = $previousConnection
    $env:BLUETUSK_APPLICATION_TEST_ALLOW_RESET = $previousReset
    if ($containerCreated) { & docker rm --force $containerName *> $null }
}
