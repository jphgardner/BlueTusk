[CmdletBinding()]
param(
    # BackupRestore: encrypted pg_dump of a database written by the candidate, restored into a fresh
    # empty PostgreSQL, compared object-by-object and reconciled by the candidate binary.
    # Rollback: a database written by the published rollback release, upgraded by the candidate,
    # then rolled back to the published release binaries against the same database.
    [Parameter(Mandatory)]
    [ValidateSet('BackupRestore', 'Rollback')]
    [string] $Rehearsal,

    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCommit,

    # Output of: ./eng/build-v1-candidate-packages.ps1 -ReleaseTrack Core -Commit <ExpectedCommit>
    [Parameter(Mandatory)]
    [string] $CandidatePackageRoot,

    # A new directory below the repository artifacts directory.
    [Parameter(Mandatory)]
    [string] $EvidenceRoot,

    # The named person who ran the rehearsal.
    [Parameter(Mandatory)]
    [ValidateLength(2, 200)]
    [string] $Operator,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $RollbackVersion = '1.0.0',

    [ValidateLength(5, 400)]
    [string] $Trigger = 'Rehearsed stop condition: the 1.1.0 candidate is withdrawn after deployment and the previous release is reinstated.',

    [string] $DecisionAuthority,

    [ValidatePattern('^[a-z0-9][a-z0-9.-]{1,62}$')]
    [string] $ContainerOwnerLabel = 'core-recovery-rehearsal'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$artifactsPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$candidateVersion = '1.1.0'
$probePackages = @(
    'BlueTusk.Data', 'BlueTusk.Streams', 'BlueTusk.Streams.Storage.PostgreSql', 'BlueTusk.Streams.Testing',
    'BlueTusk.Live', 'BlueTusk.Live.DependencyInjection', 'BlueTusk.ControlPlane', 'BlueTusk.Sync',
    'BlueTusk.Sync.PostgreSql')
if ([string]::IsNullOrWhiteSpace($DecisionAuthority)) { $DecisionAuthority = $Operator }

function Resolve-UnderArtifacts([string] $Path, [string] $Name)
{
    $full = if ([IO.Path]::IsPathRooted($Path)) { [IO.Path]::GetFullPath($Path) }
    else { [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path)) }
    if (-not $full.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "$Name '$full' must be below '$artifactsRoot'."
    }
    return $full
}

function Get-Sha256([string] $Path)
{
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Invoke-Native([string] $FilePath, [string[]] $Arguments, [string] $Failure)
{
    $output = & $FilePath @Arguments 2>&1
    if ($LASTEXITCODE -ne 0)
    {
        throw "$Failure (exit $LASTEXITCODE): $(($output | Select-Object -Last 20) -join [Environment]::NewLine)"
    }
    return $output
}

# ---- Preflight: everything that can fail without Docker or a build fails first. ----
$evidence = Resolve-UnderArtifacts $EvidenceRoot 'Rehearsal evidence'
if (Test-Path -LiteralPath $evidence)
{
    throw "Rehearsal evidence '$evidence' already exists. Preserve it and choose a new -EvidenceRoot."
}
$packageEvidence = Resolve-UnderArtifacts $CandidatePackageRoot 'Candidate package evidence'
$head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit)
{
    throw "Checked-out commit '$head' is not the rehearsal candidate '$ExpectedCommit'."
}
$dirty = @(& git -C $repositoryRoot status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0)
{
    throw 'Recovery rehearsals require a clean tracked worktree at the candidate commit.'
}
$readiness = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'v1.1-candidate-readiness.json') -Raw | ConvertFrom-Json
$image = [string]$readiness.endurancePostgreSqlImage
if ($image -notmatch '^[a-z0-9./:-]+@sha256:[0-9a-f]{64}$')
{
    throw "The rehearsal PostgreSQL image '$image' is not digest pinned."
}
$slos = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'v1-production-slos.json') -Raw | ConvertFrom-Json
$objective = @($slos.recoveryObjectives | Where-Object id -ceq 'streams-relay')
$rtoMatch = if ($objective.Count -eq 1) { [regex]::Match([string]$objective[0].rto, '^(\d+)m$') } else { $null }
if ($null -eq $rtoMatch -or -not $rtoMatch.Success -or [string]$objective[0].rpo -notmatch '^0 ')
{
    throw 'The streams-relay recovery objective must declare an RTO in minutes and a zero RPO.'
}
$rtoSeconds = [int]$rtoMatch.Groups[1].Value * 60
$rpoSeconds = 0

$null = Invoke-Native 'docker' @('version', '--format', '{{.Server.Version}}') 'Docker is unavailable'
& (Join-Path $PSScriptRoot 'verify-v1-package-evidence.ps1') -ReleaseTrack Core `
    -EvidenceRoot $packageEvidence -ExpectedCommit $ExpectedCommit | Out-Null
$packageManifestPath = Join-Path $packageEvidence 'package-manifest.json'
$candidateFeed = Join-Path $packageEvidence 'packages'

[IO.Directory]::CreateDirectory($evidence) | Out-Null
$work = Join-Path $evidence '_work'
$phaseRoot = Join-Path $evidence 'phases'
[IO.Directory]::CreateDirectory($work) | Out-Null
[IO.Directory]::CreateDirectory($phaseRoot) | Out-Null
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$containers = [Collections.Generic.List[string]]::new()
$phaseRecords = [Collections.Generic.List[object]]::new()
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
$startedUtc = [DateTimeOffset]::UtcNow

function Build-Probe([string] $Name, [string] $Version, [string] $Feed)
{
    # The probe source is copied so the candidate and rollback builds never share obj/bin state, and
    # each restore uses an isolated package folder so a cached package can never stand in.
    $root = Join-Path $work "probe-$Name"
    $source = Join-Path $root 'src'
    [IO.Directory]::CreateDirectory($source) | Out-Null
    foreach ($file in @('CoreRecoveryProbe.csproj', 'Program.cs'))
    {
        [IO.File]::Copy((Join-Path $PSScriptRoot "CoreRecoveryProbe/$file"), (Join-Path $source $file))
    }
    $configuration = Join-Path $root 'nuget.config'
    $sources = if ($null -eq $Feed)
    {
        '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
    }
    else
    {
        "<add key=`"BlueTuskCandidate`" value=`"$([Security.SecurityElement]::Escape($Feed))`" />" +
        '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
    }
    $mapping = if ($null -eq $Feed) { '' }
    else
    {
        '<packageSourceMapping><packageSource key="BlueTuskCandidate"><package pattern="BlueTusk.*" /></packageSource>' +
        '<packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>'
    }
    [IO.File]::WriteAllText($configuration,
        "<?xml version=`"1.0`" encoding=`"utf-8`"?><configuration><packageSources><clear />$sources</packageSources>$mapping</configuration>")
    $project = Join-Path $source 'CoreRecoveryProbe.csproj'
    $packages = Join-Path $root 'packages'
    $null = Invoke-Native 'dotnet' @('restore', $project, '--configfile', $configuration, '--packages', $packages,
        "-p:BlueTuskPackageVersion=$Version") "Restoring the $Name probe failed"
    $binary = Join-Path $root 'bin'
    $null = Invoke-Native 'dotnet' @('publish', $project, '--no-restore', '-c', 'Release', '-o', $binary,
        "-p:BlueTuskPackageVersion=$Version") "Building the $Name probe failed"

    $assets = Get-Content -LiteralPath (Join-Path $source 'obj/project.assets.json') -Raw | ConvertFrom-Json
    $resolved = @($assets.libraries.PSObject.Properties | Where-Object {
            $_.Value.type -eq 'package' -and $_.Name.StartsWith('BlueTusk.', [StringComparison]::Ordinal)
        } | Sort-Object Name | ForEach-Object {
            $separator = $_.Name.LastIndexOf('/')
            [ordered]@{ id = $_.Name.Substring(0, $separator); version = $_.Name.Substring($separator + 1); sha512 = [string]$_.Value.sha512 }
        })
    foreach ($id in $probePackages)
    {
        if (@($resolved | Where-Object { $_.id -ceq $id -and $_.version -ceq $Version }).Count -ne 1)
        {
            throw "The $Name probe did not resolve exactly $id $Version."
        }
    }
    $wrong = @($resolved | Where-Object { $_.version -cne $Version })
    if ($wrong.Count -ne 0)
    {
        throw "The $Name probe mixed BlueTusk versions: $(($wrong | ForEach-Object { "$($_.id) $($_.version)" }) -join ', ')."
    }
    if ($null -ne $Feed)
    {
        # Bind every resolved candidate package to the exact nupkg recorded by the candidate manifest.
        foreach ($package in $resolved)
        {
            $nupkg = Join-Path $Feed "$($package.id).$($package.version).nupkg"
            if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf))
            {
                throw "Resolved candidate package '$($package.id)' is not in the verified candidate feed."
            }
            $digest = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($nupkg)))
            if ($digest -cne $package.sha512)
            {
                throw "Resolved candidate package '$($package.id)' differs from the verified candidate nupkg."
            }
        }
    }
    return [pscustomobject]@{ Name = $Name; Version = $Version; Binary = $binary; Packages = $resolved }
}

function Start-Postgres([string] $Role)
{
    $name = "bluetusk-recovery-$runId-$Role"
    $env:POSTGRES_PASSWORD = $password
    try
    {
        $null = Invoke-Native 'docker' @('run', '-d', '--name', $name,
            '--label', "bluetusk.owner=$ContainerOwnerLabel", '--label', "bluetusk.run=$runId",
            '-e', 'POSTGRES_PASSWORD', '-e', 'POSTGRES_USER=postgres', '-e', 'POSTGRES_DB=rehearsal',
            '-p', '127.0.0.1::5432', $image) "Starting the $Role PostgreSQL failed"
    }
    finally
    {
        Remove-Item Env:\POSTGRES_PASSWORD -ErrorAction SilentlyContinue
    }
    $containers.Add($name)
    $ready = $false
    for ($attempt = 0; $attempt -lt 300; $attempt++)
    {
        # The image's initialisation server listens on the socket only, so TCP readiness means the final server.
        & docker exec $name pg_isready -h 127.0.0.1 -U postgres -d rehearsal *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw "The $Role PostgreSQL did not become ready." }
    $port = ((Invoke-Native 'docker' @('port', $name, '5432/tcp') 'Reading the PostgreSQL port failed') |
        Select-Object -First 1).ToString().Split(':')[-1]
    return [pscustomobject]@{ Name = $name; Port = [int]$port }
}

function Invoke-Sql([object] $Server, [string] $Sql)
{
    $result = Invoke-Native 'docker' @('exec', $Server.Name, 'psql', '-U', 'postgres', '-d', 'rehearsal',
        '-v', 'ON_ERROR_STOP=1', '-Atc', $Sql) "SQL on $($Server.Name) failed"
    return @($result | ForEach-Object { $_.ToString() })
}

function Get-Inventory([object] $Server)
{
    $filter = "n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg\_toast%' AND n.nspname NOT LIKE 'pg\_temp%'"
    $objects = [long](Invoke-Sql $Server (
            "SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE $filter) + " +
            "(SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE $filter) + " +
            "(SELECT count(*) FROM pg_namespace n WHERE $filter AND n.nspname <> 'public')"))[0]
    $tables = [ordered]@{}
    foreach ($table in @(Invoke-Sql $Server ("SELECT format('%I.%I', schemaname, tablename) FROM pg_tables " +
                "WHERE schemaname NOT IN ('pg_catalog', 'information_schema') ORDER BY 1")))
    {
        if ([string]::IsNullOrWhiteSpace($table)) { continue }
        $row = (Invoke-Sql $Server ("SELECT count(*) || ' ' || md5(coalesce(string_agg(t::text, E'\n' ORDER BY t::text), '')) FROM $table t"))[0]
        $parts = $row.Split(' ')
        $tables[$table] = [ordered]@{ rows = [long]$parts[0]; md5 = $parts[1] }
    }
    $rows = 0L
    foreach ($entry in $tables.Values) { $rows += $entry.rows }
    return [pscustomobject]@{ Objects = $objects; Rows = $rows; Tables = $tables }
}

function Invoke-Probe([object] $Probe, [string] $Phase, [object] $Server, [string] $Label)
{
    $index = $phaseRecords.Count + 1
    $reportPath = Join-Path $phaseRoot ('{0:D2}-{1}-{2}.json' -f $index, $Label, $Phase)
    $statePath = Join-Path $work 'probe-state.json'
    $applicationName = "bluetusk-rehearsal-$Label"
    # The throwaway server is reachable only on loopback and has no certificate, so TLS is disabled
    # explicitly (BlueTusk defaults to SSL Mode=VerifyFull).
    $env:BLUETUSK_REHEARSAL_CONNECTION_STRING =
        "Host=127.0.0.1;Port=$($Server.Port);Database=rehearsal;Username=postgres;Password=$password;SSL Mode=Disable;Channel Binding=Disable;Application Name=$applicationName"
    try
    {
        $output = & dotnet (Join-Path $Probe.Binary 'CoreRecoveryProbe.dll') $Phase $statePath $reportPath 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally
    {
        Remove-Item Env:\BLUETUSK_REHEARSAL_CONNECTION_STRING -ErrorAction SilentlyContinue
    }
    # Retain the probe console output next to its report; it never contains the connection string.
    $logPath = [IO.Path]::ChangeExtension($reportPath, '.log')
    [IO.File]::WriteAllLines($logPath, [string[]]@($output | ForEach-Object { $_.ToString().Replace($password, '***') }))
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf))
    {
        throw "The $Label $Phase probe produced no report (exit $exitCode): $(($output | Select-Object -Last 20) -join [Environment]::NewLine)"
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if ([string]$report.observations.packageVersion -cne $Probe.Version)
    {
        throw "The $Label probe ran BlueTusk $($report.observations.packageVersion), not $($Probe.Version)."
    }
    $phaseRecords.Add([ordered]@{
            binary = $Label; version = $Probe.Version; phase = $Phase; applicationName = $applicationName
            path = "phases/$([IO.Path]::GetFileName($reportPath))"; sha256 = Get-Sha256 $reportPath
            passed = ($exitCode -eq 0 -and $report.passed -eq $true)
        })
    return [pscustomobject]@{ Report = $report; Passed = ($exitCode -eq 0 -and $report.passed -eq $true); ApplicationName = $applicationName }
}

function Protect-File([string] $PlainPath, [string] $CipherPath, [byte[]] $Key)
{
    $plain = [IO.File]::ReadAllBytes($PlainPath)
    $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
    $tag = [byte[]]::new(16)
    $cipher = [byte[]]::new($plain.Length)
    $aes = [Security.Cryptography.AesGcm]::new($Key, 16)
    try { $aes.Encrypt($nonce, $plain, $cipher, $tag) } finally { $aes.Dispose() }
    $magic = [Text.Encoding]::ASCII.GetBytes('BTRB1')
    $stream = [IO.File]::Open($CipherPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try { foreach ($part in @($magic, $nonce, $tag, $cipher)) { $stream.Write($part, 0, $part.Length) } }
    finally { $stream.Dispose() }
    [Array]::Clear($plain)
}

function Unprotect-File([string] $CipherPath, [string] $PlainPath, [byte[]] $Key)
{
    $bytes = [IO.File]::ReadAllBytes($CipherPath)
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 5) -cne 'BTRB1') { throw 'The encrypted backup has an unknown format.' }
    $nonce = $bytes[5..16]
    $tag = $bytes[17..32]
    $cipher = [byte[]]::new($bytes.Length - 33)
    [Array]::Copy($bytes, 33, $cipher, 0, $cipher.Length)
    $plain = [byte[]]::new($cipher.Length)
    $aes = [Security.Cryptography.AesGcm]::new($Key, 16)
    try { $aes.Decrypt([byte[]]$nonce, $cipher, [byte[]]$tag, $plain) } finally { $aes.Dispose() }
    [IO.File]::WriteAllBytes($PlainPath, $plain)
    [Array]::Clear($plain)
}

$result = [ordered]@{
    schemaVersion = 1
    rehearsal = $Rehearsal
    scope = 'Core'
    releaseVersion = $candidateVersion
    candidateCommit = $ExpectedCommit
    operator = $Operator
    startedUtc = $startedUtc.ToString('o')
    postgreSqlImage = $image
    candidatePackageManifestSha256 = Get-Sha256 $packageManifestPath
    probeSourceSha256 = [ordered]@{
        'CoreRecoveryProbe.csproj' = Get-Sha256 (Join-Path $PSScriptRoot 'CoreRecoveryProbe/CoreRecoveryProbe.csproj')
        'Program.cs' = Get-Sha256 (Join-Path $PSScriptRoot 'CoreRecoveryProbe/Program.cs')
    }
}
$passed = $false
try
{
    $candidate = Build-Probe 'candidate' $candidateVersion $candidateFeed
    $result.candidatePackages = $candidate.Packages
    if ($Rehearsal -eq 'BackupRestore')
    {
        $source = Start-Postgres 'source'
        $seed = Invoke-Probe $candidate 'seed' $source 'candidate'
        $workload = Invoke-Probe $candidate 'advance' $source 'candidate'
        if (-not $seed.Passed -or -not $workload.Passed) { throw 'The candidate workload failed before the backup; see the phase reports.' }
        $sourceInventory = Get-Inventory $source
        $sourceCheckpoint = [string]$workload.Report.observations.checkpointAfter
        $acknowledgedOrder = [long]$workload.Report.observations.ordersAcknowledgedAfter

        # Backup: logical, consistent snapshot, encrypted at rest with a key that never touches disk.
        $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $backupStartedUtc = [DateTimeOffset]::UtcNow
        $null = Invoke-Native 'docker' @('exec', $source.Name, 'pg_dump', '-U', 'postgres', '-d', 'rehearsal', '-Fc',
            '-f', '/tmp/rehearsal.dump') 'pg_dump failed'
        $plainBackup = Join-Path $work 'rehearsal.dump'
        $null = Invoke-Native 'docker' @('cp', "$($source.Name):/tmp/rehearsal.dump", $plainBackup) 'Copying the backup failed'
        $null = Invoke-Native 'docker' @('exec', $source.Name, 'rm', '-f', '/tmp/rehearsal.dump') 'Removing the in-container backup failed'
        [IO.Directory]::CreateDirectory((Join-Path $evidence 'backup')) | Out-Null
        $encryptedBackup = Join-Path $evidence 'backup/rehearsal.dump.enc'
        Protect-File $plainBackup $encryptedBackup $key
        [IO.File]::Delete($plainBackup)
        $backupCompletedUtc = [DateTimeOffset]::UtcNow
        $backupSha = Get-Sha256 $encryptedBackup
        $backupId = "core-rehearsal-$($backupSha.Substring(0, 16))"

        # Restore into a fresh, empty, isolated server; never over the source.
        $target = Start-Postgres 'target'
        $restoreStarted = [Diagnostics.Stopwatch]::StartNew()
        $targetBefore = Get-Inventory $target
        $targetEmpty = $targetBefore.Objects -eq 0 -and $targetBefore.Tables.Count -eq 0
        $plainRestore = Join-Path $work 'restore.dump'
        Unprotect-File $encryptedBackup $plainRestore $key
        [Array]::Clear($key)
        $null = Invoke-Native 'docker' @('cp', $plainRestore, "$($target.Name):/tmp/restore.dump") 'Copying the backup into the target failed'
        [IO.File]::Delete($plainRestore)
        $null = Invoke-Native 'docker' @('exec', $target.Name, 'pg_restore', '-U', 'postgres', '-d', 'rehearsal',
            '--exit-on-error', '--no-owner', '/tmp/restore.dump') 'pg_restore failed'
        $null = Invoke-Native 'docker' @('exec', $target.Name, 'rm', '-f', '/tmp/restore.dump') 'Removing the in-container restore file failed'
        $restoredInventory = Get-Inventory $target
        $reconciliation = Invoke-Probe $candidate 'verify' $target 'candidate'
        $restoreStarted.Stop()

        $mismatches = 0
        foreach ($table in @($sourceInventory.Tables.Keys + $restoredInventory.Tables.Keys | Sort-Object -Unique))
        {
            $left = $sourceInventory.Tables[$table]
            $right = $restoredInventory.Tables[$table]
            if ($null -eq $left -or $null -eq $right -or $left.rows -ne $right.rows -or $left.md5 -cne $right.md5) { $mismatches++ }
        }
        $restoredMaxOrder = [long](Invoke-Sql $target 'SELECT coalesce(max(id), 0) FROM rehearsal.orders WHERE phase NOT LIKE ''verify-%''')[0]
        # Recovery-point gap on the source clock: how much acknowledged work the restore lacks.
        $gap = if ($restoredMaxOrder -ge $acknowledgedOrder) { 0.0 }
        else
        {
            [double](Invoke-Sql $source (
                    "SELECT extract(epoch FROM (SELECT created_utc FROM rehearsal.orders WHERE id = $acknowledgedOrder) - " +
                    "coalesce((SELECT created_utc FROM rehearsal.orders WHERE id = $restoredMaxOrder), " +
                    "(SELECT min(created_utc) FROM rehearsal.orders) - interval '1 second'))"))[0]
        }
        $restoredCheckpoint = [string]$reconciliation.Report.observations.checkpointBefore
        $details = [ordered]@{
            backupId = $backupId
            operator = $Operator
            backupEncrypted = $true
            restoreTargetEmpty = $targetEmpty
            sourceObjectCount = $sourceInventory.Objects
            restoredObjectCount = $restoredInventory.Objects
            sourceRowCount = $sourceInventory.Rows
            restoredRowCount = $restoredInventory.Rows
            sourceCheckpointPosition = $sourceCheckpoint
            restoredCheckpointPosition = $restoredCheckpoint
            rpoSeconds = $rpoSeconds
            observedRecoveryPointGapSeconds = [Math]::Max(0, $gap)
            rtoSeconds = $rtoSeconds
            observedRestoreSeconds = [Math]::Round($restoreStarted.Elapsed.TotalSeconds, 3)
            integrityMismatches = $mismatches
            reconciliationPassed = $reconciliation.Passed
        }
        $result.backup = [ordered]@{
            path = 'backup/rehearsal.dump.enc'; sha256 = $backupSha; bytes = (Get-Item -LiteralPath $encryptedBackup).Length
            encryption = 'AES-256-GCM, ephemeral key held only in memory for the run'
            startedUtc = $backupStartedUtc.ToString('o'); completedUtc = $backupCompletedUtc.ToString('o')
        }
        $result.inventory = [ordered]@{ source = $sourceInventory.Tables; restored = $restoredInventory.Tables }
        $passed = $targetEmpty -and $sourceInventory.Objects -eq $restoredInventory.Objects -and
            $sourceInventory.Rows -eq $restoredInventory.Rows -and $sourceCheckpoint -ceq $restoredCheckpoint -and
            $mismatches -eq 0 -and $reconciliation.Passed -and $details.observedRecoveryPointGapSeconds -le $rpoSeconds -and
            $details.observedRestoreSeconds -le $rtoSeconds
    }
    else
    {
        $rollback = Build-Probe 'rollback' $RollbackVersion $null
        $result.rollbackPackages = $rollback.Packages
        $server = Start-Postgres 'rollback'
        $seed = Invoke-Probe $rollback 'seed' $server 'previous-release'
        $upgrade = Invoke-Probe $candidate 'advance' $server 'candidate'
        if (-not $seed.Passed -or -not $upgrade.Passed) { throw 'The previous release or the candidate upgrade failed before rollback; see the phase reports.' }

        # Trigger: the candidate is withdrawn. Its process has stopped; prove it drained every session
        # and released its leases before the previous release takes over the same database.
        $rollbackClock = [Diagnostics.Stopwatch]::StartNew()
        $remaining = -1
        for ($attempt = 0; $attempt -lt 50; $attempt++)
        {
            $remaining = [int](Invoke-Sql $server ("SELECT count(*) FROM pg_stat_activity WHERE application_name = '$($upgrade.ApplicationName)'"))[0]
            if ($remaining -eq 0) { break }
            Start-Sleep -Milliseconds 200
        }
        $after = Invoke-Probe $rollback 'verify' $server 'previous-release'
        $rollbackClock.Stop()
        $checks = $after.Report.checks
        $observations = $after.Report.observations
        $check = { param([string] $Name) $null -ne $checks.PSObject.Properties[$Name] -and $checks.$Name -eq $true }
        $dataLoss = [long]$observations.missingAcknowledgedOrders + [long]$observations.orderIntegrityMismatches
        $details = [ordered]@{
            candidateVersion = [string]$upgrade.Report.observations.packageVersion
            rollbackVersion = [string]$after.Report.observations.packageVersion
            trigger = $Trigger
            decisionAuthority = $DecisionAuthority
            durationSeconds = [Math]::Round($rollbackClock.Elapsed.TotalSeconds, 3)
            versionCompatibilityPassed = ([string]$seed.Report.observations.packageVersion -ceq $RollbackVersion -and
                [string]$upgrade.Report.observations.packageVersion -ceq $candidateVersion -and
                [string]$after.Report.observations.packageVersion -ceq $RollbackVersion -and (& $check 'schemaInitialized'))
            connectionDrainPassed = ($remaining -eq 0)
            durableFormatCompatibilityPassed = ((& $check 'schemaInitialized') -and (& $check 'ordersReconciled') -and
                (& $check 'streamsCheckpointReconciled') -and (& $check 'liveReplayIntegrity') -and
                (& $check 'controlPlaneReconciled') -and (& $check 'syncReconciled'))
            relayCheckpointOwnershipPassed = (& $check 'streamsOwnershipFenced')
            liveClientResetPassed = ((& $check 'liveStarted') -and (& $check 'liveClientResumedOrReset') -and (& $check 'liveFreshClientConnected'))
            controlPlaneFencingPassed = (& $check 'controlPlaneFencingEnforced')
            reconciliationPassed = $after.Passed
            dataLossEvents = $dataLoss
        }
        $passed = $after.Passed -and $details.versionCompatibilityPassed -and $details.connectionDrainPassed -and
            $details.durableFormatCompatibilityPassed -and $details.relayCheckpointOwnershipPassed -and
            $details.liveClientResetPassed -and $details.controlPlaneFencingPassed -and $dataLoss -eq 0
    }
    $result.details = $details
}
catch
{
    $result.error = $_.Exception.Message
    throw
}
finally
{
    $result.phases = @($phaseRecords)
    $result.completedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $result.passed = $passed
    foreach ($container in $containers)
    {
        $labels = (& docker inspect $container --format '{{json .Config.Labels}}' 2>$null) | ConvertFrom-Json
        if ($LASTEXITCODE -eq 0 -and $labels.'bluetusk.owner' -ceq $ContainerOwnerLabel -and $labels.'bluetusk.run' -ceq $runId)
        {
            & docker rm -f $container *> $null
        }
    }
    if (Test-Path -LiteralPath $work)
    {
        # Probe state holds the run's disposable Live signing key; plaintext dumps were already removed.
        [IO.Directory]::Delete($work, $true)
    }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $evidence 'rehearsal-report.json') -Encoding utf8NoBOM
}

if (-not $passed)
{
    throw "The $Rehearsal rehearsal did not pass. Evidence of the failure is retained in '$evidence'."
}
$details | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'approval-details.json') -Encoding utf8NoBOM
& (Join-Path $PSScriptRoot 'verify-core-recovery-rehearsal.ps1') -Rehearsal $Rehearsal -EvidenceRoot $evidence -ExpectedCommit $ExpectedCommit
