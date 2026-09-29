param(
    [ValidateRange(1, 3)] [int] $Repetitions = 3,
    [ValidateRange(1024, 65535)] [int] $PrimaryPort = 55625,
    [ValidateRange(1024, 65535)] [int] $StandbyPort = 55626,
    [string] $OutputReport = 'docs/jobs/performance-reports/physical-promotion-pg18.json',
    [string] $ArtifactDirectory,
    [switch] $NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskRepository = Split-Path -Parent $PSScriptRoot
$taskOwner = 'bluetusk.jobs.workflows.physical-recovery'
$taskCampaign = 'jobs-physical-' + [Guid]::NewGuid().ToString('N')
$taskImage = 'postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
$taskDocker = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$taskProject = 'tests/BlueTusk.Workflows.PhysicalRecoveryTests/BlueTusk.Workflows.PhysicalRecoveryTests.csproj'
$taskCaptureScript = Join-Path $PSScriptRoot 'capture-ecosystem-source.py'
$taskArtifacts = if ([string]::IsNullOrWhiteSpace($ArtifactDirectory)) {
    Join-Path $taskRepository ('artifacts/jobs-physical-recovery/' + $taskCampaign)
} elseif ([IO.Path]::IsPathRooted($ArtifactDirectory)) {
    [IO.Path]::GetFullPath($ArtifactDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $taskRepository $ArtifactDirectory))
}
$taskAllowedArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRepository 'artifacts')) +
    [IO.Path]::DirectorySeparatorChar
if (-not $taskArtifacts.StartsWith($taskAllowedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Physical recovery artifacts must remain under the repository artifacts directory.'
}
$taskOutput = if ([IO.Path]::IsPathRooted($OutputReport)) { [IO.Path]::GetFullPath($OutputReport) } else { [IO.Path]::GetFullPath((Join-Path $taskRepository $OutputReport)) }
$taskEnvironmentNames = @('PRIMARY', 'STANDBY', 'PRIMARY_CONTAINER', 'STANDBY_CONTAINER', 'FIXTURE', 'REPORT', 'DOCKER', 'IMAGE')
$taskPreviousEnvironment = @{}
$taskRuns = @()
foreach ($taskSuffix in $taskEnvironmentNames) {
    $taskKey = 'BLUETUSK_JOBS_RECOVERY_' + $taskSuffix
    $taskPreviousEnvironment[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey, [EnvironmentVariableTarget]::Process)
}

function InvokeOwnedDocker([string[]] $Arguments) {
    $taskResult = & $taskDocker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Owned Docker operation failed: ' + $Arguments[0]) }
    return $taskResult
}
function InvokeOwnedSql([string] $Container, [string] $Sql) {
    return InvokeOwnedDocker -Arguments @('exec', $Container, 'psql', '-U', 'postgres', '-d', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1', '-c', $Sql)
}
function RemoveOwnedResource([string] $Kind, [string] $Name, [string] $Fixture) {
    $taskLabelExpression = if ($Kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $taskObjectJson = & $taskDocker $Kind inspect $Name --format $taskLabelExpression 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $taskLabels = $taskObjectJson | ConvertFrom-Json
    if ($taskLabels.'bluetusk.owner' -ne $taskOwner -or $taskLabels.'bluetusk.fixture' -ne $Fixture) {
        throw 'Refusing cleanup of a resource outside this owned fixture.'
    }
    if ($Kind -eq 'container') { InvokeOwnedDocker -Arguments @('rm', '-f', $Name) | Out-Null }
    else { InvokeOwnedDocker -Arguments @($Kind, 'rm', $Name) | Out-Null }
}
function WaitOwnedReady([string] $Container) {
    $taskReady = $false
    for ($taskProbe = 0; $taskProbe -lt 100; $taskProbe++) {
        & $taskDocker exec $Container pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { $taskReady = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (!$taskReady) { throw 'Owned PostgreSQL startup deadline expired.' }
}

function CaptureCandidateSource([string] $Path) {
    & python $taskCaptureScript --output $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Candidate source capture failed.' }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function CaptureTestBinaries([string] $Path) {
    $taskBinaryDirectory = Join-Path $taskRepository 'tests/BlueTusk.Workflows.PhysicalRecoveryTests/bin/Release/net10.0'
    $taskFiles = @(Get-ChildItem -LiteralPath $taskBinaryDirectory -File |
        Where-Object { $_.Extension -in '.dll', '.exe', '.json' } | Sort-Object Name | ForEach-Object {
            @{ Name = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    foreach ($taskRequiredFile in 'BlueTusk.Workflows.PhysicalRecoveryTests.exe', 'BlueTusk.Workflows.PhysicalRecoveryTests.dll', 'BlueTusk.Workflows.PhysicalRecoveryTests.deps.json', 'BlueTusk.Workflows.PhysicalRecoveryTests.runtimeconfig.json', 'BlueTusk.Data.dll', 'BlueTusk.Client.dll', 'BlueTusk.Protocol.dll', 'BlueTusk.Transport.dll') {
        if ($taskRequiredFile -notin $taskFiles.Name) { throw 'The test executable/dependency manifest is incomplete; rebuild the dedicated project.' }
    }
    $taskManifest = @{ CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); Files = $taskFiles;
        Scope = 'All executable, DLL, dependency and runtime configuration files present in the dedicated test output; includes Data/provider dependencies. The installed .NET SDK/runtime is identified separately, not hashed by this manifest.' }
    $taskManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path
    return $taskManifest
}
function AssertTestBinariesUnchanged($Before, $After) {
    if ($Before.Files.Count -ne $After.Files.Count) { throw 'Test output files changed during physical recovery execution.' }
    foreach ($taskBeforeFile in $Before.Files) {
        $taskAfterFile = @($After.Files | Where-Object { $_.Name -eq $taskBeforeFile.Name })
        if ($taskAfterFile.Count -ne 1 -or $taskAfterFile[0].Bytes -ne $taskBeforeFile.Bytes -or $taskAfterFile[0].Sha256 -ne $taskBeforeFile.Sha256) {
            throw 'Test executable/dependency bytes changed during physical recovery execution.'
        }
    }
}

Push-Location -LiteralPath $taskRepository
try {
    if (Test-Path -LiteralPath $taskArtifacts) { throw 'Physical recovery requires a fresh artifact directory.' }
    if ($PrimaryPort -eq $StandbyPort) { throw 'Physical fixture ports must differ.' }
    foreach ($taskPort in $PrimaryPort, $StandbyPort) {
        $taskListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $taskPort)
        try { $taskListener.Start() } finally { $taskListener.Stop() }
    }
    New-Item -ItemType Directory -Path $taskArtifacts -Force | Out-Null
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskOutput) -Force | Out-Null
    if (!$NoBuild) {
        & dotnet build $taskProject -c Release -nr:false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Physical recovery build failed.' }
    }
    # Keep generated evidence under ignored artifacts until the global source
    # comparison passes so report generation cannot alter its own inputs.
    $taskSourceBefore = CaptureCandidateSource (Join-Path $taskArtifacts 'source-before.json')
    $taskScriptBefore = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    $taskRuntimeInfo = @(& dotnet --info)
    if ($LASTEXITCODE -ne 0) { throw 'Installed runtime identification failed.' }
    for ($taskRun = 1; $taskRun -le $Repetitions; $taskRun++) {
        $taskFixture = $taskCampaign + '-run' + $taskRun
        $taskPrimary = $taskFixture + '-primary'
        $taskStandby = $taskFixture + '-standby'
        $taskBackup = $taskFixture + '-basebackup'
        $taskNetwork = $taskFixture + '-network'
        $taskPrimaryVolume = $taskFixture + '-primary-data'
        $taskStandbyVolume = $taskFixture + '-standby-data'
        $taskRunDirectory = Join-Path $taskArtifacts ('run' + $taskRun)
        $taskReportPath = Join-Path $taskRunDirectory 'recovery.json'
        $taskSamplePath = Join-Path $taskRunDirectory 'fixture.jsonl'
        $taskPublishedSamplePath = $taskOutput + '.run' + $taskRun + '.fixture.jsonl'
        $taskMonitor = $null
        New-Item -ItemType Directory -Path $taskRunDirectory -Force | Out-Null
        try {
            Write-Output "Provisioning fresh synchronous physical fixture $taskRun of $Repetitions"
            InvokeOwnedDocker -Arguments @('network', 'create', '--label', "bluetusk.owner=$taskOwner", '--label', "bluetusk.fixture=$taskFixture", $taskNetwork) | Out-Null
            foreach ($taskVolume in $taskPrimaryVolume, $taskStandbyVolume) {
                InvokeOwnedDocker -Arguments @('volume', 'create', '--label', "bluetusk.owner=$taskOwner", '--label', "bluetusk.fixture=$taskFixture", $taskVolume) | Out-Null
            }
            InvokeOwnedDocker -Arguments @('run', '-d', '--name', $taskPrimary, '--label', "bluetusk.owner=$taskOwner", '--label', "bluetusk.fixture=$taskFixture", '--network', $taskNetwork,
                '-p', "127.0.0.1:${PrimaryPort}:5432", '-v', "${taskPrimaryVolume}:/var/lib/postgresql/data", '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', 'POSTGRES_PASSWORD=postgres', $taskImage, # ggignore
                'postgres', '-c', 'wal_level=replica', '-c', 'max_wal_senders=4', '-c', 'max_replication_slots=4') | Out-Null
            WaitOwnedReady $taskPrimary
            InvokeOwnedSql $taskPrimary 'CREATE DATABASE bluetusk_jobs_recovery' | Out-Null
            InvokeOwnedDocker -Arguments @('exec', $taskPrimary, 'sh', '-c', 'printf "\nhost replication postgres all scram-sha-256\n" >> "$PGDATA/pg_hba.conf"') | Out-Null
            InvokeOwnedSql $taskPrimary 'SELECT pg_reload_conf()' | Out-Null
            InvokeOwnedDocker -Arguments @('run', '--rm', '--name', $taskBackup, '--label', "bluetusk.owner=$taskOwner", '--label', "bluetusk.fixture=$taskFixture", '--network', $taskNetwork,
                '-v', "${taskStandbyVolume}:/var/lib/postgresql/data", '-e', 'PGPASSWORD=postgres', '--entrypoint', 'sh', $taskImage, '-c', # ggignore
                "mkdir -p /var/lib/postgresql/data/pgdata && chown postgres:postgres /var/lib/postgresql/data/pgdata && exec gosu postgres pg_basebackup -d 'host=$taskPrimary user=postgres application_name=bluetusk_jobs_recovery_standby' -D /var/lib/postgresql/data/pgdata -c fast -X stream -R -C -S bluetusk_jobs_recovery_physical") | Out-Null
            InvokeOwnedDocker -Arguments @('run', '-d', '--name', $taskStandby, '--label', "bluetusk.owner=$taskOwner", '--label', "bluetusk.fixture=$taskFixture", '--network', $taskNetwork,
                '-p', "127.0.0.1:${StandbyPort}:5432", '-v', "${taskStandbyVolume}:/var/lib/postgresql/data", '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', 'PGPASSWORD=postgres', $taskImage, # ggignore
                'postgres', '-c', 'wal_level=replica', '-c', 'max_wal_senders=4', '-c', 'max_replication_slots=4') | Out-Null
            WaitOwnedReady $taskStandby
            InvokeOwnedSql $taskPrimary "ALTER SYSTEM SET synchronous_standby_names='FIRST 1 (bluetusk_jobs_recovery_standby)'" | Out-Null
            InvokeOwnedSql $taskPrimary "ALTER SYSTEM SET synchronous_commit='remote_apply'" | Out-Null
            InvokeOwnedSql $taskPrimary 'SELECT pg_reload_conf()' | Out-Null
            $taskSynchronous = $false
            for ($taskProbe = 0; $taskProbe -lt 100; $taskProbe++) {
                if ((InvokeOwnedSql $taskPrimary "SELECT count(*) FROM pg_stat_replication WHERE application_name='bluetusk_jobs_recovery_standby' AND state='streaming' AND sync_state='sync'") -eq '1') { $taskSynchronous = $true; break }
                Start-Sleep -Milliseconds 200
            }
            if (!$taskSynchronous) { throw 'Owned physical standby did not become synchronous before its deadline.' }
            $env:BLUETUSK_JOBS_RECOVERY_PRIMARY = "Host=127.0.0.1;Port=$PrimaryPort;Username=postgres;Password=postgres;Database=bluetusk_jobs_recovery;SSL Mode=Disable;Channel Binding=Disable" # ggignore
            $env:BLUETUSK_JOBS_RECOVERY_STANDBY = "Host=127.0.0.1;Port=$StandbyPort;Username=postgres;Password=postgres;Database=bluetusk_jobs_recovery;SSL Mode=Disable;Channel Binding=Disable" # ggignore
            $env:BLUETUSK_JOBS_RECOVERY_PRIMARY_CONTAINER = $taskPrimary
            $env:BLUETUSK_JOBS_RECOVERY_STANDBY_CONTAINER = $taskStandby
            $env:BLUETUSK_JOBS_RECOVERY_FIXTURE = $taskFixture
            $env:BLUETUSK_JOBS_RECOVERY_REPORT = $taskReportPath
            $env:BLUETUSK_JOBS_RECOVERY_DOCKER = $taskDocker
            $env:BLUETUSK_JOBS_RECOVERY_IMAGE = $taskImage
            $taskMetadata = @{ ImageDigest = $taskImage; ImageInspect = (InvokeOwnedDocker -Arguments @('image', 'inspect', $taskImage) | ConvertFrom-Json)[0];
                BeforePrimarySettings = @(InvokeOwnedSql $taskPrimary "SELECT name||'='||setting FROM pg_settings WHERE name IN ('fsync','synchronous_commit','synchronous_standby_names','full_page_writes','wal_level','max_connections') ORDER BY name") }
            $taskMetadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskRunDirectory 'environment.json')
            $taskMonitor = Start-Job -ScriptBlock {
                param($taskDockerPath, $taskContainerNames, $taskSampleFile)
                while ($true) {
                    $taskStats = & $taskDockerPath stats --no-stream --format '{{json .}}' @taskContainerNames 2>$null
                    if ($LASTEXITCODE -eq 0) {
                        foreach ($taskLine in $taskStats) {
                            @{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); Stats = ($taskLine | ConvertFrom-Json) } | ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $taskSampleFile
                        }
                    }
                    Start-Sleep -Milliseconds 500
                }
            } -ArgumentList $taskDocker, @($taskPrimary, $taskStandby), $taskSamplePath
            $taskBinaryBefore = CaptureTestBinaries (Join-Path $taskRunDirectory 'binaries-before.json')
            & dotnet test $taskProject -c Release --no-build -nr:false --nologo --logger 'trx;LogFileName=PhysicalRecovery.trx' --results-directory $taskRunDirectory
            $taskTestExit = $LASTEXITCODE
            $taskBinaryAfter = CaptureTestBinaries (Join-Path $taskRunDirectory 'binaries-after.json')
            AssertTestBinariesUnchanged $taskBinaryBefore $taskBinaryAfter
            if ($taskTestExit -ne 0) { throw 'Physical promotion recovery test failed; partial logs and owned fixture metadata are retained.' }
            [xml] $taskTrx = Get-Content -LiteralPath (Join-Path $taskRunDirectory 'PhysicalRecovery.trx') -Raw
            $taskCounters = $taskTrx.SelectSingleNode('//*[local-name()="Counters"]')
            if ([int]$taskCounters.passed -ne 1 -or [int]$taskCounters.total -ne 1 -or [int]$taskCounters.failed -ne 0) { throw 'Physical campaign did not produce one passing, non-skipped test.' }
            $taskRuns += @{ Repetition = $taskRun; Report = (Get-Content -LiteralPath $taskReportPath -Raw | ConvertFrom-Json); Metadata = $taskMetadata;
                BinariesBefore = $taskBinaryBefore; BinariesAfter = $taskBinaryAfter; BinariesUnchanged = $true;
                TrxPath = [IO.Path]::GetRelativePath($taskRepository, (Join-Path $taskRunDirectory 'PhysicalRecovery.trx')).Replace('\', '/'); FixtureSamplePath = [IO.Path]::GetRelativePath($taskRepository, $taskPublishedSamplePath).Replace('\', '/') }
            Write-Output "Physical promotion repetition $taskRun passed and raw evidence retained"
        }
        finally {
            if ($null -ne $taskMonitor) { Stop-Job -Job $taskMonitor; Receive-Job -Job $taskMonitor | Out-Null; Remove-Job -Job $taskMonitor }
            foreach ($taskContainer in $taskStandby, $taskPrimary) {
                & $taskDocker logs $taskContainer *> (Join-Path $taskRunDirectory ($taskContainer + '.log'))
            }
            RemoveOwnedResource 'container' $taskStandby $taskFixture
            RemoveOwnedResource 'container' $taskBackup $taskFixture
            RemoveOwnedResource 'container' $taskPrimary $taskFixture
            RemoveOwnedResource 'volume' $taskStandbyVolume $taskFixture
            RemoveOwnedResource 'volume' $taskPrimaryVolume $taskFixture
            RemoveOwnedResource 'network' $taskNetwork $taskFixture
        }
    }
    $taskSourceAfter = CaptureCandidateSource (Join-Path $taskArtifacts 'source-after.json')
    if ($taskSourceBefore.sourceTreeSha256 -ne $taskSourceAfter.sourceTreeSha256 -or $taskSourceBefore.fileCount -ne $taskSourceAfter.fileCount -or $taskSourceBefore.sourceBytes -ne $taskSourceAfter.sourceBytes) {
        throw 'Global candidate source changed during this physical campaign; raw observations are retained but no stable-candidate report is published.'
    }
    if ($taskScriptBefore -ne (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash) { throw 'The recovery runner changed during execution.' }
    $taskSources = @(Get-ChildItem -LiteralPath src/BlueTusk.Jobs, src/BlueTusk.Workflows, src/BlueTusk.Jobs.DependencyInjection, src/BlueTusk.Workflows.DependencyInjection, tests/BlueTusk.Workflows.PhysicalRecoveryTests -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' } | Sort-Object FullName | ForEach-Object {
            @{ Path = [IO.Path]::GetRelativePath($taskRepository, $_.FullName).Replace('\', '/'); Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    foreach ($taskRun in $taskRuns) {
        $taskRetainedSample = Join-Path $taskArtifacts ('run' + $taskRun.Repetition + '/fixture.jsonl')
        if (!(Test-Path -LiteralPath $taskRetainedSample -PathType Leaf)) { throw 'The owned fixture monitor produced no retained samples.' }
        Copy-Item -LiteralPath $taskRetainedSample -Destination (Join-Path $taskRepository $taskRun.FixtureSamplePath) -Force
    }
    @{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); Campaign = $taskCampaign; Configuration = 'Release'; Repetitions = $Repetitions; Runs = $taskRuns;
        Sources = $taskSources; SourceProvenance = @{ Before = $taskSourceBefore; After = $taskSourceAfter; Unchanged = $true }; InstalledRuntime = $taskRuntimeInfo;
        ScriptSha256 = $taskScriptBefore; ProductionQualified = $false;
        Qualification = 'Owned local synchronous PG18 primary hard-stop and physical standby promotion, existing public multihost routing, bounded Jobs/Workflows state/effect recovery; not async-loss, split-brain, rejoin, server-upgrade or production approval' } |
        ConvertTo-Json -Depth 14 | Set-Content -LiteralPath $taskOutput
}
finally {
    foreach ($taskEntry in $taskPreviousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($taskEntry.Key, $taskEntry.Value, [EnvironmentVariableTarget]::Process)
    }
    Pop-Location
}
