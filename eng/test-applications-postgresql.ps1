[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path $PSScriptRoot -Parent
$programme = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'postgresql19-programme.json') -Raw |
    ConvertFrom-Json
$image = [string]$programme.milestones[0].image
if ($image -notmatch '^postgres:19beta2-alpine@sha256:[a-f0-9]{64}$')
{
    throw 'The application integration harness requires the programme-pinned PostgreSQL 19 Beta 2 image.'
}
$containerName = "bluetusk-applications-test-$PID"
$password = "bluetusk-test-$PID"
try
{
    $containerId = (& docker run --detach --name $containerName `
        --env "POSTGRES_PASSWORD=$password" `
        --env POSTGRES_DB=bluetusk_app_test `
        --publish 127.0.0.1::5432 `
        $image).Trim()
    if ($LASTEXITCODE -ne 0 -or $containerId -notmatch '^[0-9a-f]{64}$')
    {
        throw 'Could not start the pinned PostgreSQL application test container.'
    }
    $ready = $false
    foreach ($attempt in 1..30)
    {
        & docker exec $containerName pg_isready -U postgres -d bluetusk_app_test *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Pinned PostgreSQL application test container did not become ready.' }
    $portText = (& docker port $containerName 5432/tcp).Trim()
    if ($portText -notmatch ':(\d+)$') { throw "Could not resolve test container port '$portText'." }
    $port = $Matches[1]
    $env:BLUETUSK_APPLICATION_TEST_CONNECTION = (
        "Host=127.0.0.1;Port=$port;Database=bluetusk_app_test;" +
        "Username=postgres;Password=$password;Pooling=false;SSL Mode=Disable")
    $env:BLUETUSK_APPLICATION_TEST_ALLOW_RESET = '1'
    & dotnet test (
        Join-Path $repositoryRoot 'applications/tests/BlueTusk.Applications.ArchitectureTests/BlueTusk.Applications.ArchitectureTests.csproj') `
        --configuration Release `
        --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Application PostgreSQL integration tests failed.' }
}
finally
{
    Remove-Item Env:BLUETUSK_APPLICATION_TEST_CONNECTION -ErrorAction SilentlyContinue
    Remove-Item Env:BLUETUSK_APPLICATION_TEST_ALLOW_RESET -ErrorAction SilentlyContinue
    & docker rm --force $containerName *> $null
}
