[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Regression', 'Compatibility', 'SyncConnectors')][string] $Kind,
    [Parameter(Mandatory)][string] $OutputRoot,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedCommit,
    [ValidateSet(0, 15, 16, 17, 18)][int] $PostgreSqlMajor = 0,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $PostgreSqlContainer,
    [string] $SourceRoot = (Split-Path $PSScriptRoot -Parent),
    [switch] $Build,
    [switch] $ValidateOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -Force
$toolRoot = Split-Path $PSScriptRoot -Parent
$repositoryRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$plan = Get-CoreTestPlan $Kind $PostgreSqlMajor
if ($ValidateOnly) { $plan; return }
if ($Build -and $Kind -ne 'SyncConnectors') { throw 'Build mode is limited to standalone Sync connector capture.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$environment = if ($IsWindows) { 'windows-x64' } elseif ($IsLinux) { 'linux-x64' } else { throw 'Core evidence requires Windows or Linux.' }
if ($architecture -ne [Runtime.InteropServices.Architecture]::X64) { throw 'Core test captures require x64.' }
if ($Kind -eq 'Compatibility' -and ($environment -ne 'linux-x64' -or $PostgreSqlMajor -eq 0 -or
    [string]::IsNullOrWhiteSpace($PostgreSqlContainer) -or [string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)))
{ throw 'Compatibility capture requires a live stable PostgreSQL fixture on Linux and its configured test connection.' }
if ($Kind -eq 'Regression' -and ($PostgreSqlMajor -ne 0 -or -not [string]::IsNullOrWhiteSpace($PostgreSqlContainer)))
{ throw 'Unit regression captures must not claim a PostgreSQL fixture.' }
$toolCommit = (& git -C $toolRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $toolCommit -cnotmatch '^[0-9a-f]{40}$' -or
    @(& git -C $toolRoot status --porcelain --untracked-files=normal).Count -ne 0)
{ throw 'Evidence collectors require clean committed verifier tools.' }
if ($Kind -eq 'SyncConnectors')
{
    if ($PostgreSqlMajor -ne 0 -or -not [string]::IsNullOrWhiteSpace($PostgreSqlContainer))
    { throw 'Standalone Sync test payloads must not invent authenticated fixture metadata.' }
    $requiredEnvironment = @('BLUETUSK_TEST_CONNECTION_STRING', 'BLUETUSK_NATS_URL', 'BLUETUSK_KAFKA_BOOTSTRAP_SERVERS',
        'BLUETUSK_S3_ENDPOINT', 'BLUETUSK_S3_ACCESS_KEY', 'BLUETUSK_S3_SECRET_KEY',
        'BLUETUSK_TEST_REDIS_CONNECTION_STRING', 'BLUETUSK_OPENSEARCH_URL')
    foreach ($variable in $requiredEnvironment)
    {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($variable)))
        { throw "Configure '$variable' for the actual seven-destination Sync capture." }
    }
}

function Assert-Source
{
    $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit) { throw 'Test source HEAD differs from the exact candidate commit.' }
    $status = @(& git -C $repositoryRoot status --porcelain --untracked-files=normal)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'Core evidence requires a clean committed source tree throughout the capture.' }
}
function Get-Fixture
{
    $connection = [Data.Common.DbConnectionStringBuilder]::new()
    # PowerShell adapts this IDictionary as a property bag. Invoke the CLR
    # setter so ConnectionString is parsed rather than stored as a new key.
    $connection.set_ConnectionString($env:BLUETUSK_TEST_CONNECTION_STRING)
    $hostPort = (& docker inspect --format '{{(index (index .NetworkSettings.Ports "5432/tcp") 0).HostPort}}' $PostgreSqlContainer).Trim()
    if ($LASTEXITCODE -ne 0 -or $hostPort -notmatch '^[0-9]+$' -or
        $connection['Host'] -notin @('localhost', '127.0.0.1') -or
        [string]$connection['Port'] -cne $hostPort -or $connection['Database'] -cne 'bluetusk_tests' -or
        $connection['Username'] -cne 'postgres')
    { throw 'The test connection must target the inspected fixture through its exact loopback port and database.' }
    $identity = (& docker inspect --format '{{.Config.Image}}|{{.Image}}' $PostgreSqlContainer).Trim().Split('|')
    if ($LASTEXITCODE -ne 0 -or $identity.Count -ne 2) { throw 'Could not inspect the live PostgreSQL container identity.' }
    $version = (& docker exec $PostgreSqlContainer psql -U postgres -d bluetusk_tests -At -c 'SHOW server_version_num').Trim()
    if ($LASTEXITCODE -ne 0 -or $version -cnotmatch '^[0-9]+$' -or
        [Math]::Floor([long]$version / 10000) -ne $PostgreSqlMajor -or
        $identity[0] -cnotmatch ("^postgres:$PostgreSqlMajor-alpine@sha256:[0-9a-f]{64}$"))
    { throw 'The live compatibility database must identify the exact pinned stable major.' }
    return [pscustomobject]@{ major = $PostgreSqlMajor; imageReference = $identity[0]; imageId = $identity[1]; serverVersionNumber = [long]$version }
}
Assert-Source
$output = [IO.Path]::GetFullPath((Join-Path $toolRoot $OutputRoot))
$artifacts = [IO.Path]::GetFullPath((Join-Path $toolRoot 'artifacts'))
$prefix = if ($Kind -eq 'SyncConnectors') { 'connector-tests/' } else { '' }
$captureRoot = if ($Kind -eq 'SyncConnectors') { Join-Path $output 'connector-tests' } else { $output }
$name = if ($Kind -eq 'SyncConnectors') { 'sync-connector-validation.json' } else { 'core-test-shard.json' }
if (-not $output.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $captureRoot) -or (Test-Path -LiteralPath (Join-Path $output $name)))
{ throw 'Core test capture requires fresh payload paths beneath repository artifacts; existing captures are never overwritten.' }
$ancestor = $output
while (-not [string]::IsNullOrWhiteSpace($ancestor))
{
    if ((Test-Path -LiteralPath $ancestor) -and
        ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    { throw 'Core test output must not traverse a symbolic link or junction.' }
    $ancestor = Split-Path $ancestor -Parent
}
$null = [IO.Directory]::CreateDirectory($captureRoot)
$started = [DateTimeOffset]::UtcNow.ToString('O')
$previousLanguage = $env:DOTNET_CLI_UI_LANGUAGE
$env:DOTNET_CLI_UI_LANGUAGE = 'en-US'
$rows = [Collections.Generic.List[object]]::new()
$fixture = $null
try
{
    if ($Kind -eq 'Compatibility')
    {
        $fixture = Get-Fixture
        $fixture | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'postgresql-fixture.json') -Encoding utf8NoBOM
    }
    foreach ($project in $plan)
    {
        $relative = "${prefix}tests/$($project.name)"
        $directory = Join-Path $output $relative
        $null = New-Item -ItemType Directory -Path $directory
        if ($Build)
        {
            $projectPath = Join-Path $repositoryRoot $project.path
            & dotnet restore $projectPath --locked-mode --nologo --verbosity quiet *> (Join-Path $directory 'restore.log')
            if ($LASTEXITCODE -ne 0) { throw "Locked restore failed for '$($project.name)'; raw output is retained." }
            & dotnet build $projectPath --configuration Release --no-restore --nologo --verbosity quiet *> (Join-Path $directory 'build.log')
            if ($LASTEXITCODE -ne 0) { throw "Build failed for '$($project.name)'; raw output is retained." }
        }
        $assembly = Join-Path $repositoryRoot "tests/$($project.name)/bin/Release/net10.0/$($project.name).dll"
        if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Build '$($project.name)' in Release before capturing Core test evidence." }
        if ((Get-CoreTestAssemblyVersion $assembly) -cne "1.2.0+$ExpectedCommit")
        { throw "Rebuild '$($project.name)' from the exact committed candidate before capturing evidence." }
        $assemblyHash = (Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $assembly -Destination (Join-Path $directory "$($project.name).dll")
        $arguments = @('test', (Join-Path $repositoryRoot $project.path), '--configuration', 'Release', '--no-build',
            '--no-restore', '--nologo', '--verbosity', 'quiet')
        if ($project.filter.Length -gt 0) { $arguments += @('--filter', $project.filter) }
        & dotnet @arguments --list-tests *> (Join-Path $directory 'discovery.log')
        if ($LASTEXITCODE -ne 0) { throw "Test discovery failed for '$($project.name)'; raw output is retained." }
        & dotnet @arguments --logger 'trx;LogFileName=tests.trx' --results-directory $directory *> (Join-Path $directory 'test.log')
        if ($LASTEXITCODE -ne 0) { throw "Core tests failed for '$($project.name)'; raw output is retained." }
        if ((Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash -cne $assemblyHash)
        { throw 'Test assembly changed between discovery and execution.' }
        $summary = Get-CoreTrxSummary (Join-Path $directory 'tests.trx') (Join-Path $directory 'discovery.log') $project.name
        $rows.Add([pscustomobject]@{
            name = $project.name; projectPath = $project.path; filter = $project.filter;
            discovered = $summary.Discovered; passed = $summary.Passed;
            discovery = New-CoreTestArtifact $output "$relative/discovery.log";
            trx = New-CoreTestArtifact $output "$relative/tests.trx";
            log = New-CoreTestArtifact $output "$relative/test.log";
            assembly = New-CoreTestArtifact $output "$relative/$($project.name).dll"
        })
        Write-Host "$($project.name): $($summary.Passed) discovered tests passed, zero skips."
    }
    Assert-Source
    if ((& git -C $toolRoot rev-parse HEAD).Trim() -cne $toolCommit -or
        @(& git -C $toolRoot status --porcelain --untracked-files=normal).Count -ne 0)
    { throw 'Verifier tools changed during the capture.' }
    $database = $null
    if ($Kind -eq 'Compatibility')
    {
        $endFixture = Get-Fixture
        if (($endFixture | ConvertTo-Json -Compress) -cne ($fixture | ConvertTo-Json -Compress))
        { throw 'The live PostgreSQL fixture changed during the capture.' }
        $database = [pscustomobject]@{ major = $fixture.major; imageReference = $fixture.imageReference;
            imageId = $fixture.imageId; serverVersionNumber = $fixture.serverVersionNumber;
            fixture = New-CoreTestArtifact $output 'postgresql-fixture.json' }
    }
    $shard = [pscustomobject]@{ schemaVersion = 1; scope = 'Core'; releaseVersion = '1.2.0'; sourceCommit = $ExpectedCommit;
        sourceTreeDirty = $false; kind = $Kind; environmentId = $environment; postgreSql = $database;
        startedAtUtc = $started; completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O'); projects = $rows.ToArray() }
    if ($Kind -eq 'SyncConnectors') { $shard | Add-Member -NotePropertyName toolSourceCommit -NotePropertyValue $toolCommit }
    $path = Join-Path $output $name
    $shard | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    Get-CoreTestShardReport $path $ExpectedCommit $Kind $environment $PostgreSqlMajor
}
finally { $env:DOTNET_CLI_UI_LANGUAGE = $previousLanguage }
