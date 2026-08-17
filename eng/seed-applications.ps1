[CmdletBinding()]
param(
    [ValidateSet('All', 'OrderOperations', 'ServiceTopology', 'FraudInvestigation')]
    [string] $Application = 'All',
    [switch] $Apply
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($null -eq (Get-Command psql -ErrorAction SilentlyContinue))
{
    throw 'PostgreSQL psql is required for the RC seed tool.'
}
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$targets = [ordered]@{
    OrderOperations = [pscustomobject]@{
        Environment = 'BLUETUSK_ORDERS_SEED_CONNECTION'
        File = 'order-operations.sql'
    }
    ServiceTopology = [pscustomobject]@{
        Environment = 'BLUETUSK_TOPOLOGY_SEED_CONNECTION'
        File = 'service-topology.sql'
    }
    FraudInvestigation = [pscustomobject]@{
        Environment = 'BLUETUSK_FRAUD_SEED_CONNECTION'
        File = 'fraud-investigation.sql'
    }
}
if ($Application -ne 'All')
{
    $targets = [ordered]@{ $Application = $targets[$Application] }
}

foreach ($target in $targets.GetEnumerator())
{
    $connection = [Environment]::GetEnvironmentVariable($target.Value.Environment)
    if ([string]::IsNullOrWhiteSpace($connection))
    {
        throw "Set $($target.Value.Environment) to a migration-role libpq connection string."
    }
    $serverVersion = (& psql --dbname $connection --tuples-only --no-align `
        --set ON_ERROR_STOP=1 --command 'SHOW server_version').Trim()
    if ($LASTEXITCODE -ne 0 -or $serverVersion -notmatch '^19beta3(?:\s|$)')
    {
        throw "$($target.Key) is not connected to the PostgreSQL 19 Beta 3 RC environment."
    }
}

if (-not $Apply)
{
    Write-Output "Seed preflight passed for $($targets.Count) RC application database(s); no data changed."
    return
}
if ([Environment]::GetEnvironmentVariable('BLUETUSK_RC_SEED_CONFIRM') -ne 'rc-staging-only')
{
    throw 'Set BLUETUSK_RC_SEED_CONFIRM=rc-staging-only before applying deterministic RC seed data.'
}
foreach ($target in $targets.GetEnumerator())
{
    $connection = [Environment]::GetEnvironmentVariable($target.Value.Environment)
    $file = Join-Path $repositoryRoot "applications/seed/$($target.Value.File)"
    & psql --dbname $connection --set ON_ERROR_STOP=1 --file $file
    if ($LASTEXITCODE -ne 0) { throw "Seeding $($target.Key) failed and was rolled back." }
}
Write-Output "Applied idempotent pilot seed data to $($targets.Count) RC application database(s)."
