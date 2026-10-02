param(
    [ValidateSet('quick','matrix','soak','capacity','promotion')][string]$Profile = 'quick',
    [int]$Seconds = 0,
    [int]$PrimaryPort = 55718,
    [int]$StandbyPort = 55719,
    [int]$Repetitions = 0,
    [int]$DockerCpus = 4,
    [int]$DockerMemoryMiB = 2048,
    [ValidateSet(128,4096,65536)][int]$SmokePayloadBytes = 128,
    [string]$OutputDirectory,
    [switch]$NoBuild,
    [switch]$Diagnostics
)
$ErrorActionPreference = 'Stop'
if ($Seconds -eq 0) { $Seconds = if ($Profile -in @('soak','capacity')) { 600 } else { 5 } }
if ($Seconds -lt 1 -or $Seconds -gt 3600) { throw 'Seconds must be between 1 and 3600.' }
if ($Repetitions -eq 0) { $Repetitions = if ($Profile -eq 'promotion') { 3 } else { 1 } }
if ($Repetitions -lt 1 -or $Repetitions -gt 10) { throw 'Repetitions must be between 1 and 10.' }
if ($DockerCpus -lt 1 -or $DockerCpus -gt 64 -or $DockerMemoryMiB -lt 512 -or $DockerMemoryMiB -gt 65536) { throw 'Docker CPU/memory bounds are invalid.' }
if ($PrimaryPort -eq $StandbyPort -or $PrimaryPort -lt 1024 -or $PrimaryPort -gt 65535 -or $StandbyPort -lt 1024 -or $StandbyPort -gt 65535) { throw 'Distinct unprivileged fixture ports are required.' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$dockerCommand = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$pythonCommand = (Get-Command python -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$owner = 'bluetusk.projections.load'
$image = 'postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
$campaign = 'projections-load-' + [guid]::NewGuid().ToString('N').Substring(0,16)
$artifactDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts/projections-load/$campaign" } else { [System.IO.Path]::GetFullPath($OutputDirectory) }
$environmentNames = @('BLUETUSK_PROJECTIONS_LOAD_CONNECTION_STRING','BLUETUSK_PROJECTIONS_LOAD_STANDBY','BLUETUSK_PROJECTIONS_LOAD_PRIMARY_CONTAINER','BLUETUSK_PROJECTIONS_LOAD_STANDBY_CONTAINER','BLUETUSK_PROJECTIONS_LOAD_FIXTURE','BLUETUSK_PROJECTIONS_LOAD_DOCKER','BLUETUSK_PROJECTIONS_LOAD_SERVER_SAMPLES')
# Cheap WAL I/O timing feeds pg_stat_io; checkpoint logging is explicit although PostgreSQL 18 enables it by default.
$serverSettings = @('-c','track_wal_io_timing=on','-c','log_checkpoints=on')
# -Diagnostics also logs every autovacuum, samples server wait events/I/O counters from a separate session
# and retains this exact fixture's raw server log beside the run. It is not used by the verifier.
if ($Diagnostics) { $serverSettings += @('-c','log_autovacuum_min_duration=0') }
$priorEnvironment = @{}
foreach ($name in $environmentNames) { $priorEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name,[System.EnvironmentVariableTarget]::Process) }
function InvokeDocker([string[]]$Arguments) {
    $result = & $dockerCommand @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Owned Docker operation failed: $($Arguments[0]). Sensitive output omitted." }
    return $result
}
function Query([string]$Container,[string]$Sql) {
    return InvokeDocker -Arguments @('exec',$Container,'psql','-U','postgres','-d','postgres','-At','-v','ON_ERROR_STOP=1','-c',$Sql)
}
function RemoveOwned([string]$Kind,[string]$Name) {
    $labels = & $dockerCommand $Kind inspect $Name --format '{{json .Labels}}' 2>$null
    if ($Kind -eq 'container') { $labels = & $dockerCommand inspect $Name --format '{{json .Config.Labels}}' 2>$null }
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    if ($actual.'bluetusk.owner' -ne $owner -or $actual.'bluetusk.fixture' -ne $fixture) { throw "Refusing cleanup outside owned fixture: $Name" }
    if ($Kind -eq 'container') { InvokeDocker -Arguments @('rm','-f',$Name) | Out-Null }
    else { InvokeDocker -Arguments @($Kind,'rm',$Name) | Out-Null }
}
function WaitReady([string]$Container) {
    for ($attempt=0; $attempt -lt 150; $attempt++) {
        # The entrypoint's temporary initialization server accepts Unix-socket connections, then
        # stops before PID 1 starts the final PostgreSQL server. Only the final server accepts TCP.
        & $dockerCommand exec $Container pg_isready -h 127.0.0.1 -U postgres *> $null
        if ($LASTEXITCODE -eq 0) {
            & $dockerCommand exec $Container psql -U postgres -d postgres -At -v ON_ERROR_STOP=1 -c 'SELECT 1' *> $null
            if ($LASTEXITCODE -eq 0) { return }
        }
        Start-Sleep -Milliseconds 200
    }
    throw 'Owned PostgreSQL fixture did not become ready.'
}
function SaveServerLog([string]$Container,[string]$Fixture,[string]$Path) {
    # -Diagnostics only: the raw PostgreSQL log (checkpoint/autovacuum timing) of this exact labelled
    # disposable fixture is retained beside its run for correlation with the delivery trace. Docker's
    # receive timestamps are prefixed. Never used by the verifier; default runs persist no raw logs.
    $labels = & $dockerCommand inspect $Container --format '{{json .Config.Labels}}' 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    if ($actual.'bluetusk.owner' -ne $owner -or $actual.'bluetusk.fixture' -ne $Fixture) { throw "Refusing log capture outside owned fixture: $Container" }
    Start-Process -FilePath $dockerCommand -ArgumentList @('logs','--timestamps',$Container) -NoNewWindow -Wait -RedirectStandardError $Path -RedirectStandardOutput "$Path.stdout"
}
function SaveFailureState([string]$Container,[string]$Fixture,[string]$Path) {
    # Diagnostic data stays within this exact disposable fixture; no raw server logs, SQL,
    # parameters, connection strings or payloads are persisted or printed.
    $labels = & $dockerCommand inspect $Container --format '{{json .Config.Labels}}' 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $labels.'bluetusk.owner' -ne $owner -or $labels.'bluetusk.fixture' -ne $Fixture) {
        throw 'Failed fixture-state capture label validation.'
    }
    $state = & $dockerCommand inspect $Container --format '{{json .State}}' 2>$null | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Failed fixture-state capture inspection.' }
    $slots = @(); $slotObservation = 'Unavailable'
    try {
        $rows = @(& $dockerCommand exec $Container psql -U postgres -d bluetusk_load -At -v ON_ERROR_STOP=1 -c
            "SELECT slot_name,active,coalesce(wal_status,'unknown'),coalesce(restart_lsn::text,''),coalesce(confirmed_flush_lsn::text,'') FROM pg_replication_slots WHERE slot_name LIKE 'proj_load_%' ORDER BY slot_name LIMIT 4" 2>$null)
        if ($LASTEXITCODE -eq 0) {
            foreach ($row in $rows) {
                if ($row -notmatch '^proj_load_[0-9a-f]{16}_slot(?:_rebuild)?\|[tf]\|(?:reserved|extended|unreserved|lost|unknown)\|[0-9A-F/]*\|[0-9A-F/]*$') { throw 'Unexpected bounded slot metadata row.' }
                $parts = $row.Split('|')
                $slots += [ordered]@{ Name=$parts[0]; Active=($parts[1] -eq 't'); WalStatus=$parts[2]; RestartLsn=$parts[3]; ConfirmedFlushLsn=$parts[4] }
            }
            $slotObservation = 'Captured'
        }
    } catch { $slots = @(); $slotObservation = 'Unavailable' }
    $signatures = [ordered]@{ ReplicationTimeout=$false; SlotInvalidation=$false; BackendCrash=$false;
        PostmasterCrashRecovery=$false; OutOfMemory=$false }
    $logObservation = 'Unavailable'; $logTruncated = $false; $logBytes = 0; $logLines = 0
    try {
        # Classify a finite tail one line at a time. Neither its text nor error details leave
        # process memory; clipping is reported so absent signatures are never treated as proof.
        & $dockerCommand logs --tail 128 $Container 2>&1 | ForEach-Object {
            $logLines++
            if ($logLines -gt 128) { $logTruncated = $true; return }
            $line = [string]$_
            if ($line.Length -gt 65536) { $line = $line.Substring(0,65536); $logTruncated = $true }
            $lineBytes = [System.Text.Encoding]::UTF8.GetByteCount($line) + 1
            if ($logBytes + $lineBytes -gt 1048576) { $logTruncated = $true; return }
            $logBytes += $lineBytes
            $signatures.ReplicationTimeout = $signatures.ReplicationTimeout -or
                $line.Contains('terminating walsender process due to replication timeout',[System.StringComparison]::OrdinalIgnoreCase)
            $signatures.SlotInvalidation = $signatures.SlotInvalidation -or
                $line.Contains('invalidating slot',[System.StringComparison]::OrdinalIgnoreCase)
            $signatures.BackendCrash = $signatures.BackendCrash -or
                [System.Text.RegularExpressions.Regex]::IsMatch($line,
                    'server process(?: \(PID [0-9]{1,10}\))? was terminated(?: by signal)?',
                    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
            $signatures.PostmasterCrashRecovery = $signatures.PostmasterCrashRecovery -or
                $line.Contains('terminating any other active server processes',[System.StringComparison]::OrdinalIgnoreCase)
            $signatures.OutOfMemory = $signatures.OutOfMemory -or
                $line.Contains('out of memory',[System.StringComparison]::OrdinalIgnoreCase)
        }
        if ($LASTEXITCODE -eq 0) {
            $logObservation = if ($logTruncated) { 'TruncatedTail' } else { 'CapturedTail' }
        } else {
            $signatures = [ordered]@{ ReplicationTimeout=$false; SlotInvalidation=$false; BackendCrash=$false;
                PostmasterCrashRecovery=$false; OutOfMemory=$false }
        }
    } catch { $signatures = [ordered]@{ ReplicationTimeout=$false; SlotInvalidation=$false; BackendCrash=$false;
            PostmasterCrashRecovery=$false; OutOfMemory=$false }; $logObservation = 'Unavailable' }
    [ordered]@{ CapturedUtc=[datetime]::UtcNow.ToString('o'); ProductionQualified=$false;
        ContainerRunning=[bool]$state.Running; OomKilled=[bool]$state.OOMKilled; ContainerExitCode=[int]$state.ExitCode;
        SlotObservation=$slotObservation; Slots=$slots; ServerLogObservation=$logObservation;
        ServerLogLineLimit=128; ServerLogByteLimit=1048576; ServerLogSignatures=$signatures;
        RawServerLogsPersisted=$false; RawSqlOrPayloadPersisted=$false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $Path -Encoding utf8
}
Push-Location $root
try {
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    if (-not $NoBuild) {
        & dotnet build benchmarks/BlueTusk.Projections.LoadHarness/BlueTusk.Projections.LoadHarness.csproj -c Release -nr:false
        if ($LASTEXITCODE -ne 0) { throw 'Harness Release build failed.' }
    }
    $beforePath = Join-Path $artifactDirectory 'source-before.json'
    & $pythonCommand eng/capture-ecosystem-source.py --output $beforePath
    if ($LASTEXITCODE -ne 0) { throw 'Candidate source capture failed; output must be inside the checkout.' }
    $metadata = [ordered]@{ Campaign=$campaign; ImageDigest=$image; Profile=$Profile; Seconds=$Seconds; Repetitions=$Repetitions;
        StartedUtc=[datetime]::UtcNow.ToString('o'); ProductionQualified=$false; Passed=$false; CandidateUnchanged=$false;
        SdkVersion=(& dotnet --version); DockerVersion=(& $dockerCommand version --format '{{.Server.Version}}');
        ProcessorCount=[Environment]::ProcessorCount; ProcessorIdentity=$env:PROCESSOR_IDENTIFIER; OS=[System.Runtime.InteropServices.RuntimeInformation]::OSDescription;
        ProcessArchitecture=[System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString(); DockerCpus=$DockerCpus; DockerMemoryMiB=$DockerMemoryMiB }
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'campaign.json') -Encoding utf8
    for ($run=1; $run -le $Repetitions; $run++) {
        $fixture = "$campaign-$run"
        $primary = "$fixture-primary"; $standby = "$fixture-standby"; $helper = "$fixture-basebackup"
        $network = "$fixture-network"; $primaryVolume = "$fixture-primary-data"; $standbyVolume = "$fixture-standby-data"
        $statsProcess = $null; $statsFile = $null; $statsCopy = $null; $statsError = $null
        $hostSampler = $null; $allStatsProcess = $null; $allStatsFile = $null; $allStatsCopy = $null; $allStatsError = $null
        try {
            foreach ($port in @($PrimaryPort,$StandbyPort)) {
                $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,$port)
                try { $listener.Start() } catch { throw "Fixture port $port is in use." } finally { $listener.Stop() }
            }
            InvokeDocker -Arguments @('network','create','--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",$network) | Out-Null
            InvokeDocker -Arguments @('volume','create','--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",$primaryVolume) | Out-Null
            InvokeDocker -Arguments (@('run','-d','--name',$primary,'--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",'--network',$network,
                '--cpus',"$DockerCpus",'--memory',"${DockerMemoryMiB}m",
                '-p',"127.0.0.1:${PrimaryPort}:5432",'-v',"${primaryVolume}:/var/lib/postgresql/data",'-e','PGDATA=/var/lib/postgresql/data/pgdata','-e','POSTGRES_PASSWORD=postgres',$image, # ggignore
                'postgres','-c','wal_level=logical','-c','max_wal_senders=12','-c','max_replication_slots=12','-c','max_slot_wal_keep_size=8192MB','-c','shared_buffers=128MB') + $serverSettings) | Out-Null
            WaitReady $primary
            Query $primary 'CREATE DATABASE bluetusk_load' | Out-Null
            if ($Profile -eq 'promotion') {
                InvokeDocker -Arguments @('volume','create','--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",$standbyVolume) | Out-Null
                InvokeDocker -Arguments @('exec',$primary,'sh','-c','printf "\nhost replication postgres all scram-sha-256\n" >> "$PGDATA/pg_hba.conf"') | Out-Null
                Query $primary 'SELECT pg_reload_conf()' | Out-Null
                InvokeDocker -Arguments @('run','--rm','--name',$helper,'--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",'--network',$network,
                    '-v',"${standbyVolume}:/var/lib/postgresql/data",'-e','PGPASSWORD=postgres','--entrypoint','sh',$image,'-c', # ggignore
                    "mkdir -p /var/lib/postgresql/data/pgdata && chown postgres:postgres /var/lib/postgresql/data/pgdata && exec gosu postgres pg_basebackup -d 'host=$primary user=postgres application_name=bluetusk_load_standby' -D /var/lib/postgresql/data/pgdata -c fast -X stream -R -C -S bluetusk_load_physical") | Out-Null
                InvokeDocker -Arguments (@('run','-d','--name',$standby,'--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",'--network',$network,
                    '--cpus',"$DockerCpus",'--memory',"${DockerMemoryMiB}m",
                    '-p',"127.0.0.1:${StandbyPort}:5432",'-v',"${standbyVolume}:/var/lib/postgresql/data",'-e','PGDATA=/var/lib/postgresql/data/pgdata','-e','PGPASSWORD=postgres',$image, # ggignore
                    'postgres','-c','wal_level=logical','-c','max_wal_senders=12','-c','max_replication_slots=12') + $serverSettings) | Out-Null
                WaitReady $standby
                Query $primary "ALTER SYSTEM SET synchronous_standby_names='FIRST 1 (bluetusk_load_standby)'" | Out-Null
                Query $primary "ALTER SYSTEM SET synchronous_commit='remote_apply'" | Out-Null
                Query $primary 'SELECT pg_reload_conf()' | Out-Null
                $synchronous = $false
                for ($attempt=0; $attempt -lt 150; $attempt++) {
                    if ((Query $primary "SELECT count(*) FROM pg_stat_replication WHERE application_name='bluetusk_load_standby' AND state='streaming' AND sync_state='sync'") -eq '1') { $synchronous=$true; break }
                    Start-Sleep -Milliseconds 200
                }
                if (-not $synchronous) { throw 'Owned standby did not become synchronous.' }
            }
            $env:BLUETUSK_PROJECTIONS_LOAD_CONNECTION_STRING = "Host=127.0.0.1;Port=$PrimaryPort;Username=postgres;Password=postgres;Database=bluetusk_load;SSL Mode=Disable;Channel Binding=Disable" # ggignore
            $env:BLUETUSK_PROJECTIONS_LOAD_STANDBY = "Host=127.0.0.1;Port=$StandbyPort;Username=postgres;Password=postgres;Database=bluetusk_load;SSL Mode=Disable;Channel Binding=Disable" # ggignore
            $env:BLUETUSK_PROJECTIONS_LOAD_PRIMARY_CONTAINER=$primary; $env:BLUETUSK_PROJECTIONS_LOAD_STANDBY_CONTAINER=$standby
            $env:BLUETUSK_PROJECTIONS_LOAD_FIXTURE=$fixture; $env:BLUETUSK_PROJECTIONS_LOAD_DOCKER=$dockerCommand
            $env:BLUETUSK_PROJECTIONS_LOAD_SERVER_SAMPLES = if ($Diagnostics) { '1' } else { '0' }
            # The child Docker client only observes this fixture. Its bounded-duration JSONL stream
            # records server CPU/memory/block/network observations without application payloads.
            $statsStart=[System.Diagnostics.ProcessStartInfo]::new($dockerCommand)
            $statsStart.UseShellExecute=$false; $statsStart.CreateNoWindow=$true; $statsStart.RedirectStandardOutput=$true; $statsStart.RedirectStandardError=$true
            foreach ($argument in @('stats','--format','{{json .}}',$primary)) { $statsStart.ArgumentList.Add($argument) }
            if ($Profile -eq 'promotion') { $statsStart.ArgumentList.Add($standby) }
            $statsProcess=[System.Diagnostics.Process]::Start($statsStart)
            $statsFile=[System.IO.File]::Create((Join-Path $artifactDirectory "run-$run-docker-stats.jsonl"))
            $statsCopy=$statsProcess.StandardOutput.BaseStream.CopyToAsync($statsFile)
            $statsError=$statsProcess.StandardError.ReadToEndAsync()
            if ($Diagnostics) {
                # Host contention is a confounder on a shared workstation: every 5 s record host CPU and
                # .NET build/test process CPU time, plus Docker statistics for every running container
                # (read-only observation; other containers are never stopped or altered).
                $hostSampler = Start-ThreadJob -ArgumentList (Join-Path $artifactDirectory "run-$run-host-processes.jsonl") -ScriptBlock {
                    param($Path)
                    $writer = [System.IO.StreamWriter]::new($Path, $false, [System.Text.UTF8Encoding]::new($false))
                    try {
                        while ($true) {
                            $cpu = (Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'").PercentProcessorTime
                            $processes = @(Get-Process dotnet,testhost,VBCSCompiler,MSBuild,BlueTusk.Projections.LoadHarness -ErrorAction SilentlyContinue | ForEach-Object {
                                [ordered]@{ Name=$_.Name; Id=$_.Id; CpuSeconds=[math]::Round($_.TotalProcessorTime.TotalSeconds,2); WorkingSetMiB=[math]::Round($_.WorkingSet64/1MB) } })
                            $writer.WriteLine((ConvertTo-Json -Compress -Depth 4 ([ordered]@{ Utc=[datetime]::UtcNow.ToString('o'); HostCpuPercent=$cpu; Processes=$processes })))
                            $writer.Flush()
                            Start-Sleep -Seconds 5
                        }
                    } finally { $writer.Dispose() }
                }
                $allStatsStart=[System.Diagnostics.ProcessStartInfo]::new($dockerCommand)
                $allStatsStart.UseShellExecute=$false; $allStatsStart.CreateNoWindow=$true; $allStatsStart.RedirectStandardOutput=$true; $allStatsStart.RedirectStandardError=$true
                foreach ($argument in @('stats','--format','{{json .}}')) { $allStatsStart.ArgumentList.Add($argument) }
                $allStatsProcess=[System.Diagnostics.Process]::Start($allStatsStart)
                $allStatsFile=[System.IO.File]::Create((Join-Path $artifactDirectory "run-$run-docker-stats-all.jsonl"))
                $allStatsCopy=$allStatsProcess.StandardOutput.BaseStream.CopyToAsync($allStatsFile)
                $allStatsError=$allStatsProcess.StandardError.ReadToEndAsync()
            }
            & dotnet run --no-build --project benchmarks/BlueTusk.Projections.LoadHarness/BlueTusk.Projections.LoadHarness.csproj -c Release -- $Profile $Seconds (Join-Path $artifactDirectory "run-$run.json") $SmokePayloadBytes
            if ($LASTEXITCODE -ne 0) {
                try { SaveFailureState $primary $fixture (Join-Path $artifactDirectory "run-$run-failure-state.json") }
                catch {
                    [ordered]@{ CapturedUtc=[datetime]::UtcNow.ToString('o'); ProductionQualified=$false;
                        CaptureUnavailable=$true; FailureType=$_.Exception.GetType().Name } |
                        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $artifactDirectory "run-$run-failure-state.json") -Encoding utf8
                }
                throw 'Owned Events/Projections campaign invariant or bound failed.'
            }
        } finally {
            if ($null -ne $statsProcess) {
                if (-not $statsProcess.HasExited) { $statsProcess.Kill() }
                $statsProcess.WaitForExit(); $statsCopy.GetAwaiter().GetResult() | Out-Null; $statsError.GetAwaiter().GetResult() | Out-Null
                $statsFile.Dispose(); $statsProcess.Dispose()
            }
            if ($null -ne $hostSampler) { Stop-Job -Job $hostSampler; Remove-Job -Job $hostSampler -Force; $hostSampler = $null }
            if ($null -ne $allStatsProcess) {
                if (-not $allStatsProcess.HasExited) { $allStatsProcess.Kill() }
                $allStatsProcess.WaitForExit(); $allStatsCopy.GetAwaiter().GetResult() | Out-Null; $allStatsError.GetAwaiter().GetResult() | Out-Null
                $allStatsFile.Dispose(); $allStatsProcess.Dispose(); $allStatsProcess = $null
            }
            if ($Diagnostics) {
                foreach ($server in @(@($primary,'primary'),@($standby,'standby'))) {
                    try { SaveServerLog $server[0] $fixture (Join-Path $artifactDirectory "run-$run-$($server[1]).log") }
                    catch { Write-Warning "Diagnostic $($server[1]) server log was not captured: $($_.Exception.GetType().Name)" }
                }
            }
            RemoveOwned 'container' $standby; RemoveOwned 'container' $helper; RemoveOwned 'container' $primary
            RemoveOwned 'volume' $standbyVolume; RemoveOwned 'volume' $primaryVolume; RemoveOwned 'network' $network
        }
    }
    & $pythonCommand eng/capture-ecosystem-source.py --output (Join-Path $artifactDirectory 'source-after.json')
    if ($LASTEXITCODE -ne 0) { throw 'Final candidate capture failed.' }
    $before = Get-Content -LiteralPath $beforePath -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath (Join-Path $artifactDirectory 'source-after.json') -Raw | ConvertFrom-Json
    $metadata.CandidateUnchanged = $before.sourceTreeSha256 -eq $after.sourceTreeSha256
    $metadata.Passed = $metadata.CandidateUnchanged
    $metadata.CompletedUtc = [datetime]::UtcNow.ToString('o')
    $metadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $artifactDirectory 'campaign.json') -Encoding utf8
    if (-not $metadata.CandidateUnchanged) { throw 'Source changed during measurement; raw evidence is diagnostic and does not qualify one candidate.' }
    Write-Host "Measured campaign retained in $artifactDirectory. Production qualification remains false."
} finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $priorEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [System.Environment]::SetEnvironmentVariable($name,$priorEnvironment[$name],[System.EnvironmentVariableTarget]::Process) }
    }
    Pop-Location
}
