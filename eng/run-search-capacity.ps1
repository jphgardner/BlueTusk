[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedCommit,
    [string]$OutputDirectory,
    [ValidateRange(0,3600)][int]$Seconds = 0,
    [ValidateRange(0,3)][int]$Repetitions = 0,
    [ValidateRange(1024,65535)][int]$Port = 55828
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$budgetPath = Join-Path $PSScriptRoot 'search-capacity-budgets.json'
$budget = Get-Content -LiteralPath $budgetPath -Raw | ConvertFrom-Json -Depth 20
if ($Seconds -eq 0) { $Seconds = [int]$budget.minimumSecondsPerRun }
if ($Repetitions -eq 0) { $Repetitions = [int]$budget.minimumRuns }
if ($Seconds -lt 5) { throw 'Diagnostic duration must be at least five seconds.' }
$campaign = 'search-capacity-' + [guid]::NewGuid().ToString('N').Substring(0,16)
$output = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $root "artifacts/search-capacity/$campaign" } else { [IO.Path]::GetFullPath($OutputDirectory) }
$priorConnection = $env:BLUETUSK_SEARCH_LOAD_CONNECTION_STRING
$priorCommit = $env:BLUETUSK_SEARCH_LOAD_COMMIT
$priorSource = $env:BLUETUSK_SEARCH_LOAD_SOURCE_SHA256
$priorBinary = $env:BLUETUSK_SEARCH_LOAD_BINARY_SHA256
$priorImage = $env:BLUETUSK_SEARCH_LOAD_IMAGE

function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string]$Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 30 }
function Capture([string]$Path) {
    & python eng/capture-ecosystem-source.py --output $Path
    Require ($LASTEXITCODE -eq 0) 'Candidate source capture failed.'
}
function Docker([string[]]$Arguments) {
    $result = & docker @Arguments 2>&1
    Require ($LASTEXITCODE -eq 0) "Owned Search Docker operation failed: $($Arguments[0])."
    return $result
}
function RemoveOwned([string]$Kind, [string]$Name, [string]$Fixture) {
    $template = if ($Kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $labels = & docker $Kind inspect $Name --format $template 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    Require ($actual.'bluetusk.owner' -ceq 'search-capacity' -and $actual.'bluetusk.fixture' -ceq $Fixture) "Refusing to remove a Search resource outside this fixture: $Name"
    if ($Kind -eq 'container') { Docker @('rm','-f',$Name) | Out-Null }
    else { Docker @('volume','rm',$Name) | Out-Null }
}

Push-Location -LiteralPath $root
try {
    Require ($ExpectedCommit -match '^[0-9a-fA-F]{40}$') 'ExpectedCommit must be a full 40-character SHA.'
    $head = (& git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and [string]::Equals($head, $ExpectedCommit, [StringComparison]::OrdinalIgnoreCase)) 'The checkout is not the requested commit.'
    Require (@(& git status --porcelain --untracked-files=normal).Count -eq 0) 'A clean exact-source checkout is required.'
    Require ($output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) 'OutputDirectory must be inside the checkout.'
    Require (-not (Test-Path -LiteralPath $output)) 'Choose a fresh output directory.'
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Copy-Item -LiteralPath $budgetPath -Destination (Join-Path $output 'budgets.json')
    Capture (Join-Path $output 'source-before.json')
    $source = Json (Join-Path $output 'source-before.json')
    Require ($source.dirty -eq $false -and $source.commit -ceq $head) 'Candidate source capture is not clean or exact.'
    $project = 'benchmarks/BlueTusk.Search.LoadHarness/BlueTusk.Search.LoadHarness.csproj'
    & dotnet build $project -c Release -nr:false --nologo 2>&1 | Tee-Object -FilePath (Join-Path $output 'build.log')
    Require ($LASTEXITCODE -eq 0) 'Search load harness build failed.'
    $bin = Join-Path $root 'benchmarks/BlueTusk.Search.LoadHarness/bin/Release/net10.0'
    $dll = Join-Path $bin 'BlueTusk.Search.LoadHarness.dll'
    Require (Test-Path -LiteralPath $dll -PathType Leaf) 'Search load executable is missing.'
    $binarySnapshot = Join-Path $output 'binary-snapshot'
    New-Item -ItemType Directory -Path $binarySnapshot -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $bin -File | Where-Object { $_.Extension -in @('.dll','.exe','.json') }) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $binarySnapshot $file.Name)
    }
    $binarySha = Hash $dll
    Require ((Hash (Join-Path $binarySnapshot 'BlueTusk.Search.LoadHarness.dll')) -ceq $binarySha) 'The executable snapshot changed.'
    $env:BLUETUSK_SEARCH_LOAD_COMMIT = $head
    $env:BLUETUSK_SEARCH_LOAD_SOURCE_SHA256 = $source.sourceTreeSha256
    $env:BLUETUSK_SEARCH_LOAD_BINARY_SHA256 = $binarySha
    $env:BLUETUSK_SEARCH_LOAD_IMAGE = $budget.postgreSqlImage
    [ordered]@{ Campaign = $campaign; CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256;
        Image = $budget.postgreSqlImage; RequestedSeconds = $Seconds; Runs = $Repetitions;
        Processor = [string](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name);
        LogicalProcessors = [Environment]::ProcessorCount; OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription;
        DotNetSdk = (& dotnet --version); DockerServer = (& docker version --format '{{.Server.Version}}') } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'environment.json') -Encoding utf8
    Require ($LASTEXITCODE -eq 0) 'Docker is unavailable.'

    for ($run = 1; $run -le $Repetitions; $run++) {
        $fixture = "$campaign-$run"
        $container = "$fixture-pg18"
        $volume = "$fixture-data"
        $runRoot = Join-Path $output "run-$run"
        New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
        foreach ($snapshot in Get-ChildItem -LiteralPath $binarySnapshot -File) {
            $live = Join-Path $bin $snapshot.Name
            Require ((Test-Path -LiteralPath $live -PathType Leaf) -and (Hash $live) -ceq (Hash $snapshot.FullName)) "A Search executable dependency changed before run $run."
        }
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$Port)
        try { $listener.Start() } catch { throw "Search fixture loopback port $Port is occupied." } finally { $listener.Stop() }
        try {
            Docker @('volume','create','--label','bluetusk.owner=search-capacity','--label',"bluetusk.fixture=$fixture",$volume) | Out-Null
            Docker @('run','-d','--name',$container,'--label','bluetusk.owner=search-capacity','--label',"bluetusk.fixture=$fixture",
                '--cpus','4','--memory','2g','-p',"127.0.0.1:${Port}:5432",'-v',"${volume}:/var/lib/postgresql/data",
                '-e','PGDATA=/var/lib/postgresql/data/pgdata','-e','POSTGRES_PASSWORD=postgres','-e','POSTGRES_DB=search_load', # ggignore
                $budget.postgreSqlImage,'postgres','-c','track_io_timing=on','-c','max_connections=100') | Out-Null
            $ready = $false
            for ($attempt = 0; $attempt -lt 150; $attempt++) {
                & docker exec $container pg_isready -h 127.0.0.1 -U postgres -d search_load *> $null
                if ($LASTEXITCODE -eq 0) { $ready = $true; break }
                Start-Sleep -Milliseconds 200
            }
            Require $ready 'Owned Search PostgreSQL fixture did not become ready.'
            $env:BLUETUSK_SEARCH_LOAD_CONNECTION_STRING = "Host=127.0.0.1;Port=$Port;Username=postgres;Password=postgres;Database=search_load;SSL Mode=Disable;Channel Binding=Disable" # ggignore
            & dotnet $dll $Seconds $budget.documentsPerTenant $budget.contentBytes (Join-Path $runRoot 'search-mixed.json') 2>&1 |
                Tee-Object -FilePath (Join-Path $runRoot 'workload.log')
            Require ($LASTEXITCODE -eq 0) "Search mixed run $run failed; partial evidence is retained."
            foreach ($snapshot in Get-ChildItem -LiteralPath $binarySnapshot -File) {
                $live = Join-Path $bin $snapshot.Name
                Require ((Test-Path -LiteralPath $live -PathType Leaf) -and (Hash $live) -ceq (Hash $snapshot.FullName)) "A Search executable dependency changed during run $run."
            }
        }
        finally {
            $env:BLUETUSK_SEARCH_LOAD_CONNECTION_STRING = $null
            RemoveOwned 'container' $container $fixture
            RemoveOwned 'volume' $volume $fixture
        }
        Capture (Join-Path $runRoot 'source-after.json')
        $runSource = Json (Join-Path $runRoot 'source-after.json')
        Require ($runSource.dirty -eq $false -and $runSource.commit -ceq $head -and
            $runSource.sourceTreeSha256 -ceq $source.sourceTreeSha256) "Candidate source changed during Search run $run."
    }
    Capture (Join-Path $output 'source-after.json')
    $after = Json (Join-Path $output 'source-after.json')
    Require ($after.dirty -eq $false -and $after.commit -ceq $head -and $after.sourceTreeSha256 -ceq $source.sourceTreeSha256) 'Candidate source changed during the Search campaign.'
    $full = $Seconds -ge $budget.minimumSecondsPerRun -and $Repetitions -ge $budget.minimumRuns
    $files = @(Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\', '/'); Sha256 = (Hash $_.FullName) }
    } | Sort-Object Path)
    $manifestPath = Join-Path $output 'manifest.json'
    [ordered]@{ SchemaVersion = 1; CandidateSha = $head; SourceTreeSha256 = $source.sourceTreeSha256;
        HarnessBinarySha256 = $binarySha; BudgetSha256 = (Hash $budgetPath); Runs = $Repetitions;
        QualifiedLocalCapacity = $false; ProductionQualified = $false; Files = $files } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    if ($full) {
        & (Join-Path $PSScriptRoot 'verify-search-capacity.ps1') -EvidenceDirectory $output -ExpectedCommit $head -Candidate
        $manifest = Json $manifestPath
        $manifest.QualifiedLocalCapacity = $true
        $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        Write-Output "Search local capacity gate passed for $head. Production qualification remains false."
    } else {
        Write-Output "Search diagnostic evidence retained at $output. Duration/repeats are below qualification minimums; no capacity pass is asserted."
    }
}
finally {
    $env:BLUETUSK_SEARCH_LOAD_CONNECTION_STRING = $priorConnection
    $env:BLUETUSK_SEARCH_LOAD_COMMIT = $priorCommit
    $env:BLUETUSK_SEARCH_LOAD_SOURCE_SHA256 = $priorSource
    $env:BLUETUSK_SEARCH_LOAD_BINARY_SHA256 = $priorBinary
    $env:BLUETUSK_SEARCH_LOAD_IMAGE = $priorImage
    Pop-Location
}
