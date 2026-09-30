[CmdletBinding()]
param([string] $Schema = 'public')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING)) { throw 'An owned live PostgreSQL fixture is required.' }
if ($Schema -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') { throw 'One schema identifier is required.' }
$runRoot = Join-Path $repositoryRoot ('artifacts/ecosystem/sql-package-consumer-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $runRoot 'feed'
$consumer = Join-Path $runRoot 'consumer'
$toolCache = Join-Path $runRoot 'tool-cache'
$consumerCache = Join-Path $runRoot 'consumer-cache'
$schemaTools = Join-Path $runRoot 'schema-tools'
$sqlTools = Join-Path $runRoot 'sql-tools'
New-Item -ItemType Directory -Path $feed, $consumer, $toolCache, $consumerCache -Force | Out-Null
$originalPackages = $env:NUGET_PACKAGES
$originalSchemaConnection = $env:BLUETUSK_SCHEMA_CONNECTION_STRING
$originalSqlConnection = $env:BLUETUSK_SQL_CONNECTION_STRING
$originalCatalogueSchema = $env:BLUETUSK_SQL_PACKAGE_SCHEMA
$catalogueSchema = 'sql_package_' + [Guid]::NewGuid().ToString('N')
$catalogueCreated = $false

function Invoke-Checked([string[]] $Arguments)
{
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw 'A candidate package/consumer command failed; inspect the preceding redacted command output.' }
}

try
{
    $libraries = @(
        'Transport', 'Protocol', 'TypeSystem', 'Diagnostics', 'Security', 'Client',
        'Extensions.Abstractions', 'Data', 'Sql', 'Sql.SourceGeneration', 'Schema')
    foreach ($library in $libraries)
    {
        Invoke-Checked @('pack', (Join-Path $repositoryRoot "src/BlueTusk.$library/BlueTusk.$library.csproj"),
            '-c', 'Release', '-nr:false', '--no-restore', '--output', $feed, '--verbosity', 'quiet')
    }
    foreach ($tool in @('Schema', 'Sql'))
    {
        Invoke-Checked @('pack', (Join-Path $repositoryRoot "tooling/BlueTusk.$tool.Tool/BlueTusk.$tool.Tool.csproj"),
            '-c', 'Release', '-nr:false', '--no-restore', '--output', $feed, '--verbosity', 'quiet')
    }
    $sqlVersion = (& dotnet msbuild (Join-Path $repositoryRoot 'src/BlueTusk.Sql/BlueTusk.Sql.csproj') -getProperty:Version).Trim()
    if ($LASTEXITCODE -ne 0 -or $sqlVersion -notmatch '^\d+\.\d+\.\d+[-.0-9A-Za-z]*$') { throw 'Could not resolve candidate SQL version.' }
    $schemaVersion = (& dotnet msbuild (Join-Path $repositoryRoot 'src/BlueTusk.Schema/BlueTusk.Schema.csproj') -getProperty:Version).Trim()
    if ($LASTEXITCODE -ne 0 -or $schemaVersion -notmatch '^\d+\.\d+\.\d+[-.0-9A-Za-z]*$') { throw 'Could not resolve candidate Schema version.' }
    $feedXml = [System.Security.SecurityElement]::Escape($feed)
    $config = Join-Path $runRoot 'NuGet.Config'
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources><clear /><add key="candidate" value="$feedXml" /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources>
  <packageSourceMapping><clear /><packageSource key="candidate"><package pattern="BlueTusk.*" /></packageSource><packageSource key="nuget.org"><package pattern="*" /></packageSource></packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    $env:NUGET_PACKAGES = $toolCache
    Invoke-Checked @('tool', 'install', 'BlueTusk.Schema.Tool', '--tool-path', $schemaTools, '--version', $schemaVersion, '--configfile', $config, '--no-cache')
    Invoke-Checked @('tool', 'install', 'BlueTusk.Sql.Tool', '--tool-path', $sqlTools, '--version', $sqlVersion, '--configfile', $config, '--no-cache')
    $extension = if ($IsWindows) { '.exe' } else { '' }
    $schemaCommand = Join-Path $schemaTools ('bluetusk-schema' + $extension)
    $sqlCommand = Join-Path $sqlTools ('bluetusk-sql' + $extension)
    $snapshot = Join-Path $consumer 'app.bluetusk-schema.json'
    $receipt = Join-Path $consumer 'app.bluetusk-sql.xml'
    $query = Join-Path $consumer 'types.sql'
    $queryText = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tests/BlueTusk.Sql.Tests/Queries/TypeShapes.sql') -Raw
    [IO.File]::WriteAllText($query, "-- bluetusk-validation: required`n" + $queryText, [Text.UTF8Encoding]::new($false))
    $env:BLUETUSK_SCHEMA_CONNECTION_STRING = $env:BLUETUSK_TEST_CONNECTION_STRING
    $env:BLUETUSK_SQL_CONNECTION_STRING = $env:BLUETUSK_TEST_CONNECTION_STRING
    & $schemaCommand capture --schemas $Schema --output $snapshot
    if ($LASTEXITCODE -ne 0) { throw 'Installed Schema CLI capture failed.' }
    & $sqlCommand validate --schema $snapshot --schemas $Schema --output $receipt --query $query
    if ($LASTEXITCODE -ne 0) { throw 'Installed SQL CLI validation failed.' }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'tests/BlueTusk.Sql.Tests/TypedShapeVerification.cs') -Destination (Join-Path $consumer 'TypedShapeVerification.cs')
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><IsPackable>false</IsPackable><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally><BlueTuskProductFamily>Sql</BlueTuskProductFamily></PropertyGroup>
  <ItemGroup><PackageReference Include="BlueTusk.Sql" Version="$sqlVersion" /><PackageReference Include="BlueTusk.Sql.SourceGeneration" Version="$sqlVersion" PrivateAssets="all" /></ItemGroup>
  <ItemGroup><AdditionalFiles Include="types.sql" /><AdditionalFiles Include="app.bluetusk-schema.json" /><AdditionalFiles Include="app.bluetusk-sql.xml" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $consumer 'Consumer.csproj') -Encoding utf8NoBOM
    @'
using BlueTusk.Data;
using BlueTusk.Sql.Verification;
using BlueTusk.Sql.Verification.Generated;
var catalogueSchema = Environment.GetEnvironmentVariable("BLUETUSK_SQL_PACKAGE_SCHEMA");
if (args.Length == 1 && args[0] is "--setup-catalogue" or "--cleanup-catalogue")
{
    if (string.IsNullOrWhiteSpace(catalogueSchema)) { throw new InvalidOperationException("The owned catalogue schema is required."); }
    await using var administration = BlueTuskDataSource.Create(Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("The live fixture is required."));
    await using var command = administration.CreateCommand(args[0] == "--setup-catalogue"
        ? $"CREATE SCHEMA {catalogueSchema}; CREATE TYPE {catalogueSchema}.state AS ENUM ('new', 'done'); CREATE DOMAIN {catalogueSchema}.positive AS int4 CHECK (VALUE > 0)"
        : $"DROP SCHEMA IF EXISTS {catalogueSchema} CASCADE");
    _ = await command.ExecuteNonQueryAsync();
    return;
}
if (string.IsNullOrEmpty(TypeShapes.ValidatedSchemaFingerprint)) { throw new InvalidOperationException("The package consumer lost its build-time validation receipt."); }
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
await using var source = BlueTuskDataSource.Create(Environment.GetEnvironmentVariable("BLUETUSK_TEST_CONNECTION_STRING") ?? throw new InvalidOperationException("The live fixture is required."));
await TypedShapeVerification.RunAsync(source, deadline.Token);
Console.WriteLine("Package-only SQL type roundtrip and installed CLI/offline receipt passed.");
'@ | Set-Content -LiteralPath (Join-Path $consumer 'Program.cs') -Encoding utf8NoBOM
    $env:NUGET_PACKAGES = $consumerCache
    $project = Join-Path $consumer 'Consumer.csproj'
    Invoke-Checked @('restore', $project, '--configfile', $config, '--packages', $consumerCache, '--no-cache', '--verbosity', 'quiet')
    Invoke-Checked @('run', '--project', $project, '-c', 'Release', '--no-restore', '--verbosity', 'quiet')
    $catalogueSnapshot = Join-Path $consumer 'app.bluetusk-catalog.json'
    & $schemaCommand capture-catalog --schemas $Schema --output $catalogueSnapshot
    if ($LASTEXITCODE -ne 0) { throw 'Installed Schema CLI catalogue capture failed.' }
    & $sqlCommand validate --schema $catalogueSnapshot --schemas $Schema --output $receipt --query $query
    if ($LASTEXITCODE -ne 0) { throw 'Installed SQL CLI catalogue validation failed.' }
    $projectText = [IO.File]::ReadAllText($project).Replace('app.bluetusk-schema.json', 'app.bluetusk-catalog.json')
    [IO.File]::WriteAllText($project, $projectText, [Text.UTF8Encoding]::new($false))
    Invoke-Checked @('run', '--project', $project, '-c', 'Release', '--no-restore', '--verbosity', 'quiet')
    $env:BLUETUSK_SQL_PACKAGE_SCHEMA = $catalogueSchema
    $catalogueCreated = $true
    Invoke-Checked @('run', '--project', $project, '-c', 'Release', '--no-restore', '--verbosity', 'quiet', '--', '--setup-catalogue')
    & $schemaCommand capture-catalog --schemas "$Schema,$catalogueSchema" --output $catalogueSnapshot
    if ($LASTEXITCODE -ne 0) { throw 'Installed Schema CLI owned catalogue capture failed.' }
    $catalogueQuery = Join-Path $consumer 'catalogue-types.sql'
    @"
-- bluetusk-query: BlueTusk.Sql.Verification.Generated.CatalogueTypes
-- bluetusk-validation: required
-- bluetusk-param: state enum:$catalogueSchema.state required
-- bluetusk-param: amount domain:$catalogueSchema.positive:int4 required
-- bluetusk-param: states enum:$catalogueSchema.state[] required
-- bluetusk-param: amounts domain:$catalogueSchema.positive:int4[] required
-- bluetusk-param: maybe enum:$catalogueSchema.state nullable
-- bluetusk-result: State enum:$catalogueSchema.state required
-- bluetusk-result: Amount domain:$catalogueSchema.positive:int4 required
-- bluetusk-result: States enum:$catalogueSchema.state[] required
-- bluetusk-result: Amounts domain:$catalogueSchema.positive:int4[] required
-- bluetusk-result: Maybe enum:$catalogueSchema.state nullable
SELECT `$1::$catalogueSchema.state AS "State", `$2::$catalogueSchema.positive AS "Amount",
    `$3::$catalogueSchema.state[] AS "States", `$4::$catalogueSchema.positive[] AS "Amounts",
    `$5::$catalogueSchema.state AS "Maybe";
"@ | Set-Content -LiteralPath $catalogueQuery -Encoding utf8NoBOM
    & $sqlCommand validate --schema $catalogueSnapshot --schemas "$Schema,$catalogueSchema" --output $receipt --query $query --query $catalogueQuery
    if ($LASTEXITCODE -ne 0) { throw 'Installed SQL CLI catalogue-type validation failed.' }
    $projectText = [IO.File]::ReadAllText($project).Replace('<AdditionalFiles Include="types.sql" />', '<AdditionalFiles Include="types.sql" /><AdditionalFiles Include="catalogue-types.sql" />')
    [IO.File]::WriteAllText($project, $projectText, [Text.UTF8Encoding]::new($false))
    @'
using System.Data.Common;
using BlueTusk.Data;
using BlueTusk.Sql.Verification.Generated;
using BlueTusk.TypeSystem;

namespace BlueTusk.Sql.Verification;

internal static class CatalogueVerification
{
    internal static async Task RunAsync(DbDataSource source, CancellationToken token)
    {
        var input = new CatalogueTypes.Arguments(new BlueTuskEnumValue("new"), 42,
            [new BlueTuskEnumValue("new"), new BlueTuskEnumValue("done")], [1, 2, 3], null);
        await CatalogueTypes.Definition.ValidateAsync(source, input, token);
        await using var connection = await source.OpenConnectionAsync(token);
        var count = 0;
        await foreach (var row in CatalogueTypes.Definition.ReadAsync(connection, input, cancellationToken: token))
        {
            if (row.State != input.state || row.Amount != input.amount ||
                !row.States.SequenceEqual(input.states) || !row.Amounts.SequenceEqual(input.amounts) || row.Maybe is not null)
            { throw new InvalidOperationException("Package-only catalogue type values changed during roundtrip."); }
            count++;
        }
        if (count != 1) { throw new InvalidOperationException("Package-only catalogue query returned the wrong row count."); }
    }
}
'@ | Set-Content -LiteralPath (Join-Path $consumer 'CatalogueVerification.cs') -Encoding utf8NoBOM
    $program = Join-Path $consumer 'Program.cs'
    $programText = [IO.File]::ReadAllText($program).Replace('await TypedShapeVerification.RunAsync(source, deadline.Token);',
        'await TypedShapeVerification.RunAsync(source, deadline.Token);' + "`n" + 'await CatalogueVerification.RunAsync(source, deadline.Token);')
    [IO.File]::WriteAllText($program, $programText, [Text.UTF8Encoding]::new($false))
    Invoke-Checked @('run', '--project', $project, '-c', 'Release', '--no-restore', '--verbosity', 'quiet')
    $packages = @(Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{
        formatVersion = 1; executedAtUtc = [DateTime]::UtcNow.ToString('o')
        commit = (& git -C $repositoryRoot rev-parse HEAD)
        workingTreeDirty = -not [string]::IsNullOrWhiteSpace((& git -C $repositoryRoot status --porcelain | Out-String))
        productionQualified = $false; packageOnlyConsumer = $true; isolatedRestore = $true
        installedSchemaCli = $true; installedSqlCli = $true; offlineValidationReceipt = $true
        validationModes = @('relation-format-1', 'attested-catalogue-format-1', 'attested-catalogue-enum-domain')
        packages = $packages
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runRoot 'executions.json') -Encoding utf8NoBOM
    Write-Output "Verified fresh package-only SQL consumer; evidence: $runRoot"
}
finally
{
    $cleanupFailed = $false
    if ($catalogueCreated)
    {
        $compiledConsumer = Join-Path $consumer 'bin/Release/net10.0/Consumer.dll'
        if (Test-Path -LiteralPath $compiledConsumer)
        {
            & dotnet $compiledConsumer --cleanup-catalogue
            $cleanupFailed = $LASTEXITCODE -ne 0
        }
        else { $cleanupFailed = $true }
    }
    $env:NUGET_PACKAGES = $originalPackages
    $env:BLUETUSK_SCHEMA_CONNECTION_STRING = $originalSchemaConnection
    $env:BLUETUSK_SQL_CONNECTION_STRING = $originalSqlConnection
    $env:BLUETUSK_SQL_PACKAGE_SCHEMA = $originalCatalogueSchema
    if ($cleanupFailed) { throw 'The owned SQL package catalogue fixture could not be confirmed removed.' }
}
