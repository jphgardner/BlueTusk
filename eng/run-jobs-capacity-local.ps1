[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCommit,
    [Parameter(Mandatory)]
    [string] $EvidenceRoot
)

# Runs the unchanged Jobs release capacity gate on the local reference host. The fixture is owned by a
# fresh local campaign UUID, never a GitHub run identity, and the evidence records FixtureRunKind=local.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) { throw 'Inside GitHub Actions use the Jobs release capacity workflow.' }
$name = 'bluetusk-jobs-release-pg15'
$campaign = [guid]::NewGuid().ToString()
$started = $false
Push-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
try {
    ./eng/verify-jobs-release-capacity.ps1 -Mode Preflight -ExpectedCommit $ExpectedCommit -EvidenceRoot $EvidenceRoot
    $existing = @(docker container ls -a --format '{{.Names}}' --filter "name=^/$name$")
    if ($LASTEXITCODE -ne 0 -or $existing.Count -ne 0) { throw 'The dedicated Jobs fixture name is already in use or Docker is unavailable.' }
    $budgets = Get-Content eng/jobs-release-capacity-budgets.json -Raw | ConvertFrom-Json
    $fixturePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    docker run -d --name $name --label bluetusk.owner=jobs-release-capacity --label "bluetusk.run=$campaign" --label bluetusk.run-kind=local `
        --cpus 4 --memory 2g -p 127.0.0.1:55416:5432 `
        -e POSTGRES_USER=postgres -e "POSTGRES_PASSWORD=$fixturePassword" -e POSTGRES_DB=bluetusk_jobs_release $budgets.postgreSql15Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Dedicated Jobs fixture did not start.' }
    $started = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        docker exec $name pg_isready -h 127.0.0.1 -U postgres -d bluetusk_jobs_release *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Dedicated Jobs fixture did not become ready.' }
    $env:BLUETUSK_LOCAL_CAMPAIGN_ID = $campaign
    ./eng/verify-jobs-release-capacity.ps1 -Mode Run -ExpectedCommit $ExpectedCommit -EvidenceRoot $EvidenceRoot
    ./eng/verify-jobs-release-capacity.ps1 -Mode Verify -ExpectedCommit $ExpectedCommit -EvidenceRoot $EvidenceRoot
    Write-Output "Local Jobs campaign $campaign passed; production and release qualification remain false."
}
finally {
    Remove-Item Env:BLUETUSK_LOCAL_CAMPAIGN_ID -ErrorAction SilentlyContinue
    if ($started) {
        $labels = docker inspect $name --format '{{json .Config.Labels}}' | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $labels.'bluetusk.owner' -ne 'jobs-release-capacity' -or $labels.'bluetusk.run' -ne $campaign) {
            throw 'Refusing to remove a fixture not owned by this local campaign.'
        }
        docker rm -f $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to remove this local campaign Jobs fixture.' }
    }
    Pop-Location
}
