[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Documents', 'Projections', 'Workflows', IgnoreCase = $false)]
    [string] $Family,
    [ValidateRange(1, 5)][int] $Repetitions = 3,
    [Parameter(Mandatory)][string] $ArtifactDirectory,
    [Parameter(Mandatory)][string] $OutputReport,
    # The Docker owner label for every container, volume and network this runner creates. Cleanup
    # refuses any object whose owner and fixture labels differ from the values set here.
    [ValidatePattern('^[a-z0-9][a-z0-9.-]{2,62}$')][string] $Owner = 'bluetusk.expansion.failover',
    [switch] $NoBuild
)

# Provisions one fresh synchronous PostgreSQL 18 primary/physical-standby pair per repetition and
# runs the family's failover steps against it, in policy order: the non-destructive disturbances
# first (backend termination, host-process kill, primary crash/restart) and standby promotion
# last. Each step injects its own faults and writes a raw report. This script owns the fixture,
# provenance and raw evidence only; eng/verify-expansion-failover-report.ps1 judges the evidence.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$policy = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'expansion-failover-policy.json') -Raw | ConvertFrom-Json -Depth 40
$familyPolicy = $policy.families.PSObject.Properties[$Family].Value
if ($null -eq $familyPolicy) { throw "No failover policy exists for '$Family'." }
$fixturePolicy = $familyPolicy.fixture
$image = [string]$policy.postgreSqlImage
if ($image -cnotmatch '^postgres@sha256:[0-9a-f]{64}$') { throw 'Failover fixtures require a digest-pinned PostgreSQL image.' }
$docker = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$database = [string]$fixturePolicy.database
$standbyName = [string]$fixturePolicy.standbyApplicationName
$primaryPort = [int]$fixturePolicy.primaryPort
$standbyPort = [int]$fixturePolicy.standbyPort
if ($database -cnotmatch '^[a-z_]+$' -or $standbyName -cnotmatch '^[a-z_]+$' -or [string]$fixturePolicy.walLevel -cnotin @('replica', 'logical'))
{
    throw 'The failover fixture policy names are not plain identifiers.'
}
$campaign = "$($Family.ToLowerInvariant())-failover-" + [Guid]::NewGuid().ToString('N')
$captureScript = Join-Path $PSScriptRoot 'capture-ecosystem-source.py'

function Resolve-Under([string] $Path)
{
    $resolved = if ([IO.Path]::IsPathRooted($Path)) { [IO.Path]::GetFullPath($Path) }
    else { [IO.Path]::GetFullPath((Join-Path $repository $Path)) }
    $allowed = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Failover evidence must remain under the repository artifacts directory.'
    }
    return $resolved
}
function Get-StepBinaryDirectory($Step)
{
    return Join-Path $repository "$(Split-Path -Parent ([string]$Step.project))/bin/Release/net10.0"
}
$artifacts = Resolve-Under $ArtifactDirectory
$output = Resolve-Under $OutputReport
$steps = @($familyPolicy.steps)
$prefixes = @('BLUETUSK_FAILOVER_') + @($steps | Where-Object { $_.kind -ceq 'xunit' } | ForEach-Object { [string]$_.environmentPrefix })
$variableNames = @(foreach ($prefix in $prefixes)
    {
        foreach ($suffix in 'PRIMARY', 'STANDBY', 'PRIMARY_CONTAINER', 'STANDBY_CONTAINER', 'FIXTURE', 'REPORT', 'DOCKER', 'IMAGE', 'OWNER', 'STANDBY_NAME')
        { $prefix + $suffix }
    }) + @($steps | Where-Object { $_.kind -ceq 'console' } | ForEach-Object { @($_.environment.PSObject.Properties.Name) })
$previousEnvironment = @{}
foreach ($name in ($variableNames | Sort-Object -Unique))
{
    $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, [EnvironmentVariableTarget]::Process)
}

function Invoke-OwnedDocker([string[]] $Arguments)
{
    $result = & $docker @Arguments 2>&1
    # Never echo Docker output: it can contain fixture connection settings.
    if ($LASTEXITCODE -ne 0) { throw "Owned Docker operation failed: $($Arguments[0])" }
    return $result
}
function Invoke-OwnedSql([string] $Container, [string] $Sql)
{
    return Invoke-OwnedDocker @('exec', $Container, 'psql', '-U', 'postgres', '-d', 'postgres', '-At', '-v', 'ON_ERROR_STOP=1', '-c', $Sql)
}
function Remove-OwnedResource([string] $Kind, [string] $Name, [string] $Fixture)
{
    $format = if ($Kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $json = & $docker $Kind inspect $Name --format $format 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $labels = $json | ConvertFrom-Json
    if ($null -eq $labels -or $labels.'bluetusk.owner' -cne $Owner -or $labels.'bluetusk.fixture' -cne $Fixture)
    {
        throw "Refusing cleanup of '$Name': it is outside this owned failover fixture."
    }
    if ($Kind -eq 'container') { Invoke-OwnedDocker @('rm', '-f', $Name) | Out-Null }
    else { Invoke-OwnedDocker @($Kind, 'rm', $Name) | Out-Null }
}
function Wait-OwnedReady([string] $Container)
{
    for ($probe = 0; $probe -lt 150; $probe++)
    {
        & $docker exec $Container pg_isready -h 127.0.0.1 -U postgres *> $null
        if ($LASTEXITCODE -eq 0) { return }
        Start-Sleep -Milliseconds 200
    }
    throw 'Owned PostgreSQL startup deadline expired.'
}
function Get-CandidateSource([string] $Path)
{
    & python $captureScript --output $Path | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Candidate source capture failed.' }
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Get-StepBinaries($Step, [string] $Path)
{
    $directory = Get-StepBinaryDirectory $Step
    $files = @(Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Extension -in '.dll', '.exe', '.json' } | Sort-Object Name | ForEach-Object {
            [ordered]@{ Name = $_.Name; Bytes = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
    foreach ($required in @($Step.requiredBinaries))
    {
        if ([string]$required -cnotin @($files | ForEach-Object { $_.Name }))
        {
            throw "The '$($Step.name)' output lacks '$required'; rebuild the dedicated project."
        }
    }
    $manifest = [ordered]@{
        CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Files = $files
        Scope = 'All executable, DLL, dependency and runtime configuration files in the step output. The installed .NET SDK/runtime is identified separately.'
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Path -Encoding utf8
    return $manifest
}
function Assert-BinariesUnchanged($Before, $After)
{
    if (@($Before.Files).Count -ne @($After.Files).Count) { throw 'Failover step output files changed during execution.' }
    foreach ($file in @($Before.Files))
    {
        $match = @($After.Files | Where-Object { $_.Name -ceq $file.Name })
        if ($match.Count -ne 1 -or $match[0].Bytes -ne $file.Bytes -or $match[0].Sha256 -cne $file.Sha256)
        {
            throw 'Failover step executable or dependency bytes changed during execution.'
        }
    }
}
function Get-RelativePath([string] $Path) { return [IO.Path]::GetRelativePath($repository, $Path).Replace('\', '/') }

Push-Location -LiteralPath $repository
$runs = @()
try
{
    if (Test-Path -LiteralPath $artifacts) { throw 'Failover runs require a fresh artifact directory.' }
    foreach ($port in $primaryPort, $standbyPort)
    {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        try { $listener.Start() } catch { throw "Failover fixture port $port is already in use." } finally { $listener.Stop() }
    }
    [IO.Directory]::CreateDirectory($artifacts) | Out-Null
    [IO.Directory]::CreateDirectory((Split-Path -Parent $output)) | Out-Null
    if (-not $NoBuild)
    {
        foreach ($step in $steps)
        {
            & dotnet build ([string]$step.project) -c Release -nr:false --nologo
            if ($LASTEXITCODE -ne 0) { throw "The $Family '$($step.name)' project did not build." }
        }
    }
    $sourceBefore = Get-CandidateSource (Join-Path $artifacts 'source-before.json')
    $scriptBefore = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtime = @(& dotnet --info)
    if ($LASTEXITCODE -ne 0) { throw 'Installed runtime identification failed.' }
    for ($run = 1; $run -le $Repetitions; $run++)
    {
        $fixture = "$campaign-run$run"
        $primary = "$fixture-primary"
        $standby = "$fixture-standby"
        $backup = "$fixture-basebackup"
        $network = "$fixture-network"
        $primaryVolume = "$fixture-primary-data"
        $standbyVolume = "$fixture-standby-data"
        $runDirectory = Join-Path $artifacts "run$run"
        $samplePath = Join-Path $runDirectory 'fixture.jsonl'
        $monitor = $null
        [IO.Directory]::CreateDirectory($runDirectory) | Out-Null
        $labels = @('--label', "bluetusk.owner=$Owner", '--label', "bluetusk.fixture=$fixture")
        try
        {
            Write-Output "Provisioning fresh synchronous $Family failover fixture $run of $Repetitions"
            Invoke-OwnedDocker (@('network', 'create') + $labels + @($network)) | Out-Null
            foreach ($volume in $primaryVolume, $standbyVolume)
            {
                Invoke-OwnedDocker (@('volume', 'create') + $labels + @($volume)) | Out-Null
            }
            $server = @('postgres', '-c', "wal_level=$($fixturePolicy.walLevel)", '-c', 'max_wal_senders=12',
                '-c', 'max_replication_slots=12', '-c', 'fsync=on', '-c', 'full_page_writes=on')
            Invoke-OwnedDocker (@('run', '-d', '--name', $primary) + $labels + @('--network', $network,
                '-p', "127.0.0.1:${primaryPort}:5432", '-v', "${primaryVolume}:/var/lib/postgresql/data",
                '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', 'POSTGRES_PASSWORD=postgres', $image) + $server) | Out-Null # ggignore
            Wait-OwnedReady $primary
            Invoke-OwnedSql $primary "CREATE DATABASE $database" | Out-Null
            Invoke-OwnedDocker @('exec', $primary, 'sh', '-c', 'printf "\nhost replication postgres all scram-sha-256\n" >> "$PGDATA/pg_hba.conf"') | Out-Null
            Invoke-OwnedSql $primary 'SELECT pg_reload_conf()' | Out-Null
            Invoke-OwnedDocker (@('run', '--rm', '--name', $backup) + $labels + @('--network', $network,
                '-v', "${standbyVolume}:/var/lib/postgresql/data", '-e', 'PGPASSWORD=postgres', '--entrypoint', 'sh', $image, '-c', # ggignore
                "mkdir -p /var/lib/postgresql/data/pgdata && chown postgres:postgres /var/lib/postgresql/data/pgdata && exec gosu postgres pg_basebackup -d 'host=$primary user=postgres application_name=$standbyName' -D /var/lib/postgresql/data/pgdata -c fast -X stream -R -C -S ${standbyName}_physical")) | Out-Null
            Invoke-OwnedDocker (@('run', '-d', '--name', $standby) + $labels + @('--network', $network,
                '-p', "127.0.0.1:${standbyPort}:5432", '-v', "${standbyVolume}:/var/lib/postgresql/data",
                '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', 'PGPASSWORD=postgres', $image) + $server) | Out-Null # ggignore
            Wait-OwnedReady $standby
            Invoke-OwnedSql $primary "ALTER SYSTEM SET synchronous_standby_names='FIRST 1 ($standbyName)'" | Out-Null
            Invoke-OwnedSql $primary "ALTER SYSTEM SET synchronous_commit='remote_apply'" | Out-Null
            Invoke-OwnedSql $primary 'SELECT pg_reload_conf()' | Out-Null
            $synchronous = $false
            for ($probe = 0; $probe -lt 150; $probe++)
            {
                if ((Invoke-OwnedSql $primary "SELECT count(*) FROM pg_stat_replication WHERE application_name='$standbyName' AND state='streaming' AND sync_state='sync'") -eq '1')
                { $synchronous = $true; break }
                Start-Sleep -Milliseconds 200
            }
            if (-not $synchronous) { throw 'Owned physical standby did not become synchronous before its deadline.' }
            $connection = "Username=postgres;Password=postgres;Database=$database;SSL Mode=Disable;Channel Binding=Disable" # ggignore
            $primaryConnection = "Host=127.0.0.1;Port=$primaryPort;$connection"
            foreach ($prefix in $prefixes)
            {
                [Environment]::SetEnvironmentVariable("${prefix}PRIMARY", $primaryConnection)
                [Environment]::SetEnvironmentVariable("${prefix}STANDBY", "Host=127.0.0.1;Port=$standbyPort;$connection")
                [Environment]::SetEnvironmentVariable("${prefix}PRIMARY_CONTAINER", $primary)
                [Environment]::SetEnvironmentVariable("${prefix}STANDBY_CONTAINER", $standby)
                [Environment]::SetEnvironmentVariable("${prefix}FIXTURE", $fixture)
                [Environment]::SetEnvironmentVariable("${prefix}DOCKER", $docker)
                [Environment]::SetEnvironmentVariable("${prefix}IMAGE", $image)
                [Environment]::SetEnvironmentVariable("${prefix}OWNER", $Owner)
                [Environment]::SetEnvironmentVariable("${prefix}STANDBY_NAME", $standbyName)
            }
            $metadata = [ordered]@{
                Family = $Family
                Fixture = $fixture
                Owner = $Owner
                ImageDigest = $image
                ImageInspect = (Invoke-OwnedDocker @('image', 'inspect', $image) | ConvertFrom-Json)[0] |
                    Select-Object Id, RepoDigests, Created, Architecture, Os
                BeforePrimarySettings = @(Invoke-OwnedSql $primary "SELECT name||'='||setting FROM pg_settings WHERE name IN ('fsync','synchronous_commit','synchronous_standby_names','full_page_writes','wal_level','max_connections') ORDER BY name")
            }
            $metadata | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runDirectory 'environment.json') -Encoding utf8
            $monitor = Start-Job -ScriptBlock {
                param($DockerPath, $Containers, $SampleFile)
                while ($true)
                {
                    $stats = & $DockerPath stats --no-stream --format '{{json .}}' @Containers 2>$null
                    if ($LASTEXITCODE -eq 0)
                    {
                        foreach ($line in $stats)
                        {
                            [ordered]@{ TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O'); Stats = ($line | ConvertFrom-Json) } |
                                ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $SampleFile
                        }
                    }
                    Start-Sleep -Milliseconds 500
                }
            } -ArgumentList $docker, @($primary, $standby), $samplePath
            $stepRecords = @()
            foreach ($step in $steps)
            {
                $name = [string]$step.name
                $reportPath = Join-Path $runDirectory "$name.json"
                $logPath = Join-Path $runDirectory "$name.log"
                $before = Get-StepBinaries $step (Join-Path $runDirectory "$name.binaries-before.json")
                $trxPath = $null
                switch -CaseSensitive ([string]$step.kind)
                {
                    'harness'
                    {
                        $dll = Join-Path (Get-StepBinaryDirectory $step) "$([IO.Path]::GetFileNameWithoutExtension([string]$step.project)).dll"
                        & dotnet $dll $Family $reportPath *> $logPath
                        $exit = $LASTEXITCODE
                    }
                    'console'
                    {
                        foreach ($variable in $step.environment.PSObject.Properties)
                        {
                            if ([string]$variable.Value -cne '{primary}') { throw 'Console steps may only receive the owned primary connection.' }
                            [Environment]::SetEnvironmentVariable($variable.Name, $primaryConnection)
                        }
                        $dll = Join-Path (Get-StepBinaryDirectory $step) "$([IO.Path]::GetFileNameWithoutExtension([string]$step.project)).dll"
                        $arguments = @($step.arguments | ForEach-Object { ([string]$_).Replace('{report}', $reportPath) })
                        & dotnet $dll @arguments *> $logPath
                        $exit = $LASTEXITCODE
                        foreach ($variable in $step.environment.PSObject.Properties)
                        {
                            [Environment]::SetEnvironmentVariable($variable.Name, $null)
                        }
                    }
                    'xunit'
                    {
                        [Environment]::SetEnvironmentVariable("$($step.environmentPrefix)REPORT", $reportPath)
                        $trxPath = Join-Path $runDirectory "$name.trx"
                        & dotnet test ([string]$step.project) -c Release --no-build -nr:false --nologo --logger "trx;LogFileName=$name.trx" --results-directory $runDirectory *> $logPath
                        $exit = $LASTEXITCODE
                    }
                    default { throw "Unknown failover step kind '$($step.kind)'." }
                }
                $after = Get-StepBinaries $step (Join-Path $runDirectory "$name.binaries-after.json")
                Assert-BinariesUnchanged $before $after
                if ($exit -ne 0) { throw "The $Family '$name' step failed; partial logs and owned fixture metadata are retained." }
                if ($null -ne $trxPath)
                {
                    [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
                    $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
                    if ([int]$counters.passed -ne [int]$step.tests -or [int]$counters.total -ne [int]$step.tests -or [int]$counters.failed -ne 0)
                    {
                        throw "The $Family '$name' step did not produce exactly $($step.tests) passing, non-skipped tests."
                    }
                }
                if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "The $Family '$name' step wrote no raw report." }
                $stepRecords += [ordered]@{
                    Name = $name
                    Kind = [string]$step.kind
                    Report = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 60)
                    ReportPath = Get-RelativePath $reportPath
                    LogPath = Get-RelativePath $logPath
                    TrxPath = $(if ($null -eq $trxPath) { $null } else { Get-RelativePath $trxPath })
                    BinariesBefore = $before
                    BinariesAfter = $after
                    BinariesUnchanged = $true
                }
                Write-Output "$Family failover repetition $run step '$name' passed."
            }
            $runs += [ordered]@{
                Repetition = $run
                Metadata = $metadata
                Steps = $stepRecords
                FixtureSamplePath = Get-RelativePath $samplePath
            }
        }
        finally
        {
            if ($null -ne $monitor) { Stop-Job -Job $monitor; Receive-Job -Job $monitor | Out-Null; Remove-Job -Job $monitor }
            foreach ($container in $standby, $primary)
            {
                & $docker logs $container *> (Join-Path $runDirectory "$container.log")
            }
            Remove-OwnedResource 'container' $standby $fixture
            Remove-OwnedResource 'container' $backup $fixture
            Remove-OwnedResource 'container' $primary $fixture
            Remove-OwnedResource 'volume' $standbyVolume $fixture
            Remove-OwnedResource 'volume' $primaryVolume $fixture
            Remove-OwnedResource 'network' $network $fixture
        }
    }
    $sourceAfter = Get-CandidateSource (Join-Path $artifacts 'source-after.json')
    if ($sourceBefore.sourceTreeSha256 -cne $sourceAfter.sourceTreeSha256 -or $sourceBefore.fileCount -ne $sourceAfter.fileCount -or
        $sourceBefore.sourceBytes -ne $sourceAfter.sourceBytes)
    {
        throw 'Candidate source changed during the failover campaign; raw observations are retained but no report is published.'
    }
    if ($scriptBefore -cne (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant())
    {
        throw 'The failover runner changed during execution.'
    }
    $sources = @(foreach ($root in @($familyPolicy.sourceRoots))
        {
            Get-ChildItem -LiteralPath (Join-Path $repository ([string]$root)) -Recurse -File |
                Where-Object { $_.Extension -in '.cs', '.csproj' -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }
        }) | Sort-Object FullName -Unique | ForEach-Object {
        [ordered]@{ Path = Get-RelativePath $_.FullName; Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [ordered]@{
        Family = $Family
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Campaign = $campaign
        Configuration = 'Release'
        Repetitions = $Repetitions
        Runs = $runs
        Sources = $sources
        SourceProvenance = [ordered]@{ Before = $sourceBefore; After = $sourceAfter; Unchanged = $true }
        InstalledRuntime = $runtime
        ScriptSha256 = $scriptBefore
        ProductionQualified = $false
        Qualification = [string]$familyPolicy.qualification
    } | ConvertTo-Json -Depth 80 | Set-Content -LiteralPath $output -Encoding utf8
}
finally
{
    foreach ($entry in $previousEnvironment.GetEnumerator())
    {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, [EnvironmentVariableTarget]::Process)
    }
    Pop-Location
}
