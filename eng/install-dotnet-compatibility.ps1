[CmdletBinding()]
param(
    [string] $InstallDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/dotnet'),
    [string] $DownloadCache = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/dotnet-downloads'),
    [string[]] $Framework,
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier = $(if ($IsWindows) { 'win-x64' } else { 'linux-x64' }),
    [switch] $SkipAspNetRuntime,
    [switch] $PlanOnly,
    [string] $ContractPath = (Join-Path $PSScriptRoot 'dotnet-compatibility.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json
if ($contract.schemaVersion -ne 1) { throw 'Unknown .NET compatibility contract schema.' }
if (-not $PlanOnly -and (($IsWindows -and $RuntimeIdentifier -cne 'win-x64') -or
    ($IsLinux -and $RuntimeIdentifier -cne 'linux-x64') -or (-not $IsWindows -and -not $IsLinux)))
{
    throw 'Install compatibility SDKs on their matching Windows or Linux host; use -PlanOnly to inspect another platform.'
}
if (-not $Framework -or $Framework.Count -eq 0) { $Framework = @($contract.frameworks) }
if (@($Framework | Sort-Object -Unique).Count -ne $Framework.Count -or
    @($Framework | Where-Object { $_ -cnotin $contract.frameworks }).Count -ne 0)
{
    throw 'Choose unique frameworks listed in the .NET compatibility contract.'
}
$InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory)
$DownloadCache = [IO.Path]::GetFullPath($DownloadCache)
$metadataCache = @{}
$requests = [System.Collections.Generic.List[object]]::new()
foreach ($target in $Framework)
{
    $runtimeVersion = [string] $contract.runtimeVersions.$target
    $requests.Add([pscustomobject]@{ component = 'runtime'; version = $runtimeVersion; marker = "shared/Microsoft.NETCore.App/$runtimeVersion/System.Private.CoreLib.dll" })
    if (-not $SkipAspNetRuntime)
    {
        $requests.Add([pscustomobject]@{ component = 'aspnetcore-runtime'; version = $runtimeVersion; marker = "shared/Microsoft.AspNetCore.App/$runtimeVersion/Microsoft.AspNetCore.dll" })
    }
}
# Install the SDK last so an older runtime archive cannot leave an older muxer
# at the installation root. No existing runtimes or SDKs are removed.
$requests.Add([pscustomobject]@{ component = 'sdk'; version = [string]$contract.sdkVersion; marker = "sdk/$($contract.sdkVersion)/dotnet.dll" })
$records = [System.Collections.Generic.List[object]]::new()
foreach ($request in $requests)
{
    if ($request.version -cnotmatch '^(\d+)\.0\.\d+$') { throw "Invalid stable .NET version '$($request.version)'." }
    $channel = "$($Matches[1]).0"
    $metadataUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$channel/releases.json"
    if (-not $metadataCache.ContainsKey($channel))
    {
        $metadataCache[$channel] = Invoke-RestMethod -Uri $metadataUrl
    }
    $metadata = $metadataCache[$channel]
    $candidates = foreach ($release in @($metadata.releases))
    {
        if ($request.component -eq 'sdk')
        {
            if ($release.PSObject.Properties.Name -contains 'sdks') { @($release.sdks) }
            elseif ($release.PSObject.Properties.Name -contains 'sdk') { $release.sdk }
        }
        elseif ($release.PSObject.Properties.Name -contains $request.component) { $release.($request.component) }
    }
    $component = @($candidates | Where-Object version -CEQ $request.version | Select-Object -First 1)
    if ($component.Count -ne 1) { throw "Official metadata has no '$($request.component)' version '$($request.version)'." }
    $extension = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) { '.zip' } else { '.tar.gz' }
    $prefix = if ($request.component -eq 'sdk') { 'dotnet-sdk' }
        elseif ($request.component -eq 'runtime') { 'dotnet-runtime' }
        else { 'aspnetcore-runtime' }
    $files = @($component[0].files | Where-Object {
        $_.rid -ceq $RuntimeIdentifier -and $_.name -ceq "$prefix-$RuntimeIdentifier$extension"
    })
    if ($files.Count -ne 1) { throw "Official metadata must provide exactly one $prefix archive for $RuntimeIdentifier." }
    $file = $files[0]
    $downloadUri = [uri] $file.url
    if ($downloadUri.Scheme -cne 'https' -or $downloadUri.Host -cnotin @('builds.dotnet.microsoft.com', 'dotnetcli.blob.core.windows.net', 'download.visualstudio.microsoft.com') -or
        $file.hash -cnotmatch '^[0-9a-fA-F]{128}$')
    {
        throw 'SDK/runtime archives require an official HTTPS download and a SHA512 digest.'
    }
    $record = [ordered]@{
        component = $request.component
        version = $request.version
        runtimeIdentifier = $RuntimeIdentifier
        metadataUrl = $metadataUrl
        url = $file.url
        sha512 = $file.hash.ToLowerInvariant()
        alreadyInstalled = Test-Path -LiteralPath (Join-Path $InstallDirectory $request.marker) -PathType Leaf
    }
    $records.Add([pscustomobject]$record)
    if ($PlanOnly -or $record.alreadyInstalled) { continue }
    New-Item -ItemType Directory -Path $InstallDirectory, $DownloadCache -Force | Out-Null
    $archivePath = Join-Path $DownloadCache "$prefix-$($request.version)-$RuntimeIdentifier$extension"
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf))
    {
        Invoke-WebRequest -Uri $downloadUri -OutFile $archivePath
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash -ine $file.hash)
    {
        throw "SHA512 verification failed for '$archivePath'. The archive was not extracted."
    }
    $extractDirectory = if ($request.component -eq 'sdk') { $InstallDirectory }
        else { Join-Path $DownloadCache ('expand/' + [guid]::NewGuid().ToString('N')) }
    New-Item -ItemType Directory -Path $extractDirectory -Force | Out-Null
    try
    {
        if ($extension -eq '.zip')
        {
            Expand-Archive -LiteralPath $archivePath -DestinationPath $extractDirectory -Force
        }
        else
        {
            & tar -xzf $archivePath -C $extractDirectory
            if ($LASTEXITCODE -ne 0) { throw "Extracting '$archivePath' failed." }
        }
        if ($request.component -ne 'sdk')
        {
            # The SDK supplies the host. Copy framework payloads only, so
            # adding a legacy runtime never overwrites an existing modern muxer.
            $shared = Join-Path $InstallDirectory 'shared'
            New-Item -ItemType Directory -Path $shared -Force | Out-Null
            Get-ChildItem -LiteralPath (Join-Path $extractDirectory 'shared') |
                Copy-Item -Destination $shared -Recurse -Force
        }
    }
    finally
    {
        if ($request.component -ne 'sdk')
        {
            $resolvedExtract = [IO.Path]::GetFullPath($extractDirectory)
            $cachePrefix = $DownloadCache.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
            if (-not $resolvedExtract.StartsWith($cachePrefix, [StringComparison]::OrdinalIgnoreCase))
            {
                throw 'Refusing to remove an extraction directory outside the download cache.'
            }
            Remove-Item -LiteralPath $resolvedExtract -Recurse -Force
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDirectory $request.marker) -PathType Leaf))
    {
        throw "The verified archive did not install the expected '$($request.marker)' file."
    }
}
if ($PlanOnly) { return @($records) }
$evidence = [ordered]@{
    schemaVersion = 1
    installDirectory = $InstallDirectory
    runtimeIdentifier = $RuntimeIdentifier
    artifacts = @($records)
}
New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $InstallDirectory 'bluetusk-installation.json') -Encoding utf8
$executable = Join-Path $InstallDirectory $(if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) { 'dotnet.exe' } else { 'dotnet' })
Write-Output "Installed pinned compatibility SDK/runtimes at '$executable'. Download SHA512 values are recorded in bluetusk-installation.json."
