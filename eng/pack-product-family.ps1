[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Provider', 'Streams', 'Sync', 'Live', 'ControlPlane', 'ContinuousGraph')]
    [string] $Family,

    [string] $Configuration = 'Release',
    [string] $Output = 'artifacts/packages',
    [switch] $ValidateOnly,
    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Split-Path $PSScriptRoot -Parent)
$manifestPath = Join-Path $PSScriptRoot 'product-families.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$definition = $manifest.families.$Family

if ($null -eq $definition)
{
    throw "Product family '$Family' is not registered."
}

$versionPath = Join-Path $repositoryRoot $definition.versionFile
if (-not (Test-Path -LiteralPath $versionPath))
{
    throw "Product family '$Family' references missing version file '$($definition.versionFile)'."
}

$projects = foreach ($entry in $definition.packages)
{
    $candidate = Join-Path $repositoryRoot $entry
    if (-not (Test-Path -LiteralPath $candidate))
    {
        throw "Product family '$Family' references missing package root '$entry'."
    }

    if ((Get-Item -LiteralPath $candidate) -is [System.IO.DirectoryInfo])
    {
        Get-ChildItem -LiteralPath $candidate -Filter '*.csproj' -Recurse -File
    }
    else
    {
        Get-Item -LiteralPath $candidate
    }
}

$projects = @($projects | Sort-Object FullName -Unique)
if ($ValidateOnly)
{
    Write-Output "Validated $Family release train with $($projects.Count) registered project(s); publishable=$($definition.publishable)."
    return
}

if ($definition.publishable -ne $true)
{
    throw "Product family '$Family' has not passed its publication gate."
}

if ($projects.Count -eq 0)
{
    throw "Product family '$Family' has no packages. Placeholder packages are not published."
}

$outputPath = Join-Path $repositoryRoot $Output
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

foreach ($project in $projects)
{
    $arguments = @(
        'pack',
        $project.FullName,
        '--configuration', $Configuration,
        '--output', $outputPath
    )
    if ($NoRestore)
    {
        $arguments += '--no-restore'
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "Packing '$($project.FullName)' failed with exit code $LASTEXITCODE."
    }
}
