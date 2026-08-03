[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $IncludeArtifacts,

    [switch] $IncludeDependencies,

    [switch] $IncludeUserSettings
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Split-Path $PSScriptRoot -Parent)
$repositoryPrefix = $repositoryRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$contentRoots = @(
    'benchmarks',
    'extensions',
    'identity',
    'samples',
    'src',
    'templates',
    'tests',
    'tooling'
)

function Get-SafeGeneratedPath
{
    param(
        [Parameter(Mandatory)]
        [string] $Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
            $repositoryPrefix,
            [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to clean path outside the repository: '$fullPath'."
    }

    $relativePath = ([IO.Path]::GetRelativePath($repositoryRoot, $fullPath)).Replace(
        [IO.Path]::DirectorySeparatorChar,
        [char]'/')
    & git -C $repositoryRoot check-ignore --quiet -- $relativePath
    if ($LASTEXITCODE -ne 0)
    {
        throw "Refusing to clean tracked or non-ignored path '$relativePath'."
    }

    return [pscustomobject]@{
        FullPath = $fullPath
        RelativePath = $relativePath
    }
}

$candidateDirectories = [Collections.Generic.List[string]]::new()
foreach ($contentRoot in $contentRoots)
{
    Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $contentRoot) `
        -Directory `
        -Recurse `
        -Force `
        -ErrorAction SilentlyContinue |
        Where-Object Name -in @('bin', 'obj', 'TestResults', 'coverage') |
        ForEach-Object { $candidateDirectories.Add($_.FullName) }
}

Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'clients') `
    -Directory `
    -ErrorAction SilentlyContinue |
    ForEach-Object {
        $distribution = Join-Path $_.FullName 'dist'
        if (Test-Path -LiteralPath $distribution -PathType Container)
        {
            $candidateDirectories.Add($distribution)
        }
    }

if ($IncludeArtifacts)
{
    $artifacts = Join-Path $repositoryRoot 'artifacts'
    if (Test-Path -LiteralPath $artifacts -PathType Container)
    {
        $candidateDirectories.Add($artifacts)
    }
}

if ($IncludeDependencies)
{
    $dependencies = Join-Path $repositoryRoot 'node_modules'
    if (Test-Path -LiteralPath $dependencies -PathType Container)
    {
        $candidateDirectories.Add($dependencies)
    }
}

if ($IncludeUserSettings)
{
    foreach ($userDirectory in @('.idea', '.vs', '.vscode'))
    {
        $path = Join-Path $repositoryRoot $userDirectory
        if (Test-Path -LiteralPath $path -PathType Container)
        {
            $candidateDirectories.Add($path)
        }
    }
}

$candidateFiles = [Collections.Generic.List[string]]::new()
$baselineRoot = Join-Path $repositoryRoot 'benchmarks/baselines'
if (Test-Path -LiteralPath $baselineRoot -PathType Container)
{
    Get-ChildItem -LiteralPath $baselineRoot -Recurse -File |
        Where-Object {
            $_.Name.EndsWith('.log', [StringComparison]::OrdinalIgnoreCase) -or
            $_.Name.EndsWith('-report.csv', [StringComparison]::OrdinalIgnoreCase) -or
            $_.Name.EndsWith('-report.html', [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object { $candidateFiles.Add($_.FullName) }
}

if ($IncludeUserSettings)
{
    $dotSettings = Join-Path $repositoryRoot 'BlueTusk.sln.DotSettings.user'
    if (Test-Path -LiteralPath $dotSettings -PathType Leaf)
    {
        $candidateFiles.Add($dotSettings)
    }
}

$safeTargets = @(
    @($candidateDirectories | Sort-Object -Unique) +
    @($candidateFiles | Sort-Object -Unique) |
        ForEach-Object { Get-SafeGeneratedPath -Path $_ }
)
$selectedBytes = 0L
$selectedFiles = 0L
foreach ($target in $safeTargets)
{
    if (Test-Path -LiteralPath $target.FullPath -PathType Container)
    {
        $statistics = Get-ChildItem -LiteralPath $target.FullPath `
            -Recurse `
            -File `
            -Force `
            -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum
        $selectedBytes += [long]$statistics.Sum
        $selectedFiles += $statistics.Count
    }
    elseif (Test-Path -LiteralPath $target.FullPath -PathType Leaf)
    {
        $file = Get-Item -LiteralPath $target.FullPath
        $selectedBytes += $file.Length
        $selectedFiles++
    }
}

$sizeMiB = [Math]::Round($selectedBytes / 1MB, 1)
$removedTargets = 0
$cleanupDescription =
    "Remove $($safeTargets.Count) ignored generated path(s), $selectedFiles file(s), and $sizeMiB MiB"
if ($PSCmdlet.ShouldProcess($repositoryRoot, $cleanupDescription))
{
    foreach ($target in $safeTargets | Sort-Object { $_.FullPath.Length } -Descending)
    {
        Remove-Item -LiteralPath $target.FullPath -Recurse -Force
        $removedTargets++
    }
}

if ($WhatIfPreference)
{
    Write-Output (
        "Selected $($safeTargets.Count) ignored generated path(s), " +
        "$selectedFiles file(s), and $sizeMiB MiB; no files were removed because -WhatIf was used.")
}
else
{
    Write-Output (
        "Removed $removedTargets ignored generated path(s), " +
        "$selectedFiles file(s), and $sizeMiB MiB.")
}
