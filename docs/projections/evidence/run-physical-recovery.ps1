param([int]$PrimaryPort = 55618, [int]$StandbyPort = 55619, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$fixture = 'projection-recovery-' + [guid]::NewGuid().ToString('N').Substring(0, 16)
$primary = "$fixture-primary"
$standby = "$fixture-standby"
$baseBackupHelper = "$fixture-basebackup"
$network = "$fixture-network"
$primaryVolume = "$fixture-primary-data"
$standbyVolume = "$fixture-standby-data"
$owner = 'bluetusk.projections.recovery'
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$dockerCommand = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$image = 'postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
$artifactDirectory = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts/projections-recovery/$fixture" } else { [System.IO.Path]::GetFullPath($OutputDirectory) }
$report = Join-Path $artifactDirectory 'physical-promotion-pg18.json'
$environmentNames = @('BLUETUSK_RECOVERY_PRIMARY','BLUETUSK_RECOVERY_STANDBY','BLUETUSK_RECOVERY_PRIMARY_CONTAINER','BLUETUSK_RECOVERY_STANDBY_CONTAINER','BLUETUSK_RECOVERY_FIXTURE','BLUETUSK_RECOVERY_REPORT','BLUETUSK_RECOVERY_DOCKER','BLUETUSK_RECOVERY_IMAGE')
$priorEnvironment = @{}
foreach ($name in $environmentNames) { $priorEnvironment[$name] = [System.Environment]::GetEnvironmentVariable($name, [System.EnvironmentVariableTarget]::Process) }
function InvokeFixtureDocker([string[]]$Arguments) {
    $result = & $dockerCommand @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Docker fixture operation failed: $($Arguments[0]) ($result)" }
    return $result
}
function Query([string]$Container, [string]$Sql) {
    return InvokeFixtureDocker -Arguments @('exec', $Container, 'psql', '-U', 'postgres', '-d', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1', '-c', $Sql)
}
function RemoveOwned([string]$Kind, [string]$Name) {
    $labels = & $dockerCommand $Kind inspect $Name --format '{{json .Labels}}' 2>$null
    if ($Kind -eq 'container') { $labels = & $dockerCommand inspect $Name --format '{{json .Config.Labels}}' 2>$null }
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    if ($actual.'bluetusk.owner' -ne $owner -or $actual.'bluetusk.fixture' -ne $fixture) {
        throw "Refusing cleanup of an object outside this owned fixture: $Name"
    }
    if ($Kind -eq 'container') { InvokeFixtureDocker -Arguments @('rm', '-f', $Name) | Out-Null }
    else { InvokeFixtureDocker -Arguments @($Kind, 'rm', $Name) | Out-Null }
}
try {
    New-Item -ItemType Directory -Path $artifactDirectory -Force | Out-Null
    foreach ($port in @($PrimaryPort, $StandbyPort)) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $port)
        try { $listener.Start() } catch { throw "Fixture port $port is already in use." } finally { $listener.Stop() }
    }
    InvokeFixtureDocker -Arguments @('network', 'create', '--label', "bluetusk.owner=$owner", '--label', "bluetusk.fixture=$fixture", $network) | Out-Null
    foreach ($volume in @($primaryVolume, $standbyVolume)) {
        InvokeFixtureDocker -Arguments @('volume', 'create', '--label', "bluetusk.owner=$owner", '--label', "bluetusk.fixture=$fixture", $volume) | Out-Null
    }
    InvokeFixtureDocker -Arguments @('run', '-d', '--name', $primary, '--label', "bluetusk.owner=$owner", '--label', "bluetusk.fixture=$fixture",
        '--network', $network, '-p', "127.0.0.1:${PrimaryPort}:5432", '-v', "${primaryVolume}:/var/lib/postgresql/data",
        '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', 'POSTGRES_PASSWORD=postgres', $image, # ggignore
        'postgres', '-c', 'wal_level=logical', '-c', 'max_wal_senders=12', '-c', 'max_replication_slots=12') | Out-Null
    $ready = $false
    for ($i = 0; $i -lt 100; $i++) {
        & $dockerCommand exec $primary pg_isready -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Owned primary did not become ready.' }
    Query $primary 'CREATE DATABASE bluetusk_recovery' | Out-Null
    InvokeFixtureDocker -Arguments @('exec', $primary, 'sh', '-c', 'printf "\nhost replication postgres all scram-sha-256\n" >> "$PGDATA/pg_hba.conf"') | Out-Null
    Query $primary 'SELECT pg_reload_conf()' | Out-Null
    # Only the new labelled standby volume is populated. The helper is synchronous and removed on exit.
    InvokeFixtureDocker -Arguments @('run', '--rm', '--name', $baseBackupHelper, '--label', "bluetusk.owner=$owner", '--label', "bluetusk.fixture=$fixture", '--network', $network, '-v', "${standbyVolume}:/var/lib/postgresql/data", '-e', 'PGPASSWORD=postgres', # ggignore
        '--entrypoint', 'sh', $image, '-c',
        "mkdir -p /var/lib/postgresql/data/pgdata && chown postgres:postgres /var/lib/postgresql/data/pgdata && exec gosu postgres pg_basebackup -d 'host=$primary user=postgres application_name=bluetusk_recovery_standby' -D /var/lib/postgresql/data/pgdata -c fast -X stream -R -C -S bluetusk_recovery_physical") | Out-Null
    InvokeFixtureDocker -Arguments @('run', '-d', '--name', $standby, '--label', "bluetusk.owner=$owner", '--label', "bluetusk.fixture=$fixture",
        '--network', $network, '-p', "127.0.0.1:${StandbyPort}:5432", '-v', "${standbyVolume}:/var/lib/postgresql/data",
        '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', 'PGPASSWORD=postgres', $image, # ggignore
        'postgres', '-c', 'wal_level=logical', '-c', 'max_wal_senders=12', '-c', 'max_replication_slots=12') | Out-Null
    $streaming = $false
    for ($i = 0; $i -lt 100; $i++) {
        if ((Query $primary "SELECT count(*) FROM pg_stat_replication WHERE application_name='bluetusk_recovery_standby' AND state='streaming'") -eq '1') { $streaming = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $streaming) { throw 'Owned physical standby did not begin streaming.' }
    Query $primary "ALTER SYSTEM SET synchronous_standby_names='FIRST 1 (bluetusk_recovery_standby)'" | Out-Null
    Query $primary "ALTER SYSTEM SET synchronous_commit='remote_apply'" | Out-Null
    Query $primary 'SELECT pg_reload_conf()' | Out-Null
    $synchronous = $false
    for ($i = 0; $i -lt 100; $i++) {
        if ((Query $primary "SELECT count(*) FROM pg_stat_replication WHERE application_name='bluetusk_recovery_standby' AND sync_state='sync'") -eq '1') { $synchronous = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $synchronous) { throw 'Owned physical standby did not become synchronous.' }
    $env:BLUETUSK_RECOVERY_PRIMARY = "Host=127.0.0.1;Port=$PrimaryPort;Username=postgres;Password=postgres;Database=bluetusk_recovery;SSL Mode=Disable;Channel Binding=Disable" # ggignore
    $env:BLUETUSK_RECOVERY_STANDBY = "Host=127.0.0.1;Port=$StandbyPort;Username=postgres;Password=postgres;Database=bluetusk_recovery;SSL Mode=Disable;Channel Binding=Disable" # ggignore
    $env:BLUETUSK_RECOVERY_PRIMARY_CONTAINER = $primary
    $env:BLUETUSK_RECOVERY_STANDBY_CONTAINER = $standby
    $env:BLUETUSK_RECOVERY_FIXTURE = $fixture
    $env:BLUETUSK_RECOVERY_REPORT = $report
    $env:BLUETUSK_RECOVERY_DOCKER = $dockerCommand
    $env:BLUETUSK_RECOVERY_IMAGE = $image
    Push-Location $root
    try {
        & dotnet test tests/BlueTusk.Projections.PhysicalRecoveryTests/BlueTusk.Projections.PhysicalRecoveryTests.csproj -c Release -nr:false --logger 'console;verbosity=normal' --logger 'trx;LogFileName=physical-promotion-pg18.trx' --results-directory $artifactDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Physical promotion recovery test failed.' }
        Write-Host "Physical recovery JSON/TRX retained in $artifactDirectory"
    } finally { Pop-Location }
} finally {
    foreach ($name in $environmentNames) {
        if ($null -eq $priorEnvironment[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [System.Environment]::SetEnvironmentVariable($name, $priorEnvironment[$name], [System.EnvironmentVariableTarget]::Process) }
    }
    RemoveOwned 'container' $standby
    RemoveOwned 'container' $baseBackupHelper
    RemoveOwned 'container' $primary
    RemoveOwned 'volume' $standbyVolume
    RemoveOwned 'volume' $primaryVolume
    RemoveOwned 'network' $network
}
