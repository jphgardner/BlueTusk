param(
    [ValidateSet(15, 16, 17, 18)]
    [int] $Version = 15,
    [ValidateRange(5, 3600)]
    [int] $Seconds = 600,
    [string] $Output = 'docs/jobs/performance-reports/local-storage-pg15-600s.json',
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskOriginalConnection = $env:BLUETUSK_TEST_CONNECTION_STRING
$taskObserver = $null
Push-Location -LiteralPath $taskRepository
try {
    $taskProject = 'benchmarks/BlueTusk.Workflows.LoadHarness/BlueTusk.Workflows.LoadHarness.csproj'
    if (!$NoBuild) {
        & dotnet build $taskProject -c Release -nr:false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Storage harness build failed.' }
    }
    $taskContainer = 'bluetusk-ecosystem-pg' + $Version
    $taskInspection = & docker inspect $taskContainer
    if ($LASTEXITCODE -ne 0) { throw 'Owned fixture inspection failed.' }
    $taskFixture = ($taskInspection | ConvertFrom-Json)[0]
    if (!$taskFixture.State.Running) { throw 'Owned fixture is not running.' }
    $taskEnvironment = @{}
    foreach ($taskEntry in $taskFixture.Config.Env) {
        $taskParts = $taskEntry.Split('=', 2)
        $taskEnvironment[$taskParts[0]] = $taskParts[1]
    }
    $taskPort = $taskFixture.NetworkSettings.Ports.'5432/tcp'[0].HostPort
    $env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port={0};Username={1};Password={2};Database={3};SSL Mode=Disable;Channel Binding=Disable' -f
        $taskPort, $taskEnvironment.POSTGRES_USER, $taskEnvironment.POSTGRES_PASSWORD, $taskEnvironment.POSTGRES_DB
    $taskOutputPath = [IO.Path]::GetFullPath((Join-Path $taskRepository $Output))
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $taskOutputPath) -Force
    $taskSettingsQuery = @'
SELECT json_build_object('server', version(), 'settings',
    (SELECT json_object_agg(name, json_build_object('setting',setting,'unit',unit)) FROM pg_settings
     WHERE name IN ('fsync','synchronous_commit','autovacuum','autovacuum_naptime','autovacuum_vacuum_threshold',
       'autovacuum_vacuum_scale_factor','checkpoint_timeout','max_wal_size','min_wal_size','shared_buffers',
       'max_connections','work_mem','wal_level','track_io_timing')))
'@
    $taskServerJson = & docker exec $taskContainer psql -U $taskEnvironment.POSTGRES_USER -d $taskEnvironment.POSTGRES_DB -At -c $taskSettingsQuery
    if ($LASTEXITCODE -ne 0) { throw 'Owned fixture settings observation failed.' }
    @{ StartedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); Container = $taskContainer; ImageId = $taskFixture.Image;
       PostgreSql = ($taskServerJson | ConvertFrom-Json); PerProductDurationSeconds = $Seconds;
       FixtureCpuScope = 'Docker CPU percentage includes all processes in the dedicated owned fixture; block/network counters are container-scoped.' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($taskOutputPath + '.environment.json')
    $taskStatsPath = $taskOutputPath + '.fixture.jsonl'
    [IO.File]::WriteAllText($taskStatsPath, '')
    $taskObserver = Start-Job -ArgumentList $taskContainer, $taskStatsPath, (2 * $Seconds + 120) -ScriptBlock {
        param($container, $path, $maximumSeconds)
        $duration = [Diagnostics.Stopwatch]::StartNew()
        while ($duration.Elapsed.TotalSeconds -lt $maximumSeconds) {
            $raw = & docker stats --no-stream --format '{{json .}}' $container
            if ($LASTEXITCODE -ne 0) { throw 'Owned fixture CPU observation failed.' }
            $data = $raw | ConvertFrom-Json
            $sample = @{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); ElapsedSeconds = $duration.Elapsed.TotalSeconds;
                CpuPercentage = $data.CPUPerc; MemoryUsage = $data.MemUsage; MemoryPercentage = $data.MemPerc;
                BlockIo = $data.BlockIO; NetworkIo = $data.NetIO; Pids = $data.PIDs }
            [IO.File]::AppendAllText($path, ($sample | ConvertTo-Json -Compress) + [Environment]::NewLine)
            Start-Sleep -Seconds 2
        }
    }
    & dotnet run --project $taskProject -c Release --no-build -- storage --seconds $Seconds --output $taskOutputPath
    if ($LASTEXITCODE -ne 0) { throw 'Storage or recovery verification failed.' }
    if ($taskObserver.State -eq 'Failed') { Receive-Job -Job $taskObserver -ErrorAction Stop; throw 'Fixture observer failed.' }
}
finally {
    if ($null -ne $taskObserver) {
        Stop-Job -Job $taskObserver
        Receive-Job -Job $taskObserver -ErrorAction Continue
        Remove-Job -Job $taskObserver
    }
    $env:BLUETUSK_TEST_CONNECTION_STRING = $taskOriginalConnection
    Pop-Location
}
