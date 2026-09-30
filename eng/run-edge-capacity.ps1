[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [string]$OutputDirectory,
    [ValidateRange(0,3600)][int]$Seconds = 0,
    [ValidateRange(0,3)][int]$Repetitions = 0,
    [ValidateRange(1024,65535)][int]$PostgreSqlPort = 55838,
    [ValidateRange(1024,65535)][int]$HttpPort = 55839
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$budgetPath = Join-Path $PSScriptRoot 'edge-capacity-budgets.json'
$budget = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json -Depth 20
if ($Seconds -eq 0) { $Seconds = [int]$budget.minimumSecondsPerRun }
if ($Repetitions -eq 0) { $Repetitions = [int]$budget.minimumRuns }
if ($Seconds -lt 30) { throw 'An Edge diagnostic must offer at least 30 seconds.' }
if ($PostgreSqlPort -eq $HttpPort) { throw 'The PostgreSQL and HTTP fixture ports must differ.' }
$campaign = 'edge-capacity-' + [guid]::NewGuid().ToString('N').Substring(0,16)
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts/edge-capacity/$campaign" } else { [IO.Path]::GetFullPath($OutputDirectory) }
$environmentNames = @('BLUETUSK_EDGE_LOAD_CONNECTION_STRING','BLUETUSK_EDGE_LOAD_COMMIT','BLUETUSK_EDGE_LOAD_SOURCE_SHA256',
    'BLUETUSK_EDGE_LOAD_BINARY_SHA256','BLUETUSK_EDGE_LOAD_BROWSER_SHA256','BLUETUSK_EDGE_LOAD_IMAGE')
$previous = @{}
foreach ($name in $environmentNames) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 40 }
function Capture([string]$Path) {
    & python eng/capture-ecosystem-source.py --output $Path
    Require ($LASTEXITCODE -eq 0) 'Exact candidate source capture failed.'
}
function InvokeDocker([string[]]$Arguments) {
    $result = & docker @Arguments 2>&1
    Require ($LASTEXITCODE -eq 0) "Owned Edge fixture Docker operation failed: $($Arguments[0])."
    return $result
}
function RemoveOwned([string]$Kind, [string]$Name, [string]$Fixture) {
    $template = if ($Kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $labels = & docker $Kind inspect $Name --format $template 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    Require ($actual.'bluetusk.owner' -ceq 'edge-capacity' -and $actual.'bluetusk.fixture' -ceq $Fixture) "Refusing to remove a resource outside the owned Edge fixture: $Name"
    if ($Kind -eq 'container') { InvokeDocker @('rm','-f',$Name) | Out-Null }
    else { InvokeDocker @('volume','rm',$Name) | Out-Null }
}
function AssertPortFree([int]$Port) {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
    try { $listener.Start() } catch { throw "Edge fixture loopback port $Port is occupied." } finally { $listener.Stop() }
}

Push-Location -LiteralPath $root
try {
    Require ($ExpectedCommit -match '^[0-9a-fA-F]{40}$') 'ExpectedCommit must be a full 40-character SHA.'
    $head = (& git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'Checkout is not the requested commit.'
    Require (@(& git status --porcelain --untracked-files=normal).Count -eq 0) 'A clean exact-source checkout is required.'
    Require ($output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'OutputDirectory must be inside the checkout.'
    Require (-not (Test-Path -LiteralPath $output)) 'Choose a fresh Edge evidence directory.'
    Require ($budget.schemaVersion -eq 1 -and $budget.clients -eq 8 -and $budget.minimumRuns -eq 2 -and $budget.minimumSecondsPerRun -ge 1800) 'Unsupported Edge capacity budget contract.'
    Require (-not [string]::IsNullOrWhiteSpace($env:BLUETUSK_EDGE_BROWSER_CHANNEL)) 'Set BLUETUSK_EDGE_BROWSER_CHANNEL to the installed Playwright browser channel.'
    AssertPortFree $PostgreSqlPort; AssertPortFree $HttpPort
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item -LiteralPath $budgetPath -Destination (Join-Path $output 'budgets.json')
    Capture (Join-Path $output 'source-before.json')
    $source = Json (Join-Path $output 'source-before.json')
    Require ($source.dirty -eq $false -and $source.commit -ceq $head) 'Candidate source is not clean and exact.'
    & npm ci --ignore-scripts --no-audit --no-fund 2>&1 | Tee-Object -FilePath (Join-Path $output 'npm-install.log')
    Require ($LASTEXITCODE -eq 0) 'Locked browser dependencies failed to install.'
    & npm run build --workspace '@bluetusk/edge' 2>&1 | Tee-Object -FilePath (Join-Path $output 'browser-build.log')
    Require ($LASTEXITCODE -eq 0) 'The Edge browser client did not build.'
    $project = 'benchmarks/BlueTusk.Edge.LoadHarness/BlueTusk.Edge.LoadHarness.csproj'
    & dotnet build $project -c Release -nr:false --nologo 2>&1 | Tee-Object -FilePath (Join-Path $output 'build.log')
    Require ($LASTEXITCODE -eq 0) 'The Edge capacity executable did not build.'
    $bin = Join-Path $root 'benchmarks/BlueTusk.Edge.LoadHarness/bin/Release/net10.0'
    $dll = Join-Path $bin 'BlueTusk.Edge.LoadHarness.dll'
    Require (Test-Path -LiteralPath $dll -PathType Leaf) 'The Edge capacity executable is missing.'
    $binarySnapshot = Join-Path $output 'binary-snapshot'
    New-Item -ItemType Directory -Path $binarySnapshot -Force | Out-Null
    $binaries = @()
    foreach ($file in Get-ChildItem -LiteralPath $bin -File | Where-Object { $_.Extension -in @('.dll','.exe','.json') } | Sort-Object Name) {
        $copy = Join-Path $binarySnapshot $file.Name
        Copy-Item -LiteralPath $file.FullName -Destination $copy
        Require ((Hash $copy) -ceq (Hash $file.FullName)) 'The executable dependency snapshot changed.'
        $binaries += [ordered]@{ Name = $file.Name; Sha256 = (Hash $copy) }
    }
    Require ($binaries.Count -gt 0) 'The Edge binary snapshot is empty.'
    [ordered]@{ Files = $binaries } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $binarySnapshot 'binaries.json') -Encoding utf8
    $binarySha = Hash $dll
    $env:BLUETUSK_EDGE_LOAD_COMMIT = $head
    $env:BLUETUSK_EDGE_LOAD_SOURCE_SHA256 = $source.sourceTreeSha256
    $env:BLUETUSK_EDGE_LOAD_BINARY_SHA256 = $binarySha
    $env:BLUETUSK_EDGE_LOAD_IMAGE = $budget.postgreSqlImage
    [ordered]@{ CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256; PostgreSqlImage = $budget.postgreSqlImage;
        Processor = [string](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name);
        LogicalProcessors = [Environment]::ProcessorCount; OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription;
        DotNetSdk = (& dotnet --version); Node = (& node --version); DockerServer = (& docker version --format '{{.Server.Version}}');
        BrowserChannel = $env:BLUETUSK_EDGE_BROWSER_CHANNEL; DockerCpus = 4; DockerMemoryMiB = 2048;
        RequestedSeconds = $Seconds; Runs = $Repetitions } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'runner.json') -Encoding utf8
    Require ($LASTEXITCODE -eq 0) 'The Docker server is unavailable.'

    for ($run = 1; $run -le $Repetitions; $run++) {
        $fixture = "$campaign-$run"
        $container = "$fixture-pg18"
        $volume = "$fixture-data"
        $runRoot = Join-Path $output "run-$run"
        $assets = Join-Path $runRoot 'browser-assets'
        New-Item -ItemType Directory -Path $assets -Force | Out-Null
        Copy-Item -LiteralPath 'benchmarks/BlueTusk.Edge.LoadHarness/browser.mjs' -Destination (Join-Path $runRoot 'browser.mjs')
        $assetList = @()
        foreach ($name in @('index.js','http.js','ordered.js')) {
            $live = Join-Path 'clients/edge/dist' $name
            $copy = Join-Path $assets $name
            Copy-Item -LiteralPath $live -Destination $copy
            $assetList += [ordered]@{ Name = $name; Sha256 = (Hash $copy) }
        }
        $assetList += [ordered]@{ Name = 'browser.mjs'; Sha256 = (Hash (Join-Path $runRoot 'browser.mjs')) }
        [ordered]@{ Files = $assetList } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'browser-assets.json') -Encoding utf8
        $env:BLUETUSK_EDGE_LOAD_BROWSER_SHA256 = Hash (Join-Path $runRoot 'browser-assets.json')
        foreach ($entry in $binaries) {
            $live = Join-Path $bin $entry.Name
            Require ((Test-Path -LiteralPath $live -PathType Leaf) -and (Hash $live) -ceq $entry.Sha256) "An Edge binary changed before run $run."
        }
        AssertPortFree $PostgreSqlPort; AssertPortFree $HttpPort
        $fixturePassword = [Convert]::ToHexString(
            [Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
        try {
            InvokeDocker @('volume','create','--label','bluetusk.owner=edge-capacity','--label',"bluetusk.fixture=$fixture",$volume) | Out-Null
            InvokeDocker @('run','-d','--name',$container,'--label','bluetusk.owner=edge-capacity','--label',"bluetusk.fixture=$fixture",
                '--cpus','4','--memory','2g','-p',"127.0.0.1:${PostgreSqlPort}:5432",'-v',"${volume}:/var/lib/postgresql/data",
                '-e','PGDATA=/var/lib/postgresql/data/pgdata','-e',"POSTGRES_PASSWORD=$fixturePassword",'-e','POSTGRES_DB=edge_load',
                $budget.postgreSqlImage,'postgres','-c','track_io_timing=on','-c','max_connections=100') | Out-Null
            $ready = $false
            for ($attempt = 0; $attempt -lt 150; $attempt++) {
                & docker exec $container pg_isready -h 127.0.0.1 -U postgres -d edge_load *> $null
                if ($LASTEXITCODE -eq 0) { $ready = $true; break }
                Start-Sleep -Milliseconds 200
            }
            Require $ready 'The owned Edge PostgreSQL fixture did not become ready.'
            $env:BLUETUSK_EDGE_LOAD_CONNECTION_STRING = "Host=127.0.0.1;Port=$PostgreSqlPort;Username=postgres;Password=$fixturePassword;Database=edge_load;SSL Mode=Disable;Channel Binding=Disable"
            & dotnet $dll $Seconds $HttpPort (Join-Path $runRoot 'edge-ordered.json') $root 2>&1 |
                Tee-Object -FilePath (Join-Path $runRoot 'workload.log')
            Require ($LASTEXITCODE -eq 0) "Edge ordered run $run failed; bounded partial evidence is retained."
            foreach ($entry in $binaries) {
                $live = Join-Path $bin $entry.Name
                Require ((Test-Path -LiteralPath $live -PathType Leaf) -and (Hash $live) -ceq $entry.Sha256) "An Edge binary changed during run $run."
            }
            foreach ($entry in $assetList) {
                $live = if ($entry.Name -eq 'browser.mjs') { 'benchmarks/BlueTusk.Edge.LoadHarness/browser.mjs' } else { Join-Path 'clients/edge/dist' $entry.Name }
                Require ((Hash $live) -ceq $entry.Sha256) "A browser asset changed during run $run."
            }
        }
        finally {
            $env:BLUETUSK_EDGE_LOAD_CONNECTION_STRING = $null
            RemoveOwned 'container' $container $fixture
            RemoveOwned 'volume' $volume $fixture
        }
        Capture (Join-Path $runRoot 'source-after.json')
        $runSource = Json (Join-Path $runRoot 'source-after.json')
        Require ($runSource.dirty -eq $false -and $runSource.commit -ceq $head -and
            $runSource.sourceTreeSha256 -ceq $source.sourceTreeSha256) "Candidate source changed during Edge run $run."
    }
    Capture (Join-Path $output 'source-after.json')
    $after = Json (Join-Path $output 'source-after.json')
    Require ($after.dirty -eq $false -and $after.commit -ceq $head -and $after.sourceTreeSha256 -ceq $source.sourceTreeSha256) 'Candidate source changed during the Edge campaign.'
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\','/'); Sha256 = (Hash $_.FullName) }
    } | Sort-Object Path)
    $manifestPath = Join-Path $output 'manifest.json'
    [ordered]@{ SchemaVersion = 1; CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256;
        HarnessBinarySha256 = $binarySha; BudgetSha256 = (Hash $budgetPath); Runs = $Repetitions;
        QualifiedLocalCapacity = $false; ProductionQualified = $false; Files = $files } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    if ($Seconds -ge $budget.minimumSecondsPerRun -and $Repetitions -ge $budget.minimumRuns) {
        & (Join-Path $PSScriptRoot 'verify-edge-capacity.ps1') -EvidenceDirectory $output -ExpectedCommit $head -Candidate
        $manifest = Json $manifestPath
        $manifest.QualifiedLocalCapacity = $true
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        Write-Output "Edge local ordered capacity gate passed for $head. Production qualification remains false."
    } else {
        Write-Output "Edge diagnostic evidence retained at $output. Duration/repeats are below qualification minimums; no capacity pass is asserted."
    }
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    Pop-Location
}
