[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'core-test-evidence.psm1') -Force
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$scratch = Join-Path $repositoryRoot "artifacts/core-test-reader-fixtures-$([Guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
$commit = '1' * 40
$namespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
$rejected = 0

function Save-Json([string] $Path, [object] $Value)
{ $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding utf8NoBOM }
function New-Trx([string] $Assembly, [string] $ClassName = "$Assembly.ReaderFixture")
{
    $id = [Guid]::NewGuid().ToString('D')
    $document = [Xml.XmlDocument]::new()
    # Synthetic tests exercise the reader only; these files are not release evidence.
    $document.LoadXml(@"
<TestRun xmlns="$namespace">
  <Results><UnitTestResult testId="$id" testName="$ClassName.Passes" outcome="Passed" /></Results>
  <TestDefinitions><UnitTest id="$id"><TestMethod codeBase="C:\build\$Assembly.dll" className="$ClassName" name="Passes" /></UnitTest></TestDefinitions>
  <ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" error="0" timeout="0" aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0" disconnected="0" warning="0" inProgress="0" pending="0" /></ResultSummary>
</TestRun>
"@)
    return $document
}
function New-Fixture([string] $Kind)
{
    $root = Join-Path $scratch $Kind
    $null = New-Item -ItemType Directory -Path $root
    $ids = if ($Kind -eq 'Regression') { @('linux-x64', 'windows-x64') } else { @(15, 16, 17, 18 | ForEach-Object { "postgresql-$_" }) }
    foreach ($id in $ids)
    {
        $shardRoot = Join-Path $root "runs/$id"
        $null = New-Item -ItemType Directory -Path $shardRoot
        $rows = @()
        $major = if ($Kind -eq 'Compatibility') { [int]$id.Substring('postgresql-'.Length) } else { 0 }
        foreach ($project in Get-CoreTestPlan $Kind $major)
        {
            $relative = "tests/$($project.name)"
            $directory = Join-Path $shardRoot $relative
            $null = New-Item -ItemType Directory -Path $directory
            $className = if ($project.name -ceq 'BlueTusk.EntityFrameworkCore.SpecificationTests')
            { 'Microsoft.EntityFrameworkCore.BlueTuskReaderFixture' } else { "$($project.name).ReaderFixture" }
            @('The following Tests are available:', "    $className.Passes") |
                Set-Content -LiteralPath (Join-Path $directory 'discovery.log') -Encoding utf8NoBOM
            (New-Trx $project.name $className).Save((Join-Path $directory 'tests.trx'))
            'Synthetic reader fixture, not a test execution.' | Set-Content -LiteralPath (Join-Path $directory 'test.log') -Encoding utf8NoBOM
            Copy-Item -LiteralPath (Join-Path $scratch 'reader-fixture.dll') -Destination (Join-Path $directory "$($project.name).dll")
            $rows += [pscustomobject]@{
                name = $project.name; projectPath = $project.path; filter = $project.filter; discovered = 1; passed = 1;
                discovery = New-CoreTestArtifact $shardRoot "$relative/discovery.log";
                trx = New-CoreTestArtifact $shardRoot "$relative/tests.trx";
                log = New-CoreTestArtifact $shardRoot "$relative/test.log";
                assembly = New-CoreTestArtifact $shardRoot "$relative/$($project.name).dll"
            }
        }
        $database = $null
        if ($Kind -eq 'Compatibility')
        {
            $major = [int]$id.Substring('postgresql-'.Length)
            $compose = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'compose/postgres.yml') -Raw
            $image = [regex]::Match($compose, "image: (postgres:$major-alpine@sha256:[0-9a-f]{64})").Groups[1].Value
            $fixture = [pscustomobject]@{ major = $major; imageReference = $image; imageId = "sha256:$('a' * 64)"; serverVersionNumber = $major * 10000 + 1 }
            Save-Json (Join-Path $shardRoot 'postgresql-fixture.json') $fixture
            $database = [pscustomobject]@{ major = $major; imageReference = $image; imageId = $fixture.imageId;
                serverVersionNumber = $fixture.serverVersionNumber; fixture = New-CoreTestArtifact $shardRoot 'postgresql-fixture.json' }
        }
        Save-Json (Join-Path $shardRoot 'core-test-shard.json') ([pscustomobject]@{
            schemaVersion = 1; scope = 'Core'; releaseVersion = '1.2.0'; sourceCommit = $commit; sourceTreeDirty = $false;
            kind = $Kind; environmentId = $(if ($Kind -eq 'Regression') { $id } else { 'linux-x64' });
            postgreSql = $database; startedAtUtc = '2026-09-01T00:00:00Z'; completedAtUtc = '2026-09-01T00:01:00Z'; projects = $rows
        })
    }
    $null = & (Join-Path $PSScriptRoot 'build-core-test-manifest.ps1') -EvidenceRoot $root -ExpectedCommit $commit -Kind $Kind
    return $root
}
function Update-Shard([string] $Root, [object] $Manifest, [object] $Shard)
{
    $binding = $Manifest.shards[0]
    $shardPath = Join-Path $Root $binding.manifest.path
    Save-Json $shardPath $Shard
    $binding.manifest = New-CoreTestArtifact $Root $binding.manifest.path
    Save-Json (Join-Path $Root "core-$($Manifest.kind.ToLowerInvariant())-manifest.json") $Manifest
}
function Update-Trx([string] $Root, [object] $Manifest, [object] $Shard, [scriptblock] $Mutation)
{
    $shardRoot = Split-Path (Join-Path $Root $Manifest.shards[0].manifest.path) -Parent
    $path = Join-Path $shardRoot $Shard.projects[0].trx.path
    [xml]$document = Get-Content -LiteralPath $path -Raw
    & $Mutation $document
    $document.Save($path)
    $Shard.projects[0].trx = New-CoreTestArtifact $shardRoot $Shard.projects[0].trx.path
    Update-Shard $Root $Manifest $Shard
}
try
{
    $fixtureProject = Join-Path $scratch 'ReaderFixture.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<TargetFramework>net10.0</TargetFramework><IsPackable>false</IsPackable>
<EnableDefaultCompileItems>false</EnableDefaultCompileItems><AssemblyName>CoreReaderFixture</AssemblyName>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
<EnableSourceControlManagerQueries>false</EnableSourceControlManagerQueries><EnableSourceLink>false</EnableSourceLink>
<InformationalVersion>1.2.0+$commit</InformationalVersion>
</PropertyGroup></Project>
"@ | Set-Content -LiteralPath $fixtureProject -Encoding utf8NoBOM
    '<configuration><packageSources><clear /></packageSources></configuration>' |
        Set-Content -LiteralPath (Join-Path $scratch 'NuGet.config') -Encoding utf8NoBOM
    & dotnet restore $fixtureProject --configfile (Join-Path $scratch 'NuGet.config') --nologo --verbosity quiet *> (Join-Path $scratch 'fixture-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Could not restore the dependency-free synthetic assembly fixture.' }
    & dotnet build $fixtureProject -c Release --no-restore --nologo --verbosity quiet *>> (Join-Path $scratch 'fixture-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Could not build the dependency-free synthetic assembly fixture.' }
    Copy-Item -LiteralPath (Join-Path $scratch 'bin/Release/net10.0/CoreReaderFixture.dll') -Destination (Join-Path $scratch 'reader-fixture.dll')
    $roots = @{ Regression = New-Fixture Regression; Compatibility = New-Fixture Compatibility }
    $baseline = @{}
    foreach ($file in Get-ChildItem -LiteralPath $scratch -Recurse -File)
    { $baseline[$file.FullName] = [IO.File]::ReadAllBytes($file.FullName) }
    foreach ($kind in @('Regression', 'Compatibility'))
    {
        $path = Join-Path $roots[$kind] "core-$($kind.ToLowerInvariant())-manifest.json"
        $report = Get-CoreTestManifestReport $path $commit $kind
        if ($report.ReleaseApproved -ne $false -or $report.RemoteIdentityValidated -ne $false -or
            $report.RawTestPayloadsValidated -ne $true -or $report.Passed -le 0)
        { throw 'Core test reader lost its partial-qualification boundary.' }
        $overwriteRejected = $false
        try { & (Join-Path $PSScriptRoot 'build-core-test-manifest.ps1') -EvidenceRoot $roots[$kind] -ExpectedCommit $commit -Kind $kind | Out-Null }
        catch { $overwriteRejected = $true }
        if (-not $overwriteRejected) { throw 'Existing evidence was overwritten.' }
    }
    $cases = @(
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.schemaVersion = '1' } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.scope = 'ContinuousGraphPreview' } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.releaseVersion = '1.0.0' } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.sourceCommit = '2' * 40 } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.kind = 'Compatibility' } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.shards = @($m.shards[0]) } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.shards[1] = $m.shards[0] } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.shards[0].manifest.path = '../escape.json' } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.shards[0].manifest.sha256 = 'a' * 64 } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $m.shards[0].manifest.bytes = $true } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.sourceTreeDirty = $true; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.sourceTreeDirty = 'false'; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.environmentId = 'windows-x64'; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.sourceCommit = '2' * 40; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.completedAtUtc = '2099-01-01T00:00:00Z'; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.startedAtUtc = $s.completedAtUtc; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.projects = @($s.projects[0]); Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.projects[1] = $s.projects[0]; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.projects[0].filter = 'FullyQualifiedName~Passes'; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.projects[0].projectPath = 'tests/other.csproj'; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.projects[0].passed = '1'; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) $s.projects[0].discovered = 2; Update-Shard $r $m $s } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.Results.UnitTestResult.outcome = 'NotExecuted'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.Results.UnitTestResult.outcome = 'Failed'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.Results.UnitTestResult.testName = 'BlueTusk.Data.Tests.Substitute.Passes'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.ResultSummary.Counters.total = '2'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.ResultSummary.Counters.passedButRunAborted = '1'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.TestDefinitions.UnitTest.TestMethod.codeBase = 'Other.dll'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.TestDefinitions.UnitTest.TestMethod.className = 'Other.Fixture'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.TestDefinitions.UnitTest.TestMethod.name = 'Substitute'} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $x.TestRun.TestDefinitions.UnitTest.id = [Guid]::NewGuid().ToString('D')} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s) Update-Trx $r $m $s {param($x) $null = $x.TestRun.TestDefinitions.AppendChild($x.TestRun.TestDefinitions.UnitTest.CloneNode($true))} } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s)
            $shardRoot = Split-Path (Join-Path $r $m.shards[0].manifest.path) -Parent
            'The following Tests are available:' | Set-Content -LiteralPath (Join-Path $shardRoot $s.projects[0].discovery.path)
            $s.projects[0].discovery = New-CoreTestArtifact $shardRoot $s.projects[0].discovery.path
            Update-Shard $r $m $s
        } },
        @{ Kind = 'Regression'; Change = { param($r,$m,$s)
            $shardRoot = Split-Path (Join-Path $r $m.shards[0].manifest.path) -Parent
            'not an assembly' | Set-Content -LiteralPath (Join-Path $shardRoot $s.projects[0].assembly.path)
            $s.projects[0].assembly = New-CoreTestArtifact $shardRoot $s.projects[0].assembly.path
            Update-Shard $r $m $s
        } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $m.shards = @($m.shards | Select-Object -First 3) } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $s.postgreSql.major = 19; Update-Shard $r $m $s } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $s.postgreSql.imageReference = 'postgres:15-alpine'; Update-Shard $r $m $s } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $s.postgreSql.imageId = 'latest'; Update-Shard $r $m $s } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $s.postgreSql.serverVersionNumber = 180001; Update-Shard $r $m $s } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $s.postgreSql.serverVersionNumber = '150001'; Update-Shard $r $m $s } },
        @{ Kind = 'Compatibility'; Change = { param($r,$m,$s) $s.environmentId = 'windows-x64'; Update-Shard $r $m $s } }
    )
    foreach ($case in $cases)
    {
        foreach ($entry in $baseline.GetEnumerator()) { [IO.File]::WriteAllBytes($entry.Key, $entry.Value) }
        $root = $roots[$case.Kind]
        $path = Join-Path $root "core-$($case.Kind.ToLowerInvariant())-manifest.json"
        $manifest = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        $shard = Get-Content -LiteralPath (Join-Path $root $manifest.shards[0].manifest.path) -Raw | ConvertFrom-Json
        & $case.Change $root $manifest $shard
        Save-Json $path $manifest
        $failed = $false
        try { Get-CoreTestManifestReport $path $commit $case.Kind | Out-Null } catch { $failed = $true }
        if (-not $failed) { throw "Malformed Core test evidence case $rejected was accepted." }
        $rejected++
    }
    Write-Output "Core test evidence reader self-test passed: complete 2-OS/4-database synthetic matrices, immutable manifests and $rejected rejected substitutions, skips, counter/schema or fixture changes. NOT release evidence."
}
finally
{
    $resolved = [IO.Path]::GetFullPath($scratch)
    $allowed = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolved -Leaf) -notmatch '^core-test-reader-fixtures-[0-9a-f]{32}$')
    { throw 'Refusing unexpected Core reader fixture cleanup target.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
