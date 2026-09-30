[CmdletBinding()]
param(
    [string] $Runtime,
    [string] $OutputRoot = 'artifacts/ecosystem-native'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($env:BLUETUSK_TEST_CONNECTION_STRING) -or
    [string]::IsNullOrWhiteSpace($env:BLUETUSK_SEARCH_VECTOR_CONNECTION_STRING))
{
    throw 'Native evidence requires explicit live PostgreSQL and vector fixture connection strings.'
}
$os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported native host.' }
$hostRuntime = $os + '-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ([string]::IsNullOrWhiteSpace($Runtime)) { $Runtime = $hostRuntime }
if ($Runtime -ne $hostRuntime) { throw 'Publish-and-execute evidence must use the current host runtime.' }
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff') + '-' + [Guid]::NewGuid().ToString('N')
$resultDirectory = Join-Path (Join-Path $repositoryRoot $OutputRoot) $runId
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$projects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'tests') -Directory |
    Where-Object { $_.Name -match '^BlueTusk\.(Events|Jobs|Documents|Projections|Search|Schema|Sql|Studio|Edge|Workflows)\.(NativeAotSmoke|AotSmoke)$' } |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Filter '*.csproj' -File } | Sort-Object FullName)
if ($projects.Count -eq 0) { throw 'Native executable test projects are missing.' }
$records = [Collections.Generic.List[object]]::new()
foreach ($project in $projects)
{
    $directory = Join-Path $resultDirectory $project.BaseName
    & dotnet publish $project.FullName -c Release -r $Runtime -nr:false --verbosity quiet --output $directory
    if ($LASTEXITCODE -ne 0) { throw "Native publication failed for $($project.BaseName)." }
    $executable = Join-Path $directory ($project.BaseName + $(if ($IsWindows) { '.exe' } else { '' }))
    if (-not (Test-Path -LiteralPath $executable)) { throw "Native executable missing for $($project.BaseName)." }
    & $executable
    if ($LASTEXITCODE -ne 0) { throw "Native live execution failed for $($project.BaseName)." }
    $records.Add([ordered]@{
        project = [IO.Path]::GetRelativePath($repositoryRoot, $project.FullName).Replace('\', '/')
        runtime = $Runtime; executableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
        published = $true; executed = $true; completedAt = [DateTime]::UtcNow.ToString('o')
    })
}
$commit = & git -C $repositoryRoot rev-parse HEAD
$dirty = -not [string]::IsNullOrWhiteSpace((& git -C $repositoryRoot status --porcelain | Out-String))
[ordered]@{
    schemaVersion = 1; sourceCommit = $commit; workingTreeDirty = $dirty
    productionQualified = $false; executions = @($records.ToArray())
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $resultDirectory 'executions.json') -Encoding utf8NoBOM
Write-Output "Verified $($projects.Count) native live executables on $Runtime; evidence: $resultDirectory"
