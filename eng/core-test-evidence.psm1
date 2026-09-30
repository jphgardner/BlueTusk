Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-candidate-evidence.psm1') -Force

function Get-CoreTestPlan
{
    param([ValidateSet('Regression', 'Compatibility')][string] $Kind,
        [ValidateSet(0, 15, 16, 17, 18)][int] $PostgreSqlMajor = 0)
    $projects = [Collections.Generic.List[object]]::new()
    function Add-Project([string] $Name, [string] $Filter = '')
    {
        $projects.Add([pscustomobject]@{ name = $Name; path = "tests/$Name/$Name.csproj"; filter = $Filter })
    }
    $liveDatabase = @('LiveClientQueryTests', 'LivePostgreSqlTransportMatrixTests', 'PostgreSqlLiveInvalidationStoreTests')
    $syncDatabase = @('PostgreSqlSyncConformanceTests', 'PostgreSqlSyncDestinationTests', 'PostgreSqlSyncReconciliationTests')
    if ($Kind -eq 'Regression')
    {
        foreach ($name in @('BlueTusk.Data.Tests', 'BlueTusk.Diagnostics.Tests', 'BlueTusk.Protocol.Tests',
            'BlueTusk.Security.Tests', 'BlueTusk.SourceGeneration.Tests', 'BlueTusk.Transport.Tests',
            'BlueTusk.TypeSystem.Tests', 'BlueTusk.Replication.Tests', 'BlueTusk.Replication.PgOutput.Tests',
            'BlueTusk.ConformanceTests', 'BlueTusk.Streams.Tests', 'BlueTusk.Streams.Aspire.Tests',
            'BlueTusk.Streams.DependencyInjection.Tests', 'BlueTusk.Streams.EntityFrameworkCore.Tests',
            'BlueTusk.Sync.Aspire.Tests', 'BlueTusk.Sync.DependencyInjection.Tests', 'BlueTusk.Sync.Testing.Tests',
            'BlueTusk.Sync.Webhooks.Tests', 'BlueTusk.Live.Aspire.Tests', 'BlueTusk.ControlPlane.Tests',
            'BlueTusk.ControlPlane.Kubernetes.Tests')) { Add-Project $name }
        Add-Project 'BlueTusk.Live.Tests' (($liveDatabase | ForEach-Object { "FullyQualifiedName!~BlueTusk.Live.Tests.$_" }) -join '&')
        Add-Project 'BlueTusk.Sync.Tests' (($syncDatabase | ForEach-Object { "FullyQualifiedName!~BlueTusk.Sync.Tests.$_" }) -join '&')
    }
    else
    {
        Add-Project 'BlueTusk.CompatibilityTests'
        Add-Project 'BlueTusk.Data.DependencyInjection.Tests'
        $classes = @('Batch', 'ControlPlane', 'Copy', 'Diagnostics', 'LargeObject', 'MultiHost',
            'Multiplexing', 'Notification', 'Pooling', 'Portal', 'Replication', 'SequentialReader',
            'Session', 'StatementBatch', 'StreamsRelay', 'StreamsStateStore', 'SyncRelaySource',
            'Synchronous', 'SynchronousNative', 'TypeCodec')
        $filters = @($classes | ForEach-Object { "FullyQualifiedName~BlueTusk.IntegrationTests.BlueTusk$($_)IntegrationTests" })
        $filters += 'FullyQualifiedName~BlueTusk.IntegrationTests.BlueTuskRepackIntegrationTests.PrePostgreSql19_repack_is_rejected_before_sql_is_sent'
        Add-Project 'BlueTusk.IntegrationTests' ($filters -join '|')
        Add-Project 'BlueTusk.Live.Tests' (($liveDatabase | ForEach-Object { "FullyQualifiedName~BlueTusk.Live.Tests.$_" }) -join '|')
        Add-Project 'BlueTusk.Sync.Tests' (($syncDatabase | ForEach-Object { "FullyQualifiedName~BlueTusk.Sync.Tests.$_" }) -join '|')
        $entityFrameworkFilter = (
            'FullyQualifiedName!~BlueTusk.EntityFrameworkCore.Tests.PropertyGraphQueryIntegrationTests&' +
            'FullyQualifiedName!~BlueTusk.EntityFrameworkCore.Tests.PropertyGraphMigrationIntegrationTests')
        if ($PostgreSqlMajor -eq 15)
        {
            # PostgreSQL introduced these aggregates in 16. Keep this one test
            # in every supported server shard rather than recording a PG15 skip.
            $entityFrameworkFilter += '&FullyQualifiedName!=BlueTusk.EntityFrameworkCore.Tests.PostgreSqlAggregateTranslationTests.PostgreSQL_16_strict_unique_and_any_value_aggregates_execute'
        }
        Add-Project 'BlueTusk.EntityFrameworkCore.Tests' $entityFrameworkFilter
        Add-Project 'BlueTusk.EntityFrameworkCore.SpecificationTests'
    }
    return $projects.ToArray()
}

function Assert-CoreTestProperties
{
    param([object] $Value, [string[]] $Names, [string] $Context)
    if ($Value -isnot [pscustomobject] -or @($Value.PSObject.Properties).Count -ne $Names.Count -or
        @(Compare-Object @($Value.PSObject.Properties.Name) $Names -CaseSensitive).Count -ne 0)
    { throw "$Context has an unexpected schema." }
}

function Assert-CoreTestInteger
{
    param([object] $Value, [string] $Context, [switch] $AllowZero)
    if (($Value -isnot [int] -and $Value -isnot [long]) -or
        $Value -lt $(if ($AllowZero) { 0 } else { 1 }))
    { throw "$Context must be a $(if ($AllowZero) { 'nonnegative' } else { 'positive' }) JSON integer." }
}

function Get-CoreTestArtifact
{
    param([string] $Root, [object] $Record, [string] $ExpectedPath)
    Assert-CoreTestProperties $Record @('path', 'sha256', 'bytes') 'Core test artifact'
    Assert-CoreTestInteger $Record.bytes 'Artifact byte count'
    if ($Record.path -isnot [string] -or $Record.path -cne $ExpectedPath -or
        $Record.sha256 -isnot [string] -or $Record.sha256 -cnotmatch '^[0-9a-f]{64}$')
    { throw "Test artifact '$ExpectedPath' has an invalid canonical path or digest." }
    $path = Resolve-CoreEvidenceFile $Root $ExpectedPath
    if ((Get-Item -LiteralPath $path).Length -ne $Record.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Record.sha256)
    { throw "Test artifact '$ExpectedPath' differs from its recorded content." }
    return $path
}

function New-CoreTestArtifact
{
    param([string] $Root, [string] $Path)
    $file = Resolve-CoreEvidenceFile $Root $Path
    return [pscustomobject]@{ path = $Path; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(); bytes = (Get-Item -LiteralPath $file).Length }
}

function Get-CoreTestAssemblyVersion
{
    param([string] $Path)
    $fullPath = [IO.Path]::GetFullPath($Path)
    # Windows' native version-resource reader needs an extended path for retained
    # payload trees; a long evidence path must not hide otherwise valid metadata.
    if ($IsWindows -and -not $fullPath.StartsWith('\\?\', [StringComparison]::Ordinal))
    {
        $fullPath = if ($fullPath.StartsWith('\\', [StringComparison]::Ordinal))
        { '\\?\UNC\' + $fullPath.Substring(2) } else { '\\?\' + $fullPath }
    }
    return [Diagnostics.FileVersionInfo]::GetVersionInfo($fullPath).ProductVersion
}

function Get-CoreTrxSummary
{
    param([string] $TrxPath, [string] $DiscoveryPath, [string] $Assembly)
    function Test-RequiredNamespace([string] $Name)
    {
        if ($Name.StartsWith($Assembly + '.', [StringComparison]::Ordinal)) { return $true }
        # The provider's inherited EF specification classes use the upstream
        # namespaces. TRX definitions must still bind the exact provider DLL.
        return $Assembly -ceq 'BlueTusk.EntityFrameworkCore.SpecificationTests' -and
            $Name -cmatch '^Microsoft\.EntityFrameworkCore\.(?:(?:ModelBuilding|Query|Migrations)\.)?BlueTusk'
    }
    $lines = @(Get-Content -LiteralPath $DiscoveryPath)
    $headers = @(0..($lines.Count - 1) | Where-Object { $lines[$_] -ceq 'The following Tests are available:' })
    if ($headers.Count -ne 1) { throw 'Test discovery must contain exactly one VSTest discovery header.' }
    $expected = @($lines | Select-Object -Skip ($headers[0] + 1) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
        if ($_ -cnotmatch '^    (.+)$') { throw 'Test discovery contains unexpected diagnostics.' }
        $Matches[1]
    })
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $expected)
    {
        if (-not (Test-RequiredNamespace $name) -or -not $names.Add($name))
        { throw 'Discovery must contain unique fully qualified tests from the required assembly.' }
    }
    if ($names.Count -eq 0) { throw 'An empty discovery cannot qualify a test suite.' }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 100MB
    $reader = [Xml.XmlReader]::Create($TrxPath, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    try { $document.Load($reader) } finally { $reader.Dispose() }
    $ns = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    $results = @($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $ns))
    $summary = @($document.SelectNodes('/t:TestRun/t:ResultSummary', $ns))
    $counters = @($document.SelectNodes('/t:TestRun/t:ResultSummary/t:Counters', $ns))
    if ($summary.Count -ne 1 -or $summary[0].GetAttribute('outcome') -cne 'Completed' -or $counters.Count -ne 1 -or
        $results.Count -ne $names.Count)
    { throw 'TRX must contain exactly the discovered test set and one completed summary.' }
    $definitionsById = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($definition in $document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $ns))
    {
        if (-not $definitionsById.TryAdd($definition.GetAttribute('id'), $definition))
        { throw 'TRX contains duplicate test definitions.' }
    }
    if ($definitionsById.Count -ne $results.Count) { throw 'TRX must bind exactly its result definitions.' }
    $observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($result in $results)
    {
        $name = $result.GetAttribute('testName')
        if (-not $names.Contains($name) -or -not $observed.Add($name) -or $result.GetAttribute('outcome') -cne 'Passed')
        { throw 'Required tests must execute once each, without substitutions, skips or failures.' }
        $id = $result.GetAttribute('testId')
        $guid = [Guid]::Empty
        if (-not [Guid]::TryParseExact($id, 'D', [ref]$guid) -or -not $definitionsById.ContainsKey($id))
        { throw 'Each test result must identify exactly one test definition.' }
        $method = @($definitionsById[$id].SelectNodes('t:TestMethod', $ns))
        if ($method.Count -ne 1 -or $method[0].GetAttribute('codeBase').Replace('\', '/').Split('/')[-1] -cne "$Assembly.dll")
        { throw 'Test definitions must identify the required assembly.' }
        $methodName = $method[0].GetAttribute('className') + '.' + $method[0].GetAttribute('name')
        if (-not (Test-RequiredNamespace $methodName) -or
            ($name -cne $methodName -and -not $name.StartsWith($methodName + '(', [StringComparison]::Ordinal)))
        { throw 'A test result does not identify its declared test method.' }
    }
    foreach ($field in @('total', 'executed', 'passed'))
    {
        if ($counters[0].GetAttribute($field) -cne [string]$names.Count)
        { throw "TRX counter '$field' disagrees with the discovered and observed tests." }
    }
    foreach ($field in @('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'passedButRunAborted', 'notRunnable', 'notExecuted',
        'disconnected', 'warning', 'inProgress', 'pending'))
    {
        if (-not $counters[0].HasAttribute($field) -or $counters[0].GetAttribute($field) -cne '0')
        { throw "TRX counter '$field' must be zero." }
    }
    if ($document.SelectNodes('/t:TestRun/t:ResultSummary/t:RunInfos/t:RunInfo[@outcome != "Completed"]', $ns).Count -ne 0)
    { throw 'The test host recorded a run-level error.' }
    return [pscustomobject]@{ Discovered = $names.Count; Passed = $observed.Count }
}

function Get-CoreTestShardReport
{
    param([string] $EvidencePath, [string] $ExpectedCommit, [ValidateSet('Regression', 'Compatibility')][string] $Kind,
        [string] $EnvironmentId, [int] $PostgreSqlMajor = 0)
    $root = Split-Path (Resolve-Path -LiteralPath $EvidencePath).Path -Parent
    $path = Resolve-CoreEvidenceFile $root 'core-test-shard.json'
    if ($path -cne (Resolve-Path -LiteralPath $EvidencePath).Path) { throw 'Core test shard manifest path must be canonical.' }
    $shard = Read-CoreEvidenceJson $path
    Assert-CoreTestProperties $shard @('schemaVersion', 'scope', 'releaseVersion', 'sourceCommit', 'sourceTreeDirty',
        'kind', 'environmentId', 'postgreSql', 'startedAtUtc', 'completedAtUtc', 'projects') 'Core test shard'
    if (($shard.schemaVersion -isnot [int] -and $shard.schemaVersion -isnot [long]) -or $shard.schemaVersion -ne 1 -or
        $shard.scope -cne 'Core' -or $shard.releaseVersion -cne '1.2.0' -or $ExpectedCommit -cnotmatch '^[0-9a-f]{40}$' -or
        $shard.sourceCommit -cne $ExpectedCommit -or $shard.sourceTreeDirty -isnot [bool] -or $shard.sourceTreeDirty -ne $false -or
        $shard.kind -cne $Kind -or $shard.environmentId -cne $EnvironmentId -or $shard.projects -isnot [array])
    { throw 'Core test shard scope, exact commit, clean source or environment identity is invalid.' }
    $start = [DateTimeOffset]::MinValue
    $end = [DateTimeOffset]::MinValue
    if ($shard.startedAtUtc -isnot [string] -or $shard.completedAtUtc -isnot [string] -or
        $shard.startedAtUtc -cnotmatch 'Z$|\+00:00$' -or $shard.completedAtUtc -cnotmatch 'Z$|\+00:00$' -or
        -not [DateTimeOffset]::TryParse($shard.startedAtUtc, [ref]$start) -or
        -not [DateTimeOffset]::TryParse($shard.completedAtUtc, [ref]$end) -or $end -le $start -or
        $end -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) { throw 'Core test capture timestamps are invalid.' }
    if ($Kind -eq 'Regression')
    {
        if ($EnvironmentId -cnotin @('linux-x64', 'windows-x64') -or $PostgreSqlMajor -ne 0 -or $null -ne $shard.postgreSql)
        { throw 'Core regression requires a named x64 OS and no database fixture claim.' }
    }
    else
    {
        if ($EnvironmentId -cne 'linux-x64' -or $PostgreSqlMajor -notin @(15, 16, 17, 18))
        { throw 'Core compatibility requires the Linux stable PostgreSQL 15-18 matrix.' }
        Assert-CoreTestProperties $shard.postgreSql @('major', 'imageReference', 'imageId', 'serverVersionNumber', 'fixture') 'PostgreSQL fixture'
        Assert-CoreTestInteger $shard.postgreSql.major 'PostgreSQL major'
        Assert-CoreTestInteger $shard.postgreSql.serverVersionNumber 'PostgreSQL server version'
        $fixturePath = Get-CoreTestArtifact $root $shard.postgreSql.fixture 'postgresql-fixture.json'
        $fixture = Read-CoreEvidenceJson $fixturePath
        Assert-CoreTestProperties $fixture @('major', 'imageReference', 'imageId', 'serverVersionNumber') 'Raw PostgreSQL fixture'
        foreach ($field in @('major', 'imageReference', 'imageId', 'serverVersionNumber'))
        {
            if ($fixture.PSObject.Properties[$field].Value -cne $shard.postgreSql.PSObject.Properties[$field].Value)
            { throw 'Raw PostgreSQL fixture identity differs from the shard.' }
        }
        if ($shard.postgreSql.major -ne $PostgreSqlMajor -or
            [Math]::Floor($shard.postgreSql.serverVersionNumber / 10000) -ne $PostgreSqlMajor -or
            $shard.postgreSql.imageReference -cnotmatch ("^postgres:$PostgreSqlMajor-alpine@sha256:[0-9a-f]{64}$") -or
            $shard.postgreSql.imageId -cnotmatch '^sha256:[0-9a-f]{64}$')
        { throw 'The live database is not the named digest-pinned stable PostgreSQL fixture.' }
        $compose = Get-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'eng/compose/postgres.yml') -Raw
        if (-not $compose.Contains("image: $($shard.postgreSql.imageReference)", [StringComparison]::Ordinal))
        { throw 'Compatibility fixture is not the canonical pinned Compose image.' }
    }
    $plan = Get-CoreTestPlan $Kind $PostgreSqlMajor
    if ($shard.projects.Count -ne $plan.Count) { throw 'Core test shard lacks exact project coverage.' }
    $passed = 0
    foreach ($project in $plan)
    {
        $rows = @($shard.projects | Where-Object { $_.name -ceq $project.name })
        if ($rows.Count -ne 1) { throw "Core test project '$($project.name)' must occur exactly once." }
        $row = $rows[0]
        Assert-CoreTestProperties $row @('name', 'projectPath', 'filter', 'discovered', 'passed', 'discovery', 'trx', 'log', 'assembly') 'Core test project'
        if ($row.projectPath -cne $project.path -or $row.filter -cne $project.filter)
        { throw 'A test project or selection filter differs from the canonical Core capture plan.' }
        Assert-CoreTestInteger $row.discovered 'Discovered tests'
        Assert-CoreTestInteger $row.passed 'Passed tests'
        $discovery = Get-CoreTestArtifact $root $row.discovery "tests/$($project.name)/discovery.log"
        $trx = Get-CoreTestArtifact $root $row.trx "tests/$($project.name)/tests.trx"
        $null = Get-CoreTestArtifact $root $row.log "tests/$($project.name)/test.log"
        $assemblyPath = Get-CoreTestArtifact $root $row.assembly "tests/$($project.name)/$($project.name).dll"
        $productVersion = Get-CoreTestAssemblyVersion $assemblyPath
        if ($productVersion -cne "1.2.0+$ExpectedCommit")
        { throw "The retained test assembly identifies '$productVersion', not the exact 1.2 candidate commit." }
        $summary = Get-CoreTrxSummary $trx $discovery $project.name
        if ($row.discovered -ne $summary.Discovered -or $row.passed -ne $summary.Passed)
        { throw 'Recorded test counters do not agree with the retained discovery and raw TRX.' }
        $passed += $summary.Passed
    }
    return [pscustomobject]@{ Kind = $Kind; SourceCommit = $ExpectedCommit; EnvironmentId = $EnvironmentId;
        PostgreSqlMajor = $PostgreSqlMajor; ProjectCount = $plan.Count; Passed = $passed; ReleaseApproved = $false }
}

function Get-CoreTestManifestReport
{
    param([string] $EvidencePath, [string] $ExpectedCommit, [ValidateSet('Regression', 'Compatibility')][string] $Kind)
    $root = Split-Path (Resolve-Path -LiteralPath $EvidencePath).Path -Parent
    $expectedName = "core-$($Kind.ToLowerInvariant())-manifest.json"
    $path = Resolve-CoreEvidenceFile $root $expectedName
    if ($path -cne (Resolve-Path -LiteralPath $EvidencePath).Path) { throw 'Core test manifest must have its canonical name.' }
    $manifest = Read-CoreEvidenceJson $path
    Assert-CoreTestProperties $manifest @('schemaVersion', 'scope', 'releaseVersion', 'sourceCommit', 'kind', 'shards') 'Core test manifest'
    if (($manifest.schemaVersion -isnot [int] -and $manifest.schemaVersion -isnot [long]) -or $manifest.schemaVersion -ne 1 -or
        $manifest.scope -cne 'Core' -or $manifest.releaseVersion -cne '1.2.0' -or $manifest.sourceCommit -cne $ExpectedCommit -or
        $manifest.kind -cne $Kind -or $manifest.shards -isnot [array]) { throw 'Core test manifest identity is invalid.' }
    $ids = if ($Kind -eq 'Regression') { @('linux-x64', 'windows-x64') } else { @(15, 16, 17, 18 | ForEach-Object { "postgresql-$_" }) }
    if ($manifest.shards.Count -ne $ids.Count) { throw 'Core test manifest must retain its complete OS or database matrix.' }
    $passed = 0
    foreach ($id in $ids)
    {
        $rows = @($manifest.shards | Where-Object { $_.id -ceq $id })
        if ($rows.Count -ne 1) { throw "Core test shard '$id' must occur exactly once." }
        Assert-CoreTestProperties $rows[0] @('id', 'manifest') 'Core test shard binding'
        $shardPath = Get-CoreTestArtifact $root $rows[0].manifest "runs/$id/core-test-shard.json"
        $environment = if ($Kind -eq 'Regression') { $id } else { 'linux-x64' }
        $major = if ($Kind -eq 'Regression') { 0 } else { [int]$id.Substring('postgresql-'.Length) }
        $report = Get-CoreTestShardReport $shardPath $ExpectedCommit $Kind $environment $major
        $passed += $report.Passed
    }
    return [pscustomobject]@{ Kind = $Kind; SourceCommit = $ExpectedCommit; ShardCount = $ids.Count; Passed = $passed;
        RawTestPayloadsValidated = $true; RemoteIdentityValidated = $false; ReleaseApproved = $false }
}

Export-ModuleMember -Function Get-CoreTestPlan, Get-CoreTrxSummary, New-CoreTestArtifact, Get-CoreTestAssemblyVersion,
    Get-CoreTestShardReport, Get-CoreTestManifestReport
