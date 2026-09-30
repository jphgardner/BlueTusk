param(
    [ValidateSet(15, 16, 17, 18)]
    [int[]] $Versions = @(15, 16, 17, 18),
    [string] $OutputReport = 'docs/jobs/performance-reports/postgresql-hosting-15-18-tests.json',
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskOriginalConnection = $env:BLUETUSK_TEST_CONNECTION_STRING
$taskProject = 'tests/BlueTusk.Workflows.DependencyInjection.Tests/BlueTusk.Workflows.DependencyInjection.Tests.csproj'
$taskMatrix = @()
$taskRunId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '-' + [Guid]::NewGuid().ToString('N')
Push-Location -LiteralPath $taskRepository
try {
    if (!$NoBuild) {
        & dotnet build $taskProject -c Release -nr:false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Hosting compatibility build failed.' }
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
        $taskResults = 'artifacts/jobs-hosting-matrix/' + $taskRunId + '/pg' + $taskVersion
        Write-Output "Running scoped host readiness and owned credential rotation on PostgreSQL $taskVersion"
        & dotnet test $taskProject -c Release --no-build -nr:false --nologo --logger 'trx;LogFileName=Hosting.trx' --results-directory $taskResults
        if ($LASTEXITCODE -ne 0) { throw 'Hosting version test failed.' }
        $taskTrxPath = Join-Path $taskResults 'Hosting.trx'
        [xml] $taskTrx = Get-Content -LiteralPath $taskTrxPath -Raw
        $taskCounters = $taskTrx.SelectSingleNode('//*[local-name()="Counters"]')
        if ([int]$taskCounters.passed -ne [int]$taskCounters.total -or [int]$taskCounters.failed -ne 0) {
            throw 'Skipped or failed hosting test evidence.'
        }
        $taskMatrix += @{ PostgreSqlVersion = $taskVersion; Server = $taskServer;
            Passed = [int]$taskCounters.passed; Total = [int]$taskCounters.total; TrxPath = $taskTrxPath }
    }
    $taskSources = @(Get-ChildItem -LiteralPath 'src/BlueTusk.Jobs.DependencyInjection', 'src/BlueTusk.Workflows.DependencyInjection', 'tests/BlueTusk.Workflows.DependencyInjection.Tests' -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } | Sort-Object FullName | ForEach-Object {
            @{ Path = [IO.Path]::GetRelativePath($taskRepository, $_.FullName).Replace('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    @{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); Configuration = 'Release'; Tests = $taskMatrix; Sources = $taskSources;
        Qualification = 'Owned local PostgreSQL compatibility, readiness authorization/deadline and unique-role credential replacement evidence; not failover or production qualification' } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputReport
}
finally {
    $env:BLUETUSK_TEST_CONNECTION_STRING = $taskOriginalConnection
    Pop-Location
}
