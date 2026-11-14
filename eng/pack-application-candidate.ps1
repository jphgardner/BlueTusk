[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [Parameter(Mandatory)][string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$commit = (& git -C $repository rev-parse HEAD).Trim()
if ($LASTEXITCODE -or $commit -cnotmatch '^[0-9a-f]{40}$' -or
    @(& git -C $repository status --porcelain).Count) {
    throw 'Application qualification requires a clean candidate checkout.'
}
$output = [IO.Path]::GetFullPath((Join-Path $repository $OutputDirectory))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$output.StartsWith($artifacts, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-Path -LiteralPath $output)) {
    throw 'Use a new candidate directory beneath repository artifacts.'
}
$feed = Join-Path $output 'feed'
$null = New-Item -ItemType Directory -Path $feed
$projects = @{}
foreach ($project in Get-ChildItem -LiteralPath "$repository/src" -Recurse -Filter '*.csproj') {
    $projects[$project.BaseName] = $project.FullName
}
$pending = [Collections.Generic.Queue[string]]::new()
foreach ($project in Get-ChildItem -LiteralPath "$repository/applications" -Recurse -Filter '*.csproj') {
    [xml]$document = Get-Content -LiteralPath $project.FullName -Raw
    foreach ($reference in $document.SelectNodes('//PackageReference')) {
        $id = [string]$reference.Include
        if ($id.StartsWith('BlueTusk.', [StringComparison]::Ordinal)) {
            if (!$projects.ContainsKey($id)) { throw "Missing source project for $id." }
            $pending.Enqueue($projects[$id])
        }
    }
}
$selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
while ($pending.Count) {
    $project = $pending.Dequeue()
    if (!$selected.Add($project)) { continue }
    [xml]$document = Get-Content -LiteralPath $project -Raw
    foreach ($reference in $document.SelectNodes('//ProjectReference')) {
        $dependency = [IO.Path]::GetFullPath((Join-Path (Split-Path $project -Parent) ([string]$reference.Include)))
        if (!(Test-Path -LiteralPath $dependency)) { throw "Missing dependency of $project." }
        $pending.Enqueue($dependency)
    }
}
foreach ($project in $selected | Sort-Object) {
    $name = [IO.Path]::GetFileNameWithoutExtension($project)
    dotnet pack $project -c Release -o $feed "-p:Version=$Version" -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true -m:2 *> "$output/$name.log"
    if ($LASTEXITCODE) { throw "Candidate packaging failed for $name. See $output/$name.log." }
    Write-Output "Packed $name from $commit."
}
$solution = '<Solution>' + [Environment]::NewLine
foreach ($project in $selected | Sort-Object) {
    $relative = [IO.Path]::GetRelativePath($output, $project).Replace('\', '/')
    $solution += '  <Project Path="' + $relative + '" />' + [Environment]::NewLine
}
$solution += '</Solution>'
$solutionPath = Join-Path $output 'candidate.slnx'
[IO.File]::WriteAllText($solutionPath, $solution)
$relativeOutput = [IO.Path]::GetRelativePath($repository, $output)
& "$PSScriptRoot/generate-sbom.ps1" -Solution "$relativeOutput/candidate.slnx" -PackageDirectory "$relativeOutput/feed" -OutputDirectory "$relativeOutput/sbom" -Commit $commit -NoRestore
Write-Output "Candidate packages and provenance retained in $output."
