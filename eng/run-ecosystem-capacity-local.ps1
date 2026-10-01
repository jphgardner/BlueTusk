[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCommit,
    [Parameter(Mandatory)]
    [string] $EvidenceRoot,
    [ValidateSet('Jobs', 'All')]
    [string] $Product = 'Jobs'
)

# Runs the unchanged Jobs or combined ecosystem capacity gate on the local reference host. The PostgreSQL 15
# fixture mirrors the manual workflow but is owned by a fresh local campaign UUID, never a GitHub run identity,
# and the Jobs environment records FixtureRunKind=local.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) { throw 'Inside GitHub Actions use the manual capacity workflow.' }
$gate = if ($Product -eq 'Jobs') {
    @{ Name = 'bluetusk-jobs-release-pg15'; Owner = 'jobs-release-capacity'; Port = 55416; Database = 'bluetusk_jobs_release'
       Budget = 'eng/jobs-release-capacity-budgets.json'; Verify = { param($Mode) ./eng/verify-jobs-release-capacity.ps1 -Mode $Mode -ExpectedCommit $ExpectedCommit -EvidenceRoot $EvidenceRoot } }
} else {
    @{ Name = 'bluetusk-ecosystem-pg15'; Owner = 'ecosystem-performance'; Port = 55415; Database = 'bluetusk_ecosystem'
       Budget = 'eng/ecosystem-performance-budgets.json'; Verify = { param($Mode) ./eng/verify-ecosystem-performance.ps1 -Mode $Mode -ExpectedCommit $ExpectedCommit -EvidenceRoot $EvidenceRoot -Product All } }
}
$name = $gate.Name
$campaign = [guid]::NewGuid().ToString()
$started = $false
Push-Location -LiteralPath (Split-Path $PSScriptRoot -Parent)
try {
    & $gate.Verify -Mode Preflight
    $existing = @(docker container ls -a --format '{{.Names}}' --filter "name=^/$name$")
    if ($LASTEXITCODE -ne 0 -or $existing.Count -ne 0) { throw 'The dedicated capacity fixture name is already in use or Docker is unavailable.' }
    $budgets = Get-Content $gate.Budget -Raw | ConvertFrom-Json
    $fixturePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
    docker run -d --name $name --label "bluetusk.owner=$($gate.Owner)" --label "bluetusk.run=$campaign" --label bluetusk.run-kind=local `
        --cpus 4 --memory 2g -p "127.0.0.1:$($gate.Port):5432" `
        -e POSTGRES_USER=postgres -e "POSTGRES_PASSWORD=$fixturePassword" -e "POSTGRES_DB=$($gate.Database)" $budgets.postgreSql15Image | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Dedicated capacity fixture did not start.' }
    $started = $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 150; $attempt++) {
        docker exec $name pg_isready -h 127.0.0.1 -U postgres -d $gate.Database *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Dedicated capacity fixture did not become ready.' }
    $env:BLUETUSK_LOCAL_CAMPAIGN_ID = $campaign
    & $gate.Verify -Mode Run
    & $gate.Verify -Mode Verify
    Write-Output "Local $Product capacity campaign $campaign passed; production and release qualification remain false."
}
finally {
    Remove-Item Env:BLUETUSK_LOCAL_CAMPAIGN_ID -ErrorAction SilentlyContinue
    if ($started) {
        $labels = docker inspect $name --format '{{json .Config.Labels}}' | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $labels.'bluetusk.owner' -ne $gate.Owner -or $labels.'bluetusk.run' -ne $campaign) {
            throw 'Refusing to remove a fixture not owned by this local campaign.'
        }
        docker rm -f $name | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to remove this local campaign capacity fixture.' }
    }
    Pop-Location
}
