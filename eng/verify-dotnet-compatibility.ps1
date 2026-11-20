[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $ContractPath = (Join-Path $PSScriptRoot 'dotnet-compatibility.json'),
    [switch] $RequireComplete,
    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
if ($contract.schemaVersion -ne 1 -or $contract.scope -cne 'unreleased-working-tree')
{
    throw 'Expected .NET compatibility schema 1 for the unreleased working tree.'
}
if (@($contract.requiredOperatingSystems).Count -ne 2 -or
    @(Compare-Object @('windows', 'linux') @($contract.requiredOperatingSystems) -SyncWindow 0).Count -ne 0)
{
    throw 'Runtime compatibility qualification requires both Windows and Linux.'
}
$frameworks = @($contract.frameworks)
if ($frameworks.Count -eq 0 -or @($frameworks | Sort-Object -Unique).Count -ne $frameworks.Count -or
    @($frameworks | Where-Object { $_ -cnotmatch '^net(?:[5-9]|[1-9][0-9])\.0$' }).Count -ne 0)
{
    throw 'Compatibility frameworks must be unique modern .NET framework identifiers starting at net5.0.'
}
$highestMajor = [int]($frameworks[-1] -replace '^net|\.0$', '')
$expected = @(5..$highestMajor | ForEach-Object { "net$_.0" })
if (@(Compare-Object $expected $frameworks -SyncWindow 0).Count -ne 0)
{
    throw 'Compatibility frameworks must include every major version from .NET 5 in ascending order.'
}
[xml] $targetProps = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'eng/TargetFrameworks.props') -Raw
$declaredFrameworks = @($targetProps.Project.PropertyGroup.BlueTuskRuntimeTargetFrameworks.InnerText -split ';')
if (@(Compare-Object $frameworks $declaredFrameworks -SyncWindow 0).Count -ne 0)
{
    throw 'The compatibility contract and BlueTuskRuntimeTargetFrameworks differ.'
}
$globalSdk = (Get-Content -LiteralPath (Join-Path $RepositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
if ($contract.sdkVersion -cne $globalSdk -or $globalSdk -cnotmatch '^\d+\.\d+\.\d+$')
{
    throw 'The compatibility SDK must exactly match the stable SDK pinned in global.json.'
}
if (@(Compare-Object $frameworks @($contract.runtimeVersions.PSObject.Properties.Name) -SyncWindow 0).Count -ne 0)
{
    throw 'Each compatibility framework must have exactly one pinned runtime version.'
}
foreach ($framework in $frameworks)
{
    $major = $framework -replace '^net|\.0$', ''
    if ($contract.runtimeVersions.$framework -cnotmatch "^$major\.0\.\d+$")
    {
        throw "Pinned runtime for '$framework' must be a stable patch of the same .NET major."
    }
}
foreach ($container in $contract.legacyLinuxContainers.PSObject.Properties)
{
    if ($container.Name -notin $frameworks -or
        $container.Value.image -cnotmatch '^mcr\.microsoft\.com/dotnet/sdk:[A-Za-z0-9.\-]+@sha256:[0-9a-f]{64}$' -or
        $container.Value.sdkVersion -cnotmatch '^\d+\.\d+\.\d+$')
    {
        throw 'Legacy Linux test SDK containers must use a stable SDK and an immutable official image digest.'
    }
    $edgeHost = $container.Value.PSObject.Properties['edgeHost']
    if ($null -ne $edgeHost)
    {
        $recipe = Join-Path $RepositoryRoot 'eng/compatibility/LegacyEdge.Dockerfile'
        if ($container.Name -cne 'net5.0' -or
            $edgeHost.Value.image -cnotmatch '^mcr\.microsoft\.com/dotnet/sdk:[A-Za-z0-9.\-]+@sha256:[0-9a-f]{64}$' -or
            -not (Test-Path -LiteralPath $recipe -PathType Leaf) -or
            (Get-FileHash -LiteralPath $recipe -Algorithm SHA256).Hash.ToLowerInvariant() -cne $edgeHost.Value.dockerfileSha256)
        {
            throw 'The .NET 5 Edge image requires a pinned official host and a verified Dockerfile.'
        }
    }
}
$postgresql = $contract.postgreSqlFixture
if ($postgresql.version -cnotmatch '^\d+\.\d+$' -or
    $postgresql.linuxImage -cnotmatch '^postgres:[\w.-]+@sha256:[0-9a-f]{64}$' -or
    $postgresql.windowsArchive.url -cnotmatch ('^https://get\.enterprisedb\.com/postgresql/postgresql-' + [regex]::Escape($postgresql.version) + '-[0-9]+-windows-x64-binaries\.zip$') -or
    $postgresql.windowsArchive.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    @($postgresql.testProjects).Count -eq 0 -or
    @($postgresql.testProjects | Sort-Object -Unique).Count -ne @($postgresql.testProjects).Count)
{
    throw 'The live PostgreSQL compatibility fixture requires pinned Windows binaries, an immutable Linux image, a server version, and unique test projects.'
}
$pgVector = $postgresql.pgVector
if ($pgVector.version -cnotmatch '^\d+\.\d+\.\d+$' -or
    $pgVector.linuxImage -cnotmatch '^pgvector/pgvector@sha256:[0-9a-f]{64}$' -or
    $pgVector.windowsSource.url -cnotmatch '^https://codeload\.github\.com/pgvector/pgvector/tar\.gz/[0-9a-f]{40}$' -or
    $pgVector.windowsSource.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    @($pgVector.testProjects).Count -eq 0 -or
    @($pgVector.testProjects | Sort-Object -Unique).Count -ne @($pgVector.testProjects).Count -or
    @($pgVector.testProjects | Where-Object { $_ -cnotin $postgresql.testProjects }).Count -ne 0)
{
    throw 'The pgvector fixture requires a pinned Linux image, verified Windows build source, and registered PostgreSQL test projects.'
}
$postgresqlExtensions = @($contract.postgreSqlExtensionFixtures)
if (@($postgresqlExtensions | ForEach-Object extension | Sort-Object -Unique).Count -ne $postgresqlExtensions.Count)
{ throw 'PostgreSQL extension fixtures must have unique names.' }
foreach ($fixture in $postgresqlExtensions)
{
    $validExtensionSource = switch -CaseSensitive ($fixture.extension)
    {
        'postgis' { $fixture.linuxImage -cmatch '^postgis/postgis:[\w.-]+@sha256:[0-9a-f]{64}$' -and
                $fixture.windowsExtensionArchive.url -cmatch '^https://download\.osgeo\.org/postgis/windows/pg\d+/postgis-bundle-pg\d+-[\d.]+x64\.zip$' }
        'timescaledb' { $fixture.linuxImage -cmatch '^timescale/timescaledb:[\w.-]+@sha256:[0-9a-f]{64}$' -and
                $fixture.windowsExtensionArchive.url -cmatch '^https://github\.com/timescale/timescaledb/releases/download/[\d.]+/timescaledb-postgresql-\d+-windows-amd64\.zip$' }
        default { $false }
    }
    if (-not $validExtensionSource -or $fixture.version -cnotmatch '^\d+\.\d+$' -or $fixture.extensionVersion -cnotmatch '^\d+\.\d+\.\d+$' -or
        $fixture.windowsArchive.url -cnotmatch ('^https://get\.enterprisedb\.com/postgresql/postgresql-' + [regex]::Escape($fixture.version) + '-[0-9]+-windows-x64-binaries\.zip$') -or
        $fixture.windowsArchive.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $fixture.windowsExtensionArchive.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        @($fixture.testProjects | Sort-Object -Unique).Count -ne @($fixture.testProjects).Count)
    { throw 'Invalid pinned PostgreSQL extension fixture distribution, version, or test project mapping.' }
}
$minio = $contract.minioFixture
$minioVersion = [regex]::Escape($minio.version)
if ($minio.version -cnotmatch '^RELEASE\.\d{4}-\d{2}-\d{2}T\d{2}-\d{2}-\d{2}Z$' -or
    $minio.linuxBinary.url -cnotmatch ('^https://github\.com/minio/minio/releases/download/' + $minioVersion + '/minio\.linux-amd64\.' + $minioVersion + '$') -or
    $minio.linuxBinary.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $minio.windowsBinary.url -cnotmatch ('^https://github\.com/minio/minio/releases/download/' + $minioVersion + '/minio\.windows-amd64\.' + $minioVersion + '\.exe$') -or
    $minio.windowsBinary.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    @($minio.testProjects).Count -eq 0 -or
    @($minio.testProjects | Sort-Object -Unique).Count -ne @($minio.testProjects).Count -or
    @($minio.testProjects | Where-Object { $_ -cin $postgresql.testProjects }).Count -ne 0)
{
    throw 'The MinIO fixture requires a pinned server release, verified distributions, and separate registered S3 test projects.'
}
$families = Get-Content -LiteralPath (Join-Path $RepositoryRoot 'eng/product-families.json') -Raw | ConvertFrom-Json
$inventory = @($families.families.PSObject.Properties | ForEach-Object { $_.Value.packages } | Sort-Object -Unique)
$packages = @($contract.packages)
if (@($packages.project | Sort-Object -Unique).Count -ne $packages.Count -or
    @(Compare-Object $inventory @($packages.project | Sort-Object)).Count -ne 0)
{
    throw 'Every product-family package must appear exactly once in the .NET compatibility inventory.'
}
$covered = 0
$pending = [System.Collections.Generic.List[string]]::new()
$registeredTools = @($contract.cliTools)
if (@($registeredTools.project | Sort-Object -Unique).Count -ne $registeredTools.Count -or
    @($registeredTools.command | Sort-Object -Unique).Count -ne $registeredTools.Count)
{
    throw 'CLI smoke mappings require unique package projects and commands.'
}
function Test-CompatibilityProjectReference
{
    param([string] $TestProject, [string] $RuntimeProject)
    $queue = [Collections.Generic.Queue[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $queue.Enqueue([IO.Path]::GetFullPath($TestProject))
    $target = [IO.Path]::GetFullPath($RuntimeProject)
    $rootPrefix = $RepositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    while ($queue.Count -ne 0)
    {
        $current = $queue.Dequeue()
        if (-not $seen.Add($current)) { continue }
        [xml] $referenceProject = Get-Content -LiteralPath $current -Raw
        foreach ($reference in $referenceProject.SelectNodes('/Project/ItemGroup/ProjectReference'))
        {
            if ($reference.HasAttribute('Condition') -or $reference.ParentNode.HasAttribute('Condition') -or
                $reference.Include.Contains('$') -or $null -ne $reference.SelectSingleNode('ReferenceOutputAssembly[text()="false"]')) { continue }
            $relative = $reference.Include.Replace('\', [IO.Path]::DirectorySeparatorChar)
            $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path $current -Parent) $relative))
            if (-not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $resolved -PathType Leaf)) { continue }
            if ($resolved -ieq $target) { return $true }
            $queue.Enqueue($resolved)
        }
    }
    return $false
}
function Get-CompatibilityProjectTargets
{
    param([xml] $Project)

    $targets = @($Project.SelectNodes('/Project/PropertyGroup/TargetFrameworks'))
    if ($targets.Count -ne 1 -or $targets[0].HasAttribute('Condition') -or $targets[0].ParentNode.HasAttribute('Condition'))
    { return '' }
    $expression = $targets[0].InnerText
    if ($expression -ceq '$(BlueTuskEntityFrameworkTargetFrameworks)')
    {
        $defaults = @($Project.SelectNodes('/Project/PropertyGroup/BlueTuskEntityFrameworkTargetFrameworks'))
        if ($defaults.Count -ne 1 -or $defaults[0].GetAttribute('Condition') -cne "'`$(BlueTuskEntityFrameworkTargetFrameworks)' == ''" -or
            $defaults[0].ParentNode.HasAttribute('Condition')) { return '' }
        $expression = $defaults[0].InnerText
    }
    return $expression
}
foreach ($package in $packages)
{
    $projectPath = Join-Path $RepositoryRoot $package.project
    if ($package.project -cnotmatch '^(src|extensions|identity|templates|tooling)/[A-Za-z0-9.\-/]+\.csproj$' -or
        $package.project.Contains('..') -or -not (Test-Path -LiteralPath $projectPath -PathType Leaf))
    {
        throw "Invalid compatibility project path '$($package.project)'."
    }
    $expectedKind = if ($package.project.StartsWith('templates/', [StringComparison]::Ordinal)) { 'template' }
        elseif ($package.project -cin @('src/BlueTusk.SourceGeneration/BlueTusk.SourceGeneration.csproj',
            'src/BlueTusk.Sql.SourceGeneration/BlueTusk.Sql.SourceGeneration.csproj')) { 'analyzer' }
        else { 'runtime' }
    if ($package.kind -cne $expectedKind)
    {
        throw "Package '$($package.project)' must be classified as '$expectedKind'."
    }
    if ($package.kind -cne 'runtime')
    {
        if ($package.status -cne 'not-applicable' -or $null -ne $package.minimumFramework -or @($package.testProjects).Count -ne 0)
        {
            throw "Build-time package '$($package.project)' must not make a runtime coverage claim."
        }
        continue
    }
    if ($package.status -cnotin @('pending', 'covered') -or $package.minimumFramework -cnotin $frameworks)
    {
        throw "Runtime package '$($package.project)' has an invalid status or minimum framework."
    }
    if ($package.minimumFramework -cne 'net5.0' -and
        ([string]::IsNullOrWhiteSpace($package.constraint) -or $package.constraintSource -cnotmatch '^https://'))
    {
        throw "Runtime package '$($package.project)' needs an explicit upstream constraint and primary source for its minimum."
    }
    if ($package.status -ceq 'pending')
    {
        $pending.Add($package.project)
        continue
    }
    $covered++
    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $minimumIndex = [Array]::IndexOf($frameworks, $package.minimumFramework)
    $requiredFrameworks = @($frameworks[$minimumIndex..($frameworks.Count - 1)])
    $targets = Get-CompatibilityProjectTargets $project
    $expectedTargets = if ($minimumIndex -eq 0) { '$(BlueTuskRuntimeTargetFrameworks)' } else { $requiredFrameworks -join ';' }
    if ($targets -cne $expectedTargets)
    {
        throw "Covered runtime package '$($package.project)' must declare '$expectedTargets'."
    }
    $tool = @($registeredTools | Where-Object project -CEQ $package.project)
    if ($tool.Count -ne 0)
    {
        if (-not $package.project.StartsWith('tooling/', [StringComparison]::Ordinal) -or
            $tool[0].packageId -cne [IO.Path]::GetFileNameWithoutExtension($package.project) -or
            $tool[0].command -cnotmatch '^bluetusk(?:-[a-z]+)?$' -or
            $project.Project.PropertyGroup.PackAsTool -cne 'true' -or
            $project.Project.PropertyGroup.ToolCommandName -cne $tool[0].command)
        { throw "Invalid installed CLI smoke mapping for '$($package.project)'." }
        if (@($package.testProjects).Count -eq 0) { continue }
    }
    if (@($package.testProjects).Count -eq 0 -or
        @($package.testProjects | Sort-Object -Unique).Count -ne @($package.testProjects).Count)
    {
        throw "Covered runtime package '$($package.project)' needs unique executable test projects."
    }
    foreach ($testPath in @($package.testProjects))
    {
        if ($testPath -cnotmatch '^tests/[A-Za-z0-9.\-/]+\.csproj$' -or $testPath.Contains('..') -or
            -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $testPath) -PathType Leaf))
        {
            throw "Invalid compatibility test project '$testPath'."
        }
        [xml] $testProject = Get-Content -LiteralPath (Join-Path $RepositoryRoot $testPath) -Raw
        if ((Get-CompatibilityProjectTargets $testProject) -cne $expectedTargets)
        {
            throw "Compatibility tests '$testPath' must cover '$expectedTargets'."
        }
        if (-not (Test-CompatibilityProjectReference -TestProject (Join-Path $RepositoryRoot $testPath) -RuntimeProject $projectPath))
        {
            throw "Compatibility tests '$testPath' must reference '$($package.project)' through an explicit unconditional project-reference chain."
        }
    }
}
$coveredTests = @($packages | Where-Object status -CEQ 'covered' | ForEach-Object { $_.testProjects } | Sort-Object -Unique)
if (@($registeredTools | Where-Object { $_.project -cnotin @($packages | Where-Object status -CEQ 'covered' | ForEach-Object project) }).Count -ne 0)
{
    throw 'Every CLI smoke mapping must belong to a covered runtime package.'
}
$fixtureTests = @($postgresql.testProjects) + @($minio.testProjects) + @($postgresqlExtensions | ForEach-Object testProjects)
if (@($fixtureTests | Sort-Object -Unique).Count -ne $fixtureTests.Count -or
    @($fixtureTests | Where-Object { $_ -cnotin $coveredTests }).Count -ne 0)
{
    throw 'Every compatibility fixture test project must be registered as executable package coverage and bind to one fixture.'
}
$serviceFixtures = @($contract.dockerServiceFixtures)
$serviceTests = @($serviceFixtures | ForEach-Object testProjects)
$serviceRepositories = @{
    kafka = 'apache/kafka'
    nats = 'nats'
    opensearch = 'opensearchproject/opensearch'
    redis = 'redis'
}
if (@($serviceFixtures.kind | Sort-Object -Unique).Count -ne $serviceFixtures.Count -or
    @($serviceTests | Sort-Object -Unique).Count -ne $serviceTests.Count -or
    @($serviceTests | Where-Object { $_ -cnotin $coveredTests }).Count -ne 0)
{
    throw 'Docker services require unique fixture kinds and registered executable test projects.'
}
foreach ($fixture in $serviceFixtures)
{
    if ($fixture.kind -cnotin $serviceRepositories.Keys -or
        $fixture.image -cnotmatch ('^' + [regex]::Escape($serviceRepositories[$fixture.kind]) + ':[a-zA-Z0-9.-]+@sha256:[0-9a-f]{64}$'))
    {
        throw 'Each Docker service fixture must use the pinned upstream image by digest.'
    }
}
if (@('tests/BlueTusk.Data.DependencyInjection.Tests/BlueTusk.Data.DependencyInjection.Tests.csproj',
    'tests/BlueTusk.RuntimeCompatibility.Tests/BlueTusk.RuntimeCompatibility.Tests.csproj' |
    Where-Object { $_ -cnotin $postgresql.testProjects }).Count -ne 0)
{
    throw 'The provider and dependency injection live test suites must use the pinned PostgreSQL compatibility fixture.'
}
if ($RequireComplete -and $pending.Count -ne 0)
{
    throw "Full .NET compatibility is incomplete: $($pending.Count) runtime packages still lack executable coverage: $($pending -join ', ')."
}
$summary = [pscustomobject]@{
    schemaVersion = 1
    frameworks = $frameworks
    coveredRuntimePackages = $covered
    pendingRuntimePackages = @($pending)
    fullRuntimeCoverage = $pending.Count -eq 0
    packageCount = $packages.Count
}
if ($PassThru) { return $summary }
Write-Output "Verified .NET compatibility inventory: $covered covered runtime packages, $($pending.Count) pending; $($frameworks -join ', ')."
