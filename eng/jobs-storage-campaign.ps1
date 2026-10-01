param(
    [ValidateSet(15, 16, 17, 18)]
    [int] $Version = 15,
    [ValidateRange(5, 3600)]
    [int] $Seconds = 600,
    [ValidateSet('Repeated', 'SeededHighEntropy')]
    [string] $PayloadMode = 'SeededHighEntropy',
    [ValidateSet('Combined', 'Jobs')]
    [string] $Product = 'Combined',
    [ValidatePattern('^bluetusk-(?:ecosystem-pg(?:15|16|17|18)|jobs-release-pg15)$')]
    [string] $FixtureName,
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
    if ([string]::IsNullOrWhiteSpace($FixtureName)) { $FixtureName = 'bluetusk-ecosystem-pg' + $Version }
    if ($Product -eq 'Jobs' -and ($Version -ne 15 -or $FixtureName -cne 'bluetusk-jobs-release-pg15')) {
        throw 'Jobs release capacity requires its dedicated PostgreSQL 15 fixture.'
    }
    $taskContainer = $FixtureName
    $taskInspection = & docker inspect $taskContainer
    if ($LASTEXITCODE -ne 0) { throw 'Owned fixture inspection failed.' }
    $taskFixture = ($taskInspection | ConvertFrom-Json)[0]
    if (!$taskFixture.State.Running) { throw 'Owned fixture is not running.' }
    # A GitHub workflow run owns the fixture inside Actions; outside Actions only a local campaign UUID may own it.
    $taskRunKind = ''
    if (![string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)) { $taskRunKind = 'github' }
    elseif ([string]$env:BLUETUSK_LOCAL_CAMPAIGN_ID -cmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$') { $taskRunKind = 'local' }
    $taskRunId = if ($taskRunKind -eq 'github') { $env:GITHUB_RUN_ID } else { $env:BLUETUSK_LOCAL_CAMPAIGN_ID }
    $taskRunKindLabel = $taskFixture.Config.Labels.PSObject.Properties['bluetusk.run-kind']
    if ($Product -eq 'Jobs' -and
        ($taskFixture.Config.Labels.'bluetusk.owner' -cne 'jobs-release-capacity' -or
         $taskRunKind -eq '' -or
         $taskFixture.Config.Labels.'bluetusk.run' -cne $taskRunId -or
         ($taskRunKind -eq 'local') -ne ($null -ne $taskRunKindLabel -and $taskRunKindLabel.Value -ceq 'local'))) {
        throw 'Jobs release capacity fixture is not owned by this workflow run or local campaign.'
    }
    $taskImageReference = ''
    $taskRepoDigests = @()
    if ($Product -eq 'Jobs') {
        $taskBudget = Get-Content (Join-Path $PSScriptRoot 'jobs-release-capacity-budgets.json') -Raw | ConvertFrom-Json
        $taskImageReference = [string]$taskBudget.postgreSql15Image
        $taskImageInspection = & docker image inspect $taskImageReference
        if ($LASTEXITCODE -ne 0) { throw 'Pinned Jobs fixture image inspection failed.' }
        $taskImage = ($taskImageInspection | ConvertFrom-Json)[0]
        $taskRepoDigests = @($taskImage.RepoDigests)
        $taskExpectedDigest = ($taskImageReference -split '@')[-1]
        if ($taskImage.Id -cne $taskFixture.Image -or
            $taskExpectedDigest -notmatch '^sha256:[0-9a-f]{64}$' -or
            @($taskRepoDigests | Where-Object { ([string]$_).EndsWith("@$taskExpectedDigest", [StringComparison]::Ordinal) }).Count -eq 0) {
            throw 'Jobs fixture image does not match the pinned registry digest.'
        }
    }
    $taskEnvironment = @{}
    foreach ($taskEntry in $taskFixture.Config.Env) {
        $taskParts = $taskEntry.Split('=', 2)
        $taskEnvironment[$taskParts[0]] = $taskParts[1]
    }
    $taskPort = $taskFixture.NetworkSettings.Ports.'5432/tcp'[0].HostPort
    $env:BLUETUSK_TEST_CONNECTION_STRING = 'Host=127.0.0.1;Port={0};Username={1};Password={2};Database={3};SSL Mode=Disable;Channel Binding=Disable' -f
        $taskPort, $taskEnvironment.POSTGRES_USER, $taskEnvironment.POSTGRES_PASSWORD, $taskEnvironment.POSTGRES_DB
    $taskOutputPath = if ([IO.Path]::IsPathRooted($Output)) {
        [IO.Path]::GetFullPath($Output)
    } else {
        [IO.Path]::GetFullPath((Join-Path $taskRepository $Output))
    }
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
       ProductScope = $Product; FixtureOwner = $taskFixture.Config.Labels.'bluetusk.owner';
       FixtureRunId = $taskFixture.Config.Labels.'bluetusk.run'; FixtureRunKind = $taskRunKind;
       ImageReference = $taskImageReference; ImageRepoDigests = $taskRepoDigests;
       PostgreSql = ($taskServerJson | ConvertFrom-Json); PerProductDurationSeconds = $Seconds;
       PayloadMode = $PayloadMode;
       FixtureCpuScope = 'Docker CPU percentage includes all processes in the dedicated owned fixture; block/network counters are container-scoped.' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($taskOutputPath + '.environment.json')
    $taskStatsPath = $taskOutputPath + '.fixture.jsonl'
    [IO.File]::WriteAllText($taskStatsPath, '')
    $taskProducts = if ($Product -eq 'Jobs') { 1 } else { 2 }
    $taskObservationGrace = if ($Product -eq 'Jobs') { 600 } else { 120 }
    $taskObserver = Start-Job -ArgumentList $taskContainer, $taskStatsPath, ($taskProducts * $Seconds + $taskObservationGrace) -ScriptBlock {
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
    $taskProfile = if ($Product -eq 'Jobs') { 'storage-jobs' } else { 'storage' }
    & dotnet run --project $taskProject -c Release --no-build -- $taskProfile --seconds $Seconds --output $taskOutputPath --payload-mode $PayloadMode
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
