[CmdletBinding()]
param(
    [string] $SolutionPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'BlueTusk.slnx')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Split-Path $PSScriptRoot -Parent)
$repositoryPrefix = $repositoryRoot.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fullSolutionPath = (Resolve-Path -LiteralPath $SolutionPath).Path
[xml] $solution = Get-Content -LiteralPath $fullSolutionPath -Raw
$failures = [Collections.Generic.List[string]]::new()
$comparer = [StringComparer]::OrdinalIgnoreCase

function Test-Ordered
{
    param(
        [Parameter(Mandatory)]
        [string[]] $Values
    )

    $ordered = [string[]]$Values.Clone()
    [Array]::Sort($ordered, $comparer)
    for ($index = 0; $index -lt $Values.Count; $index++)
    {
        if (-not $comparer.Equals($Values[$index], $ordered[$index]))
        {
            return $false
        }
    }

    return $true
}

function Get-ExpectedArea
{
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    if ($ProjectPath.StartsWith('benchmarks/', [StringComparison]::Ordinal))
    {
        return 'Benchmarks'
    }

    if ($ProjectPath -match '^(src|tests|samples)/BlueTusk\.ContinuousGraph' -or
        $ProjectPath -match '^samples/BlueTusk\.Samples\.ContinuousGraph')
    {
        return 'ContinuousGraph'
    }

    if ($ProjectPath -match '^(src|tests)/BlueTusk\.Live')
    {
        return 'Live'
    }

    if ($ProjectPath -match '^src/BlueTusk\.(ControlPlane|Dashboard)' -or
        $ProjectPath -match '^tests/BlueTusk\.ControlPlane')
    {
        return 'Operations'
    }

    if ($ProjectPath -match '^(src|tests)/BlueTusk\.Streams' -or
        $ProjectPath -match '^samples/BlueTusk\.Samples\.Streams' -or
        $ProjectPath -match '^tooling/BlueTusk\.Streams')
    {
        return 'Streams'
    }

    if ($ProjectPath -match '^(src|tests)/BlueTusk\.Sync')
    {
        return 'Sync'
    }

    if ($ProjectPath -match '^tests/BlueTusk\.(IntegrationTests|StressTests)')
    {
        return 'Tests'
    }

    return 'Provider'
}

$folders = @($solution.SelectNodes('/Solution/Folder'))
$rootProjects = @($solution.SelectNodes('/Solution/Project'))
if ($rootProjects.Count -gt 0)
{
    $failures.Add('Projects must be assigned to a product-oriented solution folder.')
}

$folderNames = @($folders | ForEach-Object { $_.GetAttribute('Name') })
if (-not (Test-Ordered -Values $folderNames))
{
    $failures.Add('Solution folders are not ordered deterministically.')
}

$folderPattern =
    '^/(Benchmarks|ContinuousGraph|Live|Operations|Provider|Streams|Sync|Tests)' +
    '(/[A-Za-z][A-Za-z0-9]*)*/$'
$genericFolders = @('/src/', '/tests/', '/extensions/', '/samples/', '/tooling/')
$registeredProjects = [Collections.Generic.List[string]]::new()

foreach ($folder in $folders)
{
    $folderName = $folder.GetAttribute('Name')
    if ($folderName -notmatch $folderPattern)
    {
        $failures.Add("Solution folder '$folderName' is not a valid product-oriented path.")
    }

    if ($genericFolders -contains $folderName)
    {
        $failures.Add("Generic filesystem solution folder '$folderName' is not allowed.")
    }

    $projects = @(
        $folder.SelectNodes('Project') |
            ForEach-Object { $_.GetAttribute('Path') }
    )
    if ($projects.Count -eq 0)
    {
        $failures.Add("Solution folder '$folderName' is empty.")
        continue
    }

    if (-not (Test-Ordered -Values $projects))
    {
        $failures.Add("Projects in solution folder '$folderName' are not ordered deterministically.")
    }

    $actualArea = $folderName.Trim('/').Split('/')[0]
    foreach ($project in $projects)
    {
        $registeredProjects.Add($project)
        if ([IO.Path]::IsPathRooted($project) -or
            $project.Contains('\', [StringComparison]::Ordinal) -or
            $project.Split('/') -contains '..')
        {
            $failures.Add("Solution project path '$project' is not a normalized repository-relative path.")
            continue
        }

        $fullProjectPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $project))
        if (-not $fullProjectPath.StartsWith(
                $repositoryPrefix,
                [StringComparison]::OrdinalIgnoreCase))
        {
            $failures.Add("Solution project path '$project' escapes the repository.")
            continue
        }

        if (-not (Test-Path -LiteralPath $fullProjectPath -PathType Leaf))
        {
            $failures.Add("Solution project '$project' does not exist.")
        }

        $expectedArea = Get-ExpectedArea -ProjectPath $project
        if (-not [string]::Equals(
                $actualArea,
                $expectedArea,
                [StringComparison]::Ordinal))
        {
            $failures.Add(
                "Solution project '$project' is under '$actualArea'; expected '$expectedArea'.")
        }
    }
}

$duplicates = @(
    $registeredProjects |
        Group-Object |
        Where-Object Count -gt 1
)
foreach ($duplicate in $duplicates)
{
    $failures.Add("Solution project '$($duplicate.Name)' is registered more than once.")
}

$projectRoots = @(
    'benchmarks',
    'extensions',
    'identity',
    'samples',
    'src',
    'templates',
    'tests',
    'tooling'
)
$diskProjects = @(
    foreach ($projectRoot in $projectRoots)
    {
        Get-ChildItem -LiteralPath (Join-Path $repositoryRoot $projectRoot) `
            -Filter '*.csproj' `
            -Recurse `
            -File |
            ForEach-Object {
                ([IO.Path]::GetRelativePath($repositoryRoot, $_.FullName)).Replace(
                    [IO.Path]::DirectorySeparatorChar,
                    [char]'/')
            }
    }
) | Where-Object {
    -not $_.StartsWith(
        'templates/BlueTusk.Extension/content/',
        [StringComparison]::Ordinal)
}

$registeredSet = [Collections.Generic.HashSet[string]]::new(
    $registeredProjects,
    $comparer)
$diskSet = [Collections.Generic.HashSet[string]]::new(
    [string[]]$diskProjects,
    $comparer)

foreach ($project in $diskSet)
{
    if (-not $registeredSet.Contains($project))
    {
        $failures.Add("Repository project '$project' is missing from the solution.")
    }

    [xml] $projectDocument = Get-Content `
        -LiteralPath (Join-Path $repositoryRoot $project) `
        -Raw
    $redundantImplicitUsings = @(
        $projectDocument.SelectNodes('/Project/PropertyGroup/ImplicitUsings') |
            Where-Object InnerText -eq 'enable'
    )
    if ($redundantImplicitUsings.Count -gt 0)
    {
        $failures.Add(
            "Project '$project' redundantly declares ImplicitUsings=enable; it is inherited centrally.")
    }

    $redundantNullable = @(
        $projectDocument.SelectNodes('/Project/PropertyGroup/Nullable') |
            Where-Object InnerText -eq 'enable'
    )
    if ($redundantNullable.Count -gt 0)
    {
        $failures.Add(
            "Project '$project' redundantly declares Nullable=enable; it is inherited centrally.")
    }

    $isPackageRoot =
        $project.StartsWith('src/', [StringComparison]::Ordinal) -or
        $project.StartsWith('extensions/', [StringComparison]::Ordinal) -or
        $project.StartsWith('identity/', [StringComparison]::Ordinal) -or
        $project.StartsWith('tooling/', [StringComparison]::Ordinal)
    $explicitlyNonPackable = @(
        $projectDocument.SelectNodes('/Project/PropertyGroup/IsPackable') |
            Select-Object -Last 1 |
            Where-Object InnerText -eq 'false'
    ).Count -gt 0
    $hasDescription =
        @($projectDocument.SelectNodes('/Project/PropertyGroup/Description')).Count -gt 0
    if ($isPackageRoot -and -not $explicitlyNonPackable -and -not $hasDescription)
    {
        $failures.Add("Packable project '$project' has no package description.")
    }
}

foreach ($project in $registeredSet)
{
    if (-not $diskSet.Contains($project))
    {
        $failures.Add("Solution project '$project' is not an eligible repository project.")
    }
}

if ($failures.Count -gt 0)
{
    throw "Solution layout validation failed:`n- $($failures -join "`n- ")"
}

Write-Output (
    "Verified $($registeredSet.Count) projects in $($folders.Count) " +
    'ordered product-oriented solution folders; two embedded template projects are intentionally excluded.')
