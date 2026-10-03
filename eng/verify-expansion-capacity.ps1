[CmdletBinding()]
param(
    [ValidateSet('Preflight', 'Run', 'Verify')]
    [string] $Mode = 'Verify',
    # The four expansion families whose capacity gate uses the shared harness host.
    [Parameter(Mandatory)]
    [ValidateSet('Events', 'Schema', 'Sql', 'Studio', IgnoreCase = $false)]
    [string] $Family,
    [Parameter(Mandatory)]
    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $ExpectedCommit,
    # Defaults to artifacts/<family>-release-capacity, the workflow's own artifacts directory.
    [string] $EvidenceRoot,
    # Archived re-verification only: the GitHub run that produced the evidence, so the fixture
    # owner is bound to that exact run rather than to the verifying run.
    [ValidateRange(1, [long]::MaxValue)]
    [long] $ProducerRunId,
    # Local diagnostic campaigns only. Shorter or fewer runs are labelled diagnostic, evaluated
    # without certifying anything, and rejected by Verify mode and by readiness.
    [ValidateRange(10, 1799)]
    [int] $DiagnosticSeconds,
    [ValidateRange(1, 2)]
    [int] $DiagnosticRepetitions = 1,
    # Local campaigns may label their fixture with the host owner; workflow runs always use
    # '<family>-release-capacity' bound to GITHUB_RUN_ID.
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,62}$')]
    [string] $FixtureOwner,
    [ValidateRange(1024, 65535)]
    [int] $Port
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'expansion-capacity-evidence.psm1') -Force
$definition = Get-ExpansionCapacityDefinition -Family $Family
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = "artifacts/$($definition.Slug)-release-capacity" }
$evidence = [IO.Path]::GetFullPath((Join-Path $repository $EvidenceRoot))
$budgetPath = Join-Path $PSScriptRoot $definition.BudgetFile
$diagnostic = $PSBoundParameters.ContainsKey('DiagnosticSeconds')
if ($PSBoundParameters.ContainsKey('ProducerRunId') -and $Mode -ne 'Verify') { throw 'ProducerRunId applies only to archived Verify mode.' }
if (($diagnostic -or $PSBoundParameters.ContainsKey('FixtureOwner') -or $PSBoundParameters.ContainsKey('DiagnosticRepetitions')) -and $Mode -ne 'Run')
{ throw 'Diagnostic and fixture-owner options apply only to Run mode.' }
if (-not $PSBoundParameters.ContainsKey('Port'))
{
    $Port = switch ($Family) { 'Events' { 55840 } 'Schema' { 55841 } 'Sql' { 55842 } 'Studio' { 55843 } }
}

function Require([bool] $Condition, [string] $Message) { if (-not $Condition) { throw $Message } }
function Hash([string] $Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Json([string] $Path) { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 64 }
function Capture([string] $Path)
{
    & python eng/capture-ecosystem-source.py --output $Path | Out-Host
    Require ($LASTEXITCODE -eq 0) 'Candidate source capture failed.'
    $capture = Json $Path
    Require ($capture.dirty -eq $false -and [string]$capture.commit -ceq $ExpectedCommit) 'Candidate source capture is not clean or exact.'
    return $capture
}
function InvokeDocker([string[]] $Arguments)
{
    $result = & docker @Arguments 2>&1
    Require ($LASTEXITCODE -eq 0) "Owned $Family fixture Docker operation failed: $($Arguments[0])."
    return $result
}
function RemoveOwned([string] $Kind, [string] $Name, [string] $Owner, [string] $RunId)
{
    $template = if ($Kind -eq 'container') { '{{json .Config.Labels}}' } else { '{{json .Labels}}' }
    $labels = & docker $Kind inspect $Name --format $template 2>$null
    if ($LASTEXITCODE -ne 0) { return }
    $actual = $labels | ConvertFrom-Json
    Require ($null -ne $actual -and [string]$actual.'bluetusk.owner' -ceq $Owner -and [string]$actual.'bluetusk.run' -ceq $RunId -and
        [string]$actual.'bluetusk.family' -ceq $Family) "Refusing to remove a $Kind outside this $Family campaign: $Name"
    if ($Kind -eq 'container') { InvokeDocker @('rm', '-f', $Name) | Out-Null }
    else { InvokeDocker @('volume', 'rm', $Name) | Out-Null }
}
function AssertSnapshot([string] $Snapshot, [string] $Binaries)
{
    foreach ($record in @((Json (Join-Path $Snapshot 'binaries.json')).Files))
    {
        $live = Join-Path $Binaries $record.Name
        Require ((Test-Path -LiteralPath $live -PathType Leaf) -and (Hash $live) -ceq [string]$record.Sha256 -and
            (Hash (Join-Path $Snapshot $record.Name)) -ceq [string]$record.Sha256) "A $Family harness binary changed during the campaign."
    }
}

Push-Location -LiteralPath $repository
try
{
    $head = (& git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and $head -ceq $ExpectedCommit) 'The checkout is not the requested exact candidate commit.'
    Require (@(& git status --porcelain --untracked-files=normal).Count -eq 0) 'The checkout must be clean.'
    $budget = Json $budgetPath
    Assert-ExpansionCapacityBudget -Budget $budget -Family $Family
    $processor = [string](Get-CimInstance Win32_Processor | Select-Object -First 1 -ExpandProperty Name)
    Require ($processor.Contains([string]$budget.referenceProcessor, [StringComparison]::OrdinalIgnoreCase)) (
        "This is not the $Family capacity reference processor.")
    Require ($evidence.StartsWith($repository + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) (
        'EvidenceRoot must be inside the checkout.')
    if ($Mode -eq 'Preflight')
    {
        Write-Output "Clean exact candidate $head and $Family capacity contract verified."
        return
    }

    if ($Mode -eq 'Verify')
    {
        $producer = if ($PSBoundParameters.ContainsKey('ProducerRunId')) { [string]$ProducerRunId } else { '' }
        $result = Assert-ExpansionCapacityEvidence -EvidenceRoot $evidence -Family $Family -ExpectedCommit $head -BudgetPath $budgetPath -ProducerRunId $producer
        Write-Output "Archived $Family capacity evidence verified for $head across $($result.Runs) exact-source runs. Production qualification remains false."
        return
    }

    # Run: produce the campaign. A workflow run binds the fixture to GITHUB_RUN_ID; a local run
    # binds it to a fresh campaign UUID.
    $github = -not [string]::IsNullOrWhiteSpace($env:GITHUB_RUN_ID)
    if ($github)
    {
        Require (-not $diagnostic -and -not $PSBoundParameters.ContainsKey('FixtureOwner') -and $env:GITHUB_RUN_ID -cmatch '^[1-9][0-9]*$') (
            'Workflow capacity runs are full qualification campaigns owned by their GitHub run.')
        $runKind = 'github'; $runId = $env:GITHUB_RUN_ID; $owner = $definition.FixtureOwner
    }
    else
    {
        $runKind = 'local'; $runId = [guid]::NewGuid().ToString()
        $owner = if ($PSBoundParameters.ContainsKey('FixtureOwner')) { $FixtureOwner } else { $definition.FixtureOwner }
    }
    $seconds = if ($diagnostic) { $DiagnosticSeconds } else { [int]$budget.secondsPerRun }
    $repetitions = if ($diagnostic) { $DiagnosticRepetitions } else { [int]$budget.repetitions }
    Require (-not (Test-Path -LiteralPath $evidence)) 'Choose a fresh evidence directory.'
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    Copy-Item -LiteralPath $budgetPath -Destination (Join-Path $evidence 'budgets.json')
    $source = Capture (Join-Path $evidence 'source-before.json')
    [ordered]@{ Family = $Family; Diagnostic = $diagnostic; Processor = $processor; LogicalProcessors = [Environment]::ProcessorCount
        OperatingSystem = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        DotNetSdk = [string](& dotnet --version); DockerServer = [string](& docker version --format '{{.Server.Version}}')
        FixtureCpus = $budget.fixture.cpus; FixtureMemoryMiB = $budget.fixture.memoryMiB } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $evidence 'runner.json') -Encoding utf8
    Require ($LASTEXITCODE -eq 0) 'Docker is unavailable on the reference host.'

    & dotnet build $definition.HarnessProject -c Release -nr:false --nologo
    Require ($LASTEXITCODE -eq 0) "The $Family capacity harness did not build."
    $binaries = Join-Path $repository $definition.HarnessBinaryDirectory
    $snapshot = Join-Path $evidence 'binary-snapshot'
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
    $records = foreach ($file in @(Get-ChildItem -LiteralPath $binaries -File | Where-Object { $_.Extension -in @('.dll', '.exe', '.json') } | Sort-Object Name))
    {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $snapshot $file.Name)
        [ordered]@{ Name = $file.Name; Sha256 = (Hash $file.FullName) }
    }
    [ordered]@{ Harness = $definition.Harness; Files = @($records) } | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $snapshot 'binaries.json') -Encoding utf8
    $harness = Join-Path $snapshot "$($definition.Harness).dll"
    Require (Test-Path -LiteralPath $harness -PathType Leaf) "The $Family harness executable is missing."
    $binarySha = Hash $harness
    AssertSnapshot $snapshot $binaries

    $prior = @{}
    foreach ($name in @('CONNECTION_STRING', 'COMMIT', 'SOURCE_SHA256', 'BINARY_SHA256', 'IMAGE', 'FIXTURE_OWNER', 'FIXTURE_RUN_KIND', 'FIXTURE_RUN_ID'))
    { $prior[$name] = [Environment]::GetEnvironmentVariable("BLUETUSK_CAPACITY_$name") }
    try
    {
        $env:BLUETUSK_CAPACITY_COMMIT = $head
        $env:BLUETUSK_CAPACITY_SOURCE_SHA256 = $source.sourceTreeSha256
        $env:BLUETUSK_CAPACITY_BINARY_SHA256 = $binarySha
        $env:BLUETUSK_CAPACITY_IMAGE = $budget.postgreSqlImage
        $env:BLUETUSK_CAPACITY_FIXTURE_OWNER = $owner
        $env:BLUETUSK_CAPACITY_FIXTURE_RUN_KIND = $runKind
        $env:BLUETUSK_CAPACITY_FIXTURE_RUN_ID = $runId
        for ($run = 1; $run -le $repetitions; $run++)
        {
            $runRoot = Join-Path $evidence "run-$run"
            New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
            AssertSnapshot $snapshot $binaries
            $container = "bluetusk-$($definition.Slug)-capacity-$runId-$run"
            $volume = "$container-data"
            $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
            try { $listener.Start() } catch { throw "The $Family fixture loopback port $Port is occupied." } finally { $listener.Stop() }
            $labels = @('--label', "bluetusk.owner=$owner", '--label', "bluetusk.run=$runId", '--label', "bluetusk.family=$Family")
            try
            {
                InvokeDocker (@('volume', 'create') + $labels + @($volume)) | Out-Null
                $fixturePassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))
                InvokeDocker (@('run', '-d', '--name', $container) + $labels + @('--cpus', [string]$budget.fixture.cpus,
                    '--memory', "$($budget.fixture.memoryMiB)m", '-p', "127.0.0.1:${Port}:5432", '-v', "${volume}:/var/lib/postgresql/data",
                    '-e', 'PGDATA=/var/lib/postgresql/data/pgdata', '-e', "POSTGRES_PASSWORD=$fixturePassword", '-e', 'POSTGRES_DB=capacity',
                    $budget.postgreSqlImage, 'postgres', '-c', "max_connections=$($budget.fixture.maxConnections)", '-c', 'track_io_timing=on')) | Out-Null
                $ready = $false
                for ($attempt = 0; $attempt -lt 300; $attempt++)
                {
                    & docker exec $container pg_isready -h 127.0.0.1 -U postgres -d capacity *> $null
                    if ($LASTEXITCODE -eq 0) { $ready = $true; break }
                    Start-Sleep -Milliseconds 200
                }
                Require $ready "The owned $Family PostgreSQL fixture did not become ready."
                $imageId = [string](InvokeDocker @('inspect', $container, '--format', '{{.Image}}'))
                [ordered]@{
                    Container = $container
                    ImageReference = [string]$budget.postgreSqlImage
                    ImageId = $imageId.Trim()
                    RepoDigests = @((InvokeDocker @('image', 'inspect', $imageId.Trim(), '--format', '{{json .RepoDigests}}')) | ConvertFrom-Json)
                    Labels = ((InvokeDocker @('inspect', $container, '--format', '{{json .Config.Labels}}')) | ConvertFrom-Json)
                } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runRoot 'fixture.json') -Encoding utf8
                $env:BLUETUSK_CAPACITY_CONNECTION_STRING = "Host=127.0.0.1;Port=$Port;Username=postgres;Password=$fixturePassword;Database=capacity;SSL Mode=Disable;Channel Binding=Disable"
                & dotnet $harness $seconds (Join-Path $evidence 'budgets.json') $runRoot 2>&1 | Tee-Object -FilePath (Join-Path $runRoot 'workload.log')
                Require ($LASTEXITCODE -eq 0) "$Family capacity run $run failed; partial evidence is retained."
                AssertSnapshot $snapshot $binaries
            }
            finally
            {
                $env:BLUETUSK_CAPACITY_CONNECTION_STRING = $null
                RemoveOwned 'container' $container $owner $runId
                RemoveOwned 'volume' $volume $owner $runId
            }
            [void](Capture (Join-Path $runRoot 'source-after.json'))
        }
    }
    finally
    {
        foreach ($name in $prior.Keys) { [Environment]::SetEnvironmentVariable("BLUETUSK_CAPACITY_$name", $prior[$name]) }
    }
    $after = Capture (Join-Path $evidence 'source-after.json')
    Require ($after.sourceTreeSha256 -ceq $source.sourceTreeSha256) "Candidate source changed during the $Family campaign."
    $files = @(Get-ChildItem -LiteralPath $evidence -Recurse -File | ForEach-Object {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($evidence, $_.FullName).Replace('\', '/'); Sha256 = (Hash $_.FullName) }
    } | Sort-Object { $_.Path })
    $manifestPath = Join-Path $evidence 'manifest.json'
    $manifest = [ordered]@{ SchemaVersion = 1; Family = $Family; Qualification = $definition.Qualification; CandidateSha = $head
        SourceTreeSha256 = $source.sourceTreeSha256; HarnessBinarySha256 = $binarySha; BudgetSha256 = (Hash $budgetPath)
        Repetitions = $repetitions; SecondsPerRun = $seconds; Diagnostic = $diagnostic; QualifiedLocalCapacity = $false
        ProductionQualified = $false; FixtureOwner = $owner; FixtureRunKind = $runKind; FixtureRunId = $runId; Files = $files }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    if ($diagnostic)
    {
        $evaluation = Assert-ExpansionCapacityEvidence -EvidenceRoot $evidence -Family $Family -ExpectedCommit $head -BudgetPath $budgetPath -Evaluation
        [ordered]@{
            Diagnostic = $true
            ReleaseEvidence = $false
            Note = 'Reduced-duration diagnostic campaign. It proves the harness end to end; it is not qualification evidence and Verify mode rejects it.'
            Family = $Family; CandidateSha = $head; SecondsPerRun = $seconds; Repetitions = $repetitions
            BudgetComparisons = $evaluation.Findings
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'diagnostic-evaluation.json') -Encoding utf8
        $failed = @($evaluation.Findings | Where-Object { -not $_.Passed })
        Write-Output "$Family DIAGNOSTIC campaign retained at $evidence; $($evaluation.Findings.Count) budget comparisons, $($failed.Count) outside the qualification budget (including duration/repetition minimums). Not release evidence."
        return
    }
    [void](Assert-ExpansionCapacityEvidence -EvidenceRoot $evidence -Family $Family -ExpectedCommit $head -BudgetPath $budgetPath -Candidate)
    $manifest.QualifiedLocalCapacity = $true
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    Write-Output "Manual $Family capacity gate passed for $head. Production qualification remains false."
}
finally { Pop-Location }
