[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet',
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$DotNetPath = (Get-Command $DotNetPath -CommandType Application -ErrorAction Stop).Source
$fixtureRoot = Join-Path $RepositoryRoot ('artifacts/dotnet-compatibility/legacy-api-tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$sourceRoot = Join-Path $RepositoryRoot 'src/BlueTusk.Data'
$originalHashes = @{}
foreach ($kind in 'Shipped', 'Unshipped')
{
    $name = "PublicAPI.$kind.txt"
    $originalHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $sourceRoot $name) -Algorithm SHA256).Hash
    Copy-Item -LiteralPath (Join-Path $sourceRoot $name) -Destination (Join-Path $fixtureRoot $name)
}
$sentinel = 'override Example.UnrelatedApi.KeepTheOverride() -> void'
Add-Content -LiteralPath (Join-Path $fixtureRoot 'PublicAPI.Unshipped.txt') -Value $sentinel -Encoding utf8
$importPath = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'LegacyPublicApi.targets'))
$fixture = @"
<Project>
  <PropertyGroup>
    <BlueTuskLegacyPublicApi>true</BlueTuskLegacyPublicApi>
    <IntermediateOutputPath>`$(MSBuildProjectDirectory)/obj/`$(TargetFramework)/</IntermediateOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <AdditionalFiles Include="`$(MSBuildProjectDirectory)/PublicAPI.Shipped.txt;`$(MSBuildProjectDirectory)/PublicAPI.Unshipped.txt" />
  </ItemGroup>
  <Import Project="$importPath" />
  <Target Name="VerifySelection" DependsOnTargets="GenerateBlueTuskLegacyDataPublicApi">
    <WriteLinesToFile File="`$(MSBuildProjectDirectory)/selected-`$(TargetFramework).txt" Lines="@(AdditionalFiles)" Overwrite="true" />
  </Target>
</Project>
"@
$project = Join-Path $fixtureRoot 'BlueTusk.Data.proj'
$fixture | Set-Content -LiteralPath $project -Encoding utf8
foreach ($framework in 'net5.0', 'net6.0', 'net7.0', 'net8.0')
{
    $log = Join-Path $fixtureRoot "$framework.log"
    & $DotNetPath msbuild $project -t:VerifySelection "-p:TargetFramework=$framework" -nologo -v:minimal *> $log
    if ($LASTEXITCODE -ne 0) { throw "Legacy API generator fixture failed for '$framework': $(Get-Content -LiteralPath $log -Raw)" }
    $selected = @(Get-Content -LiteralPath (Join-Path $fixtureRoot "selected-$framework.txt"))
    if ($selected.Count -ne 2) { throw "Expected two public API inputs for '$framework'." }
    if ($framework -ceq 'net8.0')
    {
        if (@($selected | Where-Object { $_ -match 'LegacyPublicApi' }).Count -ne 0)
        {
            throw 'Modern target API inputs must remain unchanged.'
        }
        continue
    }
    if (@($selected | Where-Object { $_ -notmatch 'LegacyPublicApi' }).Count -ne 0)
    {
        throw "Legacy target '$framework' retained an untransformed API input."
    }
    $output = Join-Path $fixtureRoot "obj/$framework/LegacyPublicApi"
    $shipped = Get-Content -LiteralPath (Join-Path $output 'PublicAPI.Shipped.txt') -Raw
    $unshipped = Get-Content -LiteralPath (Join-Path $output 'PublicAPI.Unshipped.txt') -Raw
    if (-not $unshipped.Contains($sentinel, [StringComparison]::Ordinal))
    {
        throw 'Unrelated public API declarations must not be rewritten or filtered.'
    }
    $hasNativeBatchTypes = $shipped.Contains('System.Data.Common.DbBatch', [StringComparison]::Ordinal)
    if (($framework -ceq 'net5.0' -and $hasNativeBatchTypes) -or ($framework -cne 'net5.0' -and -not $hasNativeBatchTypes))
    {
        throw "Batch API aliases are incorrect for '$framework'."
    }
    $hasNativeDataSource = $shipped.Contains('System.Data.Common.DbDataSource', [StringComparison]::Ordinal)
    if (($framework -ceq 'net7.0' -and -not $hasNativeDataSource) -or ($framework -cne 'net7.0' -and $hasNativeDataSource))
    {
        throw "Data-source API aliases are incorrect for '$framework'."
    }
    $parameterOverride = 'override BlueTusk.Data.BlueTuskBatchCommand.CreateParameter('
    if (($framework -ceq 'net5.0' -and -not $shipped.Contains($parameterOverride, [StringComparison]::Ordinal)) -or
        ($framework -cne 'net5.0' -and $shipped.Contains($parameterOverride, [StringComparison]::Ordinal)))
    {
        throw "Batch parameter override declaration is incorrect for '$framework'."
    }
    if ($framework -cne 'net7.0' -and -not $unshipped.Contains('BlueTusk.Data.Compatibility.BlueTuskLegacyDataSource', [StringComparison]::Ordinal))
    {
        throw "Legacy adapter APIs are missing from '$framework'."
    }
}
$aliasFixture = $fixture.Replace('<BlueTuskLegacyPublicApi>true</BlueTuskLegacyPublicApi>', '<BlueTuskLegacyDataSourcePublicApi>true</BlueTuskLegacyDataSourcePublicApi>').Replace('GenerateBlueTuskLegacyDataPublicApi', 'GenerateBlueTuskLegacyDataSourcePublicApi')
$aliasFixture | Set-Content -LiteralPath $project -Encoding utf8
foreach ($framework in 'net5.0', 'net6.0')
{
    & $DotNetPath msbuild $project -t:VerifySelection "-p:TargetFramework=$framework" -nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "Alias-only API generation failed for '$framework'." }
    foreach ($kind in 'Shipped', 'Unshipped')
    {
        $name = "PublicAPI.$kind.txt"
        $original = (Get-Content -LiteralPath (Join-Path $fixtureRoot $name) -Raw).Replace("`r`n", "`n")
        $actual = Get-Content -LiteralPath (Join-Path $fixtureRoot "obj/$framework/LegacyDataSourcePublicApi/$name") -Raw
        if ($actual -cne $original.Replace('System.Data.Common.DbDataSource', 'BlueTusk.Data.Compatibility.BlueTuskLegacyDataSource'))
        {
            throw 'Alias-only mode must change only the missing BCL data-source type name.'
        }
    }
}
$efSource = Join-Path $RepositoryRoot 'src/BlueTusk.EntityFrameworkCore'
foreach ($kind in 'Shipped', 'Unshipped')
{
    Copy-Item -LiteralPath (Join-Path $efSource "PublicAPI.$kind.txt") -Destination (Join-Path $fixtureRoot "PublicAPI.$kind.txt") -Force
}
$efFixture = $fixture.Replace('<BlueTuskLegacyPublicApi>true</BlueTuskLegacyPublicApi>', '<BlueTuskLegacyEntityFrameworkPublicApi>true</BlueTuskLegacyEntityFrameworkPublicApi>').Replace('GenerateBlueTuskLegacyDataPublicApi', 'GenerateBlueTuskLegacyEntityFrameworkPublicApi')
$efProject = Join-Path $fixtureRoot 'BlueTusk.EntityFrameworkCore.proj'
$efFixture | Set-Content -LiteralPath $efProject -Encoding utf8
foreach ($framework in 'net5.0', 'net8.0', 'net9.0')
{
    & $DotNetPath msbuild $efProject -t:VerifySelection "-p:TargetFramework=$framework" -nologo -v:quiet
    if ($LASTEXITCODE -ne 0) { throw "EF API generation failed for '$framework'." }
    $output = Join-Path $fixtureRoot "obj/$framework/LegacyEntityFrameworkPublicApi"
    $shipped = Get-Content -LiteralPath (Join-Path $output 'PublicAPI.Shipped.txt') -Raw
    $unshipped = Get-Content -LiteralPath (Join-Path $output 'PublicAPI.Unshipped.txt') -Raw
    if ($shipped.Contains('Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder', [StringComparison]::Ordinal) -or
        -not $shipped.Contains('System.Action<BlueTusk.EntityFrameworkCore.BlueTuskUpdateSettersBuilder<TSource!>!>!', [StringComparison]::Ordinal) -or
        -not $unshipped.Contains('BlueTuskUpdateSettersBuilder<TSource>.SetProperty<TProperty>', [StringComparison]::Ordinal))
    {
        throw 'Legacy EF setters must retain the fluent API through an explicit typed adapter baseline.'
    }
    $hasLegacyCheckConstraint = $unshipped.Contains('BlueTuskLegacyCheckConstraintExtensions.HasCheckConstraint', [StringComparison]::Ordinal)
    if (($framework -ceq 'net5.0') -ne $hasLegacyCheckConstraint -or
        ($framework -ceq 'net5.0' -and $shipped.Contains('Microsoft.EntityFrameworkCore.Metadata.IReadOnlyModel', [StringComparison]::Ordinal)))
    {
        throw "Incorrect EF 5 check-constraint and metadata API specialization for '$framework'."
    }
}
foreach ($name in $originalHashes.Keys)
{
    if ((Get-FileHash -LiteralPath (Join-Path $sourceRoot $name) -Algorithm SHA256).Hash -cne $originalHashes[$name])
    {
        throw 'Legacy API generation modified a source public API baseline.'
    }
}
Write-Output 'Verified Data/EF legacy adapters, exact override differences, alias-only mode, modern input preservation, and unchanged source baselines.'
