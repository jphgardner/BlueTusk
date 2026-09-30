param(
    [ValidateRange(1,30)][int]$CellSeconds = 3,
    [ValidateRange(1,21600)][int]$SustainedSeconds = 60,
    [ValidateRange(1024,65535)][int]$Port = 55818,
    [string]$OutputDirectory,
    [string]$ReferenceHost = 'local-Windows-DotNet10-Docker-PG18-cpu4-mem2GiB',
    [ValidateSet('PackageDefaults','ToastVacuumFast')][string]$MaintenanceProfile = 'PackageDefaults',
    [ValidateSet('InlineJsonb','AttachedContent')][string]$StorageMode = 'InlineJsonb',
    [ValidateSet('None','Once','Three')][string]$ObserverTimeoutFault = 'None',
    [ValidateSet('None','Timeout')][string]$PostWriteFault = 'None',
    [ValidateSet('None','Once','Three')][string]$CleanupFault = 'None',
    [ValidateRange(1048576,1099511627776)][long]$MaximumDatabaseBytes = 25769803776,
    [ValidateRange(1048576,1099511627776)][long]$MinimumFilesystemAvailableBytes = 8589934592,
    [ValidateRange(0,300)][int]$IdleDrainSeconds = 120,
    [switch]$NoBuild,
    [switch]$Microbenchmarks
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$fixture = 'documents-load-' + [guid]::NewGuid().ToString('N').Substring(0,16)
$container = "$fixture-pg18"
$volume = "$fixture-data"
$owner = 'bluetusk.documents.load'
$image = 'postgres@sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873'
$dockerCommand = (Get-Command docker -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts/documents-load/$fixture" } else { [IO.Path]::GetFullPath($OutputDirectory) }
$envNames = @('BLUETUSK_DOCUMENTS_LOAD_CONNECTION_STRING','BLUETUSK_DOCUMENTS_LOAD_CELL_SECONDS','BLUETUSK_DOCUMENTS_LOAD_SECONDS','BLUETUSK_DOCUMENTS_LOAD_STORAGE_MODE','BLUETUSK_DOCUMENTS_LOAD_REPORT','BLUETUSK_DOCUMENTS_LOAD_HOST','BLUETUSK_DOCUMENTS_LOAD_IMAGE','BLUETUSK_DOCUMENTS_LOAD_SOURCE_FINGERPRINT',
    'BLUETUSK_DOCUMENTS_LOAD_MAINTENANCE_PROFILE','BLUETUSK_DOCUMENTS_LOAD_MAX_DATABASE_BYTES','BLUETUSK_DOCUMENTS_LOAD_MIN_FILESYSTEM_BYTES','BLUETUSK_DOCUMENTS_LOAD_FILESYSTEM_SAMPLE','BLUETUSK_DOCUMENTS_LOAD_IDLE_DRAIN_SECONDS','BLUETUSK_DOCUMENTS_LOAD_OBSERVER_FAULT','BLUETUSK_DOCUMENTS_LOAD_POST_WRITE_FAULT','BLUETUSK_DOCUMENTS_LOAD_CLEANUP_FAULT')
$previous = @{}
foreach ($name in $envNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process') }
$observerProcess = $null
$guardSample = Join-Path $output 'filesystem-observations'
$guardStop = Join-Path $output 'stop-filesystem-observer.signal'
function FixtureDocker([string[]]$Arguments) {
    $result = & $dockerCommand @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Owned Documents Docker operation failed: $($Arguments[0]) ($result)" }
    return $result
}
function RemoveOwned([string]$Kind, [string]$Name) {
    $template = if ($Kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $labels = & $dockerCommand $Kind inspect $Name --format $template 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    if ($actual.'bluetusk.owner' -ne $owner -or $actual.'bluetusk.fixture' -ne $fixture) { throw "Refusing to remove a resource outside the owned Documents fixture: $Name" }
    if ($Kind -eq 'container') { FixtureDocker -Arguments @('rm','-f',$Name) | Out-Null }
    else { FixtureDocker -Arguments @('volume','rm',$Name) | Out-Null }
}
try {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    if ((Test-Path -LiteralPath $guardSample) -or (Test-Path -LiteralPath $guardStop)) {
        throw 'Choose a fresh owned Documents output directory for the filesystem observer.'
    }
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port)
    try { $listener.Start() } catch { throw "Documents fixture loopback port $Port is in use." } finally { $listener.Stop() }
    FixtureDocker -Arguments @('volume','create','--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",$volume) | Out-Null
    FixtureDocker -Arguments @('run','-d','--name',$container,'--label',"bluetusk.owner=$owner",'--label',"bluetusk.fixture=$fixture",
        '--cpus','4','--memory','2g','-p',"127.0.0.1:${Port}:5432",'-v',"${volume}:/var/lib/postgresql/data",'-e','PGDATA=/var/lib/postgresql/data/pgdata',
        '-e','POSTGRES_PASSWORD=postgres','-e','POSTGRES_DB=documents_load',$image,'postgres','-c','track_io_timing=on','-c','max_connections=40') | Out-Null # ggignore
    $ready = $false
    for ($attempt=0; $attempt -lt 150; $attempt++) {
        & $dockerCommand exec $container pg_isready -U postgres -d documents_load *> $null
        if ($LASTEXITCODE -eq 0) { $ready=$true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Owned Documents PostgreSQL fixture did not become ready.' }
    $project = Join-Path $root 'benchmarks/BlueTusk.Documents.LoadHarness/BlueTusk.Documents.LoadHarness.csproj'
    if (-not $NoBuild) {
        & dotnet build $project -c Release -nr:false -p:BuildProjectReferences=false
        if ($LASTEXITCODE -ne 0) { throw 'Documents workload build failed.' }
    }
    $candidatePaths = @(& git -C $root ls-files --cached --others --exclude-standard) | Sort-Object -Unique
    if ($LASTEXITCODE -ne 0) { throw 'Candidate source inventory failed.' }
    $beforeHashes = @{}
    $fingerprintLines = foreach ($path in $candidatePaths) {
        $absolute = Join-Path $root $path
        $hash = if (Test-Path -LiteralPath $absolute -PathType Leaf) { (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash } else { 'missing' }
        $beforeHashes[$path] = $hash
        "$path $hash"
    }
    $fingerprintText = [Text.Encoding]::UTF8.GetBytes(($fingerprintLines -join "`n"))
    $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($fingerprintText))
    $fingerprintLines | Set-Content -LiteralPath (Join-Path $output 'candidate-inputs.sha256') -Encoding utf8
    $prefixes = @('src/BlueTusk.Documents/','src/BlueTusk.Data/','src/BlueTusk.TypeSystem/','benchmarks/BlueTusk.Documents.LoadHarness/')
    $common = @('Directory.Build.props','Directory.Build.targets','Directory.Packages.props','.editorconfig','global.json','NuGet.Config','nuget.config','eng/versions/Documents.props','eng/versions/Provider.props','eng/run-documents-load.ps1','eng/watch-documents-fixture-space.ps1')
    $scopedPaths = @($candidatePaths | Where-Object {
        $path = $_; ($common -contains $path) -or @($prefixes | Where-Object { $path.StartsWith($_,[StringComparison]::Ordinal) }).Count -gt 0
    })
    $sourceSnapshot = Join-Path $output 'runtime-source-before'
    foreach ($path in $scopedPaths) {
        $absolute = Join-Path $root $path
        if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) { continue }
        $destination = Join-Path $sourceSnapshot $path
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $absolute -Destination $destination
    }
    $binaryDirectory = Join-Path $root 'benchmarks/BlueTusk.Documents.LoadHarness/bin/Release/net10.0'
    $binarySnapshot = Join-Path $output 'runtime-bin-before'
    New-Item -ItemType Directory -Path $binarySnapshot -Force | Out-Null
    $binaryHashes = @(foreach ($file in Get-ChildItem -LiteralPath $binaryDirectory -File) {
        if ($file.Extension -notin @('.dll','.json','.pdb','.exe')) { continue }
        $destination = Join-Path $binarySnapshot $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        [ordered]@{ Name = $file.Name; Sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash }
    })
    $env:BLUETUSK_DOCUMENTS_LOAD_CONNECTION_STRING = "Host=127.0.0.1;Port=$Port;Username=postgres;Password=postgres;Database=documents_load;SSL Mode=Disable;Channel Binding=Disable" # ggignore
    $env:BLUETUSK_DOCUMENTS_LOAD_CELL_SECONDS = $CellSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_DOCUMENTS_LOAD_SECONDS = $SustainedSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_DOCUMENTS_LOAD_STORAGE_MODE = $StorageMode
    $env:BLUETUSK_DOCUMENTS_LOAD_REPORT = Join-Path $output 'documents-load.json'
    $env:BLUETUSK_DOCUMENTS_LOAD_HOST = $ReferenceHost
    $env:BLUETUSK_DOCUMENTS_LOAD_IMAGE = $image
    $env:BLUETUSK_DOCUMENTS_LOAD_SOURCE_FINGERPRINT = $fingerprint
    $env:BLUETUSK_DOCUMENTS_LOAD_MAINTENANCE_PROFILE = $MaintenanceProfile
    $env:BLUETUSK_DOCUMENTS_LOAD_MAX_DATABASE_BYTES = $MaximumDatabaseBytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_DOCUMENTS_LOAD_MIN_FILESYSTEM_BYTES = $MinimumFilesystemAvailableBytes.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_DOCUMENTS_LOAD_FILESYSTEM_SAMPLE = $guardSample
    $env:BLUETUSK_DOCUMENTS_LOAD_IDLE_DRAIN_SECONDS = $IdleDrainSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    $env:BLUETUSK_DOCUMENTS_LOAD_OBSERVER_FAULT = $ObserverTimeoutFault
    $env:BLUETUSK_DOCUMENTS_LOAD_POST_WRITE_FAULT = $PostWriteFault
    $env:BLUETUSK_DOCUMENTS_LOAD_CLEANUP_FAULT = $CleanupFault
    $watcher = Join-Path $PSScriptRoot 'watch-documents-fixture-space.ps1'
    $watcherStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $watcherStart.UseShellExecute = $false
    $watcherStart.CreateNoWindow = $true
    foreach ($argument in @('-NoLogo','-NoProfile','-NonInteractive','-File',$watcher,'-Container',$container,
        '-Fixture',$fixture,'-Output',$guardSample,'-StopFile',$guardStop,'-MaximumSeconds','24000')) {
        [void]$watcherStart.ArgumentList.Add([string]$argument)
    }
    $observerProcess = [Diagnostics.Process]::Start($watcherStart)
    if ($null -eq $observerProcess) { throw 'Owned Docker filesystem observer could not start.' }
    $watcherReady = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($observerProcess.HasExited) { throw 'Owned Docker filesystem observer stopped before its first sample.' }
        if ((Test-Path -LiteralPath $guardSample -PathType Container) -and
            (Test-Path -LiteralPath (Join-Path $guardSample '000001.json') -PathType Leaf)) { $watcherReady = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $watcherReady) { throw 'Owned Docker filesystem observer did not supply a timely initial sample.' }
    $executable = Join-Path $root 'benchmarks/BlueTusk.Documents.LoadHarness/bin/Release/net10.0/BlueTusk.Documents.LoadHarness.dll'
    & dotnet $executable 2>&1 | Tee-Object -FilePath (Join-Path $output 'workload.log')
    $harnessExitCode = $LASTEXITCODE
    if ($harnessExitCode -eq 0 -and $observerProcess.HasExited) { throw 'Owned Docker filesystem observer stopped before the successful campaign finished.' }
    $afterHashes = @{}
    $finalPaths = @(& git -C $root ls-files --cached --others --exclude-standard) | Sort-Object -Unique
    if ($LASTEXITCODE -ne 0) { throw 'Final candidate inventory failed.' }
    $finalLines = foreach ($path in $finalPaths) {
        $absolute = Join-Path $root $path
        $hash = if (Test-Path -LiteralPath $absolute -PathType Leaf) { (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash } else { 'missing' }
        $afterHashes[$path] = $hash
        "$path $hash"
    }
    $finalLines | Set-Content -LiteralPath (Join-Path $output 'candidate-inputs-final.sha256') -Encoding utf8
    $finalFingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($finalLines -join "`n"))))
    $changed = @($beforeHashes.Keys + $afterHashes.Keys | Sort-Object -Unique | Where-Object { $beforeHashes[$_] -ne $afterHashes[$_] })
    $scopedChanges = @($changed | Where-Object {
        $path = $_; ($common -contains $path) -or @($prefixes | Where-Object { $path.StartsWith($_,[StringComparison]::Ordinal) }).Count -gt 0
    })
    $evidencePath = if ($harnessExitCode -eq 0) { $env:BLUETUSK_DOCUMENTS_LOAD_REPORT } else { [IO.Path]::ChangeExtension($env:BLUETUSK_DOCUMENTS_LOAD_REPORT,'.failure.json') }
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf) -and $harnessExitCode -ne 0) {
        $fallback = [ordered]@{
            Status = 'unexpected-process-exit'; Code = 'no-executable-report'; HarnessExitCode = $harnessExitCode
            FailedUtc = [DateTimeOffset]::UtcNow.ToString('o'); SourceFingerprint = $fingerprint
            Assemblies = @($binaryHashes | Where-Object { $_.Name -like 'BlueTusk.*.dll' } | ForEach-Object {
                [ordered]@{ Name = [IO.Path]::GetFileNameWithoutExtension($_.Name); Sha256 = $_.Sha256 }
            })
        }
        $fallback | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    }
    if (-not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) { throw 'Documents executable produced no bounded success evidence.' }
    $rawEvidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
    $observedAssemblies = if ($harnessExitCode -eq 0) { $rawEvidence.Environment.Assemblies } else { $rawEvidence.Assemblies }
    $identities = @(foreach ($assembly in $observedAssemblies) {
        $snapshotEntry = @($binaryHashes | Where-Object Name -eq ($assembly.Name + '.dll'))
        if ($snapshotEntry.Count -ne 1 -or $snapshotEntry[0].Sha256 -ne $assembly.Sha256) { throw "Measured dependency differs from the pre-run executable snapshot: $($assembly.Name)" }
        [ordered]@{ Name = $assembly.Name; MeasurementStartSha256 = $assembly.Sha256; SnapshotSha256 = $snapshotEntry[0].Sha256 }
    })
    [ordered]@{
        CapturedUtc = [DateTimeOffset]::UtcNow.ToString('o'); Status = if ($harnessExitCode -eq 0) { 'success' } else { 'failed' }
        EvidenceFile = [IO.Path]::GetFileName($evidencePath); RawReportSha256 = (Get-FileHash -LiteralPath $evidencePath -Algorithm SHA256).Hash
        BeforeGlobalFingerprint = $fingerprint; AfterGlobalFingerprint = $finalFingerprint; GlobalCandidateUnchanged = ($changed.Count -eq 0)
        ChangedCandidatePaths = $changed; ScopedRuntimeSourcesUnchanged = ($scopedChanges.Count -eq 0); ChangedScopedPaths = $scopedChanges
        ScopedSourceEntries = @($scopedPaths | ForEach-Object { [ordered]@{ Path = $_; Before = $beforeHashes[$_]; After = $afterHashes[$_] } })
        MeasuredAssembliesMatchedSnapshot = $identities; SnapshotBinaryHashes = $binaryHashes
        SustainedCheckpointSha256 = if (Test-Path -LiteralPath ([IO.Path]::ChangeExtension($env:BLUETUSK_DOCUMENTS_LOAD_REPORT,'.sustained-checkpoint.json')) -PathType Leaf) {
            (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($env:BLUETUSK_DOCUMENTS_LOAD_REPORT,'.sustained-checkpoint.json')) -Algorithm SHA256).Hash
        } else { $null }
        Limits = 'Pre-run executable and scoped source snapshots are retained. Whole-tree drift and shared-host contention must be disclosed separately. This is local evidence, not production qualification.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'bindings.json') -Encoding utf8
    FixtureDocker -Arguments @('stats','--no-stream','--format','{{json .}}',$container) | Set-Content -LiteralPath (Join-Path $output 'docker-stats-final.json') -Encoding utf8
    FixtureDocker -Arguments @('exec',$container,'psql','-U','postgres','-d','documents_load','-At','-v','ON_ERROR_STOP=1','-c',"SELECT count(*) FROM pg_namespace WHERE nspname LIKE 'docs_load_%' OR nspname LIKE 'docs_crash_%'") | Set-Content -LiteralPath (Join-Path $output 'owned-schema-cleanup.txt') -Encoding utf8
    if ($harnessExitCode -ne 0) { throw 'Documents fixture campaign stopped; bounded failure/binary/source evidence is retained.' }
    if ($Microbenchmarks) {
        & dotnet $executable --microbenchmarks --job short --filter '*DocumentStagingBenchmarks*' --artifacts (Join-Path $output 'benchmarkdotnet') 2>&1 | Tee-Object -FilePath (Join-Path $output 'microbenchmarks.log')
        if ($LASTEXITCODE -ne 0) { throw 'Documents microbenchmark run failed.' }
    }
    Write-Output "Verified Documents campaign reports: $output"
}
finally {
    $containerRemoved = $false
    $volumeRemoved = $false
    try {
        try {
            if ($null -ne $observerProcess) {
                if (-not $observerProcess.HasExited) {
                    [IO.File]::WriteAllText($guardStop, 'stop', [Text.UTF8Encoding]::new($false))
                    if (-not $observerProcess.WaitForExit(5000)) {
                        $observerProcess.Kill($true)
                        [void]$observerProcess.WaitForExit(5000)
                    }
                }
                $observerProcess.Dispose()
            }
        }
        finally { RemoveOwned -Kind container -Name $container; $containerRemoved = $true }
    }
    finally {
        try { RemoveOwned -Kind volume -Name $volume; $volumeRemoved = $true }
        finally {
            foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
            if (Test-Path -LiteralPath $output -PathType Container) {
                [ordered]@{ Fixture = $fixture; CleanedUtc = [DateTimeOffset]::UtcNow.ToString('o')
                    LabelCheckedContainerRemoved = $containerRemoved; LabelCheckedVolumeRemoved = $volumeRemoved } |
                    ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $output 'owned-resource-cleanup.json') -Encoding utf8
            }
        }
    }
}
