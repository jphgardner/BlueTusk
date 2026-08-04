[CmdletBinding()]
param(
    [string] $RuntimeIdentifier,
    [string] $OutputRoot = 'artifacts/provider-core-smoke',
    [switch] $NoRestore,
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier))
{
    $architecture = switch ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)
    {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported smoke architecture '$($_)'." }
    }
    $platform = if ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [Runtime.InteropServices.OSPlatform]::Windows))
    {
        'win'
    }
    elseif ([Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [Runtime.InteropServices.OSPlatform]::Linux))
    {
        'linux'
    }
    else
    {
        throw 'Provider-core publish smoke currently supports Windows and Linux.'
    }

    $RuntimeIdentifier = "$platform-$architecture"
}

$resolvedOutput = if ([IO.Path]::IsPathRooted($OutputRoot))
{
    $OutputRoot
}
else
{
    Join-Path $repositoryRoot $OutputRoot
}
$runtimeOutput = Join-Path $resolvedOutput $RuntimeIdentifier
$budgets = Get-Content (
    Join-Path $PSScriptRoot 'provider-core-publish-budgets.json') -Raw |
    ConvertFrom-Json
$measurements = [Collections.Generic.List[object]]::new()
$definitions = @(
    @{
        Name = 'trimmed'
        Project = 'tests/BlueTusk.TrimSmoke/BlueTusk.TrimSmoke.csproj'
        Executable = 'BlueTusk.TrimSmoke'
    },
    @{
        Name = 'nativeAot'
        Project = 'tests/BlueTusk.NativeAotSmoke/BlueTusk.NativeAotSmoke.csproj'
        Executable = 'BlueTusk.NativeAotSmoke'
    }
)

foreach ($definition in $definitions)
{
    $publishDirectory = Join-Path $runtimeOutput $definition.Name
    $arguments = @(
        'publish',
        (Join-Path $repositoryRoot $definition.Project),
        '--configuration', 'Release',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--output', $publishDirectory
    )
    if ($NoRestore)
    {
        $arguments += '--no-restore'
    }

    if (-not $SkipPublish)
    {
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0)
        {
            throw "$($definition.Name) publish failed with exit code $LASTEXITCODE."
        }
    }

    $executableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal))
    {
        "$($definition.Executable).exe"
    }
    else
    {
        $definition.Executable
    }
    $executablePath = Join-Path $publishDirectory $executableName
    if (-not (Test-Path -LiteralPath $executablePath))
    {
        throw "$($definition.Name) publish did not produce '$executablePath'."
    }

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $executionOutput = @(& $executablePath 2>&1)
    $exitCode = $LASTEXITCODE
    $timer.Stop()
    if ($exitCode -ne 0)
    {
        throw "$($definition.Name) smoke exited with $exitCode`: $($executionOutput -join [Environment]::NewLine)"
    }

    $marker = @($executionOutput | Where-Object {
            $_ -match '^BLUETUSK_PROVIDER_CORE_SMOKE_OK allocatedBytes=(\d+)$'
        })
    if ($marker.Count -ne 1)
    {
        throw "$($definition.Name) smoke did not emit exactly one success marker."
    }
    $null = $marker[0] -match 'allocatedBytes=(\d+)'
    $allocatedBytes = [long]$Matches[1]
    $files = @(Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
    $totalBytes = [long]($files | Measure-Object -Property Length -Sum).Sum
    $deployableFiles = @($files | Where-Object {
            $_.Extension -notin @('.pdb', '.xml')
        })
    $deployableBytes = [long](
        $deployableFiles | Measure-Object -Property Length -Sum).Sum
    $executableBytes = [long](Get-Item -LiteralPath $executablePath).Length
    $startupMilliseconds = [Math]::Round($timer.Elapsed.TotalMilliseconds, 3)
    $budget = $budgets.modes.PSObject.Properties[$definition.Name].Value

    if ($deployableBytes -gt [long]$budget.maximumDeployableBytes)
    {
        throw (
            "$($definition.Name) deployable output is $deployableBytes bytes; " +
            "budget is $($budget.maximumDeployableBytes).")
    }
    if ($startupMilliseconds -gt [double]$budget.maximumStartupMilliseconds)
    {
        throw "$($definition.Name) startup is $startupMilliseconds ms; budget is $($budget.maximumStartupMilliseconds)."
    }
    if ($allocatedBytes -gt [long]$budget.maximumManagedAllocatedBytes)
    {
        throw "$($definition.Name) allocated $allocatedBytes bytes; budget is $($budget.maximumManagedAllocatedBytes)."
    }

    $measurements.Add([ordered]@{
            mode = $definition.Name
            files = $files.Count
            totalBytes = $totalBytes
            deployableFiles = $deployableFiles.Count
            deployableBytes = $deployableBytes
            symbolsAndDocumentationBytes = $totalBytes - $deployableBytes
            executableBytes = $executableBytes
            startupMilliseconds = $startupMilliseconds
            managedAllocatedBytes = $allocatedBytes
        })
}

$commit = @(& git -C $repositoryRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $commit.Count -ne 1)
{
    throw 'Could not resolve the provider-core smoke commit.'
}
$report = [ordered]@{
    schemaVersion = 1
    commit = $commit[0]
    recordedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runtimeIdentifier = $RuntimeIdentifier
    frameworkDescription = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
    osDescription = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    measurements = $measurements
}
$reportPath = Join-Path $runtimeOutput 'report.json'
[IO.Directory]::CreateDirectory($runtimeOutput) | Out-Null
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $reportPath -Encoding utf8
Write-Output (
    "Verified trimmed and NativeAOT provider-core publishes for $RuntimeIdentifier; " +
    "report=$reportPath.")
