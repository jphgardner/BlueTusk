param(
    [ValidateSet(15, 16, 17, 18)]
    [int[]] $Versions = @(15, 16, 17),
    [string] $OutputReport = 'docs/jobs/performance-reports/postgresql-15-17-tests.json',
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskOriginalConnection = $env:BLUETUSK_TEST_CONNECTION_STRING
$taskMatrix = @()
$taskRunId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '-' + [Guid]::NewGuid().ToString('N')
Push-Location -LiteralPath $taskRepository
try {
    if (!$NoBuild) {
        foreach ($taskFamily in 'Jobs', 'Workflows') {
            & dotnet build "tests/BlueTusk.$taskFamily.Tests/BlueTusk.$taskFamily.Tests.csproj" -c Release -nr:false --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Family compatibility build failed.' }
        }
    }

    foreach ($taskVersion in $Versions) {
        $taskName = 'bluetusk-ecosystem-pg' + $taskVersion
        $taskFixtureJson = & docker inspect $taskName
        if ($LASTEXITCODE -ne 0) { throw 'Owned fixture inspection failed.' }
        $taskFixture = ($taskFixtureJson | ConvertFrom-Json)[0]
        if (!$taskFixture.State.Running) { throw 'Owned fixture is not running.' }
        $taskEnvironment = @{}
        foreach ($taskEntry in $taskFixture.Config.Env) {
            $taskParts = $taskEntry.Split('=', 2)
            $taskEnvironment[$taskParts[0]] = $taskParts[1]
        }

        $taskPort = $taskFixture.NetworkSettings.Ports.'5432/tcp'[0].HostPort
        $env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port={0};Username={1};Password={2};Database={3};SSL Mode=Disable;Channel Binding=Disable' -f
            $taskPort, $taskEnvironment.POSTGRES_USER, $taskEnvironment.POSTGRES_PASSWORD, $taskEnvironment.POSTGRES_DB
        $taskServer = & docker exec $taskName psql -U $taskEnvironment.POSTGRES_USER -d $taskEnvironment.POSTGRES_DB -At -c 'SELECT version()'
        if ($LASTEXITCODE -ne 0) { throw 'Owned fixture metadata failed.' }
        $taskResults = 'artifacts/jobs-postgresql-matrix/' + $taskRunId + '/pg' + $taskVersion
        foreach ($taskFamily in 'Jobs', 'Workflows') {
            Write-Output "Running $taskFamily on PostgreSQL $taskVersion"
            & dotnet test "tests/BlueTusk.$taskFamily.Tests/BlueTusk.$taskFamily.Tests.csproj" -c Release --no-build -nr:false --nologo --logger "trx;LogFileName=$taskFamily.trx" --results-directory $taskResults
            if ($LASTEXITCODE -ne 0) { throw 'Family version test failed.' }
            $taskTrxPath = Join-Path $taskResults ($taskFamily + '.trx')
            [xml] $taskTrx = Get-Content -LiteralPath $taskTrxPath -Raw
            $taskCounters = $taskTrx.SelectSingleNode('//*[local-name()="Counters"]')
            if ([int]$taskCounters.passed -ne [int]$taskCounters.total -or [int]$taskCounters.failed -ne 0) {
                throw 'Skipped or failed version test evidence.'
            }

            $taskMatrix += @{ PostgreSqlVersion = $taskVersion; Server = $taskServer; Family = $taskFamily;
                Passed = [int]$taskCounters.passed; Total = [int]$taskCounters.total; TrxPath = $taskTrxPath }
        }
    }

    @{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); Configuration = 'Release'; Tests = $taskMatrix;
        Qualification = 'Owned local fixture compatibility evidence; production qualification remains open' } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputReport
}
finally {
    $env:BLUETUSK_TEST_CONNECTION_STRING = $taskOriginalConnection
    Pop-Location
}
