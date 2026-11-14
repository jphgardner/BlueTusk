[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [string[]] $PackageId,
    [string] $RepositoryRoot = (Split-Path $PSScriptRoot -Parent),
    [string] $ContractPath = (Join-Path $PSScriptRoot 'dotnet-compatibility.json'),
    [switch] $RequireComplete,
    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$inventory = & (Join-Path $PSScriptRoot 'verify-dotnet-compatibility.ps1') -RepositoryRoot $RepositoryRoot `
    -ContractPath $ContractPath -PassThru
$contract = Get-Content -LiteralPath $ContractPath -Raw | ConvertFrom-Json

function Read-ArchiveXml
{
    param([IO.Compression.ZipArchiveEntry] $Entry)

    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 4MB
    $stream = $Entry.Open()
    try
    {
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try
        {
            $document = [Xml.XmlDocument]::new()
            $document.XmlResolver = $null
            $document.Load($reader)
            return $document
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-ArchiveText
{
    param([IO.Compression.ZipArchiveEntry] $Entry)
    if ($Entry.Length -le 0 -or $Entry.Length -gt 1MB) { throw "Unexpected text asset size in '$($Entry.FullName)'." }
    $reader = [IO.StreamReader]::new($Entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Assert-ExactFrameworks
{
    param([string[]] $Expected, [string[]] $Actual, [string] $Context)
    $sortedExpected = @($Expected | Sort-Object -CaseSensitive)
    $sortedActual = @($Actual | Sort-Object -CaseSensitive)
    if ($sortedExpected.Count -ne $sortedActual.Count -or
        @(Compare-Object $sortedExpected $sortedActual -CaseSensitive -SyncWindow 0).Count -ne 0)
    {
        throw "$Context frameworks differ. Expected [$($sortedExpected -join ', ')], found [$($sortedActual -join ', ')]."
    }
}

function Assert-AssemblyTargetFramework
{
    param([IO.Compression.ZipArchiveEntry] $Entry, [string] $Framework)

    if ($Entry.Length -le 0 -or $Entry.Length -gt 64MB)
    { throw "Package assembly '$($Entry.FullName)' has an unexpected size." }
    $stream = $Entry.Open()
    $buffer = [IO.MemoryStream]::new()
    try
    {
        $stream.CopyTo($buffer)
        $buffer.Position = 0
        $pe = [Reflection.PortableExecutable.PEReader]::new($buffer)
        try
        {
            if (-not $pe.HasMetadata) { throw "Package asset '$($Entry.FullName)' is not a managed assembly." }
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
            $definition = $metadata.GetAssemblyDefinition()
            if ($metadata.GetString($definition.Name) -cne [IO.Path]::GetFileNameWithoutExtension($Entry.FullName))
            { throw "Package asset '$($Entry.FullName)' contains a different assembly identity." }
            foreach ($handle in $definition.GetCustomAttributes())
            {
                $attribute = $metadata.GetCustomAttribute($handle)
                if ($attribute.Constructor.Kind -ne [Reflection.Metadata.HandleKind]::MemberReference) { continue }
                $member = $metadata.GetMemberReference([Reflection.Metadata.MemberReferenceHandle]::op_Explicit($attribute.Constructor))
                if ($member.Parent.Kind -ne [Reflection.Metadata.HandleKind]::TypeReference) { continue }
                $type = $metadata.GetTypeReference([Reflection.Metadata.TypeReferenceHandle]::op_Explicit($member.Parent))
                if ($metadata.GetString($type.Namespace) -cne 'System.Runtime.Versioning' -or
                    $metadata.GetString($type.Name) -cne 'TargetFrameworkAttribute') { continue }
                $reader = $metadata.GetBlobReader($attribute.Value)
                if ($reader.ReadUInt16() -eq 1 -and $reader.ReadSerializedString() -ceq ('.NETCoreApp,Version=v' + $Framework.Substring(3)))
                { return }
                break
            }
            throw "Package asset '$($Entry.FullName)' declares a different compiled target framework than '$Framework'."
        }
        finally { $pe.Dispose() }
    }
    finally { $buffer.Dispose(); $stream.Dispose() }
}

$runtime = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
$allPackages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $contract.packages)
{
    $projectPath = Join-Path $RepositoryRoot $package.project
    [xml] $project = Get-Content -LiteralPath $projectPath -Raw
    $explicitId = $project.SelectSingleNode('/Project/PropertyGroup/PackageId')
    $id = if ($null -eq $explicitId) { [IO.Path]::GetFileNameWithoutExtension($projectPath) } else { $explicitId.InnerText }
    if (-not $allPackages.Add($id)) { throw "Duplicate package ID '$id' in the product-family inventory." }
    if ($package.kind -cne 'runtime') { continue }
    $toolCommand = $project.SelectSingleNode('/Project/PropertyGroup/ToolCommandName')
    $packAsTool = $project.SelectSingleNode('/Project/PropertyGroup/PackAsTool')
    $assembly = $project.SelectSingleNode('/Project/PropertyGroup/AssemblyName')
    $runtime.Add($id, [pscustomobject]@{
        project = $package.project
        status = $package.status
        frameworks = @($contract.frameworks[[Array]::IndexOf($contract.frameworks, $package.minimumFramework)..($contract.frameworks.Count - 1)])
        kind = $(if ($null -ne $packAsTool -and $packAsTool.InnerText -ceq 'true') { 'tool' } else { 'library' })
        command = $(if ($null -ne $toolCommand) { $toolCommand.InnerText } else { $null })
        assemblyName = $(if ($null -ne $assembly) { $assembly.InnerText } else { [IO.Path]::GetFileNameWithoutExtension($projectPath) })
    })
}
$selected = @(if ($PackageId) { $PackageId } elseif ($RequireComplete) { $runtime.Keys }
    else { $runtime.Keys | Where-Object { $runtime[$_].status -ceq 'covered' } })
if ($selected.Count -eq 0 -or @($selected | Sort-Object -Unique).Count -ne $selected.Count -or
    @($selected | Where-Object { -not $runtime.ContainsKey($_) }).Count -ne 0)
{ throw 'Select unique runtime package IDs from the compatibility contract.' }
if ($RequireComplete -and $selected.Count -ne $runtime.Count) { throw 'Complete package qualification requires the whole runtime inventory.' }

$selectedIds = [Collections.Generic.HashSet[string]]::new([string[]]$selected, [StringComparer]::OrdinalIgnoreCase)
$verified = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File -Filter '*.nupkg')
{
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try
    {
        $specs = @($zip.Entries | Where-Object { $_.FullName -cmatch '^[^/\\]+\.nuspec$' })
        if ($specs.Count -ne 1) { throw "Package '$($file.Name)' must contain a single root nuspec." }
        $spec = Read-ArchiveXml $specs[0]
        $metadata = $spec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) { throw "Package '$($file.Name)' has no nuspec metadata." }
        $identity = $metadata.SelectSingleNode("*[local-name()='id']")
        $version = $metadata.SelectSingleNode("*[local-name()='version']")
        if ($null -eq $identity -or $null -eq $version -or $file.Name -ine "$($identity.InnerText).$($version.InnerText).nupkg")
        { throw "Package '$($file.Name)' and its nuspec identity differ." }
        $id = $identity.InnerText
        if (-not $selectedIds.Contains($id)) { continue }
        if ($verified.ContainsKey($id)) { throw "Multiple artifacts found for selected runtime package '$id'." }
        $definition = $runtime[$id]
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries)
        {
            $name = $entry.FullName
            if ($name -match '(^/|\\|:|(^|/)\.\.?(/|$))' -or -not $paths.Add($name))
            { throw "Package '$id' has an unsafe or duplicate archive path '$name'." }
        }
        $type = $metadata.SelectSingleNode("*[local-name()='packageTypes']/*[local-name()='packageType'][@name='DotnetTool']")
        $assetPrefix = if ($definition.kind -ceq 'tool') { 'tools' } else { 'lib' }
        $wrongPrefix = if ($definition.kind -ceq 'tool') { 'lib' } else { 'tools' }
        if (@($zip.Entries | Where-Object FullName -Like "$wrongPrefix/*").Count -ne 0 -or
            (($definition.kind -ceq 'tool') -ne ($null -ne $type)))
        { throw "Package '$id' does not have the contracted $($definition.kind) layout." }
        $assets = @($zip.Entries | Where-Object { $_.FullName -clike "$assetPrefix/*/*" })
        $actual = @($assets | ForEach-Object { $_.FullName.Split('/')[1] } | Sort-Object -Unique -CaseSensitive)
        Assert-ExactFrameworks -Expected $definition.frameworks -Actual $actual -Context "Package '$id' $assetPrefix assets"
        foreach ($framework in $definition.frameworks)
        {
            $folder = if ($definition.kind -ceq 'tool') { "tools/$framework/any" } else { "lib/$framework" }
            $assembly = $zip.GetEntry("$folder/$($definition.assemblyName).dll")
            if ($null -eq $assembly -or $assembly.Length -eq 0) { throw "Package '$id' is missing its $framework assembly." }
            Assert-AssemblyTargetFramework -Entry $assembly -Framework $framework
            if ($definition.kind -ceq 'tool')
            {
                $settings = $zip.GetEntry("$folder/DotnetToolSettings.xml")
                $config = $zip.GetEntry("$folder/$($definition.assemblyName).runtimeconfig.json")
                if ($null -eq $settings -or $null -eq $config)
                { throw "Tool '$id' is missing its $framework settings or runtime configuration." }
                $tool = Read-ArchiveXml $settings
                $command = $tool.SelectSingleNode('/DotNetCliTool/Commands/Command')
                if ($null -eq $command -or $command.GetAttribute('Name') -cne $definition.command -or
                    $command.GetAttribute('EntryPoint') -cne "$($definition.assemblyName).dll" -or $command.GetAttribute('Runner') -cne 'dotnet')
                { throw "Tool '$id' has an incorrect $framework command or entrypoint." }
                $runtimeOptions = (Read-ArchiveText $config | ConvertFrom-Json).runtimeOptions
                if ($runtimeOptions.tfm -cne $framework) { throw "Tool '$id' contains the wrong runtime configuration for $framework." }
            }
        }
        if ($definition.kind -ceq 'library')
        {
            $dependencies = $metadata.SelectSingleNode("*[local-name()='dependencies']")
            if ($null -eq $dependencies -or @($dependencies.SelectNodes("*[local-name()='dependency']")).Count -ne 0)
            { throw "Package '$id' must declare framework-specific nuspec dependency groups." }
            $dependencyGroups = @($dependencies.SelectNodes("*[local-name()='group']") | ForEach-Object { $_.GetAttribute('targetFramework') })
            Assert-ExactFrameworks -Expected $definition.frameworks -Actual $dependencyGroups -Context "Package '$id' nuspec dependency groups"
        }
        $verified.Add($id, [pscustomobject]@{
            packageId = $id; version = $version.InnerText; distribution = $definition.kind
            frameworks = $definition.frameworks; path = $file.FullName
            sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        })
    }
    finally { $zip.Dispose() }
}
$missing = @($selected | Where-Object { -not $verified.ContainsKey($_) } | Sort-Object)
if ($missing.Count -ne 0) { throw "Missing selected runtime packages: $($missing -join ', ')." }
$report = [pscustomobject]@{
    schemaVersion = 1
    runtimeInventoryComplete = $inventory.fullRuntimeCoverage
    allRuntimeAssetsVerified = $verified.Count -eq $runtime.Count
    packages = @($verified.Values | Sort-Object packageId)
}
if ($PassThru) { return $report }
Write-Output "Verified exact .NET assets and NuGet metadata in $($verified.Count) runtime packages. All runtime assets verified: $($report.allRuntimeAssetsVerified)."
